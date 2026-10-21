# 激活项目环境（只影响当前 PowerShell 窗口，关掉就恢复）
#
# 用法：  . .\scripts\env.ps1
#         ^ 前面那个点号不能少，它表示"在当前窗口执行"，否则设置不会留下

$root = Split-Path -Parent $PSScriptRoot

# .NET SDK 装在用户目录，没进系统 PATH，这里临时补上
$dotnetHome = Join-Path $env:USERPROFILE '.dotnet'
$dotnetExe  = Join-Path $dotnetHome 'dotnet.exe'

if (Test-Path $dotnetExe) {
    $env:DOTNET_ROOT = $dotnetHome
    $env:PATH = "$dotnetHome;$env:PATH"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
} else {
    Write-Host '[!] 没找到 .NET SDK，先运行 scripts\setup.ps1' -ForegroundColor Yellow
}

# 项目内的 Python（只在跑排查脚本时需要）
$pyDir = Join-Path $root 'env\python\Scripts'
if (Test-Path $pyDir) {
    $env:PATH = "$pyDir;$env:PATH"
    $env:VIRTUAL_ENV = Join-Path $root 'env\python'
} else {
    Write-Host '[i] 项目内没有 Python 环境，需要时运行 scripts\setup.ps1' -ForegroundColor DarkGray
}

# 常用路径，方便直接 cd
$env:DY_ROOT = $root
$env:DY_MPV  = Join-Path $root 'assets\mpv\mpv.exe'

Write-Host ''
if (Test-Path $dotnetExe) {
    Write-Host ("  dotnet  " + (& $dotnetExe --version)) -ForegroundColor Green
}
if (Test-Path $pyDir) {
    Write-Host ("  python  " + (& (Join-Path $pyDir 'python.exe') --version)) -ForegroundColor Green
}
if (-not (Test-Path $env:DY_MPV)) {
    Write-Host '  mpv     未下载（播放时需要，运行 scripts\fetch-mpv.ps1）' -ForegroundColor DarkGray
} else {
    Write-Host '  mpv     已就绪' -ForegroundColor Green
}
Write-Host ''
Write-Host ('  项目根目录：' + $root) -ForegroundColor DarkGray
Write-Host ''
