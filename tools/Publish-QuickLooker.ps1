param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "",
    [switch]$UseMinGW
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root "artifacts\PicPreview-win-x64"
}

$output = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force -Path $output | Out-Null

dotnet publish (Join-Path $root "src\QuickLooker.App\QuickLooker.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -o $output

dotnet publish (Join-Path $root "src\QuickLooker.Thumbnailer\QuickLooker.Thumbnailer.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -o $output

$shellProject = Join-Path $root "src\QuickLooker.ShellExtension\QuickLooker.ShellExtension.vcxproj"
$shellSource = Join-Path $root "src\QuickLooker.ShellExtension\QuickLookerThumbnailProvider.cpp"
$shellDef = Join-Path $root "src\QuickLooker.ShellExtension\QuickLooker.ShellExtension.def"
$shellDll = Join-Path $output "QuickLooker.ShellExtension.dll"
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
$windowsSdkInclude = "C:\Program Files (x86)\Windows Kits\10\Include"

if (-not $UseMinGW -and (Test-Path $msbuild) -and (Test-Path $windowsSdkInclude)) {
    & $msbuild $shellProject /p:Configuration=$Configuration /p:Platform=x64 "/p:OutDir=$output\" /m

    if ($LASTEXITCODE -ne 0) {
        throw "Shell extension build failed."
    }
}
else {
    $gpp = Get-Command g++.exe -ErrorAction SilentlyContinue

    if ($null -eq $gpp) {
        $mingwDefault = "C:\msys64\ucrt64\bin\g++.exe"

        if (Test-Path $mingwDefault) {
            $gpp = Get-Item $mingwDefault
        }
    }

    if ($null -eq $gpp) {
        throw "Neither Windows SDK/MSBuild nor MinGW g++ was found. Install Windows 10/11 SDK or MSYS2 MinGW."
    }

    $arguments = @(
        "-shared",
        "-std=c++17",
        "-DUNICODE",
        "-D_UNICODE",
        "-O2",
        "-Wall",
        "-Wextra",
        "-static",
        "-static-libgcc",
        "-static-libstdc++",
        $shellSource,
        $shellDef,
        "-lole32",
        "-lshlwapi",
        "-lshell32",
        "-lwindowscodecs",
        "-lgdi32",
        "-luuid",
        "-Wl,--kill-at",
        "-o",
        $shellDll
    )

    & $gpp.Source @arguments

    if ($LASTEXITCODE -ne 0) {
        throw "Shell extension build failed."
    }
}

Write-Host "Published to $output"
Write-Host "Run tools\Register-ShellExtension.ps1 after publishing to enable Explorer thumbnails."
