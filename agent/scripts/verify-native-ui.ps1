param(
    [string]$UnityPath = 'C:/Program Files/Unity/Hub/Editor/6000.6.0f1/Editor/Unity.exe'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity executable not found: $UnityPath"
}

# Opening this project twice can invalidate the editor checks and asset import.
$editors = Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'"
foreach ($editor in $editors) {
    if ($editor.CommandLine -and $editor.CommandLine.Replace('\', '/').Contains($projectRoot.Replace('\', '/'))) {
        throw 'Close the Unity editor for this project before running batch checks.'
    }
}

$runName = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputPath = Join-Path $projectRoot "agent/logs/native-ui-runs/$runName"
New-Item -ItemType Directory -Path $outputPath | Out-Null
$logPath = Join-Path $outputPath 'Editor.log'
$arguments = @(
    '-batchmode',
    '-projectPath', ('"{0}"' -f $projectRoot),
    '-logFile', ('"{0}"' -f $logPath),
    '-executeMethod', 'TiltBrush.TestCHRISNativeUI.Run'
)
$process = Start-Process -FilePath $UnityPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
Write-Output "Native UI checks started (PID $($process.Id)). Log: $logPath"
$process.WaitForExit()
$process.Refresh()
$process.ExitCode | Set-Content -LiteralPath (Join-Path $outputPath 'exit-code.txt')
if ($process.ExitCode -ne 0) {
    throw "Native UI checks failed with exit code $($process.ExitCode). See $logPath"
}
Write-Output "Native UI checks passed. Log: $logPath"
