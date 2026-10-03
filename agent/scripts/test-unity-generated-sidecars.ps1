# Exercises sidecar classification without starting Unity or changing project assets.
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'unity-generated-sidecars.ps1')

$fixtureRoot = Join-Path $projectRoot ('agent/logs/sidecar-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$assetDirectory = Join-Path $fixtureRoot 'Assets/CHRIS/Runtime'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
$source = Join-Path $assetDirectory 'CHRISNewCode.cs'
$sidecar = "$source.meta"
'class CHRISNewCode {}' | Set-Content -LiteralPath $source
$before = @{ 'Assets/CHRIS/Runtime/CHRISNewCode.cs' = '??' }
$snapshot = [datetime]::UtcNow
$eligible = Get-EligibleUnitySidecars $fixtureRoot $before
if ($eligible.Count -ne 1 -or $eligible['Assets/CHRIS/Runtime/CHRISNewCode.cs.meta'] -ne 'Assets/CHRIS/Runtime/CHRISNewCode.cs') {
    throw 'New source was not eligible for a Unity sidecar.'
}

$relative = 'Assets/CHRIS/Runtime/CHRISNewCode.cs.meta'
$after = @{ $relative = '??' }
"fileFormatVersion: 2`nguid: 0123456789abcdef0123456789abcdef`n" | Set-Content -LiteralPath $sidecar
$approved = Confirm-NewUnitySidecar $fixtureRoot $relative $eligible $after $snapshot ([datetime]::UtcNow)
if ($approved.Owner -ne 'Assets/CHRIS/Runtime/CHRISNewCode.cs' -or
    $approved.Sha256 -ne (Get-FileHash -LiteralPath $sidecar -Algorithm SHA256).Hash) {
    throw 'Valid sidecar was not recorded with its source and hash.'
}

function Assert-Rejected([scriptblock]$Action, [string]$Case) {
    try { & $Action; throw "Accepted invalid sidecar: $Case" }
    catch {
        if ($_.Exception.Message -like "Accepted invalid sidecar:*") { throw }
    }
}

Assert-Rejected { Confirm-NewUnitySidecar $fixtureRoot $relative $eligible @{ $relative = ' M' } $snapshot ([datetime]::UtcNow) } 'modified status'
'not a Unity meta file' | Set-Content -LiteralPath $sidecar
Assert-Rejected { Confirm-NewUnitySidecar $fixtureRoot $relative $eligible $after $snapshot ([datetime]::UtcNow) } 'invalid header'
"fileFormatVersion: 2`nguid: 0123456789abcdef0123456789abcdef`n" | Set-Content -LiteralPath $sidecar
(Get-Item -LiteralPath $sidecar).LastWriteTimeUtc = $snapshot.AddMinutes(-1)
Assert-Rejected { Confirm-NewUnitySidecar $fixtureRoot $relative $eligible $after $snapshot ([datetime]::UtcNow) } 'old timestamp'

$notNew = @{ 'Assets/CHRIS/Runtime/CHRISNewCode.cs' = ' M' }
if ((Get-EligibleUnitySidecars $fixtureRoot $notNew).Count -ne 0) {
    throw 'Tracked source was eligible for a new sidecar.'
}
if ((Get-EligibleUnitySidecars $fixtureRoot $before).Count -ne 0) {
    throw 'Existing sidecar was eligible for replacement.'
}

Write-Output "Sidecar checks passed; fixture: $fixtureRoot"
