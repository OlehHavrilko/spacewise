# NFR-PERF-011 / FR-DEL-001: the MSI must not create services, drivers, Run keys, scheduled tasks or custom actions.
# Only the standard WiX UI wizard helpers (names starting with WixUI, UI sequence only) are tolerated as custom actions.
param([Parameter(Mandatory)][string]$Msi)
$ErrorActionPreference = "Stop"
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $installer, @($Msi, 0))
function Query([string]$Sql) {
    $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @($Sql))
    $null = $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    while ($rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)) { $rec }
}
function TextColumn([string]$Sql) {
    foreach ($row in (Query $Sql)) { $row.GetType().InvokeMember('StringData', 'GetProperty', $null, $row, 1) }
}
$tables = @(TextColumn 'SELECT `Name` FROM `_Tables`')
$failed = @()
foreach ($table in 'ServiceInstall', 'ServiceControl', 'Registry', 'Environment', 'Class', 'TypeLib') {
    $n = if ($tables -contains $table) { @(Query "SELECT * FROM ``$table``").Count } else { 0 }
    "{0,-15} rows={1}" -f $table, $n
    if ($n -gt 0) { $failed += $table }
}
if ($tables -contains 'CustomAction') {
    $custom = @(TextColumn 'SELECT `Action` FROM `CustomAction`')
    $execute = @(TextColumn 'SELECT `Action` FROM `InstallExecuteSequence`')
    $foreign = @($custom | Where-Object { $_ -notin 'WixUIPrintEula_X64', 'WixUIValidatePath_X64' -or $_ -in $execute })
    if ($foreign.Count -gt 0) { throw "MSI contains unexpected custom actions: $($foreign -join ', ')" }
    "WiX UI helpers: $($custom.Count)"
}
if ($failed.Count -gt 0) { throw "MSI contains forbidden tables: $($failed -join ', ')" }
"OK: $Msi passed the installer table audit"
