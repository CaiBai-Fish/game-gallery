# 更新日志

本文件记录所有值得注意的改动。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)。

## [1.0.3] - 2026-09-15

### 变更

- **免安装版从「单文件 exe」改成「zip 解压即用」**。单文件形态在本项目上不可靠：本地同一份脚本编出来的
  `-portable.exe` 能跑，CI 编出来的却在启动时崩（`COMException 0x80040111`「ClassFactory 无法供应请求的类」，
  崩在 `Microsoft.UI.Xaml.Application.Start`，即 WinRT 激活找不到类）；1.0.2 加的
  `IncludeAllContentForSelfExtract=true` 只让本地那份恢复，CI 那份依旧崩。
  现在 zip 用的是与 MSI / EXE 安装程序相同的多文件自包含载荷（已反复验证可运行），体积也从 162 MB 降到 64.7 MB。
  解压后双击 `GameGallery.exe` 即可，仍然免安装、不写注册表
- 免安装版与安装程序共用同一个发布目录，CI 少一次全量 publish
## [1.0.2] - 2026-09-15

### 修复

- **单文件便携版启动即崩**：崩在 `Microsoft.UI.Xaml.Application.Start`，异常是
  `COMException (0x80040111)：ClassFactory 无法供应请求的类`——自包含 + 单文件组合下原生组件没有被解包，
  WinRT 激活找不到类。加 `IncludeAllContentForSelfExtract=true`，让它启动前把整套文件解到
  `%TEMP%\.net\...` 再运行（实测 4 秒内出窗口）。**1.0.0 / 1.0.1 的 `-portable.exe` 都有这个问题**
- **单文件便携版的"程序目录"取错**：它把自己解压到 `%TEMP%` 运行，之前拿 `AppContext.BaseDirectory`
  当程序目录，会把自动更新装进临时目录；改为用可执行文件所在目录（`Environment.ProcessPath`）。
  这也会影响"程序目录不可写"的判断
## [1.0.1] - 2026-09-15

### 新增

- **EXE 安装程序**（Inno Setup 6）：per-user、免 UAC、装到 `%LOCALAPPDATA%\Programs\GameGallery`、目录固定不带版本号。
  现在三种产物齐全：`-setup.exe`（推荐）、`.msi`、`-portable.exe`。简体中文语言文件随仓库携带，
  向导按系统界面语言自动匹配（不弹「选择安装语言」）；卸载时问一次是否连用户数据一起删（默认保留，静默卸载直接保留）
- 发布工作流增加 Inno 构建步骤，`-setup.exe` 进 Release 附件与 `hashes` 清单

### 变更

- **更新程序改为「下载官方安装程序并静默运行」**，不再在应用内覆盖程序目录：下载 → 校验 SHA-256 → 退出应用
  → 独立脚本以 `/SILENT /NORESTART` 安装 → 装完启动新版本；失败或被取消则启动原版本，安装包留在原处供重试
- 安装位置随「原形态」：免安装版传 `/DIR=<当前目录>` 装回原处并保持免安装，安装版交给安装程序记录的目录；
  判据是卸载注册表项的 `InstallLocation`（不再用「目录里有没有 unins000.exe」）
- 点「下载并安装」不再多弹一次确认框（点击本身就是确认）；只有程序目录不可写时才退回手动下载出口

### 文档

- `docs/UPDATE-LOGIC.md`：数据契约、客户端安装流程、服务端发布链路、验收清单全部同步为 Inno 版

## [1.0.0] - 2026-09-15

### 新增

- 「更新日志」按钮改为打开一个**固定大小的浮窗**，用 [Markdig](https://github.com/xoofx/markdig) 解析
  `CHANGELOG.md`，再把 AST 渲染成原生 WinUI 元素（标题、有序/无序列表、行内代码、粗体/斜体、链接、引用、代码块、分隔线）。
  窗口不可拖拽调整、不可最大化，同一时刻只有一个实例
- 更新日志内容优先从网络获取（raw → github.com 的 blob 页面 → api），失败时退回本地缓存并在状态行标注「离线」

### 说明

- 没有用 WebView2 渲染 Markdown：那会多一个运行时依赖（Evergreen 运行时），而 Markdig 解析 + 手写渲染
  只多一个纯托管包，还能直接跟随应用主题。

### 文档

- 新增 [docs/UPDATE-LOGIC.md](docs/UPDATE-LOGIC.md)：把这套更新逻辑（多路版本探测、`hashes` 分支校验、
  tag → 编译 → Release 的发布链路）整理成可移植的实现清单，新项目照做即可

## [0.1.2] - 2026-09-15

### 新增

- **单实例限制**：已经有窗口在运行时，再次启动会把它切到前台（最小化时先还原），不再开第二个窗口
- **更新程序**：设置 → 检查更新发现新版本后可以直接「下载并安装」——从 Release 下载 MSI，
  与本仓库 `hashes` 分支里的 SHA-256 清单核对，**通过才运行安装程序**；不匹配会删掉安装包并拒绝安装
- 发布工作流新增一步：推 tag 时自动算出本次产物的 SHA-256，写进 `hashes` 分支的 `<版本>.txt`

### 变更

- 版本号探测新增一条**保底**：前四路（API 的 releases/latest → API 的 tags →
  github.com 的 releases/latest 跳转 → github.com 的 tags 页面）都拿不到时，
  改从 `CHANGELOG.md` 的第一个 `## [x.y.z]` 标题读版本号。
  仓库还没发 Release、也没打 tag 时这一步能兜住

## [0.1.1] - 2026-09-15

### 新增

- 设置面板新增**检查更新**：比对 GitHub 仓库上的最新发布 / 标签与当前版本，发现新版本可一键打开发布页
- 设置面板显示当前版本号，并提供**更新日志**入口
- 新增本更新日志
- 新增 `LICENSE`（MIT）
- 新增 GitHub Actions 工作流 `release.yml`：推送 `x.y.z` 格式的 tag 时自动编译并发布 Release（MSI + 便携版）；
  手动触发只编译并把产物挂到 artifacts。发布前会校验 tag 与 `csproj` 里的 `<Version>` 是否一致

### 说明

- 检查更新允许失败：`api.github.com` 的未认证请求按出口 IP 限流（每小时 60 次），配额用尽时返回 403。
  因此实现里做了四路探测（API 的 `releases/latest` → API 的 `tags` → `github.com/releases/latest` 的 302 →
  `github.com/tags` 页面），任何一路成功即可；全部失败时给出具体原因，并保留手动打开发布页的入口。

## [0.1.0] - 2026-09-14

首个公开版本。

### 新增

- **四级定位游戏安装目录**：HoYoPlay 状态库（`gamedata.dat`，最可靠）→ 注册表卸载项的 `InstallLocation`
  → 磁盘特征扫描（按 `YuanShen.exe` / `StarRail.exe` / `ZenlessZoneZero.exe` / `BH3.exe`）
  → 手动添加的文件夹；每一级的结果都能在「设置 → 定位详情」里看到
- **缩略图网格**：左下角尺寸滑杆（120–420 px）、按时间 / 文件名 / 大小排序、按文件名搜索
- **大图查看器**：从缩略图的位置与尺寸展开铺满窗口，关闭时缩回缩略图；
  以光标为锚点的滚轮缩放；按住左键平移；双击在「适应窗口」与 200% 之间切换；键盘翻页；相邻图片后台预解码
- **文件操作**：收藏（跨会话保存，独立「收藏」标签页）、复制到剪贴板（同时提供文件与位图格式）、
  在资源管理器中显示、用默认应用打开、删除到回收站（带确认对话框，可从回收站还原）、多选批量操作
- **右键菜单**：缩略图与大图上均可直接「在资源管理器中显示」或「复制到剪贴板」
- **自动刷新**：截图文件夹出现新文件时自动更新列表，截完图回到软件就能看到
- **主题**：浅色 / 深色 / 跟随系统
- 导航栏游戏图标复用 HoYoPlay 已下载到本机的图标文件，找不到时回退到内置字体图标

### 打包

- MSI 安装包：per-user 安装，不需要管理员权限，创建开始菜单与桌面快捷方式
- 单文件便携版：双击运行，免安装、不写注册表
- 两种包都自包含 .NET 8 与 Windows App SDK 运行时

[1.0.0]: https://github.com/CaiBai-Fish/game-gallery/compare/0.1.2...1.0.0
[1.0.3]: https://github.com/CaiBai-Fish/game-gallery/compare/1.0.2...1.0.3
[1.0.2]: https://github.com/CaiBai-Fish/game-gallery/compare/1.0.1...1.0.2
[1.0.1]: https://github.com/CaiBai-Fish/game-gallery/compare/1.0.0...1.0.1
[未发布]: https://github.com/CaiBai-Fish/game-gallery/compare/1.0.3...main
[0.1.2]: https://github.com/CaiBai-Fish/game-gallery/compare/0.1.1...0.1.2
[0.1.1]: https://github.com/CaiBai-Fish/game-gallery/compare/0.1.0...0.1.1
[0.1.0]: https://github.com/CaiBai-Fish/game-gallery/releases/tag/0.1.0
