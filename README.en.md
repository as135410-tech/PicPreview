# PicPreview

<p>
  <a href="README.en.md"><img src="https://img.shields.io/badge/Language-English-2ea44f?style=for-the-badge" alt="English"></a>
  <a href="README.md"><img src="https://img.shields.io/badge/%E8%AF%AD%E8%A8%80-%E7%AE%80%E4%BD%93%E4%B8%AD%E6%96%87-2ea44f?style=for-the-badge" alt="简体中文"></a>
</p>

PicPreview is a Windows image preview app for common image formats and PSD-style design files. It includes a desktop viewer and Windows Explorer thumbnail integration.

## Download

Download the latest Windows installer from the [Releases](https://github.com/as135410-tech/PicPreview/releases/latest) page.

The MSI installer is self-contained, installs to `C:\Program Files\PicPreview` by default, adds a Start Menu shortcut, and automatically registers the Explorer thumbnail provider. Administrator permission is required during installation.

## Features

- Preview common image files in a Windows desktop app.
- Browse folders, drag and drop files, view thumbnails, and open a large preview.
- Generate thumbnails for `.psd`, `.psb`, `.tga`, `.webp`, `.avif`, `.heic`, and `.heif` in Windows Explorer.
- Use a shared thumbnail pipeline powered by Magick.NET and a local SQLite cache.
- Includes `QuickLooker.Thumbnailer`, a command-line renderer used by the Explorer integration.

Some project and script filenames still use the original internal codename `QuickLooker`; the product name shown to users is PicPreview.

## Cache

PicPreview stores its thumbnail cache under `%LOCALAPPDATA%\PicPreview`.

- `picpreview-cache.db` stores the thumbnail index.
- `thumbs\` stores generated PNG thumbnails.
- Cache maintenance runs at most once per day.
- Entries not used for 90 days are removed.
- The thumbnail folder is trimmed to 512 MB.

## Build

```powershell
dotnet build .\QuickLooker.slnx
```

The .NET projects build with the installed .NET Desktop SDK.
The Explorer extension lives in `src\QuickLooker.ShellExtension` and is built by the publish script because it needs a native C++ toolchain.

Create a publish folder:

```powershell
.\tools\Publish-QuickLooker.ps1
```

The publish script prefers Visual Studio C++ with Windows SDK. If Windows SDK is not installed but MSYS2 MinGW is available, it falls back to MinGW.

## Explorer Thumbnails

If you install PicPreview with the MSI installer, the Explorer thumbnail provider is registered automatically during installation and is ready to use after setup. You do not need to run the scripts below.

The scripts below are mainly for development: when you build from source and use the publish folder directly, register the Explorer thumbnail provider from an elevated PowerShell window:

```powershell
.\tools\Register-ShellExtension.ps1 -RestartExplorer
```

To remove the Explorer integration from a development setup:

```powershell
.\tools\Unregister-ShellExtension.ps1 -RestartExplorer
```

The Explorer thumbnail provider is registered at machine scope, so installation, uninstallation, and manual registration require administrator permission.

## Build an Installer

Install WiX once:

```powershell
dotnet tool install --global wix
wix eula accept wix7
```

Then create a Windows MSI:

```powershell
.\tools\Build-Installer.ps1 -Version 1.0.0 -UseMinGW
```

The MSI is written to `artifacts\installer\PicPreview-1.0.0-win-x64.msi`. The setup wizard includes a destination-folder page, so the install directory can be changed during installation.
