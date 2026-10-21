# 抖音桌面版（C# / .NET 10）

一个不加载抖音网页、直接走接口和原生播放器的 Windows 客户端。
目标：刷视频比网页版快，没有广告和埋点。

---

## 先看这里

| 想知道什么 | 看哪个文件 |
| --- | --- |
| 这个项目怎么设计的、为什么不用网页 | `PLAN.md` |
| 电脑上装了哪些东西、能不能删 | `docs/环境说明.md` |
| 文件夹为什么这么分 | `docs/目录结构.md` |
| 已经摸清的接口和踩过的坑 | `docs/接口笔记.md` |

---

## 三分钟跑起来

在项目根目录打开 PowerShell：

```powershell
# 1. 激活环境（把 dotnet 加进 PATH，只在当前窗口有效）
. .\scripts\env.ps1

# 2. 运行
cd src
dotnet run --project DouyinDesktop.App
```

如果提示找不到 `dotnet`，说明环境没装，跑一次 `.\scripts\setup.ps1`。

> 直接双击 `bin\Debug\net10.0-windows\DouyinDesktop.exe` 会报
> "You must install or update .NET"——因为 .NET 10 装在用户目录、没进系统 PATH。
> 请用上面的 `env.ps1` 方式启动，或者在系统环境变量里设置 `DOTNET_ROOT=C:\Users\<你>\.dotnet`。

**操作**：↑↓ 切换、空格暂停、←→ 快退快进 5 秒、M 静音、F 全屏、R 换一批、L 登录。

---

## 目录一览

```
douyin-desktop-v2/
├─ PLAN.md              方案（先读这个）
├─ docs/                说明文档
├─ src/                 源代码（.NET 解决方案）
│  ├─ DouyinDesktop.App/      界面（WPF）
│  ├─ DouyinDesktop.Core/     签名 + 接口 + 数据模型 + 流代理
│  ├─ DouyinDesktop.Player/   播放器（驱动 mpv）
│  ├─ DouyinDesktop.Tests/    签名校验
│  ├─ DouyinDesktop.Probe/    数据层探针（命令行）
│  └─ DouyinDesktop.PlayerProbe/ 播放器行为探针（命令行）
├─ tools/
│  ├─ golden/           签名测试向量（移植验收基准）
│  └─ probe/            截图/按键脚本（开发期验证界面用）
├─ scripts/             一键脚本（环境、下载）
├─ assets/mpv/          mpv 播放器（需下载，不进版本库）
├─ env/python/          Python 虚拟环境（仅跑探针用，不进版本库）
└─ vendor/              参考仓库（只读参考，不进版本库）
```

---

## 当前进度

- [x] 方案确定：原生接口 + mpv 播放，不加载网页
- [x] 签名移植：32 组 golden 向量逐字节一致，服务端已接受
- [x] 数据层：凭据自注册、推荐流、播放地址、翻页去重
- [x] 播放器：mpv 嵌入窗口 + IPC 控制 + 开头段预取
- [x] 界面：竖屏视频 + 作者/标题/统计 + 键盘与滚轮
- [x] 扫码登录：WebView2 跑官方登录页，抓 Cookie 复用
- [ ] 图集作品展示（目前会跳过）

---

## 实测数据（2026-10-21）

| 项目 | 结果 |
| --- | --- |
| 凭据注册（首次） | 461 ms，之后读磁盘缓存 |
| 拉 10 条推荐 | 2.0–3.2 s |
| 播放地址 → MP4 字节 | HTTP 206 + `video/mp4`，首包 216–562 ms |
| 翻页去重 | 新一批 10 条，重复 0 条 |
| **切换视频（命中预取）** | **35–218 ms，平均 65 ms** |
| 切换视频（快速连按） | 150–220 ms |
| 切换视频（未命中，走网络） | 600–1400 ms |
| 全屏进出 | 无重新加载（mpv 不重启） |
| mpv 解码 | H.264 1922×1080 30fps + AAC |
| 内存 | 主程序 ~190 MB，mpv ~230 MB |

---

## 已知限制

1. **第一条永远要等网络。** 冷启动时没有"上一条"可预取，首帧约 1–2 秒。
2. **图集作品直接跳过。** 没有视频流，需要单独的图片轮播逻辑。
3. **登录态靠 Cookie，过期要重扫。** 没有做自动续期，`sessionid` 失效后按 L 重新扫码。
4. **全屏只铺主屏。** 多显示器时全屏会跑到主屏上。

---

## 说明

仅供个人学习使用。不内置任何绕过付费/版权的内容，也不分发抓取到的视频。
