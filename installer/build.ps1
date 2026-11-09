# Builds the self-contained app and per-machine MSI into a fresh artifacts directory.
param([string]$Version = "0.4.0", [switch]$Sign)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
function Get-DevSigningCert {
    $subject = "CN=Spacewise Dev (self-signed)"
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3)
    }
    Export-Certificate -Cert $cert -FilePath (Join-Path $stage 'Spacewise-dev.cer') | Out-Null
    return $cert
}
function Sign-File($path) {
    $r = Set-AuthenticodeSignature -FilePath $path -Certificate (Get-DevSigningCert) -HashAlgorithm SHA256 -TimestampServer "http://timestamp.digicert.com"
    if ($r.Status -notin "Valid", "UnknownError") { Write-Warning "sign $path : $($r.Status) $($r.StatusMessage)" }
    "signed $path ($($r.Status))"
}
$stage = Join-Path $root ('artifacts\msi-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $stage 'publish'
$msi = Join-Path $stage "Spacewise-$Version-x64.msi"
New-Item -ItemType Directory -Path $stage | Out-Null
dotnet publish "$root\src\Spacewise.Desktop" -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
if ($Sign) { Sign-File "$publish\Spacewise.Desktop.exe" }
dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }
dotnet tool run wix -- build "$root\installer\Spacewise.wxs" -arch x64 -ext WixToolset.UI.wixext -d "Version=$Version" -d "PublishDir=$publish" -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
if ($Sign) { Sign-File $msi }
Get-FileHash -LiteralPath $msi -Algorithm SHA256 | Format-List
& "$PSScriptRoot\verify.ps1" -Msi $msi
Write-Output "MSI: $msi"
