# Builds the self-contained app and the per-machine MSI into artifacts\. Usage: .\installer\build.ps1 [-Version 0.1.0]
param([string]$Version = "0.1.0", [switch]$Sign)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
function Get-DevSigningCert {
    $subject = "CN=Spacewise Dev (self-signed)"
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3)
    }
    Export-Certificate -Cert $cert -FilePath "$root/artifacts/Spacewise-dev.cer" | Out-Null
    return $cert
}
function Sign-File($path) {
    $r = Set-AuthenticodeSignature -FilePath $path -Certificate (Get-DevSigningCert) -HashAlgorithm SHA256 -TimestampServer "http://timestamp.digicert.com"
    if ($r.Status -notin "Valid", "UnknownError") { Write-Warning "sign $path : $($r.Status) $($r.StatusMessage)" }
    "signed $path ($($r.Status))"
}
$publish = Join-Path $root "artifacts\publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish "$root\src\Spacewise.Desktop" -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
if ($Sign) { Sign-File "$publish\Spacewise.Desktop.exe" }
dotnet tool restore
dotnet tool run wix -- build "$root\installer\Spacewise.wxs" -arch x64 -ext WixToolset.UI.wixext -d "Version=$Version" -d "PublishDir=$publish" -o "$root\artifacts\Spacewise-$Version-x64.msi"
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
if ($Sign) { Sign-File "$root/artifacts/Spacewise-$Version-x64.msi" }
Get-FileHash "$root\artifacts\Spacewise-$Version-x64.msi" -Algorithm SHA256 | Format-List
& "$PSScriptRoot\verify.ps1" -Msi "$root\artifacts\Spacewise-$Version-x64.msi"
