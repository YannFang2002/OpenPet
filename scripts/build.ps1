param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Find-Dotnet {
    # Prefer the SDK bundled with this workspace (shared with the OpenTRCC reference).
    $bundled = Join-Path $root '..\..\reference\OpenTRCC\.dotnet\dotnet.exe'
    if (Test-Path $bundled) { return (Resolve-Path $bundled).Path }

    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    throw 'dotnet not found. Install the .NET 9 SDK, or use reference\OpenTRCC\.dotnet\dotnet.exe.'
}

$dotnet = Find-Dotnet
& $dotnet build (Join-Path $root 'OpenPet.sln') -c $Configuration
exit $LASTEXITCODE
