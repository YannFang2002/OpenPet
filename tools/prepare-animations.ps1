# 需要 Python 3.9+ 与 ffmpeg（PATH 中可用）。
# 真正的转码逻辑在 prepare_animations.py：ffmpeg 必须用 libvpx-vp9 解码，
# 否则 webm 的 alpha 通道会丢，转出来背景全黑。
#
#   pwsh -File tools/prepare-animations.ps1
#   pwsh -File tools/prepare-animations.ps1 -All -Force
param(
    [switch]$All,
    [switch]$Force,
    [int]$Workers = 6,
    [int]$Limit = 0
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script = Join-Path $PSScriptRoot 'prepare_animations.py'

$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
    $python = Get-Command py -ErrorAction SilentlyContinue
}
if (-not $python) {
    throw '未找到 Python。请安装 Python 3.9+，并确保 python 在 PATH 中。'
}

$arguments = @($script, '--workers', $Workers)
if ($All) { $arguments += '--all' }
if ($Force) { $arguments += '--force' }
if ($Limit -gt 0) { $arguments += @('--limit', $Limit) }

Push-Location $root
try {
    & $python.Source @arguments
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
