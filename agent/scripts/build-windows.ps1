param(
    [string]$Name = 'dev',
    [string]$UnityPath = 'C:/Program Files/Unity/Hub/Editor/6000.6.0f1/Editor/Unity.exe',
    [switch]$SkipChecks
)

# Runs the native editor checks, then builds the Windows OpenXR player into
# Build/CHRIS-<Name>-<timestamp>/OpenBrush.exe and points Build/CHRIS-current at it.
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'unity-generated-sidecars.ps1')
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity executable not found: $UnityPath"
}

function Get-DirtyPaths {
    $raw = git -C $projectRoot -c core.safecrlf=false status --porcelain=v1 -z --untracked-files=all
    $entries = @{}
    if ($raw) {
        foreach ($item in ($raw -split "`0")) {
            if ($item.Length -gt 3) { $entries[$item.Substring(3)] = $item.Substring(0, 2) }
        }
    }
    return $entries
}

function Get-ProjectPath([string]$relative) {
    $path = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
    $prefix = $projectRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escaped project root: $relative"
    }
    return $path
}

function Get-SourceHash([string]$relative) {
    $path = Get-ProjectPath $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

# Snapshot only paths Unity is known to generate. A dirty file at entry is never restored,
# because it may already hold another agent's work. Unknown changes stop the build for review.
$knownGenerated = @(
    'Assets/AddressableAssetsData/link.xml',
    'Assets/AddressableAssetsData/link.xml.meta',
    'Assets/AddressableAssetsData/Windows.meta',
    'Assets/AddressableAssetsData/Windows/addressables_content_state.bin',
    'Assets/AddressableAssetsData/Windows/addressables_content_state.bin.meta',
    'Assets/Fonts/NotoSansCJK-Light SDF.asset',
    'Assets/Generated/ShaderWarmup/OpenBrushBrushVariants.shadervariants',
    'Assets/Generated/ShaderWarmup/open-brush-brush-variant-inventory.json',
    'Assets/Resources/PerformanceTestRunInfo.json',
    'Assets/Resources/PerformanceTestRunInfo.json.meta',
    'Assets/Resources/PerformanceTestRunSettings.json',
    'Assets/Resources/PerformanceTestRunSettings.json.meta',
    'Assets/Resources/UnityGLTFSettings.asset',
    'Assets/Settings/Open Brush Universal Render Pipeline Asset.asset',
    'Assets/UniversalRenderPipelineGlobalSettings.asset',
    'ProjectSettings/ProjectSettings.asset',
    'Assets/Scenes/Main.unity',
    'Assets/Scenes/Loading.unity',
    'Assets/Scenes/Temp_Loading.unity.meta',
    'Assets/Scenes/Temp_Main.unity.meta'
)
$knownSet = @{}
foreach ($path in $knownGenerated) { $knownSet[$path] = $true }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$buildDir = Join-Path $projectRoot "Build/CHRIS-$Name-$stamp"
$logDir = Join-Path $projectRoot "agent/logs/builds/$Name-$stamp"
$stateDir = Join-Path $logDir 'unity-state'
New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
$dirtyBefore = Get-DirtyPaths
$snapshotUtc = [datetime]::UtcNow
$eligibleSidecars = Get-EligibleUnitySidecars $projectRoot $dirtyBefore
$beforeHashes = @{}
foreach ($path in (@($knownGenerated) + @($dirtyBefore.Keys) | Sort-Object -Unique)) {
    $beforeHashes[$path] = Get-SourceHash $path
}
foreach ($path in $knownGenerated) {
    if ($null -eq $beforeHashes[$path]) { continue }
    $backup = Join-Path $stateDir (Join-Path 'before' $path)
    New-Item -ItemType Directory -Path (Split-Path $backup) -Force | Out-Null
    Copy-Item -LiteralPath (Get-ProjectPath $path) -Destination $backup
}

if (-not $SkipChecks) {
    & (Join-Path $PSScriptRoot 'verify-native-ui.ps1') -UnityPath $UnityPath
}

# verify-native-ui.ps1 rejects an open editor too; repeat the check for -SkipChecks.
$editors = Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'"
foreach ($editor in $editors) {
    if ($editor.CommandLine -and $editor.CommandLine.Replace('\', '/').Contains($projectRoot.Replace('\', '/'))) {
        throw 'Close the Unity editor for this project before building.'
    }
}

$logPath = Join-Path $logDir 'Build.log'
$exePath = Join-Path $buildDir 'OpenBrush.exe'
$arguments = @(
    '-batchmode', '-nographics', '-quit',
    '-projectPath', ('"{0}"' -f $projectRoot),
    '-logFile', ('"{0}"' -f $logPath),
    '-executeMethod', 'BuildTiltBrush.CommandLine',
    '-btb-target', 'StandaloneWindows64',
    '-btb-display', 'OpenXR',
    '-btb-out', ('"{0}"' -f $exePath),
    '-btb-stamp', "chris-$Name-$stamp"
)
$process = Start-Process -FilePath $UnityPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
Write-Output "Build started (PID $($process.Id)). Log: $logPath"

# The batch editor can stall after "Build Finished" (seen in earlier CHRIS builds); give it two
# minutes to exit on its own once the result is logged, then stop it.
$finishedAt = $null
$result = $null
while (-not $process.HasExited) {
    Start-Sleep -Seconds 5
    if (-not $result -and (Test-Path -LiteralPath $logPath)) {
        $line = Select-String -LiteralPath $logPath -Pattern 'Build Finished, Result: (\w+)' | Select-Object -Last 1
        if ($line) {
            $result = $line.Matches[0].Groups[1].Value
            $finishedAt = Get-Date
        }
    }
    if ($finishedAt -and ((Get-Date) - $finishedAt).TotalSeconds -gt 120) {
        Write-Output "Editor still running two minutes after 'Build Finished, Result: $result'; stopping it."
        Stop-Process -Id $process.Id -Force
        break
    }
}
if (-not $result) {
    $line = Select-String -LiteralPath $logPath -Pattern 'Build Finished, Result: (\w+)' | Select-Object -Last 1
    if ($line) { $result = $line.Matches[0].Groups[1].Value }
}
$dirtyAfter = Get-DirtyPaths
$newPaths = @($dirtyAfter.Keys | Where-Object { -not $dirtyBefore.ContainsKey($_) -and -not $knownSet.ContainsKey($_) } | Sort-Object)
$approvedSidecars = @{}
$unexpected = @()
foreach ($path in $newPaths) {
    if (-not $eligibleSidecars.ContainsKey($path)) {
        $unexpected += $path
        continue
    }
    try {
        $approvedSidecars[$path] = Confirm-NewUnitySidecar $projectRoot $path $eligibleSidecars $dirtyAfter $snapshotUtc $process.ExitTime.ToUniversalTime()
    }
    catch {
        $unexpected += "$path ($($_.Exception.Message))"
    }
}
$changedAtEntry = @($dirtyBefore.Keys | Where-Object { (Get-SourceHash $_) -ne $beforeHashes[$_] } | Sort-Object)
if ($unexpected.Count -gt 0 -or $changedAtEntry.Count -gt 0) {
    @('Unexpected new paths:', $unexpected, 'Preexisting files changed:', $changedAtEntry) |
        Set-Content -LiteralPath (Join-Path $stateDir 'needs-review.txt')
    throw "Build changed unknown or preexisting source files; inspect $stateDir before any restoration"
}

$restored = @()
foreach ($path in $knownGenerated) {
    if ($dirtyBefore.ContainsKey($path) -or (Get-SourceHash $path) -eq $beforeHashes[$path]) { continue }
    $target = Get-ProjectPath $path
    # A write after Unity exited cannot be a build side effect; preserve it for review.
    if ((Test-Path -LiteralPath $target -PathType Leaf) -and
        (Get-Item -LiteralPath $target).LastWriteTimeUtc -gt $process.ExitTime.ToUniversalTime().AddSeconds(2)) {
        throw "Known path changed after Unity exited; preserving $target for review"
    }
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        $after = Join-Path $stateDir (Join-Path 'after' $path)
        New-Item -ItemType Directory -Path (Split-Path $after) -Force | Out-Null
        Copy-Item -LiteralPath $target -Destination $after
    }
    if ($null -eq $beforeHashes[$path]) {
        if (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target }
    }
    else {
        $backup = Join-Path $stateDir (Join-Path 'before' $path)
        if ((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $beforeHashes[$path]) {
            throw "Snapshot backup changed; preserving $target for review"
        }
        Copy-Item -LiteralPath $backup -Destination $target -Force
    }
    $restored += $path
}
$restored | Set-Content -LiteralPath (Join-Path $stateDir 'restored.txt')
$dirtyFinal = Get-DirtyPaths
$statusDiff = @($dirtyFinal.Keys | Where-Object {
    if ($dirtyBefore.ContainsKey($_)) { return $dirtyFinal[$_] -ne $dirtyBefore[$_] }
    return -not $approvedSidecars.ContainsKey($_) -or $dirtyFinal[$_] -ne '??'
})
$statusDiff += @($dirtyBefore.Keys | Where-Object { -not $dirtyFinal.ContainsKey($_) })
$sidecarHashDiff = @($approvedSidecars.Keys | Where-Object { (Get-SourceHash $_) -ne $approvedSidecars[$_].Sha256 })
if ($statusDiff.Count -gt 0 -or $sidecarHashDiff.Count -gt 0 -or
    @($knownGenerated | Where-Object { (Get-SourceHash $_) -ne $beforeHashes[$_] }).Count -gt 0) {
    throw "Source differs from the pre-build snapshot; inspect $stateDir"
}
@($approvedSidecars.Keys | Sort-Object | ForEach-Object {
    "$($_)`t$($approvedSidecars[$_].Owner)`t$($approvedSidecars[$_].Sha256)"
}) | Set-Content -LiteralPath (Join-Path $stateDir 'generated-sidecars.tsv')
Write-Output "Restored $($restored.Count) known Unity-generated path(s); archived changed bytes in $stateDir"

$errors = @(Select-String -LiteralPath $logPath -Pattern 'error CS\d+|::error ::' | ForEach-Object { $_.Line })
if ($result -ne 'Success' -or -not (Test-Path -LiteralPath $exePath) -or $errors.Count -gt 0) {
    $errors | Select-Object -First 20 | ForEach-Object { Write-Output $_ }
    throw "Build failed (result: $result). See $logPath"
}

# Snapshot the exact source of this build so a later commit can be checked against it.
$sourceDir = Join-Path $logDir 'source'
New-Item -ItemType Directory -Force -Path $sourceDir | Out-Null
& (Join-Path $PSScriptRoot 'write-source-patch.ps1') -Repository $projectRoot -Output (Join-Path $sourceDir 'tracked.patch')
foreach ($path in (git -C $projectRoot -c core.safecrlf=false ls-files --others --exclude-standard)) {
    $saved = Join-Path $sourceDir (Join-Path 'untracked' $path)
    New-Item -ItemType Directory -Force -Path (Split-Path $saved) | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot $path) -Destination $saved
}

$head = git -C $projectRoot -c core.safecrlf=false rev-parse --short HEAD
$branch = git -C $projectRoot -c core.safecrlf=false branch --show-current
$dirty = git -C $projectRoot -c core.safecrlf=false status --short
@(
    "Build: CHRIS-$Name-$stamp",
    "Branch: $branch at $head (uncommitted changes listed below are included)",
    "Unity: 6000.6.0f1, StandaloneWindows64, OpenXR",
    "Build log: $logPath",
    "Player log: %USERPROFILE%\AppData\LocalLow\Icosa Foundation\Open Brush\Player.log",
    '',
    'Uncommitted:',
    $dirty
) | Set-Content -LiteralPath (Join-Path $buildDir 'BUILD-INFO.txt') -Encoding utf8

# Stable path for the newest build, so shortcuts do not change between builds.
$current = Join-Path $projectRoot 'Build/CHRIS-current'
if (Test-Path -LiteralPath $current) {
    $item = Get-Item -LiteralPath $current -Force
    if ($item.LinkType -ne 'Junction') {
        throw "Refusing to replace a non-junction build path: $current"
    }
    Remove-Item -LiteralPath $current -Force
}
New-Item -ItemType Junction -Path $current -Target $buildDir | Out-Null

Write-Output "Build succeeded: $exePath"
Write-Output "Newest build is always at: $current\OpenBrush.exe"
