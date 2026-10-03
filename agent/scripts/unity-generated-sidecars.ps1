# Functions shared by the Windows build helper and its isolated sidecar check.
function Resolve-UnitySidecarPath([string]$ProjectRoot, [string]$Relative) {
    $root = [System.IO.Path]::GetFullPath($ProjectRoot)
    $path = [System.IO.Path]::GetFullPath((Join-Path $root $Relative))
    $prefix = $root.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escaped project root: $Relative"
    }
    return $path
}

function Get-EligibleUnitySidecars([string]$ProjectRoot, [hashtable]$DirtyBefore) {
    $eligible = @{}
    foreach ($asset in $DirtyBefore.Keys) {
        # Only a source file already added before the snapshot can acquire a new sidecar.
        if ($DirtyBefore[$asset] -notin @('??', 'A ', ' A', 'AM') -or
            $asset -cnotmatch '^Assets/(?:[^/]+/)*[^/]+\.cs$') { continue }
        $sidecar = "$asset.meta"
        $source = Resolve-UnitySidecarPath $ProjectRoot $asset
        $meta = Resolve-UnitySidecarPath $ProjectRoot $sidecar
        if ($DirtyBefore.ContainsKey($sidecar) -or
            -not (Test-Path -LiteralPath $source -PathType Leaf) -or
            (Test-Path -LiteralPath $meta)) { continue }
        $item = Get-Item -LiteralPath $source
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        $eligible[$sidecar] = $asset
    }
    return $eligible
}

function Confirm-NewUnitySidecar(
    [string]$ProjectRoot, [string]$Relative, [hashtable]$Eligible,
    [hashtable]$DirtyAfter, [datetime]$SnapshotUtc, [datetime]$ExitUtc) {
    if (-not $Eligible.ContainsKey($Relative) -or $DirtyAfter[$Relative] -ne '??') {
        throw "Unexpected Unity sidecar: $Relative"
    }
    $path = Resolve-UnitySidecarPath $ProjectRoot $Relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Unity sidecar is missing: $Relative"
    }
    $item = Get-Item -LiteralPath $path
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -gt 65536 -or
        $item.LastWriteTimeUtc -lt $SnapshotUtc.AddSeconds(-2) -or
        $item.LastWriteTimeUtc -gt $ExitUtc.AddSeconds(2)) {
        throw "Unity sidecar needs review: $Relative"
    }
    $content = Get-Content -LiteralPath $path -Raw
    if (-not [regex]::IsMatch($content,
        '\AfileFormatVersion: 2\r?\nguid: [0-9a-f]{32}(?:\r?\n|\z)',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        throw "Invalid Unity sidecar header: $Relative"
    }
    return [pscustomobject]@{
        Relative = $Relative
        Owner = $Eligible[$Relative]
        Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
}
