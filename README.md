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
# 1. 激活环境（把 dotnet 和 python 加进 PATH，只在当前窗口有效）
. .\scripts\env.ps1

# 2. 编译并运行
cd src
dotnet run --project DouyinDesktop.App
```

如果提示找不到 `dotnet`，说明环境没装，跑一次：

```powershell
.\scripts\setup.ps1
```

---

## 目录一览

```
douyin-desktop-v2/
├─ PLAN.md              方案（先读这个）
├─ docs/                说明文档
├─ src/                 源代码（.NET 解决方案）
│  ├─ DouyinDesktop.App/     界面（WPF）
│  ├─ DouyinDesktop.Core/    签名 + 接口 + 数据模型
│  ├─ DouyinDesktop.Player/  播放器（驱动 mpv）
│  └─ DouyinDesktop.Tests/   测试（含签名校验）
├─ tools/
│  ├─ golden/           签名测试向量（移植验收基准）
│  └─ probe/            临时验证脚本（Python，排查用）
├─ scripts/             一键脚本（环境、下载）
├─ assets/mpv/          mpv 播放器（需下载，不进版本库）
├─ env/python/          Python 虚拟环境（仅跑探针用，不进版本库）
└─ vendor/              参考仓库（只读参考，不进版本库）
```

---

## 当前进度

- [x] 方案确定：原生接口 + mpv 播放，不加载网页
- [x] 数据链路实测打通（推荐流 → 播放地址 → MP4 流）
- [x] 签名测试向量（32 组，可复现）
- [ ] 签名移植到 C#
- [ ] 数据层
- [ ] 播放器
- [ ] 扫码登录
- [ ] 界面

---

## 说明

仅供个人学习使用。不内置任何绕过付费/版权的内容，也不分发抓取到的视频。
