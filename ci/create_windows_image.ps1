### This script is a guide for Improbable's internal build machine images.
### If you don't work at Improbable, this may be interesting as a guide to what software versions we use for our
### automation, but not much more than that.

### Run from an Administrator command prompt with:
### powershell -executionpolicy Unrestricted ci/create-image.sh
### The machine will reboot after the script runs.

# https://chocolatey.org/docs/how-to-setup-offline-installation
$CHOCOLATEY_VERSION="0.10.11"
$GIT_VERSION="2.18.0"
$SPATIAL_VERSION="1.1.9"

Import-Module BitsTransfer
$ErrorActionPreference = "Stop"

mkdir c:\build

Write-Host "Installing Chocolatey..."
$env:chocolateyVersion = "$CHOCOLATEY_VERSION"
Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://chocolatey.org/install.ps1'))

Write-Host "Installing git $GIT_VERSION ..."
& choco install --yes git --version="$GIT_VERSION"

Write-Host "Installing Spatial CLI $SPATIAL_VERSION..."
& choco install spatial --yes --version="$SPATIAL_VERSION"

Start-BitsTransfer -Source https://download.microsoft.com/download/5/8/9/589A8843-BA4D-4E63-BCB2-B2380E5556FD/vs_professional.exe -Destination "c:\build\vs-installer.exe"

choco install VisualStudio2015Professional --yes -packageParameters "--AdminFile C:\Users\danielhall\Install.xml" --force --execution-timeout=0

choco install windows-sdk-8.1 --yes --force --execution-timeout=0

choco install dotnet3.5 --yes --force --execution-timeout=0

# Direct link to installer at https://my.visualstudio.com/Downloads?q=Visual Studio 2017
# VS Professional 15.7
#$VISUAL_STUDIO_HASH="09e0efec-0cfd-4015-b732-e8543589a6e0/1c89b3fa4ca0d287138916dca1625a0e"

#Start-BitsTransfer -Source "https://download.visualstudio.microsoft.com/download/pr/$VISUAL_STUDIO_HASH/vs_professional.exe" -Destination "c:\build\vs-installer.exe"

#Write-Host "Installing Visual studio..."
#Start-Process -Wait -NoNewWindow /build/vs-installer.exe -ArgumentList `
#    "--add", "Microsoft.VisualStudio.Workload.Universal", `
#    "--add", "Microsoft.VisualStudio.Workload.NativeDesktop", `
#    "--add", "Microsoft.VisualStudio.Workload.NativeGame", `
#    "--add", "Microsoft.VisualStudio.Component.Windows10SDK.17134.Desktop", `
#    "--add", "Microsoft.VisualStudio.Workload.NetCoreTools", `
#    "--add", "Microsoft.VisualStudio.Component.Windows81SDK", `
#    "--quiet", "--norestart", "--wait"
#if ($LASTEXITCODE -ne 0) {
#    throw("Failed to install Visual Studio")
#}

# Write-Host "Installing Windbg..."
# & choco install windbg

# Copy-File -Path "C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\pdbcopy.exe" - Destination "C:\Program Files (x86)\MSBuild\Microsoft\VisualStudio\v12.0\AppxPackage"