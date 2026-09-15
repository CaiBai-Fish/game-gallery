# 文件哈希

这个分支只放每个版本安装包的 SHA-256 清单，由 `.github/workflows/release.yml` 在推 tag 时自动更新。

格式（每行一个文件，两个空格分隔）：

```
<sha256>  <文件名>
```

程序（设置 → 检查更新 → 下载并安装）会下载 `GameGallery-<版本>.msi`，
再取本分支的 `<版本>.txt` 核对哈希，**不匹配就删掉安装包并且不安装**。

手工核对：

```powershell
Get-FileHash .\GameGallery-0.1.1.msi -Algorithm SHA256
```