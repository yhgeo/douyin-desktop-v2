# 一键自检：编译 → 单元测试 → 偷懒代码扫描 → 离线探针
#
# 用法：  powershell -ExecutionPolicy Bypass -File scripts\check.ps1
#         powershell -ExecutionPolicy Bypass -File scripts\check.ps1 -Full   # 额外跑联网探针
#
# 设计原则：默认**不联网、不弹窗**。联网的部分放 -Full 里，手动决定什么时候跑。

param([switch]$Full)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# dotnet 输出是 UTF-8；不设这个的话，中文在 PowerShell 5.1 里会显示成乱码
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$dotnetHome = Join-Path $env:USERPROFILE '.dotnet'
$env:DOTNET_ROOT = $dotnetHome
$env:PATH = "$dotnetHome;$dotnetHome\tools;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$failed = 0
function Step($name, $block) {
    Write-Host ""
    Write-Host "=== $name ===" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[x] $name 失败" -ForegroundColor Red
        $script:failed++
    }
}

Step '编译（全部项目）' {
    & dotnet build (Join-Path $root 'DouyinDesktop.sln') -v q --nologo
}

Step '单元测试（签名 golden）' {
    & dotnet test (Join-Path $root 'src\DouyinDesktop.Tests\DouyinDesktop.Tests.csproj') --nologo -v q
}

# slopwatch：只报「相对基线新增」的偷懒模式（空 catch、禁用测试、抑制警告）
Write-Host ""
Write-Host "=== 偷懒代码扫描（slopwatch）===" -ForegroundColor Cyan
if (Get-Command slopwatch -ErrorAction SilentlyContinue) {
    Push-Location $root
    & slopwatch scan
    $swExit = $LASTEXITCODE
    Pop-Location
    if ($swExit -ne 0) {
        Write-Host "[x] 检测到新增的偷懒模式（基线在 .slopwatch\baseline.json）" -ForegroundColor Red
        $script:failed++
    }
} else {
    Write-Host "[i] 没装 slopwatch，跳过。装法：dotnet tool install --global Slopwatch.Cmd" -ForegroundColor DarkGray
}

# 离线探针：预取缓存的读写/换流拒绝/容量清理，不联网
Step '离线探针（预取缓存）' {
    & dotnet run --project (Join-Path $root 'src\DouyinDesktop.Probe') --no-build -- cache
}

if ($Full) {
    Write-Host ""
    Write-Host "=== 联网探针（凭据 → 推荐流 → 播放地址）===" -ForegroundColor Yellow
    & dotnet run --project (Join-Path $root 'src\DouyinDesktop.Probe') --no-build
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[x] 数据链路异常" -ForegroundColor Red
        $failed++
    }
}

Write-Host ""
if ($failed -eq 0) {
    Write-Host "全部通过" -ForegroundColor Green
    exit 0
}
Write-Host "$failed 项失败" -ForegroundColor Red
exit 1
