# 下载 mpv 播放器到 assets\mpv
#
# mpv 是视频解码和播放的引擎。我们不自己写解码器，只遥控它。
# 约 120MB，不进版本库。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'assets\mpv'

if (Test-Path (Join-Path $dest 'mpv.exe')) {
    $ver = (& (Join-Path $dest 'mpv.exe') --version 2>&1 | Select-Object -First 1)
    Write-Host "  [--] 已存在：$ver" -ForegroundColor DarkGray
    return
}

if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw '需要 winget（Windows 10 1809+ 自带）。也可以手动下载 mpv 解压到 assets\mpv'
}

New-Item -ItemType Directory -Path $dest -Force | Out-Null

Write-Host '  通过 winget 安装 mpv（约 120MB）...'
winget install shinchiro.mpv --location $dest `
    --accept-package-agreements --accept-source-agreements --disable-interactivity

# 安装器会附带文档、卸载程序，删掉
foreach ($junk in @('doc', 'installer', 'unins000.dat', 'unins000.exe', 'updater.bat')) {
    $p = Join-Path $dest $junk
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}

$mpv = Join-Path $dest 'mpv.exe'
if (-not (Test-Path $mpv)) { throw "安装后没找到 mpv.exe，检查 $dest" }

Write-Host ('  [ok] ' + (& $mpv --version 2>&1 | Select-Object -First 1)) -ForegroundColor Green
