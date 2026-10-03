# Exercises the real build patch writer in a disposable local repository.
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$run = Join-Path $projectRoot ('agent/logs/build-patch-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $run 'fixture'
$replay = Join-Path $run 'replay'
$patch = Join-Path $run 'tracked.patch'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null

& git init -q $fixture
if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the isolated Git fixture' }
& git -C $fixture config core.autocrlf false
if ($LASTEXITCODE -ne 0) { throw 'Could not configure the isolated Git fixture' }

$textFile = Join-Path $fixture 'lines.txt'
$binaryFile = Join-Path $fixture 'bytes.bin'
[System.IO.File]::WriteAllBytes($textFile, [System.Text.Encoding]::UTF8.GetBytes("alpha`nbeta`n"))
[System.IO.File]::WriteAllBytes($binaryFile, [byte[]](0, 1, 2, 3, 4, 255))
& git -C $fixture add -- lines.txt bytes.bin
if ($LASTEXITCODE -ne 0) { throw 'Could not stage the isolated fixture baseline' }
& git -C $fixture -c user.name='CHRIS test' -c user.email='chris-test@example.invalid' commit -q -m baseline
if ($LASTEXITCODE -ne 0) { throw 'Could not commit the isolated fixture baseline' }

# Change both line endings and binary bytes. A PowerShell text pipeline can damage either patch.
[System.IO.File]::WriteAllBytes($textFile, [System.Text.Encoding]::UTF8.GetBytes("alpha`r`nbeta updated`r`n"))
[System.IO.File]::WriteAllBytes($binaryFile, [byte[]](0, 255, 10, 13, 0, 42, 128))
& (Join-Path $PSScriptRoot 'write-source-patch.ps1') -Repository $fixture -Output $patch
if (-not (Test-Path -LiteralPath $patch -PathType Leaf) -or
    -not [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($patch)).Contains('GIT binary patch')) {
    throw 'Source patch is missing its binary payload'
}

& git -c core.autocrlf=false clone -q --no-hardlinks $fixture $replay
if ($LASTEXITCODE -ne 0) { throw 'Could not clone the isolated fixture baseline' }
& git -C $replay -c core.whitespace=cr-at-eol apply --check $patch
if ($LASTEXITCODE -ne 0) { throw 'Generated patch cannot apply to the isolated baseline' }
& git -C $replay -c core.whitespace=cr-at-eol apply $patch
if ($LASTEXITCODE -ne 0) { throw 'Generated patch failed to replay' }

foreach ($name in @('lines.txt', 'bytes.bin')) {
    $expected = (Get-FileHash -LiteralPath (Join-Path $fixture $name) -Algorithm SHA256).Hash
    $actual = (Get-FileHash -LiteralPath (Join-Path $replay $name) -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw "Patch replay changed bytes in $name" }
}
Write-Output "PASS: text line endings and binary bytes replay exactly. Evidence: $run"
