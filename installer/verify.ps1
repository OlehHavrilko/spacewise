# NFR-PERF-011 / FR-DEL-001: the MSI must not create services, drivers, Run keys, scheduled tasks or custom actions.
param([Parameter(Mandatory)][string]$Msi)
$ErrorActionPreference = "Stop"
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $installer, @($Msi, 0))
function Rows($table) {
    try {
        $view = $db.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $db, @("SELECT * FROM ``$table``"))
        $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null)
        $count = 0
        while ($view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)) { $count++ }
        return $count
    } catch { return 0 }
}
$failed = @()
foreach ($table in "ServiceInstall", "ServiceControl", "Registry", "CustomAction", "Environment", "Class", "TypeLib") {
    $n = Rows $table
    "{0,-15} rows={1}" -f $table, $n
    if ($n -gt 0) { $failed += $table }
}
if ($failed.Count -gt 0) { throw "MSI contains forbidden tables: $($failed -join ', ')" }
"OK: $Msi has no services, registry values or custom actions"
