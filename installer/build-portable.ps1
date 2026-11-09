param([string]$Version = '0.4.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root ('artifacts\portable-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $stage 'Spacewise'
New-Item -ItemType Directory -Path $publish -Force | Out-Null
dotnet publish "$root\src\Spacewise.Desktop" -c Release -r win-x64 --self-contained -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed' }
New-Item -ItemType File -Path (Join-Path $publish 'portable.flag') | Out-Null
Copy-Item -LiteralPath "$root\docs\PORTABLE.md" -Destination (Join-Path $publish 'README.md')
$archive = Join-Path $stage "Spacewise-$Version-win-x64-portable.zip"
Compress-Archive -LiteralPath $publish -DestinationPath $archive
Get-FileHash -LiteralPath $archive -Algorithm SHA256
Write-Output "Portable archive: $archive"
