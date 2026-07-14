param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [switch]$UseMinGW
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$artifacts = Join-Path $root "artifacts"
$installerRoot = Join-Path $artifacts "installer"
$payload = Join-Path $installerRoot "payload"
$wxs = Join-Path $installerRoot "PicPreview.Generated.wxs"
$license = Join-Path $installerRoot "License.rtf"
$msi = Join-Path $installerRoot "PicPreview-$Version-win-x64.msi"

$provider = "{5BB47C0C-7A24-4ADC-9F23-072422343BA7}"
$thumbnailHandler = "{E357FCCD-A995-4576-B01F-234630154E96}"
$photoshopImageClsid = "{1F963D79-3062-4F86-997A-1A4074FD35E0}"
$upgradeCode = "{23CC9F5C-F56B-4AA7-A53F-91AF8E2AF8BF}"
$imageExtensions = @(".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".psd", ".psb", ".tga", ".ico", ".heic", ".heif", ".avif")
$thumbnailExtensions = @(".psd", ".psb", ".tga", ".webp", ".avif", ".heic", ".heif")

function Reset-Directory {
    param([string]$Path)

    if (Test-Path $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Escape-Xml {
    param([string]$Value)
    return [System.Security.SecurityElement]::Escape($Value)
}

function New-WixId {
    param([string]$Prefix, [string]$Material)

    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    $bytes = $sha1.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Material))
    $sha1.Dispose()
    $hash = -join ($bytes | ForEach-Object { $_.ToString("X2") })
    $hash = $hash.Substring(0, 16)
    return "$Prefix$hash"
}

function Find-MSBuild {
    $vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"

    if (Test-Path $vswhere) {
        $installationPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath

        if (-not [string]::IsNullOrWhiteSpace($installationPath)) {
            $candidate = Join-Path $installationPath "MSBuild\Current\Bin\MSBuild.exe"

            if (Test-Path $candidate) {
                return $candidate
            }
        }
    }

    $candidates = @(
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )

    return $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

function Build-ShellExtension {
    param([string]$OutputPath)

    $shellProject = Join-Path $root "src\QuickLooker.ShellExtension\QuickLooker.ShellExtension.vcxproj"
    $shellSource = Join-Path $root "src\QuickLooker.ShellExtension\QuickLookerThumbnailProvider.cpp"
    $shellDef = Join-Path $root "src\QuickLooker.ShellExtension\QuickLooker.ShellExtension.def"
    $msbuild = Find-MSBuild
    $windowsSdkInclude = "C:\Program Files (x86)\Windows Kits\10\Include"

    if (-not $UseMinGW -and -not [string]::IsNullOrWhiteSpace($msbuild) -and (Test-Path $windowsSdkInclude)) {
        $outDir = Split-Path -Parent $OutputPath
        $arguments = @(
            $shellProject,
            "/p:Configuration=$Configuration",
            "/p:Platform=x64",
            "/p:OutDir=$outDir\",
            "/m"
        )

        if ($msbuild -like "*\2019\*") {
            $arguments += "/p:PlatformToolset=v142"
        }

        & $msbuild @arguments

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
            $OutputPath
        )

        & $gpp.Source @arguments

        if ($LASTEXITCODE -ne 0) {
            throw "Shell extension build failed."
        }
    }
}

function Get-RelativePathFromPayload {
    param([string]$Path)

    $fullPayload = [System.IO.Path]::GetFullPath($payload).TrimEnd('\')
    $fullPath = [System.IO.Path]::GetFullPath($Path)

    if ($fullPath.Length -eq $fullPayload.Length) {
        return ""
    }

    return $fullPath.Substring($fullPayload.Length + 1)
}

function Get-RelativeDirectory {
    param([string]$Directory)

    $relative = Get-RelativePathFromPayload $Directory

    if ($relative -eq ".") {
        return ""
    }

    return $relative
}

Reset-Directory $installerRoot
Reset-Directory $payload

dotnet publish (Join-Path $root "src\QuickLooker.App\QuickLooker.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $payload

dotnet publish (Join-Path $root "src\QuickLooker.Thumbnailer\QuickLooker.Thumbnailer.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $payload

Build-ShellExtension (Join-Path $payload "QuickLooker.ShellExtension.dll")

$directories = Get-ChildItem -LiteralPath $payload -Recurse -Directory | Sort-Object FullName
$directoryIds = @{}
$directoryIds[""] = "INSTALLFOLDER"

foreach ($directory in $directories) {
    $relative = Get-RelativeDirectory $directory.FullName
    $directoryIds[$relative] = New-WixId "Dir_" $relative
}

$componentRefs = New-Object System.Collections.Generic.List[string]
$xml = New-Object System.Collections.Generic.List[string]
$xml.Add('<?xml version="1.0" encoding="utf-8"?>')
$xml.Add('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui">')
$xml.Add("  <Package Name=`"PicPreview`" Manufacturer=`"PicPreview`" Version=`"$Version`" UpgradeCode=`"$upgradeCode`" Scope=`"perMachine`">")
$xml.Add('    <MajorUpgrade DowngradeErrorMessage="A newer version of PicPreview is already installed." />')
$xml.Add('    <MediaTemplate EmbedCab="yes" CompressionLevel="high" />')
$xml.Add('    <ui:WixUI Id="WixUI_InstallDir" InstallDirectory="INSTALLFOLDER" />')
$xml.Add("    <WixVariable Id=`"WixUILicenseRtf`" Value=`"$license`" />")
$xml.Add('    <StandardDirectory Id="ProgramFiles64Folder">')
$xml.Add('      <Directory Id="INSTALLFOLDER" Name="PicPreview" />')
$xml.Add('    </StandardDirectory>')
$xml.Add('    <StandardDirectory Id="ProgramMenuFolder">')
$xml.Add('      <Directory Id="ApplicationProgramsFolder" Name="PicPreview" />')
$xml.Add('    </StandardDirectory>')

foreach ($relative in @("") + ($directories | ForEach-Object { Get-RelativeDirectory $_.FullName })) {
    $directoryPath = if ($relative -eq "") { $payload } else { Join-Path $payload $relative }
    $directoryId = $directoryIds[$relative]
    $xml.Add("    <DirectoryRef Id=`"$directoryId`">")

    $childDirectories = Get-ChildItem -LiteralPath $directoryPath -Directory | Sort-Object Name

    foreach ($childDirectory in $childDirectories) {
        $childRelative = Get-RelativeDirectory $childDirectory.FullName
        $childId = $directoryIds[$childRelative]
        $childName = Escape-Xml $childDirectory.Name
        $xml.Add("      <Directory Id=`"$childId`" Name=`"$childName`" />")
    }

    $files = Get-ChildItem -LiteralPath $directoryPath -File | Sort-Object Name
    $fileIndex = 0

    foreach ($file in $files) {
        $fileRelative = Get-RelativePathFromPayload $file.FullName
        $componentId = New-WixId "Cmp_" $fileRelative
        $fileId = New-WixId "File_" $fileRelative
        $source = Escape-Xml $file.FullName
        $language = if ($file.Name -eq "e_sqlite3.dll") { ' DefaultLanguage="0"' } else { '' }

        $xml.Add("      <Component Id=`"$componentId`" Guid=`"*`">")
        $xml.Add("        <File Id=`"$fileId`" Source=`"$source`" KeyPath=`"yes`"$language />")
        $xml.Add('      </Component>')
        $componentRefs.Add($componentId)
        $fileIndex++
    }

    $xml.Add('    </DirectoryRef>')
}

$registryComponentId = "RegistryEntries"
$xml.Add('    <DirectoryRef Id="INSTALLFOLDER">')
$xml.Add("      <Component Id=`"$registryComponentId`" Guid=`"{18C8A885-71C2-47C3-A22C-6BD42882AED1}`">")
$xml.Add('        <RegistryValue Root="HKLM" Key="Software\PicPreview" Name="Installed" Value="1" Type="integer" KeyPath="yes" />')
$xml.Add('        <RegistryValue Root="HKLM" Key="Software\PicPreview" Name="ThumbnailerPath" Value="[INSTALLFOLDER]QuickLooker.Thumbnailer.exe" Type="string" />')
$xml.Add('        <RegistryValue Root="HKLM" Key="Software\PicPreview\Capabilities" Name="ApplicationName" Value="PicPreview" Type="string" />')
$xml.Add('        <RegistryValue Root="HKLM" Key="Software\PicPreview\Capabilities" Name="ApplicationDescription" Value="PicPreview image viewer" Type="string" />')
$xml.Add('        <RegistryValue Root="HKLM" Key="Software\PicPreview\Capabilities" Name="ApplicationIcon" Value="[INSTALLFOLDER]PicPreview.exe,0" Type="string" />')
$xml.Add('        <RegistryValue Root="HKLM" Key="Software\RegisteredApplications" Name="PicPreview" Value="Software\PicPreview\Capabilities" Type="string" />')
$xml.Add('        <RegistryValue Root="HKLM" Key="Software\Classes\PicPreview.ImageFile" Value="PicPreview Image" Type="string" />')
$xml.Add('        <RegistryValue Root="HKLM" Key="Software\Classes\PicPreview.ImageFile\DefaultIcon" Value="[INSTALLFOLDER]PicPreview.exe,0" Type="string" />')
$xml.Add('        <RegistryValue Root="HKLM" Key="Software\Classes\PicPreview.ImageFile\shell\open\command" Value="&quot;[INSTALLFOLDER]PicPreview.exe&quot; &quot;%1&quot;" Type="string" />')
$xml.Add('        <RegistryValue Root="HKLM" Key="Software\Classes\Applications\PicPreview.exe" Value="PicPreview" Type="string" />')
$xml.Add('        <RegistryValue Root="HKLM" Key="Software\Classes\Applications\PicPreview.exe\shell\open\command" Value="&quot;[INSTALLFOLDER]PicPreview.exe&quot; &quot;%1&quot;" Type="string" />')
$xml.Add("        <RegistryValue Root=`"HKLM`" Key=`"Software\Classes\CLSID\$provider`" Value=`"PicPreview Thumbnail Provider`" Type=`"string`" />")
$xml.Add("        <RegistryValue Root=`"HKLM`" Key=`"Software\Classes\CLSID\$provider\InprocServer32`" Value=`"[INSTALLFOLDER]QuickLooker.ShellExtension.dll`" Type=`"string`" />")
$xml.Add("        <RegistryValue Root=`"HKLM`" Key=`"Software\Classes\CLSID\$provider\InprocServer32`" Name=`"ThreadingModel`" Value=`"Apartment`" Type=`"string`" />")
$xml.Add("        <RegistryValue Root=`"HKLM`" Key=`"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved`" Name=`"$provider`" Value=`"PicPreview Thumbnail Provider`" Type=`"string`" />")

foreach ($extension in $imageExtensions) {
    $xml.Add("        <RegistryValue Root=`"HKLM`" Key=`"Software\PicPreview\Capabilities\FileAssociations`" Name=`"$extension`" Value=`"PicPreview.ImageFile`" Type=`"string`" />")
    $xml.Add("        <RegistryValue Root=`"HKLM`" Key=`"Software\Classes\Applications\PicPreview.exe\SupportedTypes`" Name=`"$extension`" Value=`"`" Type=`"string`" />")
    $xml.Add("        <RegistryValue Root=`"HKLM`" Key=`"Software\Classes\$extension\OpenWithProgids`" Name=`"PicPreview.ImageFile`" Value=`"`" Type=`"string`" />")
}

foreach ($extension in $thumbnailExtensions) {
    $xml.Add("        <RegistryValue Root=`"HKLM`" Key=`"Software\Classes\$extension\ShellEx\$thumbnailHandler`" Value=`"$provider`" Type=`"string`" />")
    $xml.Add("        <RegistryValue Root=`"HKLM`" Key=`"Software\Classes\SystemFileAssociations\$extension\ShellEx\$thumbnailHandler`" Value=`"$provider`" Type=`"string`" />")
}

$xml.Add("        <RegistryValue Root=`"HKLM`" Key=`"Software\Classes\Photoshop.Image.22\ShellEx\$thumbnailHandler`" Value=`"$provider`" Type=`"string`" />")
$xml.Add("        <RegistryValue Root=`"HKLM`" Key=`"Software\Classes\CLSID\$photoshopImageClsid\ShellEx\$thumbnailHandler`" Value=`"$provider`" Type=`"string`" />")
$xml.Add('      </Component>')
$xml.Add('    </DirectoryRef>')
$componentRefs.Add($registryComponentId)

$shortcutComponentId = "StartMenuShortcut"
$xml.Add('    <DirectoryRef Id="ApplicationProgramsFolder">')
$xml.Add("      <Component Id=`"$shortcutComponentId`" Guid=`"{38D5CF94-1874-4EF3-913C-0F691D6D1D3E}`">")
$xml.Add('        <Shortcut Id="QuickLookerStartMenuShortcut" Name="PicPreview" Description="PicPreview image preview" Target="[INSTALLFOLDER]PicPreview.exe" WorkingDirectory="INSTALLFOLDER" />')
$xml.Add('        <RemoveFolder Id="ApplicationProgramsFolder" On="uninstall" />')
$xml.Add('        <RegistryValue Root="HKCU" Key="Software\PicPreview" Name="StartMenuShortcut" Value="1" Type="integer" KeyPath="yes" />')
$xml.Add('      </Component>')
$xml.Add('    </DirectoryRef>')
$componentRefs.Add($shortcutComponentId)

$xml.Add('    <Feature Id="MainFeature" Title="PicPreview" Level="1">')

foreach ($componentId in $componentRefs) {
    $xml.Add("      <ComponentRef Id=`"$componentId`" />")
}

$xml.Add('    </Feature>')
$xml.Add('  </Package>')
$xml.Add('</Wix>')

Set-Content -LiteralPath $license -Encoding ASCII -Value @'
{\rtf1\ansi\deff0
{\fonttbl{\f0 Segoe UI;}}
\fs20 PicPreview\par
\par
This software is provided as-is for personal use.\par
\par
It installs an Explorer thumbnail provider so PSD and related image files can show thumbnails in Windows Explorer.\par
\par
}
'@

Set-Content -LiteralPath $wxs -Value $xml -Encoding UTF8

wix build $wxs -arch x64 -ext WixToolset.UI.wixext -o $msi -pdbtype none

if ($LASTEXITCODE -ne 0) {
    throw "Installer build failed."
}

Write-Host "Installer created: $msi"
