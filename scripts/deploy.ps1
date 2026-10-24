# 发布 + 覆盖到本机安装目录
#
# 用法：  powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1
#         powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Target "E:\某处\DouyinDesktop"
#
# 只覆盖程序本身和两个文档：
#   DouyinDesktop.exe / 使用说明.txt / diagnose.bat
# **不碰** assets\（mpv，115MB，重复复制纯属浪费）和 data\（登录态、设置、缓存）。

param(
    [string]$Target = 'D:\Douyin_Desktop_2.0'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$dotnetHome = Join-Path $env:USERPROFILE '.dotnet'
$env:DOTNET_ROOT = $dotnetHome
$env:PATH = "$dotnetHome;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (-not (Test-Path $Target)) {
    Write-Host "[x] 目标目录不存在：$Target" -ForegroundColor Red
    exit 1
}

$running = Get-Process DouyinDesktop -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[x] 程序正在运行（pid $($running.Id -join ',')），先关掉再发布——exe 被占用复制不过去" -ForegroundColor Red
    exit 1
}

$out = Join-Path $root 'dist\pkg\core\DouyinDesktop'

Write-Host '[1/3] 发布…' -ForegroundColor Cyan
& dotnet publish (Join-Path $root 'src\DouyinDesktop.App') -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o $out | Select-Object -Last 2

if ($LASTEXITCODE -ne 0) { Write-Host '[x] 发布失败' -ForegroundColor Red; exit 1 }

# 发布目录里会带 pdb 和 WebView2 的 xml 文档，安装包不需要
Get-ChildItem $out -Filter '*.pdb' | Remove-Item -Force
Get-ChildItem $out -Filter 'Microsoft.Web.WebView2.*.xml' | Remove-Item -Force

# 使用说明.txt / diagnose.bat 不在源码里，publish 不会带。
# 它们的**唯一来源**是 packaging\（进版本库，改了这里才会生效）。
$pkg = Join-Path $root 'packaging'
if (Test-Path $pkg) {
    Copy-Item (Join-Path $pkg '*') $out -Force
    Write-Host '      已从 packaging\ 补齐说明文件' -ForegroundColor DarkGray
} else {
    Write-Host '[!] 没有 packaging\ 目录，安装包会缺使用说明.txt' -ForegroundColor Yellow
}

Write-Host '[2/3] 复制到安装目录…' -ForegroundColor Cyan
$files = @('DouyinDesktop.exe', '使用说明.txt', 'diagnose.bat')
foreach ($f in $files) {
    $src = Join-Path $out $f
    if (-not (Test-Path $src)) {
        # 这两个文件不在源码里，是手工放进 dist\pkg 的。
        # 静默跳过会让人以为更新了、实际安装目录还是旧的说明文档——所以要喊出来。
        Write-Host ("      [!] 缺少 {0}：publish 输出里没有，安装目录这一项不会更新" -f $f) -ForegroundColor Yellow
        continue
    }
    Copy-Item $src (Join-Path $Target $f) -Force
    $size = [math]::Round((Get-Item $src).Length / 1MB, 1)
    Write-Host ("      {0}  {1} MB" -f $f, $size) -ForegroundColor DarkGray
}

Write-Host '[3/3] 完成' -ForegroundColor Green
$exe = Get-Item (Join-Path $Target 'DouyinDesktop.exe')
Write-Host ("      {0}  {1:yyyy-MM-dd HH:mm}  {2} MB" -f $exe.FullName, $exe.LastWriteTime,
    [math]::Round($exe.Length / 1MB, 1)) -ForegroundColor DarkGray
Write-Host '      assets\ 和 data\ 没有动（不重复复制）' -ForegroundColor DarkGray
