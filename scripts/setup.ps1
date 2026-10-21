# 一键初始化：把缺的东西补上。可以反复运行，已存在的会跳过。
#
# 用法：  .\scripts\setup.ps1
# 加 -Force 可强制重装某一项，例如：  .\scripts\setup.ps1 -Python -Force

param(
    [switch]$Dotnet,
    [switch]$Python,
    [switch]$Vendor,
    [switch]$Mpv,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# 一个都没指定时，检查全部
$all = -not ($Dotnet -or $Python -or $Vendor -or $Mpv)
if ($all) { $Dotnet = $true; $Python = $true; $Vendor = $true; $Mpv = $true }

function Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Ok($text)   { Write-Host "  [ok] $text" -ForegroundColor Green }
function Skip($text) { Write-Host "  [--] $text" -ForegroundColor DarkGray }

# ---------- .NET SDK ----------
if ($Dotnet) {
    Step '.NET SDK'
    $dotnetExe = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if ((Test-Path $dotnetExe) -and -not $Force) {
        Skip ("已安装 " + (& $dotnetExe --version))
    } else {
        $installer = Join-Path $env:USERPROFILE 'dotnet-install.ps1'
        if (-not (Test-Path $installer)) {
            Write-Host '  下载安装脚本...'
            Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
        }
        Write-Host '  安装 .NET 10 SDK 到用户目录（约 300MB 下载，784MB 占用）...'
        & $installer -Channel 10.0 -InstallDir (Join-Path $env:USERPROFILE '.dotnet') -NoPath
        Ok ("安装完成 " + (& $dotnetExe --version))
    }
}

# ---------- Python 环境 ----------
if ($Python) {
    Step 'Python 虚拟环境（仅跑排查脚本用）'
    $venvPy = Join-Path $root 'env\python\Scripts\python.exe'
    if ((Test-Path $venvPy) -and -not $Force) {
        Skip '已存在'
    } else {
        if ($Force -and (Test-Path (Join-Path $root 'env\python'))) {
            Write-Host '  删除旧环境...'
            Remove-Item (Join-Path $root 'env\python') -Recurse -Force
        }
        # 优先用 WorkBuddy 自带的 Python，其次找系统的
        $base = $null
        $candidates = @(
            "$env:USERPROFILE\.workbuddy-ai\binaries\python\versions\3.13.12\python.exe",
            "D:\Python\python.exe"
        )
        foreach ($c in $candidates) { if (Test-Path $c) { $base = $c; break } }
        if (-not $base) { $base = (Get-Command python -ErrorAction SilentlyContinue).Source }
        if (-not $base) { throw '找不到 Python，请先安装 Python 3.11+，或手动创建 env\python' }

        Write-Host ("  用 $base 创建环境...")
        & $base -m venv (Join-Path $root 'env\python')
        & $venvPy -m pip install -q --upgrade pip
        Write-Host '  安装依赖...'
        & $venvPy -m pip install -q curl_cffi requests loguru cryptography python-dotenv `
            urllib3 retry beautifulsoup4 ecdsa qrcode pillow openpyxl aiofiles "protobuf>=5.27"
        Ok '环境就绪'
    }
}

# ---------- 参考仓库 ----------
if ($Vendor) {
    Step '参考仓库'
    if ((Test-Path (Join-Path $root 'vendor\DouYin_Spider')) -and -not $Force) {
        Skip '已存在'
    } else {
        if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
            Write-Host '  [!] 没装 gh，改用 git clone（可能较慢）' -ForegroundColor Yellow
            git clone --depth=1 https://github.com/cv-cat/DouYin_Spider.git (Join-Path $root 'vendor\DouYin_Spider')
            git clone --depth=1 https://github.com/yhgeo/douyin-desktop.git (Join-Path $root 'vendor\douyin-desktop-v1')
        } else {
            gh repo clone cv-cat/DouYin_Spider (Join-Path $root 'vendor\DouYin_Spider') -- --depth=1
            gh repo clone yhgeo/douyin-desktop (Join-Path $root 'vendor\douyin-desktop-v1') -- --depth=1
        }
        Ok '已克隆到 vendor\'
    }
}

# ---------- mpv ----------
if ($Mpv) {
    Step 'mpv 播放器'
    $mpvExe = Join-Path $root 'assets\mpv\mpv.exe'
    if ((Test-Path $mpvExe) -and -not $Force) {
        Skip '已存在'
    } else {
        & (Join-Path $PSScriptRoot 'fetch-mpv.ps1')
    }
}

Write-Host "`n全部完成。接下来运行：" -ForegroundColor Cyan
Write-Host '  . .\scripts\env.ps1' -ForegroundColor White
Write-Host ''
