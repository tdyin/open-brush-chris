param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
$repositoryPath = (Resolve-Path -LiteralPath $Repository).Path
$outputPath = [System.IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path (Split-Path $outputPath) -Force | Out-Null

# Git writes the patch bytes itself. PowerShell text pipelines can alter newlines or binary data.
& git -C $repositoryPath -c core.safecrlf=false diff --binary HEAD --output $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "Git failed to write source patch to $outputPath (exit $LASTEXITCODE)"
}
