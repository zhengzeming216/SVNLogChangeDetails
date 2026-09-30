# Svn Method Lens

在 Visual Studio 2022 / 2026 的 C# 编辑器里，把 **SVN 的方法级归属**直接显示在每个方法名上方 —— 体验对齐 Git 的 CodeLens。

![preview](Resources/preview.png)

SVN 原生只有行级 blame，而 VS 的 CodeLens 只对 Git 生效。本插件补齐了这块：读取 SVN 工作副本的 `svn blame` 数据，聚合到每个方法 / 构造函数 / 属性上，内联显示：

```
最后修改 张三，35 天前 · 2 名作者，6 次更改（r10273）
public async Task<ResultList<ShipViewModel>> GetShipList(...)
{
```

- **有本地未提交改动**的方法显示：`本地未提交改动 · 主作者 张三 · N 名作者`
- **点击标注**：弹出该方法涉及的提交历史（修订号 / 作者 / 时间 / 提交说明），结果带缓存

## 特性

- 类 Git CodeLens 的行上方预留空带（`ILineTransformSource`），不遮挡代码
- 随滚动按视口增量绘制（`AddAdornment` 只对已排版行生效，故只在视口附近添加装饰）
- 文件内容哈希级缓存：内存 + 磁盘（`%LOCALAPPDATA%\SvnMethodLens\cache`），大文件首次 `svn blame` 较慢（取决于行数 × 修订数），重开文件秒出
- 计算不因编辑中断（排水泵模式），编辑后自动接力重算
- 标注与弹窗均使用 VS 主题色（`VsBrushes.GrayTextKey` 等），深浅色主题自适应
- 不依赖 Roslyn（避免与 VS 自带 Roslyn 版本冲突），方法边界由轻量花括号栈扫描器（`EditorMethodScanner`）识别，已与 Roslyn 精确扫描交叉验证

## 环境要求

- Visual Studio 2022 (17.x) 或 2026 (18.x)，Community / Professional / Enterprise
- .NET Framework 4.7.2+
- TortoiseSVN 自带的 `svn.exe`（默认查找 `D:\Program Files\TortoiseSVN\bin\svn.exe`，可在 `BlameService.cs` 中修改）
- SVN 1.8+ 工作副本（使用了 `--show-item wc-root`）

## 安装

双击 `dist/SvnMethodLens.vsix`，选择目标 VS 实例安装，装完重启 VS。

> 若非管理员安装后扩展不可见（VSIXInstaller 已知问题：会把 `extensionDir` 写成 Program Files 悬空路径），运行 `install.ps1` 一键修复重装。

## 构建

```powershell
dotnet build SvnMethodLens.VsExt/SvnMethodLens.VsExt.csproj -c Release
# 打包 vsix（需 VS SDK BuildTools 的 VsixUtil.exe）
VsixUtil.exe package -outputPath dist/SvnMethodLens.vsix `
  -sourceManifest SvnMethodLens.VsExt/source.extension.vsixmanifest `
  -files SvnMethodLens.VsExt/bin/Release/net48/files.json `
  -installationFolder SvnMethodLens -noValidate
```

`Resources/logo.png` 与 `preview.png` 由 `make_logo.py` 生成（Python + Pillow）。

## 项目结构

```
SvnMethodLens.Svn/     svn.exe 调用层（blame --xml / log --xml / wc-root 解析）
SvnMethodLens.Core/    Roslyn 精确方法扫描 + 时间线分析（供 CLI 复用，VSIX 不引用）
SvnMethodLens.VsExt/   VS 扩展：装饰层、行变换源、缓存服务、轻量扫描器
ScanTest/              扫描器回归工具（与 Roslyn 结果对照）
```

## 诊断

日志：`%LOCALAPPDATA%\SvnMethodLens\log.txt`（视图创建 / blame 耗时 / 重画结果 / 失败原因）。
缓存：`%LOCALAPPDATA%\SvnMethodLens\cache\*.blame`，删除即强制重算。

---
作者：Marvin（zhengzeming216）
