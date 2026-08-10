param(
    [string]$PublishDir = "",
    [switch]$RestartExplorer
)

$ErrorActionPreference = "Stop"

function Remove-RegistryTree {
    param(
        [Microsoft.Win32.RegistryKey]$Root,
        [string]$SubKey
    )

    $Root.DeleteSubKeyTree($SubKey, $false)
}

function Remove-RegistryValue {
    param(
        [Microsoft.Win32.RegistryKey]$Root,
        [string]$SubKey,
        [string]$Name
    )

    $key = $Root.OpenSubKey($SubKey, $true)

    if ($null -eq $key) {
        return
    }

    $key.DeleteValue($Name, $false)
    $key.Dispose()
}

$root = Resolve-Path (Join-Path $PSScriptRoot "..")

if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $root "artifacts\PicPreview-win-x64"
}

$publish = [System.IO.Path]::GetFullPath($PublishDir)
$dll = Join-Path $publish "QuickLooker.ShellExtension.dll"

if (-not (Test-Path $dll)) {
    throw "Shell extension DLL was not found: $dll"
}

$process = Start-Process `
    -FilePath "$env:windir\System32\regsvr32.exe" `
    -ArgumentList @("/u", "/s", $dll) `
    -Wait `
    -PassThru

if ($process.ExitCode -ne 0) {
    throw "regsvr32 unregister failed with exit code $($process.ExitCode)."
}

Remove-ItemProperty `
    -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved" `
    -Name "{5BB47C0C-7A24-4ADC-9F23-072422343BA7}" `
    -ErrorAction SilentlyContinue

$thumbnailHandler = "{E357FCCD-A995-4576-B01F-234630154E96}"
$provider = "{5BB47C0C-7A24-4ADC-9F23-072422343BA7}"
$extensions = @(".psd", ".psb", ".tga", ".webp", ".avif", ".heic", ".heif", ".zip")

Remove-RegistryTree -Root ([Microsoft.Win32.Registry]::LocalMachine) -SubKey "Software\Classes\CLSID\$provider"
Remove-RegistryValue -Root ([Microsoft.Win32.Registry]::LocalMachine) -SubKey "Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved" -Name $provider
Remove-RegistryTree -Root ([Microsoft.Win32.Registry]::LocalMachine) -SubKey "Software\PicPreview"
Remove-RegistryTree -Root ([Microsoft.Win32.Registry]::LocalMachine) -SubKey "Software\QuickLooker"

foreach ($extension in $extensions) {
    Remove-RegistryTree -Root ([Microsoft.Win32.Registry]::LocalMachine) -SubKey "Software\Classes\$extension\ShellEx\$thumbnailHandler"
    Remove-RegistryTree -Root ([Microsoft.Win32.Registry]::LocalMachine) -SubKey "Software\Classes\SystemFileAssociations\$extension\ShellEx\$thumbnailHandler"

    $extensionKey = [Microsoft.Win32.Registry]::ClassesRoot.OpenSubKey($extension)

    if ($null -eq $extensionKey) {
        continue
    }

    $progId = [string]$extensionKey.GetValue("")
    $extensionKey.Dispose()

    if ([string]::IsNullOrWhiteSpace($progId)) {
        continue
    }

    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree("Software\Classes\$progId\ShellEx\$thumbnailHandler", $false)
    Remove-RegistryTree -Root ([Microsoft.Win32.Registry]::LocalMachine) -SubKey "Software\Classes\$progId\ShellEx\$thumbnailHandler"

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

    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree("Software\Classes\CLSID\$fileTypeClsid\ShellEx\$thumbnailHandler", $false)
    Remove-RegistryTree -Root ([Microsoft.Win32.Registry]::LocalMachine) -SubKey "Software\Classes\CLSID\$fileTypeClsid\ShellEx\$thumbnailHandler"
}

if ($RestartExplorer) {
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Process explorer.exe
}

Write-Host "Explorer thumbnail provider unregistered for the current user."
