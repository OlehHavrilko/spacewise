# Opt-in end-to-end test. Installs and removes only a unique per-user MSI fixture.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root ('artifacts\uninstall-test-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $stage
$publisher = 'Spacewise QA ' + [Guid]::NewGuid().ToString('N')
$name = 'Uninstall Fixture'
$product = [Guid]::NewGuid().ToString('B')
$upgrade = [Guid]::NewGuid().ToString('B')
$component = [Guid]::NewGuid().ToString('B')
$vendor = Join-Path $env:LOCALAPPDATA $publisher
$location = Join-Path $vendor $name
$leftover = Join-Path $location 'leftover.cfg'
$sentinel = Join-Path $vendor 'outside.txt'
$msi = Join-Path $stage 'Fixture.msi'
$source = Join-Path $stage 'Fixture.wxs'
$systemMsi = Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'

function Invoke-Msi([string]$Arguments) {
    (Start-Process -FilePath $systemMsi -ArgumentList $Arguments -WindowStyle Hidden -PassThru -Wait).ExitCode
}

'Spacewise test fixture' | Set-Content -LiteralPath (Join-Path $stage 'installed.txt')
@"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="$name" Manufacturer="$publisher" Version="1.0.0" ProductCode="$product" UpgradeCode="$upgrade" Scope="perUser">
    <MediaTemplate EmbedCab="yes" />
    <SetProperty Id="ARPINSTALLLOCATION" Value="[FIXTUREDIR]" After="CostFinalize" />
    <StandardDirectory Id="LocalAppDataFolder">
      <Directory Id="FIXTUREVENDOR" Name="$publisher">
        <Directory Id="FIXTUREDIR" Name="$name">
          <Component Id="Fixture" Guid="$component">
            <File Source="$(Join-Path $stage 'installed.txt')" />
            <RegistryValue Root="HKCU" Key="Software\$publisher" Name="Fixture" Type="integer" Value="1" KeyPath="yes" />
          </Component>
        </Directory>
      </Directory>
    </StandardDirectory>
    <Feature Id="Main"><ComponentRef Id="Fixture" /></Feature>
  </Package>
</Wix>
"@ | Set-Content -LiteralPath $source -Encoding utf8

Push-Location $root
try {
    dotnet tool run wix -- build $source -arch x64 -o $msi
    if ($LASTEXITCODE -ne 0) { throw 'Fixture MSI build failed' }
    $code = Invoke-Msi "/i `"$msi`" /qn /norestart /l*v `"$stage\install.log`""
    if ($code -ne 0) { throw "Fixture MSI install returned $code. See $stage\install.log" }

    $bin = Join-Path $root 'src\Spacewise.Desktop\bin\Release\net10.0-windows'
    Add-Type -Path (Join-Path $bin 'Spacewise.Core.dll')
    Add-Type -Path (Join-Path $bin 'Spacewise.Platform.Windows.dll')
    $program = @([Spacewise.Platform.Windows.ProgramInventory]::Read($true) |
        Where-Object { $_.MsiProductCode -eq [Guid]$product -and $_.Publisher -eq $publisher })
    if ($program.Count -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $location 'installed.txt'))) {
        throw 'Fixture registration or payload was not found'
    }
    'possible leftover' | Set-Content -LiteralPath $leftover
    'unrelated file' | Set-Content -LiteralPath $sentinel

    $command = [Spacewise.Platform.Windows.ProgramUninstaller]::Prepare($program[0])
    if ($null -eq $command -or $command.FileName -ne $systemMsi) { throw 'Fixture command was not recognized as MSI' }
    $code = Invoke-Msi "$($command.Arguments) /qn /l*v `"$stage\uninstall.log`""
    if ($code -ne 0) { throw "Fixture MSI uninstall returned $code. See $stage\uninstall.log" }
    if ([Spacewise.Platform.Windows.ProgramInventory]::RegistrationExists($program[0].Id)) { throw 'Fixture is still registered' }
    if (Test-Path -LiteralPath (Join-Path $location 'installed.txt')) { throw 'The installed fixture payload survived uninstall' }

    $folders = [Spacewise.Core.UserFolders]::FromEnvironment()
    $locations = [Spacewise.Platform.Windows.ProgramUninstaller]::RemainingLocations($program[0],
        [Spacewise.Platform.Windows.ProgramInventory]::Read($true), $folders)
    if (-not ($locations | Where-Object { $_.Path -eq $location -and $_.CanOfferRemoval })) { throw 'Fixture remnant scope was not found' }
    $cancellation = [Threading.CancellationToken]::None
    $scan = [Spacewise.Platform.Windows.MetadataScanner]::new().ScanAsync(
        [Spacewise.Core.ScanRequest]::new($location, [string[]]@(), 100, 32), $null, $cancellation).GetAwaiter().GetResult()
    $finding = @($scan.LargestFiles | Where-Object { $_.Path -eq $leftover })
    if ($finding.Count -ne 1 -or $null -eq $finding[0].Identity -or $null -eq $finding[0].LastWriteUtc) {
        throw 'Fixture leftover was not captured by the metadata scanner'
    }
    $item = [Spacewise.Core.PlanItem]::new('program.remnant', 1, 'delete', $leftover, $finding[0].LogicalBytes,
        $finding[0].LastWriteUtc, $finding[0].Identity)
    $plan = [Spacewise.Core.CleanupPlan]::Create([Spacewise.Core.PlanItem[]]@($item), [DateTime]::UtcNow,
        [TimeSpan]::FromMinutes(1), 'fixture')
    $executor = [Spacewise.Core.PlanExecutor]::new([Spacewise.Platform.Windows.HandleFileRemover]::new($true),
        $folders, [TimeProvider]::System, $null)
    $result = $executor.Execute($plan, $cancellation, $null)
    if ($result.Removed -ne 1 -or (Test-Path -LiteralPath $leftover) -or -not (Test-Path -LiteralPath $sentinel)) {
        throw 'Fixture remnant deletion failed or touched an unrelated file'
    }
    'PASS: per-user MSI install, inventory, uninstall, remnant scan and direct deletion.'
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $msi) { $null = Invoke-Msi "/x $product /qn /norestart" }
    foreach ($path in @($leftover, $sentinel)) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path }
    }
    foreach ($path in @($location, $vendor)) {
        if ((Test-Path -LiteralPath $path -PathType Container) -and -not (Get-ChildItem -LiteralPath $path -Force)) {
            Remove-Item -LiteralPath $path
        }
    }
    "Fixture build and logs: $stage"
}
