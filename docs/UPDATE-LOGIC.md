# 更新逻辑实现清单（可移植版）

这份文档描述本项目采用的「检查更新 + 更新程序」标准做法，目的是**换语言、换框架、换项目时照着做即可**，不必重新推导。
WinUI 3 的具体实现见 `src/GameGallery/Services/UpdateService.cs` 与 `.github/workflows/release.yml`。

---

## 1. 数据契约

先固定这五件事，客户端与服务端都依赖它们：

| # | 契约 | 本项目的取值 |
| --- | --- | --- |
| 1 | 版本号来源 | 依次探测：API `releases/latest` → API `tags` → `github.com/releases/latest` 的 302 → `github.com/tags` 页面 → **`CHANGELOG.md` 的第一个 `## [x.y.z]`（保底）** |
| 2 | tag 与文件名 | tag 为 `x.y.z`（也兼容 `vx.y.z`）；产物固定命名 `<App>-<版本>.msi`、`<App>-<版本>-portable.exe` |
| 3 | 安装包下载地址 | `https://github.com/<owner>/<repo>/releases/download/<原始 tag>/<App>-<版本>.msi`（tag 可能带 `v`，文件名不带） |
| 4 | 哈希清单 | `hashes` 分支下的 `<版本>.txt`，每行 `<sha256>␣␣<文件名>`（小写十六进制、两个空格分隔） |
| 5 | 更新日志格式 | `CHANGELOG.md`，Keep a Changelog：`## [x.y.z] - YYYY-MM-DD`，未发布写 `## [未发布]` |

> 第 4 条的分支只放清单（孤儿分支，不带源码树），这样"文件哈希"这件事不会和源码历史纠缠。

---

## 2. 客户端：检查更新

按顺序探测，**第一个拿到版本号的就用**，全部失败才报错：

1. `GET api.github.com/repos/<owner>/<repo>/releases/latest` → JSON 的 `tag_name`（404 = 仓库还没发 Release，换下一路；403 = 限流，换下一路）
2. `GET api.github.com/repos/<owner>/<repo>/tags` → 数组第一个元素的 `name`
3. `GET github.com/<owner>/<repo>/releases/latest`，**不要跟随重定向**，读 `Location`：匹配 `/releases/tag/([^/?#]+)` 就是 tag；跳到 `/releases` 说明没有 Release
4. `GET github.com/<owner>/<repo>/tags`，正则 `/releases/tag/([^"'\?#]+)` 取第一个
5. 保底：取 `CHANGELOG.md` 原文，正则 `(?m)^##\s*\[?(\d+\.\d+(?:\.\d+)?)\]?` 取第一个标题。这一路自身再做三级回退：
   `raw.githubusercontent.com/<owner>/<repo>/main/CHANGELOG.md` → `github.com/<owner>/<repo>/blob/main/CHANGELOG.md`（解析内嵌 JSON，见下）→ `api.github.com/.../contents/CHANGELOG.md?ref=main`（请求头 `Accept: application/vnd.github.raw`）

拿到 tag 后归一化（去掉 `v` 前缀）再和本地版本比较：大于 = 有新版本，等于 = 最新，小于 = 本地是开发版。

**为什么不能只用 API**（实测）：`api.github.com` 未认证请求按出口 IP 限流 60 次/小时，经常直接 403；`raw.githubusercontent.com` 在部分网络下不可达；`github.com` 本身很稳。三者覆盖面不同，所以要多路。

### 从 blob 页面里取出文件原文

`github.com/<owner>/<repo>/blob/<ref>/<path>` 是 HTML，不能直接当文本文档解析。页面里有：

```html
<script type="application/json" data-target="react-app.embeddedData">{...}</script>
```

这段 JSON 的 `payload.codeViewBlobLayoutRoute.StyledBlob.rawLines` 是**文件原文的字符串数组**，逐项拼接（每行后加 `\n`）即可还原。把它当第二数据源，比抓 HTML 正则稳得多。

---

## 3. 客户端：更新程序

```
用户点「下载并安装」
  → 确认对话框（版本号、体积、会退出程序）
  → 下载 releases/download/<tag>/<App>-<版本>.msi 到临时目录，带百分比进度
  → 取 hashes 分支的 <版本>.txt，找出该文件名的 SHA-256
       ├─ 清单取不到        → 删文件 + 提示「为安全起见不自动安装，请手动下载」
       └─ 哈希不匹配        → 删文件 + 显示期望值与实测值
  → 校验通过 → 运行 msiexec /i <msi> → 退出本程序
```

要点：

- 哈希比较用**实测重算**的 SHA-256（流式读取，别整文件读进内存），不匹配时必须先删文件再报错。
- 下载地址用原始 tag，文件名用归一化版本号——两者别混用。
- 拿不到清单也不装：`hashes` 分支缺失意味着"无法验证"，不是"可以放行"。
- 便携版不做自我替换（运行中的 exe 无法覆盖自己），所以「下载并安装」一律装 MSI 版；这一点要写进已知限制。
- 更新日志窗口/页面同样复用第 5 条数据契约，内容取不到时退回本地缓存并明确标注「离线」。

---

## 4. 服务端：发布工作流

触发：`push` 的 tag 匹配 `[0-9]*.[0-9]*.[0-9]*` 或 `v[0-9]*.[0-9]*.[0-9]*`；另加 `workflow_dispatch` 供手动跑（只编译 + 上传 artifacts，不发 Release）。

步骤顺序（顺序本身有讲究）：

1. 检出源码 → 装 SDK / 工具链（如 WiX）
2. **解析版本号并校验 tag 与工程版本一致**（不一致直接失败）
3. 构建两个产物：MSI（自包含多文件、per-user、免管理员）+ 便携单文件
4. **从 CHANGELOG 提取本版本小节**写入发布说明文件
5. **生成 SHA-256 清单并推送到 `hashes` 分支**（每版本一个 `<版本>.txt`）
6. 上传 artifacts（手动运行时看）
7. `gh release create <tag> <msi> <portable> --notes-file <说明文件>`

两个已经踩过的坑：

- **第 5 步必须在第 4 步之后**：为了写清单会 `git checkout` 到 `hashes` 分支，切过去之后工作区里就没有 `CHANGELOG.md` 了。
- **Git 分支操作要判 `$LASTEXITCODE`**：在 GitHub Actions 的 `pwsh` 里，原生命令返回非零可能直接抛错，也可能被静默吞掉；显式判断并 `throw` 才可靠。另外**绝不要用空的 commit 变量拼 refspec**（`git push origin "${commit}:refs/heads/x"` 在 `$commit` 为空时会**删除分支**）。
- 幂等：分支已存在时走 `checkout -B <branch> origin/<branch>`，不存在才建孤儿分支。

---

## 5. 验收清单（自动化，不看"感觉对"）

| 断言 | 怎么量 |
| --- | --- |
| 版本探测在本机实际命中哪一路 | 日志打印来源名字（如"从「github.com 的 releases/latest 跳转」读到 0.1.2"） |
| 下载的安装包可信 | 实测 `SHA256` 与 `hashes` 分支清单逐字符相等 |
| 哈希不匹配会拒绝安装 | 故意改一个字，断言文件被删除且没有启动安装程序 |
| 清单缺失会拒绝安装 | 用一个没有清单的版本号，断言提示手动下载 |
| 发布链路端到端 | 推一个 tag，等 CI 绿，检查 Release 附件与 `hashes` 分支新增的 `<版本>.txt` |
| tag 与版本号不一致会失败 | 故意打一个与工程版本不同的 tag（可在本地用 `workflow_dispatch` 逻辑验证） |

用 PowerShell 复算哈希做交叉验证的最小命令：

```powershell
# 取清单里的期望值
$expected = (Invoke-RestMethod 'https://raw.githubusercontent.com/<owner>/<repo>/hashes/<版本>.txt') -split "`n" |
    Where-Object { $_ -match [regex]::Escape('<App>-<版本>.msi') } |
    ForEach-Object { ($_ -split '\s+')[0] }

# 实测值
$actual = (Get-FileHash .\<App>-<版本>.msi -Algorithm SHA256).Hash.ToLower()

"$expected`n$actual`n一致=$($expected -eq $actual)"
```

---

## 6. 移植到新项目

1. 复制本仓库的 `UpdateService`（或按第 2、3 节重写）：只依赖 HTTP 客户端 + SHA-256 + 一个临时目录。
2. 改三个常量：`owner`、`repo`、当前版本从程序集信息读（不要写常量）。
3. 复制 `.github/workflows/release.yml`，改产物名与构建命令。
4. 仓库建好第一次发版后，确认 `hashes` 分支出现了 `<版本>.txt`（第一次由工作流创建孤儿分支）。
5. 把第 5 节的六条断言写成脚本，纳入每次发版前的检查。
