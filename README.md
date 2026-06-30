# PicPreview

PicPreview is a Windows image preview prototype aimed at common images plus PSD-style design files.

## What is included

- WPF desktop viewer with folder browsing, drag-and-drop, thumbnails, and large preview.
- Shared thumbnail pipeline using Magick.NET and a local SQLite cache.
- `QuickLooker.Thumbnailer`, a command-line renderer that can be called by Explorer integration.
- C++ Shell thumbnail provider for Explorer thumbnails of `.psd`, `.psb`, `.tga`, `.webp`, `.avif`, `.heic`, and `.heif`.

Some project and script filenames still use the original internal codename `QuickLooker`; the product name shown to users is PicPreview.

## Cache

PicPreview stores its own thumbnail cache under `%LOCALAPPDATA%\PicPreview`.

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

For a publish folder:

```powershell
.\tools\Publish-QuickLooker.ps1
```

The publish script prefers Visual Studio C++ with Windows SDK. If Windows SDK is not installed but MSYS2 MinGW is available, it falls back to MinGW.

## Enable Explorer thumbnails

After publishing:

```powershell
.\tools\Register-ShellExtension.ps1 -RestartExplorer
```

Run this from an elevated PowerShell window. Explorer's thumbnail pipeline needs the native provider registered at machine scope on this Windows setup.

To remove the Explorer integration:

```powershell
.\tools\Unregister-ShellExtension.ps1 -RestartExplorer
```

The Explorer thumbnail provider is registered at machine scope, so installation and uninstallation require administrator permission.

## Build an installer

Install WiX once:

```powershell
dotnet tool install --global wix
wix eula accept wix7
```

Then create a Windows MSI:

```powershell
.\tools\Build-Installer.ps1 -UseMinGW
```

The MSI is written to `artifacts\installer\PicPreview-1.0.0-win-x64.msi`. It is self-contained, installs to `C:\Program Files\PicPreview`, adds a Start Menu shortcut, and registers the Explorer thumbnail provider at machine scope. The installer must be run as administrator.
The setup wizard includes a destination-folder page, so the install directory can be changed during installation.
