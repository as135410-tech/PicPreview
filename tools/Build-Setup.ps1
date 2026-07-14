param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [switch]$UseMinGW
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$installerRoot = Join-Path $root "artifacts\installer"
$msi = Join-Path $installerRoot "PicPreview-$Version-win-x64.msi"
$bundleWxs = Join-Path $installerRoot "PicPreview.Bundle.Generated.wxs"
$setup = Join-Path $installerRoot "PicPreview-Setup-$Version-win-x64.exe"
$icon = Join-Path $root "src\QuickLooker.App\Assets\PicPreview.ico"
$logo = Join-Path $root "src\QuickLooker.App\Assets\PicPreview.png"
$theme = Join-Path $root "tools\installer\PicPreviewTheme.xml"
$localization = Join-Path $root "tools\installer\PicPreviewTheme.wxl"
$bundleUpgradeCode = "{0DD11663-EB52-497B-887F-57A6152E9421}"

function Escape-Xml {
    param([string]$Value)
    return [System.Security.SecurityElement]::Escape($Value)
}

$installerArguments = @{
    Version = $Version
    Configuration = $Configuration
    UseMinGW = $UseMinGW
}

& (Join-Path $PSScriptRoot "Build-Installer.ps1") @installerArguments

if (-not (Test-Path -LiteralPath $msi)) {
    throw "Installer build did not produce the expected MSI: $msi"
}

$msiSource = Escape-Xml $msi
$iconSource = Escape-Xml $icon
$logoSource = Escape-Xml $logo
$themeSource = Escape-Xml $theme
$localizationSource = Escape-Xml $localization

$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"
     xmlns:bal="http://wixtoolset.org/schemas/v4/wxs/bal"
     xmlns:util="http://wixtoolset.org/schemas/v4/wxs/util">
  <Bundle Name="PicPreview"
          Manufacturer="PicPreview"
          Version="$Version"
          UpgradeCode="$bundleUpgradeCode"
          Compressed="yes"
          IconSourceFile="$iconSource"
          AboutUrl="https://github.com/as135410-tech/PicPreview">
    <BootstrapperApplication>
      <bal:WixStandardBootstrapperApplication Theme="hyperlinkLargeLicense"
                                              ThemeFile="$themeSource"
                                              LocalizationFile="$localizationSource"
                                              LicenseUrl=""
                                              LogoFile="$logoSource"
                                              ShowVersion="yes"
                                              SuppressOptionsUI="yes"
                                              LaunchTarget="[InstallFolder]\PicPreview.exe"
                                              LaunchWorkingFolder="[InstallFolder]\" />
    </BootstrapperApplication>

    <Variable Name="InstallRoot"
              Type="string"
              Value=""
              Persisted="yes"
              bal:Overridable="yes" />

    <Variable Name="InstallFolder"
              Type="formatted"
              Value="[InstallRoot]\PicPreview$Version"
              bal:Overridable="yes" />

    <util:RegistrySearch Id="FindProgramFiles64"
                         Variable="InstallRoot"
                         Root="HKLM"
                         Key="SOFTWARE\Microsoft\Windows\CurrentVersion"
                         Value="ProgramFilesDir"
                         Result="value"
                         Bitness="always64"
                         Condition="InstallRoot = &quot;&quot;" />

    <Chain>
      <MsiPackage Id="PicPreviewMsi"
                  SourceFile="$msiSource"
                  Compressed="yes"
                  Visible="no"
                  Vital="yes">
        <MsiProperty Name="INSTALLFOLDER" Value="[InstallFolder]" />
      </MsiPackage>
    </Chain>
  </Bundle>
</Wix>
"@

Set-Content -LiteralPath $bundleWxs -Value $xml -Encoding UTF8

wix build $bundleWxs `
    -arch x64 `
    -ext WixToolset.BootstrapperApplications.wixext `
    -ext WixToolset.Util.wixext `
    -o $setup `
    -pdbtype none

if ($LASTEXITCODE -ne 0) {
    throw "Setup bundle build failed. Ensure WixToolset.BootstrapperApplications.wixext/7.0.0 and WixToolset.Util.wixext/7.0.0 are installed."
}

Write-Host "Modern setup created: $setup"
