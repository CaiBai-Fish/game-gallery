# 游戏截图图库

[![Release](https://github.com/CaiBai-Fish/game-gallery/actions/workflows/release.yml/badge.svg)](https://github.com/CaiBai-Fish/game-gallery/actions/workflows/release.yml)

一个 Windows 桌面小工具，用来在同一个窗口里快速浏览 **原神**、**崩坏：星穹铁道**、**绝区零**（顺带支持 **崩坏3**）的游戏截图。

装完即用：自动找到游戏安装目录和截图文件夹，不需要任何手动配置。

---

## 安装

仓库只包含源码，`dist\` 下的二进制不入库，自己构建即可（见「从源码构建」）：

```powershell
.\scripts\package-setup.ps1    # -> dist\GameGallery-1.0.3-setup.exe  （EXE 安装程序，推荐）
.\scripts\package-msi.ps1      # -> dist\GameGallery-1.0.3.msi
.\scripts\package.ps1          # -> dist\GameGallery-1.0.3-portable.zip（解压即用）
```

**推荐用 `GameGallery-1.0.3-setup.exe`**：双击按向导装即可，静默安装用

```bat
GameGallery-1.0.3-setup.exe /SILENT /NORESTART
```

MSI 也可以（适合企业分发）：

```bat
msiexec /i GameGallery-1.0.3.msi /qn
```

**都不需要管理员权限**——per-user 安装，程序装到 `%LOCALAPPDATA%\Programs\GameGallery`，卸载信息注册在当前用户下，会正常出现在「设置 → 应用 → 已安装的应用」里。安装完会创建开始菜单和桌面快捷方式（安装程序里可以勾掉桌面图标）。程序目录固定、不带版本号，所以自动更新覆盖的就是这个目录。

这里同样不弹「选择安装语言」：按系统界面语言自动匹配，匹配不到才问；重装时沿用上次的选择。

不想安装的话，用 `GameGallery-1.0.3-portable.zip`：解压到任意目录，双击里面的 `GameGallery.exe` 即可，免安装、不写注册表。

三种包都**自包含**：已经带上 .NET 8 和 Windows App SDK 运行时，目标机器不需要预装任何东西。

**系统要求**：Windows 10 1809 或更高（x64），Windows 11 全部支持。

### 卸载

「设置 → 应用 → 已安装的应用」里卸载即可；命令行的话，安装程序版用

```bat
"%LOCALAPPDATA%\Programs\GameGallery\unins000.exe" /SILENT
```

MSI 版用：

```bat
msiexec /x GameGallery-1.0.3.msi /qn
```

卸载会删掉程序、快捷方式和注册表项，但**保留**缩略图缓存、收藏和设置（在 `%LOCALAPPDATA%\GameGallery`）。
用安装程序卸载时会问一次是否连这些数据一起删（默认保留，静默卸载则直接保留）。

---

## 功能

### 浏览
- **左侧竖直导航栏**列出各游戏（游戏图标 + 截图数量），左上角汉堡按钮可在「完整栏」和「图标条」之间切换；图标在两种状态下尺寸完全一致（34 px），收起时只隐藏文字，导航项本身不重建，所以切换时不会有重排闪烁
- 缩略图网格，缩略图尺寸可调（120–420 px）
- 排序：拍摄时间（新→旧 / 旧→新）、文件名、文件大小
- 按文件名搜索
- 截图文件夹有新文件时自动刷新，截完图回到软件就能看到
- 浅色 / 深色 / 跟随系统主题
- 设置里有**检查更新**：比对仓库上最新的发布 / 标签（都拿不到时退回 CHANGELOG），发现新版本可以「下载并安装」（核对哈希后由独立脚本静默安装官方安装程序）或打开发布页；同时显示当前版本号
- **更新日志**：点设置里的「更新日志」会开一个固定大小的浮窗，用 Markdig 解析 `CHANGELOG.md` 并渲染成原生控件（不可拖拽调整、不可最大化，同时只开一个）
- **单实例**：已经有窗口在运行时，再次启动只会把它切到前台（最小化会先还原），不会开出第二个窗口

### 大图查看器
- **共享元素过渡**：双击缩略图时，图片从缩略图的位置和大小放大铺满窗口；关闭时再缩回它在网格里的缩略图
- **打开时自适应铺满窗口，缩放角标显示 100%**；比例是相对「适应窗口」的，滚轮放大到 200% 就是相对适应尺寸再放大一倍
- **滚轮缩放，以鼠标位置为锚点**（不是简单居中缩放）
- 按住左键拖拽平移；双击在「适应窗口 / 100%」之间切换
- 键盘翻页，相邻图片后台预解码，翻页基本无等待
- 大图解码期间先显示已缓存的缩略图占位

### 文件操作
- 收藏 / 取消收藏，跨会话保存，有独立的「收藏」标签页
- 复制到剪贴板（同时提供「文件」和「位图」两种格式，可直接粘进资源管理器、聊天工具、画图）
- 在资源管理器中显示
- 用默认应用打开
- **删除到回收站**，有确认对话框，可从回收站还原
- 多选（Ctrl / Shift / Ctrl+A）后批量操作

### 右键菜单
在缩略图或大图上点右键，可以直接「在资源管理器中显示」或「复制到剪贴板」。多选状态下会对所有选中项生效。

---

## 快捷键

| 场景 | 按键 | 作用 |
| --- | --- | --- |
| 图库 | `Enter` / 双击 | 打开大图查看器 |
| 图库 | `←→↑↓` | 在网格里移动 |
| 图库 | `Ctrl+A` | 全选当前列表 |
| 图库 | `Ctrl+F` | 聚焦搜索框 |
| 图库 | `F5` | 重新扫描 |
| 通用 | `Ctrl+C` | 复制选中项到剪贴板 |
| 通用 | `F` | 收藏 / 取消收藏 |
| 通用 | `O` | 在资源管理器中显示 |
| 通用 | `Delete` | 删除到回收站（需确认） |
| 查看器 | `Esc` | 返回图库 |
| 查看器 | `←` `→` / `PageUp` `PageDown` / `空格` | 上一张 / 下一张 |
| 查看器 | `Home` `End` | 第一张 / 最后一张 |
| 查看器 | `+` `-` | 放大 / 缩小 |
| 查看器 | `0` | 适应窗口（回到 100%） |
| 查看器 | `1` | 放大到 200% |
| 查看器 | 滚轮 | 以光标为中心缩放 |
| 查看器 | 双击 | 适应窗口（100%） ↔ 200% |
| 查看器 | 按住左键拖动 | 平移 |

---

## 关于「游戏安装目录怎么找」

原神 / 星穹铁道 / 绝区零 都是通过 HoYoPlay 启动器安装的，而**它们不会把安装目录写进常规注册表**。实测：

| 位置 | 实际内容 |
| --- | --- |
| `HKCU\Software\miHoYo\原神` 等 | 只有画质、账号、SDK 设置，**没有安装路径** |
| `HKLM\...\Uninstall\原神` | `InstallLocation = <启动器目录>`，是启动器不是游戏本体 |

真正可靠的来源是 HoYoPlay 自己的状态库：

```
%APPDATA%\miHoYo\HYP\1_1\data\gamedata.dat
```

它是「长度前缀 + JSON 对象」的拼接，每个游戏都有明文 `installPath`。本工具按四级定位，并把每一级的结果显示在「设置 → 定位详情」里：

1. **HoYoPlay 状态库** —— 直接拿到安装目录，最可靠
2. **注册表卸载项** —— `InstallLocation` 确实指向游戏目录时才采用
3. **磁盘特征扫描** —— 按 `YuanShen.exe` / `StarRail.exe` / `ZenlessZoneZero.exe` / `BH3.exe` 识别，只扫每个盘的一层加常见容器目录两层，不做整盘遍历
4. **手动添加的文件夹** —— 兜底

截图目录按各游戏的固定布局查找（取第一个存在的）：

| 游戏 | 相对安装目录 |
| --- | --- |
| 原神 | `ScreenShot` |
| 崩坏：星穹铁道 | `StarRail_Data\ScreenShots` ← 注意是复数，而且在子目录里 |
| 绝区零 | `ScreenShot` |
| 崩坏3 | `ScreenShot` |

### 图标

**导航栏里的游戏图标**来自 HoYoPlay 已经下载到本机的
`%APPDATA%\miHoYo\HYP\<版本>\ico\<gameBiz>.ico`，不再另行分发（那属于游戏本身的美术资源）。找不到图标文件时会自动回退到内置字体图标，不影响功能；设 `GAMEGALLERY_NO_ICONS=1` 可强制走这条路径排查问题。

**程序自己的图标**源图是 `installer/AppIcon.png`，由 `scripts\make-icon.ps1` 生成多尺寸 `installer/GameGallery.ico`（16 / 24 / 32 / 48 / 64 / 128 / 256）。这里有两个容易踩的坑：

1. **ICO 中必须是 32 bpp 的 DIB 条目，不能是 PNG 条目。** `ApplicationIcon` 对应的 SDK 任务会直接忽略 PNG-in-ICO，**而且不报任何错**——症状是"图片换了但 exe 图标没变"。脚本因此自己写 BITMAPINFOHEADER + BGRA 行 + 全零 AND 掩码。
2. **EXE 内嵌图标和窗口图标是两回事。** 文件资源管理器显示的是 PE 内的图标资源（`ApplicationIcon`）；标题栏、任务栏按钮、Alt+Tab 用的是窗口的 `WM_SETICON`。**未打包的 WinUI 3 应用不会自动继承 EXE 图标**，不显式设置就是 Windows App SDK 的默认图标——诡异之处在于同一个 exe「资源管理器里是对的、程序里是错的」。程序启动时调用 `AppWindow.SetIcon()` 指向随生成复制出来的 `Assets\GameGallery.ico`，找不到该文件时回退为 EXE 自身的图标资源（单文件便携版走的就是这条回退）。

---

## 数据与缓存

默认在 `%LOCALAPPDATA%\GameGallery\`：

```
settings.json      设置（缩略图大小、排序、主题、手动添加的文件夹等）
favorites.json     收藏列表
thumbnails\        缩略图缓存（512px JPEG）
startup.log        启动日志与异常堆栈（排查问题用）
```

`%LOCALAPPDATA%` 不可写时会自动回退到程序目录下的 `GameGalleryData\`（绿色版模式）；
也可以用环境变量 `GAMEGALLERY_DATA_DIR` 显式指定。

缩略图缓存键包含文件大小与修改时间，所以截图被覆盖时会自动重新生成。想清理就在「设置 → 清理缩略图缓存」里点一下。

诊断开关：设 `GAMEGALLERY_NO_ICONS=1` 可强制回退到字体图标，用于排查图标文件异常。

---

## 常见问题

**没找到任何截图文件夹？**
打开「设置 → 定位详情」看每一级定位的结果。如果游戏装在非常规位置，用「设置 → 添加」手动指定截图文件夹即可。

**截图数量不对？**
只扫描截图目录的当前层，不递归子目录（这四个游戏的截图目录都是平铺的）。按 `F5` 重新扫描。

**收藏丢了？**
收藏按绝对路径保存，移动或重命名文件后会失效。

---

## 从源码构建

需要：
- .NET 8 SDK（或更高）
- **Visual Studio**，并勾选能提供 PRI 生成任务的工作负载（「通用 Windows 平台开发」/「Windows 应用开发」）
- 打 MSI 还需要 **WiX 3 的免安装二进制**（`candle` / `light` / `heat`），见下

```powershell
.\scripts\build.ps1                 # 构建 Release（框架依赖，产物小）
.\scripts\launch.ps1                # 构建并启动
.\scripts\make-icon.ps1             # 由 installer\AppIcon.png 重新生成 installer\GameGallery.ico
.\scripts\package-setup.ps1         # 生成 EXE 安装程序 -> dist\GameGallery-1.0.3-setup.exe（Inno Setup 6）
.\scripts\package-msi.ps1           # 生成 MSI 安装包  -> dist\GameGallery-1.0.3.msi
.\scripts\package.ps1               # 生成免安装 zip    -> dist\GameGallery-1.0.3-portable.zip
```

WiX 免安装版（不往系统里装任何东西，解压即用）：

```powershell
Invoke-WebRequest 'https://github.com/wixtoolset/wix3/releases/download/wix3112rtm/wix311-binaries.zip' -OutFile wix.zip
Expand-Archive wix.zip -DestinationPath build\tools\wix
```

> **为什么不能用 `dotnet build`？**
> WinUI 3 要生成 `resources.pri`，对应的 MSBuild 任务 `Microsoft.Build.Packaging.Pri.Tasks.ExpandPriContent`
> 只随 Visual Studio 的 UWP 相关组件安装。`dotnet build` 用的 .NET SDK 自带 MSBuild 找不到它，会以 `MSB4062` 失败。
> `build.ps1` 会自己去找 Visual Studio 的 `MSBuild.exe`（优先 vswhere，其次常见安装路径）。

> **`.ps1` 里的中文必须配 UTF-8 BOM。**
> 没有 BOM 时 Windows PowerShell 会按系统 ANSI 代码页（简中是 GBK）读脚本，中文注释和字符串会变成乱码，
> 轻则文案出错，重则字符串终止符失配、整个脚本解析失败。所有脚本都存成「UTF-8 with BOM」。

### 自动发布

`.github/workflows/release.yml`：推 `x.y.z`（或 `vx.y.z`）格式的 tag 时自动在 `windows-latest` 上编译，
生成 MSI 与便携版，取 `CHANGELOG.md` 里对应小节作为发布说明，建 Release 并把两个安装包作为附件传上去。
手动触发（Actions → Release → Run workflow）只编译并把产物挂到 artifacts，不发 Release。

工作流里有两处是给 WinUI 3 准备的：一是用 vswhere 定位 **Visual Studio 的 MSBuild**（原因见上），
二是先解压 WiX 3 免安装二进制到 `build\tools\wix`。另外它会校验 tag 与 `csproj` 里的 `<Version>` 一致，
不一致直接失败——避免打出一个版本号和 tag 对不上的包。

> **为什么 MSI 里不用单文件版？**
> MSI 本来就会把几百个文件收进 CAB 并按文件逐个安装，用单文件毫无好处，反而会让程序**每次启动都先把自己解压到临时目录**。
> 所以 MSI 装的是「自包含 + 常规多文件」的发布目录（488 个文件），清单由 WiX 自带的 `heat.exe` 自动采集，新增依赖不用改脚本。
> 单文件只用于免安装版：一个 exe 双击就能用。

### 工程结构

```
src/GameGallery/
├── GameGallery.csproj           net8.0-windows10.0.19041.0，非打包 WinUI 3
├── app.manifest                 PerMonitorV2 DPI、长路径感知、UTF-8 代码页
├── App.xaml(.cs)                应用入口 + 全局异常记录 + 共享样式
├── MainWindow.xaml(.cs)         左侧导航 / 工具栏 / 缩略图网格 / 状态栏
├── Views/PhotoViewer.xaml(.cs)  大图查看器（自实现缩放平移 + 从缩略图展开的过渡）
├── Views/ChangelogWindow.xaml(.cs)  更新日志浮窗（固定大小、不可调整）
├── Views/MarkdownRenderer.cs    Markdig 的 AST → WinUI 元素
├── Models/                      PhotoItem、GameDefinition、AppSettings
├── Services/
│   ├── GameLocator.cs           四级定位策略
│   ├── LibraryService.cs        扫描截图目录 + FileSystemWatcher 自动刷新
│   ├── ThumbnailService.cs      磁盘缩略图缓存 + 大图解码
│   ├── GameIconService.cs       复用 HoYoPlay 下载的游戏图标
│   ├── AppStorage.cs            设置/收藏持久化 + 可移植回退
│   ├── ShellInterop.cs          资源管理器、回收站、剪贴板、文件夹选择器
│   └── UpdateService.cs         五路探测最新版本、下载安装包并核对 hashes 分支的 SHA-256
├── Converters/Converters.cs     x:Bind 函数绑定用的静态辅助方法
└── Assets/GameGallery.ico       由 installer\GameGallery.ico 复制而来，运行时窗口图标

installer/
├── GameGallery.iss              Inno Setup 6 的 EXE 安装程序定义（per-user、免 UAC、目录固定）
├── ChineseSimplified.isl        简体中文语言文件（官方 Inno 不自带，随仓库携带）
├── GameGallery.wxs              WiX 3 的 MSI 定义（per-user 安装、开始菜单/桌面快捷方式）
├── GameGallery.zh-CN.wxl        中文语言包（Codepage 936）
├── AppIcon.png                  应用图标源图
└── GameGallery.ico              由 make-icon.ps1 生成的多尺寸图标

scripts/
├── build.ps1 / launch.ps1       构建 / 构建并启动
├── make-icon.ps1                由源图生成多尺寸 ICO
├── package-setup.ps1            Inno Setup 6 编译 EXE 安装程序
├── package-msi.ps1              自包含多文件发布 + heat 采集 + candle/light 出 MSI
├── package.ps1                  自包含单文件便携版
└── verify-*.ps1                 基于 UI Automation 的自动化验证（见下）

构建.cmd / 启动图库.cmd          双击即用的两个入口
```

---

## 自动化验证

`scripts/` 下是基于 UI Automation 的端到端测试：真的启动程序、模拟键鼠，并按**像素**和界面状态断言。一键跑全部：

```powershell
.\scripts\verify-all.ps1                 # appicon / nav / icons / gallery / e2e / real / msi
.\scripts\verify-all.ps1 -SkipReal       # 本机没装游戏时跳过真实截图库压测
```

每个套件也可以单独跑，完整输出在 `build\verify\<套件>.log`：

| 套件 | 覆盖 |
| --- | --- |
| `verify-appicon.ps1` | 运行时窗口图标：从源图取特征色，对标题栏和任务栏按钮的实拍区域做像素匹配 |
| `verify-changelog.ps1` | 更新日志浮窗：能打开、窗口样式里没有 `WS_THICKFRAME`/`WS_MAXIMIZEBOX`（即不可调整大小）、Markdig 渲染出的文本与链接、重复点击只开一个 |
| `verify-nav.ps1` | 导航栏：收起/展开时文字与图标的显隐、图标实际渲染尺寸（读 UIA 边界矩形）、导航项有没有被重建 |
| `verify-icons.ps1` | 导航栏游戏图标：真实图标 vs 强制字体图标两次渲染做 A/B 像素比对 |
| `verify-gallery.ps1` | 显示与交互：适应窗口的像素校验、打开过渡动画、缓动非线性、双击 / Enter、两处右键菜单、卡死回归（CPU 自旋检测） |
| `verify-e2e.ps1` | 主流程：首次发现、导航筛选、搜索、查看器、右键菜单、删除确认与取消、回收站，以及「删除确认期间库被刷新」的竞态 |
| `verify-real.ps1` | 真实截图库压测：207 张截图（含 4K PNG 与 jpg），连翻 14 张、缩放、全选 |
| `verify-msi.ps1` | MSI：静默安装 → 快捷方式 → 系统注册 → 启动 → 静默卸载 → 清理 |
| `verify-update-from-msi.ps1` | 更新流程端到端（17 项）：装旧版 MSI → 检查更新 → 下载并安装 → 独立脚本静默安装 → 自动启动新版本 → 安装记录核对 → 清理 |
| `verify-update-e2e.ps1` | 同上但以免安装版（zip）为起点 |

单独跑时的参数（临时目录必须是可丢弃的）：

```powershell
.\scripts\verify-e2e.ps1     -Exe <exe> -DataDir <临时目录> -TestDir <临时目录>
.\scripts\verify-gallery.ps1 -Exe <exe> -DataDir <临时目录> -TestDir <临时目录>
.\scripts\verify-real.ps1    -Exe <exe> -DataDir <临时目录>
.\scripts\verify-icons.ps1   -Exe <exe> -DataDir <临时目录>
.\scripts\verify-msi.ps1     -MsiPath dist\GameGallery-1.0.3.msi
```

> `verify-e2e.ps1` 会真的把 `-TestDir` 里的文件移到回收站，请只指向临时目录。

`scripts\probe-nav-refresh.ps1` 不是断言套件，而是一次性诊断工具：高频采样导航栏状态，用来定位「收起/展开之后某一拍才变化」这类时序问题——导航栏那个 1 秒延迟就是它测出来的（详见下文实现决定）。

### 几个值得一提的验证手法

- **适应窗口**不是看 UI 属性，而是用纯色测试图截图，量出图片实际绘制的像素范围是否为 1424×801。
- **过渡动画**用窄条高速采样量「图片在某一行的横向范围随时间的变化」：打开时应从缩略图的位置和尺寸连续长大到铺满，关闭时应连续缩回缩略图原位。实测打开时首帧就是缩略图宽度 `210 → 1099 → 1204 → … → 1424`，关闭时 `1424@x8 → 582@x193 → … → 210@x274`（缩略图原位），全程单调、无过冲。
- **导航栏时序**用 UIA 每 ~120ms 采样一次「有文字的导航项数量」，把「点击后第几帧生效」变成可读的时间线；据此确认收起/展开都在点击后首帧完成，之后 2.1 秒内无任何变化。
- **导航栏图标尺寸**不看声明的 `Width`，而是读 UIA 给出的渲染边界矩形：折叠态曾经因为内容区只剩 32px 被裁成 `32×34`，现在两种状态都是 `34×34`。
- **卡死回归**读进程累计 CPU 时间：曾经有个无上限的自我重入队导致 UI 线程忙等，卡死时累计烧掉 135 秒 CPU，现在整轮测试的增量在 1 秒以内。
- **导航栏图标**把「用真实图标」和「强制字体图标」两次渲染的同一区域逐像素比对：4 个游戏标签页各有 44–49 个像素不同，而字体图标的基线是 0。
- **窗口图标**不能只看 EXE 资源：那只能证明文件图标对。套件从源图取饱和度最高的高频色（`48,134,253`），再对标题栏图标区域和任务栏按钮的实拍区域做匹配（实测 55 / 112 个像素命中），Windows App SDK 的默认图标是纯蓝 `0,0,255`，与特征色相差 142，不会误判。
- **「不可调整大小的浮窗」不看感觉，读窗口样式**：`GetWindowLong(GWL_STYLE)` 里必须没有 `WS_THICKFRAME`（否则能拖边框）和 `WS_MAXIMIZEBOX`；实测 `style=0x14CA0000`，两项都没有、`WS_MINIMIZEBOX` 保留。

---

## 许可

[MIT](LICENSE)。

游戏名称与图标的相关权利归米哈游所有。本程序**不重新分发**任何游戏美术资源：导航栏图标是直接读取你本机
HoYoPlay 已经下载好的图标文件，找不到时回退到内置字体图标（见「图标」一节）。

---

## 更新日志

见 [CHANGELOG.md](CHANGELOG.md)。程序里「设置 → 更新日志」会开一个固定大小的浮窗，用 Markdig 解析并把内容渲染成原生控件；「设置 → 检查更新」会比对仓库上最新的发布 / 标签，发现新版本时给出「下载并安装」入口。

> 这套更新逻辑（五路版本探测、`hashes` 分支 SHA-256 校验、tag → 编译 → Release 的自动发布）已经整理成可移植的实现清单：
> [docs/UPDATE-LOGIC.md](docs/UPDATE-LOGIC.md)。换语言或换框架的项目照那份文档做即可。

### 检查更新 / 更新程序

版本号按**五路**探测，前四路是「正式发布 / 标签」，第五路是保底：

1. `api.github.com/repos/<owner>/<repo>/releases/latest` —— 最理想，JSON，还能拿到发布页地址
2. `api.github.com/repos/<owner>/<repo>/tags` —— 仓库还没有正式 Release 时的来源
3. `github.com/<owner>/<repo>/releases/latest` 的 302 —— 有 Release 时 `Location` 指向 `/releases/tag/<tag>`，没有时指向 `/releases`；不受 API 配额限制
4. `github.com/<owner>/<repo>/tags` 页面的 HTML —— 抓 `/releases/tag/<tag>` 链接，同样不受配额限制
5. **`CHANGELOG.md` 里第一个 `## [x.y.z]` 标题** —— 保底：仓库既没发 Release 也没打 tag 时，前四路都拿不到东西，这一步兜住

为什么不用一条：`api.github.com` 的未认证请求按出口 IP 限流（每小时 60 次），配额用尽时一律 403——实测本机出口 IP 就经常是 0（同一个出口后面可能有别的程序在刷）；`raw.githubusercontent.com` 在部分网络下不可达。第 5 路本身也做了三级回退（raw → github.com 的 blob 页面 → api），blob 页面里内嵌了文件原文，可以当纯文本解析。五路全失败时显示具体原因（403 / 超时 / 不可达），并保留「打开发布页」的手动出口。

**「下载并安装」是带哈希校验的**：从 Release 取 `GameGallery-<版本>-setup.exe`，
再取 `hashes` 分支下的 `<版本>.txt`（由发布工作流在推 tag 时自动写入，格式 `<sha256>  <文件名>`），
两者不一致就**删掉安装包并拒绝安装**；拿不到清单也不会自动装，只给手动下载的出口。
校验通过后交给一个独立脚本：等本程序退出 → `/SILENT /NORESTART` 静默安装（显示进度但不问"是否重启"）→
装完启动新版本；安装失败或被取消则启动原版本，安装包留在原处供重试。安装位置按"原形态"决定：
免安装版传 `/DIR=<当前目录>` 装回原处，安装版不传（判据是卸载注册表项的 `InstallLocation`）。

发布新版本时按顺序做三件事：改 `CHANGELOG.md`（新增一节）→ 改 `src/GameGallery/GameGallery.csproj` 里的 `<Version>` → 打 tag 推上去。
工作流会自动编译、建 Release、并把产物哈希写进 `hashes` 分支，不需要手工维护。

---

## 已知限制

- **只扫描截图目录的当前层，不递归子目录。** 这四个游戏的截图目录都是平铺的，递归会带来「误加一个大目录就扫全盘」的风险。
- **没有代码签名**，首次运行可能出现 SmartScreen 提示。
- 定位依赖 HoYoPlay 的状态库文件格式；如果米哈游改了 `gamedata.dat` 的结构，第 1 级会失效，但第 2、3 级和手动添加仍然可用。
- 收藏按绝对路径保存，移动/重命名文件后失效。
- 删除只走回收站，没有「彻底删除」选项。
- 更新程序装的是**官方安装程序**（`-setup.exe`，Inno Setup 版）：免安装形态会传 `/DIR=<当前目录>` 装回原处保持免安装，安装版则交给安装程序用它记录的目录，避免同一版本出现两个安装位置。程序目录不可写时（例如装到了 `Program Files`）不自动装，只给手动下载出口。

---

## 实现上几个非显然的决定

- **查看器的缩放平移是自己实现的，没有用 `ScrollViewer.ZoomMode="Enabled"`。** 因为 `ScrollViewer` 会先消费滚轮事件去做垂直滚动，导致「滚轮缩放」和滚动互相打架。
- **图片放在 `Canvas` 里，不是 `Grid` 里。** `Grid` 会把子元素约束成单元格尺寸，图片会被先拉伸到视口大小、再叠加一次缩放变换，结果只画出左上角约 74%。`Canvas` 不会约束子元素。
- **查看器的位置和大小全部走布局（`Canvas.Left/Top` + `Width/Height`），完全不用渲染变换。** 之前用 `CompositeTransform` 同时承担「适应缩放」和「用户缩放」，任何一步算错或没来得及提交，比例就会被叠加两次 —— 典型症状是"图片只画出视口的 74%"。改成走布局后，元素的实际矩形就等于屏幕上看到的矩形。
- **缩放比例的基准是「适应窗口」= 100%**，不是「1 图片像素 = 1 屏幕像素」。1920×1080 的截图在 1440 宽的窗口里自适应后角标就是 100%，看起来是铺满的。
- **共享元素过渡必须等「适应窗口」真的算完再启动。** 视口还没布局好时 `FitToViewport()` 会延后重试；如果此时就把过渡放出去，动画会以**尚未缩放的原始尺寸**为终点，看起来就是"动画结束时过度放大、随即恢复正常"。现在 `FitToViewport()` 返回是否成功，只有成功才放动画。
- **不存在「等布局完成就自己轮询重试」这种写法。** 曾经在入场动画里用 `DispatcherQueue.TryEnqueue` 自我重试等待视口尺寸：Normal 优先级会饿死 Low 优先级的布局/渲染 tick，布局永远完不成，界面直接卡死。现在入场动画的缩放中心由 XAML 的 `RenderTransformOrigin` 固定，完全不需要测量视口；仅有的一次重试有次数上限、走 Low 优先级，并且另有 `SizeChanged` 兜底。
- **`Window` 里的 `DataTemplate` 不能用 `IValueConverter`。** XAML 编译器为 `{x:Bind ... Converter=...}` 生成的代码需要一个 `FrameworkElement` 作为 converter lookup root，而 `Window` 不是 `FrameworkElement`，会直接编译失败。所以可见性判断改成静态函数绑定。
- **`GridView.SelectedItem` 在 `SelectionMode="Extended"` 下不可靠**，实测会返回 `null` 而 `SelectedItems` 里明明有值。所有取「当前项」的地方都从 `SelectedItems` 取。
- **`ContextFlyout` 里的菜单项拿不到数据项**：弹出层不在可视树里，`DataContext` 为空。缩略图模板因此额外绑定了 `Tag="{x:Bind}"`。
- **`GridViewItem` 会消费 `Enter`**，导致挂在根节点上的 Enter 快捷键收不到，所以用 `PreviewKeyDown` 在隧道阶段先接住。
- **查看器缓存只存解码后的 `SoftwareBitmap`，`SoftwareBitmapSource` 一律在显示路径上串行创建。** 让后台预取也去创建（并发调用 `SetBitmapAsync`）会稳定触发原生崩溃。
- **每次扫描都会重建 `PhotoItem`，所以跨刷新定位照片必须按文件路径，不能按对象身份。** 查看器保存的是「正在显示的那张照片」本身，刷新后按路径重新对齐下标，否则画面和当前项会错位，删除/收藏会作用到别的文件上。
- **MSI 用 `-sval` 跳过了 ICE 校验。** ICE 需要连上 Windows Installer 服务，在受限的构建环境里会以 `LGHT0216/0217` 失败。产物本身不依赖它；在普通开发机上可以去掉 `-sval` 做完整校验。
- **导航栏的收起/展开不重建导航项，只切文字的 `Visibility` 和间距。** 早期实现是重建 `NavigationViewItem`，后果有两个：NavigationView 会重播一次选中指示条动画（看起来就是"刷新了一下"），而展开时能否恢复文字取决于重建那一刻 `DisplayMode` 是否已更新——读到的往往是过期值，于是永久只剩图标。
- **导航栏的状态同步改由布局驱动。** `PaneOpened` / `PaneClosed` / `DisplayModeChanged` 触发时 `Nav.IsPaneOpen` 还停在旧值，据此判断必然错一拍：实测展开后文字要 ~1.2 秒才出现（这 1.2 秒又恰好等于"补一次"的定时器，很容易误判成动画慢）。现在订阅 `Nav.LayoutUpdated`，布局完成时属性一定已生效，并且只在状态真的不一致时才动手，避免布局自激。
- **折叠态的栏宽不能想当然。** 展开态的导航项左右各有 16px 内边距（给"图标列 + 文字"预留），折叠栏若继续沿用，34px 的图标只剩 22px 可见。所以显式设 `CompactPaneLength="52"`，图标在两种状态下都是完整的 34×34。
- **更新日志没有用 WebView2，而是 Markdig 解析 + 手写渲染。** WebView2 能直接显示 Markdown 转出的 HTML，但要多一个 Evergreen 运行时依赖（打包体积和部署都跟着变复杂）。Markdig 是纯托管包，把它的 AST 走一遍生成 WinUI 元素，顺带还能用主题资源，深浅色自动跟随。
- **「不可调整大小的浮窗」靠 `OverlappedPresenter`，不是设了尺寸就算。** 只 `AppWindow.Resize()` 的话用户照样能拖边框；要 `presenter.IsResizable = false` + `IsMaximizable = false`，验证时读的是窗口样式里的 `WS_THICKFRAME`/`WS_MAXIMIZEBOX` 有没有消失。
