param(
    [string]$PublishDir = "",
    [switch]$RestartExplorer
)

$ErrorActionPreference = "Stop"

function Set-RegistryDefaultValue {
    param(
        [Microsoft.Win32.RegistryKey]$Root,
        [string]$SubKey,
        [string]$Value
    )

    $key = $Root.CreateSubKey($SubKey)
    $key.SetValue("", $Value, [Microsoft.Win32.RegistryValueKind]::String)
    $key.Dispose()
}

function Set-RegistryStringValue {
    param(
        [Microsoft.Win32.RegistryKey]$Root,
        [string]$SubKey,
        [string]$Name,
        [string]$Value
    )

    $key = $Root.CreateSubKey($SubKey)
    $key.SetValue($Name, $Value, [Microsoft.Win32.RegistryValueKind]::String)
    $key.Dispose()
}

$root = Resolve-Path (Join-Path $PSScriptRoot "..")

if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $root "artifacts\PicPreview-win-x64"
}

$publish = [System.IO.Path]::GetFullPath($PublishDir)
$dll = Join-Path $publish "QuickLooker.ShellExtension.dll"
$thumbnailer = Join-Path $publish "QuickLooker.Thumbnailer.exe"

if (-not (Test-Path $dll)) {
    throw "Shell extension DLL was not found: $dll"
}

if (-not (Test-Path $thumbnailer)) {
    throw "Thumbnailer executable was not found: $thumbnailer"
}

New-Item -Path "HKCU:\Software\PicPreview" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\PicPreview" -Name "ThumbnailerPath" -Value $thumbnailer
New-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved" -Force | Out-Null
Set-ItemProperty `
    -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved" `
    -Name "{5BB47C0C-7A24-4ADC-9F23-072422343BA7}" `
    -Value "PicPreview Thumbnail Provider"

$process = Start-Process `
    -FilePath "$env:windir\System32\regsvr32.exe" `
    -ArgumentList @("/s", $dll) `
    -Wait `
    -PassThru

if ($process.ExitCode -ne 0) {
    throw "regsvr32 failed with exit code $($process.ExitCode)."
}

$thumbnailHandler = "{E357FCCD-A995-4576-B01F-234630154E96}"
$provider = "{5BB47C0C-7A24-4ADC-9F23-072422343BA7}"
$extensions = @(".psd", ".psb", ".tga", ".webp", ".avif", ".heic", ".heif")

Set-RegistryDefaultValue `
    -Root ([Microsoft.Win32.Registry]::LocalMachine) `
    -SubKey "Software\Classes\CLSID\$provider" `
    -Value "PicPreview Thumbnail Provider"

Set-RegistryDefaultValue `
    -Root ([Microsoft.Win32.Registry]::LocalMachine) `
    -SubKey "Software\Classes\CLSID\$provider\InprocServer32" `
    -Value $dll

Set-RegistryStringValue `
    -Root ([Microsoft.Win32.Registry]::LocalMachine) `
    -SubKey "Software\Classes\CLSID\$provider\InprocServer32" `
    -Name "ThreadingModel" `
    -Value "Apartment"

Set-RegistryStringValue `
    -Root ([Microsoft.Win32.Registry]::LocalMachine) `
    -SubKey "Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved" `
    -Name $provider `
    -Value "PicPreview Thumbnail Provider"

Set-RegistryStringValue `
    -Root ([Microsoft.Win32.Registry]::LocalMachine) `
    -SubKey "Software\PicPreview" `
    -Name "ThumbnailerPath" `
    -Value $thumbnailer

foreach ($extension in $extensions) {
    Set-RegistryDefaultValue `
        -Root ([Microsoft.Win32.Registry]::LocalMachine) `
        -SubKey "Software\Classes\$extension\ShellEx\$thumbnailHandler" `
        -Value $provider

    Set-RegistryDefaultValue `
        -Root ([Microsoft.Win32.Registry]::LocalMachine) `
        -SubKey "Software\Classes\SystemFileAssociations\$extension\ShellEx\$thumbnailHandler" `
        -Value $provider

    $extensionKey = [Microsoft.Win32.Registry]::ClassesRoot.OpenSubKey($extension)

    if ($null -eq $extensionKey) {
        continue
    }

    $progId = [string]$extensionKey.GetValue("")
    $extensionKey.Dispose()

    if ([string]::IsNullOrWhiteSpace($progId)) {
        continue
    }

    $progIdHandlerKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("Software\Classes\$progId\ShellEx\$thumbnailHandler")
    $progIdHandlerKey.SetValue("", $provider, [Microsoft.Win32.RegistryValueKind]::String)
    $progIdHandlerKey.Dispose()

    Set-RegistryDefaultValue `
        -Root ([Microsoft.Win32.Registry]::LocalMachine) `
        -SubKey "Software\Classes\$progId\ShellEx\$thumbnailHandler" `
        -Value $provider

    $progIdKey = [Microsoft.Win32.Registry]::ClassesRoot.OpenSubKey("$progId\CLSID")

    if ($null -eq $progIdKey) {
        $progIdKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("Software\Classes\$progId\CLSID")
    }

    if ($null -eq $progIdKey) {
        continue
    }

    $fileTypeClsid = [string]$progIdKey.GetValue("")
    $progIdKey.Dispose()

    if ([string]::IsNullOrWhiteSpace($fileTypeClsid)) {
        continue
    }

    $fileTypeHandlerKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("Software\Classes\CLSID\$fileTypeClsid\ShellEx\$thumbnailHandler")
    $fileTypeHandlerKey.SetValue("", $provider, [Microsoft.Win32.RegistryValueKind]::String)
    $fileTypeHandlerKey.Dispose()

    Set-RegistryDefaultValue `
        -Root ([Microsoft.Win32.Registry]::LocalMachine) `
        -SubKey "Software\Classes\CLSID\$fileTypeClsid\ShellEx\$thumbnailHandler" `
        -Value $provider
}

if ($RestartExplorer) {
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Process explorer.exe
}

Write-Host "Explorer thumbnail provider registered for the current user."
Write-Host "If thumbnails do not appear immediately, restart Explorer or sign out and back in."
