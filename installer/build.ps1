# Builds the self-contained app and the per-machine MSI into artifacts\. Usage: .\installer\build.ps1 [-Version 0.1.0]
param([string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "artifacts\publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish "$root\src\Spacewise.Desktop" -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
dotnet tool restore
dotnet tool run wix -- build "$root\installer\Spacewise.wxs" -arch x64 -d "Version=$Version" -d "PublishDir=$publish" -o "$root\artifacts\Spacewise-$Version-x64.msi"
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
Get-FileHash "$root\artifacts\Spacewise-$Version-x64.msi" -Algorithm SHA256 | Format-List
& "$PSScriptRoot\verify.ps1" -Msi "$root\artifacts\Spacewise-$Version-x64.msi"
