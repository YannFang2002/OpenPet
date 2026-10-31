# 提交前敏感信息扫描（pre-commit hook）。
# 扫描暂存区（index）的全部文本内容，命中任一规则即拒绝提交并打印命中行。
#
# 安装：git config core.hooksPath scripts/githooks
# 绕过：确认无误时可使用 `git commit --no-verify` 强制提交。
$ErrorActionPreference = 'Stop'

try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
} catch {
    # 没有控制台时忽略。
}

# 敏感词用拼接构造，避免扫描脚本自身命中规则。
$patterns = @(
    '[A-Za-z]:\\Users\\[^\\[:space:]]+',
    ('02_Local' + 'WorkSpace'),
    '\b192\.168\.\d{1,3}\.\d{1,3}\b',
    '\b10\.\d{1,3}\.\d{1,3}\.\d{1,3}\b',
    'ghp_[A-Za-z0-9]{20,}',
    'github_pat_[A-Za-z0-9_]{20,}',
    'sk-[A-Za-z0-9]{20,}',
    '[\w.+-]+@(qq|163|gmail)\.com'
)

$combined = ($patterns | ForEach-Object { '(' + $_ + ')' }) -join '|'
$hits = @(git grep --cached -n -I -E $combined 2>$null)
$code = $LASTEXITCODE

if ($code -gt 1) {
    Write-Host "敏感信息扫描失败：git grep 退出码 $code" -ForegroundColor Yellow
    exit $code
}

if ($code -eq 0 -and $hits.Count -gt 0) {
    Write-Host ""
    Write-Host "提交被拒绝：暂存区命中疑似敏感信息（共 $($hits.Count) 行）" -ForegroundColor Red
    $hits | Select-Object -First 50 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    if ($hits.Count -gt 50) {
        Write-Host "  ...（其余命中已省略）" -ForegroundColor Red
    }

    Write-Host ""
    Write-Host "请先移除本机路径 / 内网 IP / 密钥 / 私人邮箱；" -ForegroundColor Yellow
    Write-Host "确认无误时可用 git commit --no-verify 临时绕过本检查。" -ForegroundColor Yellow
    exit 1
}

exit 0
