param(
    [string]$Name = 'dev',
    [string]$UnityPath = 'C:/Program Files/Unity/Hub/Editor/6000.6.0f1/Editor/Unity.exe',
    [switch]$SkipChecks
)

# Runs the native editor checks, then builds the Windows OpenXR player into
# Build/CHRIS-<Name>-<timestamp>/OpenBrush.exe and points Build/CHRIS-current at it.
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity executable not found: $UnityPath"
}

function Get-DirtyPaths {
    $raw = git -C $projectRoot status --porcelain=v1 -z --untracked-files=all
    $entries = @{}
    if ($raw) {
        foreach ($item in ($raw -split "`0")) {
            if ($item.Length -gt 3) { $entries[$item.Substring(3)] = $item.Substring(0, 2) }
        }
    }
    return $entries
}

# The checks and the player build rewrite tracked settings (URP runtime settings, preloaded
# assets, shader warmup, GLTF package version) and leave untracked Addressables/temp-scene files.
# Only paths that become dirty during this run are restored; earlier local changes are kept.
$dirtyBefore = Get-DirtyPaths

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

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$buildDir = Join-Path $projectRoot "Build/CHRIS-$Name-$stamp"
$logDir = Join-Path $projectRoot "agent/logs/builds/$Name-$stamp"
New-Item -ItemType Directory -Path $logDir | Out-Null
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
$sideEffects = @($dirtyAfter.Keys | Where-Object { -not $dirtyBefore.ContainsKey($_) } | Sort-Object)
if ($sideEffects.Count -gt 0) {
    $tracked = @($sideEffects | Where-Object { $dirtyAfter[$_] -ne '??' })
    $untracked = @($sideEffects | Where-Object { $dirtyAfter[$_] -eq '??' })
    if ($tracked.Count -gt 0) {
        git -C $projectRoot diff --binary -- $tracked | Set-Content -LiteralPath (Join-Path $logDir 'build-side-effects.patch') -Encoding utf8
        git -C $projectRoot checkout -- $tracked
    }
    foreach ($path in $untracked) {
        $saved = Join-Path $logDir (Join-Path 'untracked' $path)
        New-Item -ItemType Directory -Force -Path (Split-Path $saved) | Out-Null
        Move-Item -LiteralPath (Join-Path $projectRoot $path) -Destination $saved
    }
    # Remove folders the build created that are now empty.
    foreach ($path in $untracked) {
        $dir = Split-Path (Join-Path $projectRoot $path)
        while ($dir.Length -gt $projectRoot.Length -and (Test-Path -LiteralPath $dir) -and -not (Get-ChildItem -LiteralPath $dir -Force)) {
            Remove-Item -LiteralPath $dir
            $dir = Split-Path $dir
        }
    }
    $sideEffects | Set-Content -LiteralPath (Join-Path $logDir 'build-side-effects.txt') -Encoding utf8
    Write-Output "Restored $($sideEffects.Count) path(s) the checks/build changed; saved in $logDir"
}

$errors = @(Select-String -LiteralPath $logPath -Pattern 'error CS\d+|::error ::' | ForEach-Object { $_.Line })
if ($result -ne 'Success' -or -not (Test-Path -LiteralPath $exePath) -or $errors.Count -gt 0) {
    $errors | Select-Object -First 20 | ForEach-Object { Write-Output $_ }
    throw "Build failed (result: $result). See $logPath"
}

# Snapshot the exact source of this build so a later commit can be checked against it.
$sourceDir = Join-Path $logDir 'source'
New-Item -ItemType Directory -Force -Path $sourceDir | Out-Null
git -C $projectRoot diff --binary HEAD | Set-Content -LiteralPath (Join-Path $sourceDir 'tracked.patch') -Encoding utf8 -NoNewline
foreach ($path in (git -C $projectRoot ls-files --others --exclude-standard)) {
    $saved = Join-Path $sourceDir (Join-Path 'untracked' $path)
    New-Item -ItemType Directory -Force -Path (Split-Path $saved) | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot $path) -Destination $saved
}

$head = git -C $projectRoot rev-parse --short HEAD
$branch = git -C $projectRoot branch --show-current
$dirty = git -C $projectRoot status --short
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
if (Test-Path -LiteralPath $current) { cmd /c rmdir "$current" | Out-Null }
cmd /c mklink /J "$current" "$buildDir" | Out-Null

Write-Output "Build succeeded: $exePath"
Write-Output "Newest build is always at: $current\OpenBrush.exe"
