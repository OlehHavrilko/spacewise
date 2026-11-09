param(
    [Parameter(Mandatory)][string]$PreviousMsi,
    [Parameter(Mandatory)][string]$CandidateMsi,
    [Parameter(Mandatory)][string]$PortableZip
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This test installs and uninstalls Spacewise. Run it on a disposable GitHub-hosted Windows runner.'
}
$previous = (Resolve-Path -LiteralPath $PreviousMsi).Path
$candidate = (Resolve-Path -LiteralPath $CandidateMsi).Path
$portable = (Resolve-Path -LiteralPath $PortableZip).Path
$testRoot = Join-Path $env:RUNNER_TEMP 'spacewise-upgrade'
$null = New-Item -ItemType Directory -Path $testRoot
$app = Join-Path $env:ProgramFiles 'Spacewise'
$exe = Join-Path $app 'Spacewise.Desktop.exe'
$data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Spacewise'
$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Spacewise.lnk'
$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer

function Read-MsiProperty([string]$Msi, [string]$Property) {
    $db = $windowsInstaller.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $windowsInstaller, @($Msi, 0))
    $query = 'SELECT `Value` FROM `Property` WHERE `Property` = ''' + $Property + ''''
    $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @($query))
    $null = $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    if ($null -eq $record) { throw "Missing MSI property $Property" }
    $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
}

function Invoke-Installer([string]$Arguments, [string]$Step) {
    $log = Join-Path $testRoot "$Step.log"
    $msiexec = Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'
    $p = Start-Process -FilePath $msiexec -ArgumentList "$Arguments /qn /norestart /l*v `"$log`"" -WindowStyle Hidden -PassThru -Wait
    if ($p.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 22 | Write-Host }
        throw "MSI step $Step returned $($p.ExitCode)"
    }
}

function Test-Desktop([string]$Executable, [hashtable]$ProcessEnvironment = @{}) {
    $p = Start-Process -FilePath $Executable -ArgumentList '--smoke-test' -Environment $ProcessEnvironment -WindowStyle Hidden -PassThru
    if (-not $p.WaitForExit(30000)) { Stop-Process -Id $p.Id; throw 'Desktop startup check timed out' }
    if ($p.ExitCode -ne 0) { throw "Desktop startup check returned $($p.ExitCode)" }
}

function Get-KeyHashes([string]$Root) {
    foreach ($file in 'keys/cache.key', 'keys/journal.key') {
        (Get-FileHash -LiteralPath (Join-Path $Root $file) -Algorithm SHA256).Hash
    }
}

$previousCode = Read-MsiProperty $previous 'ProductCode'
$candidateCode = Read-MsiProperty $candidate 'ProductCode'
$upgradeCode = Read-MsiProperty $previous 'UpgradeCode'
if ($previousCode -eq $candidateCode -or $upgradeCode -ne (Read-MsiProperty $candidate 'UpgradeCode') -or
    (Read-MsiProperty $previous 'ProductVersion') -ne '0.3.0' -or
    (Read-MsiProperty $candidate 'ProductVersion') -ne '0.4.0') {
    throw 'Expected distinct product codes, matching upgrade codes and versions 0.3.0 and 0.4.0'
}
$uninstallRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
$previousRegistration = Join-Path $uninstallRoot $previousCode
$candidateRegistration = Join-Path $uninstallRoot $candidateCode
foreach ($path in $app, $data, $previousRegistration, $candidateRegistration) {
    if (Test-Path -LiteralPath $path) { throw "Existing Spacewise data or installation found at $path" }
}

Invoke-Installer "/i `"$previous`"" 'install-0.3.0'
if (-not (Test-Path -LiteralPath $previousRegistration)) { throw 'Previous version was not registered' }
Test-Desktop $exe
$keys = @(Get-KeyHashes $data)
$marker = Join-Path $data 'upgrade-sentinel.txt'
$token = [Guid]::NewGuid().ToString('N')
Set-Content -LiteralPath $marker -Value $token -NoNewline

Invoke-Installer "/i `"$candidate`"" 'upgrade-0.4.0'
if ((Test-Path -LiteralPath $previousRegistration) -or -not (Test-Path -LiteralPath $candidateRegistration) -or
    -not (Test-Path -LiteralPath $shortcut)) { throw 'Unexpected product registration or shortcut after upgrade' }
foreach ($file in 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'Inference/Spacewise.Inference.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $app $file) -PathType Leaf)) { throw "Missing installed file $file" }
}
Test-Desktop $exe
if ((Get-Content -LiteralPath $marker -Raw) -ne $token -or (Compare-Object $keys @(Get-KeyHashes $data))) {
    throw 'The upgrade changed the sentinel or database keys'
}

Invoke-Installer "/x $candidateCode" 'uninstall-0.4.0'
if ((Test-Path -LiteralPath $exe) -or (Test-Path -LiteralPath $shortcut) -or (Test-Path -LiteralPath $candidateRegistration)) {
    throw 'Executable, shortcut or registration survived uninstall'
}
foreach ($file in 'cache.db', 'journal.db', 'upgrade-sentinel.txt') {
    if (-not (Test-Path -LiteralPath (Join-Path $data $file) -PathType Leaf)) { throw "Uninstall removed user file $file" }
}
if ((Get-Content -LiteralPath $marker -Raw) -ne $token -or (Compare-Object $keys @(Get-KeyHashes $data))) {
    throw 'Uninstall changed the sentinel or database keys'
}
Write-Host 'PASS: install 0.3.0, upgrade 0.4.0, remove 0.4.0 and preserve user data.'

$extract = Join-Path $testRoot 'portable'
Expand-Archive -LiteralPath $portable -DestinationPath $extract
$portableApp = Join-Path $extract 'Spacewise'
$portableExe = Join-Path $portableApp 'Spacewise.Desktop.exe'
$portableKey = Join-Path $portableApp 'Data/keys/portable-master.json'
$portableEnv = @{ SPACEWISE_PORTABLE_PASSWORD = [Guid]::NewGuid().ToString('N') }
Test-Desktop $portableExe $portableEnv
$portableHash = (Get-FileHash -LiteralPath $portableKey -Algorithm SHA256).Hash
Test-Desktop $portableExe $portableEnv
if ((Get-FileHash -LiteralPath $portableKey -Algorithm SHA256).Hash -ne $portableHash) { throw 'Portable key changed' }
Write-Host 'PASS: extracted portable ZIP starts twice with the same data key.'
