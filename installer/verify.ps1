# NFR-PERF-011 / FR-DEL-001: the MSI must not create services, drivers, Run keys, scheduled tasks or custom actions.
# Only the standard WiX UI wizard helpers (names starting with WixUI, UI sequence only) are tolerated as custom actions.
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
function ForeignCustomActions {
    try {
        $view = $db.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $db, @("SELECT ``Action`` FROM ``CustomAction``"))
        $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null)
        $names = @()
        while ($rec = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)) {
            $names += $rec.GetType().InvokeMember("StringData", "GetProperty", $null, $rec, 1)
        }
        return @($names | Where-Object { $_ -notmatch '^(WixUI|WIXUI|SetWixUI)' })
    } catch { return @() }
}
$failed = @()
foreach ($table in "ServiceInstall", "ServiceControl", "Registry", "CustomAction", "Environment", "Class", "TypeLib") {
    $n = Rows $table
    if ($table -eq "CustomAction") {
        $foreign = ForeignCustomActions
        if ($n -gt 0) { "  custom actions: $($n) total, foreign: $($foreign -join ',')" }
        $n = $foreign.Count
    }
    "{0,-15} rows={1}" -f $table, $n
    if ($n -gt 0) { $failed += $table }
}
if ($failed.Count -gt 0) { throw "MSI contains forbidden tables: $($failed -join ', ')" }
"OK: $Msi has no services, registry values or custom actions"
