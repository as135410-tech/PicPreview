# PicPreview

<p>
  <a href="README.md"><img src="https://img.shields.io/badge/Language-English-2ea44f?style=for-the-badge" alt="English"></a>
  <a href="README.zh-CN.md"><img src="https://img.shields.io/badge/%E8%AF%AD%E8%A8%80-%E7%AE%80%E4%BD%93%E4%B8%AD%E6%96%87-2ea44f?style=for-the-badge" alt="简体中文"></a>
</p>

PicPreview 是一个 Windows 图片预览工具，面向常见图片格式和 PSD 类设计文件。它包含桌面预览程序，也支持 Windows 资源管理器缩略图集成。

## 下载

请到 [Releases](https://github.com/as135410-tech/PicPreview/releases/latest) 页面下载最新 Windows 安装包。

MSI 安装包是自包含的，默认安装到 `C:\Program Files\PicPreview`，会添加开始菜单快捷方式，并注册资源管理器缩略图组件。安装时需要管理员权限。

## 功能

- 在 Windows 桌面程序中预览常见图片文件。
- 支持文件夹浏览、拖放文件、缩略图列表和大图预览。
- 支持在 Windows 资源管理器中为 `.psd`、`.psb`、`.tga`、`.webp`、`.avif`、`.heic`、`.heif` 生成缩略图。
- 使用基于 Magick.NET 和本地 SQLite 缓存的共享缩略图管线。
- 包含 `QuickLooker.Thumbnailer` 命令行渲染器，供资源管理器集成调用。

部分项目和脚本文件名仍使用早期内部代号 `QuickLooker`；面向用户展示的产品名是 PicPreview。

## 缓存

PicPreview 会把缩略图缓存存储在 `%LOCALAPPDATA%\PicPreview`。

- `picpreview-cache.db` 存储缩略图索引。
- `thumbs\` 存储生成的 PNG 缩略图。
- 缓存维护最多每天运行一次。
- 90 天未使用的缓存记录会被清理。
- 缩略图文件夹会被限制在 512 MB 以内。

## 构建

```powershell
dotnet build .\QuickLooker.slnx
```

.NET 项目使用已安装的 .NET Desktop SDK 构建。
资源管理器扩展位于 `src\QuickLooker.ShellExtension`，因为它需要原生 C++ 工具链，所以会由发布脚本构建。

创建发布目录：

```powershell
.\tools\Publish-QuickLooker.ps1
```

发布脚本会优先使用带 Windows SDK 的 Visual Studio C++ 工具链。如果未安装 Windows SDK，但系统中有 MSYS2 MinGW，则会回退使用 MinGW。

## 资源管理器缩略图

发布后，在管理员 PowerShell 中注册资源管理器缩略图组件：

```powershell
.\tools\Register-ShellExtension.ps1 -RestartExplorer
```

移除资源管理器集成：

```powershell
.\tools\Unregister-ShellExtension.ps1 -RestartExplorer
```

资源管理器缩略图组件会注册到机器级别，因此安装和卸载都需要管理员权限。

## 构建安装包

首次安装 WiX：

```powershell
dotnet tool install --global wix
wix eula accept wix7
```

然后创建 Windows MSI：

```powershell
.\tools\Build-Installer.ps1 -Version 1.0.0 -UseMinGW
```

MSI 会输出到 `artifacts\installer\PicPreview-1.0.0-win-x64.msi`。安装向导包含安装目录选择页面，因此安装目录可以在安装时修改。
