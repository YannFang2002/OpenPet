param(
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bundled = Join-Path $root '..\..\reference\OpenTRCC\.dotnet\dotnet.exe'
if (Test-Path $bundled) {
    $dotnet = (Resolve-Path $bundled).Path
} else {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $cmd) {
        throw 'dotnet not found. Install the .NET 9 SDK.'
    }

    $dotnet = $cmd.Source
}

$out = Join-Path $root "artifacts\OpenPet-$Runtime"
$selfContainedArg = if ($SelfContained) { 'true' } else { 'false' }
& $dotnet publish (Join-Path $root 'src\OpenPet.App\OpenPet.App.csproj') `
    -c Release `
    -r $Runtime `
    --self-contained $selfContainedArg `
    -o $out

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "发布目录：$out"
