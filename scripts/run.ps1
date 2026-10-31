param(
    [string]$Configuration = 'Release',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$exe = Join-Path $root "src\OpenPet.App\bin\$Configuration\net9.0-windows\OpenPet.exe"
if (-not (Test-Path $exe)) {
    throw "找不到 $exe，请先构建。"
}

Start-Process -FilePath $exe
