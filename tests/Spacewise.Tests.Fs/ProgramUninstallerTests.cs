using Spacewise.Core;
using Spacewise.Platform.Windows;

namespace Spacewise.Tests.Fs;

public sealed class ProgramUninstallerTests
{
    [Fact]
    public void Msi_uses_product_code_and_requests_no_automatic_restart()
    {
        var code = Guid.NewGuid();
        var command = ProgramUninstaller.Prepare(Program(null) with { MsiProductCode = code });
        Assert.NotNull(command);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), command.FileName);
        Assert.Equal($"/x {code:B} /norestart", command.Arguments);
    }

    [Fact]
    public void Vendor_launch_requires_an_explicit_executable_under_the_registered_install_folder()
    {
        using var fixture = new FsFixture();
        string install = fixture.Dir("Vendor App");
        string executable = fixture.File(@"Vendor App\uninstall.exe", 0);
        var program = Program(install) with { UninstallString = $"\"{executable}\" /remove \"example arg\"" };
        var command = ProgramUninstaller.Prepare(program);
        Assert.NotNull(command);
        Assert.Equal(executable, command.FileName);
        Assert.Equal("/remove \"example arg\"", command.Arguments);
        Assert.Null(ProgramUninstaller.Prepare(program with { UninstallString = executable + " /remove" }));
        Assert.Null(ProgramUninstaller.Prepare(program with { UninstallString = "cmd.exe /c anything" }));
        Assert.Null(ProgramUninstaller.Prepare(program with { RegisteredInstallLocation = null }));
        Assert.Null(ProgramUninstaller.Prepare(program with { RegisteredInstallLocation = fixture.Dir("Other") }));
    }

    [Fact]
    public void Leftover_scan_rejects_shared_roots_and_overlapping_installations()
    {
        UserFolders folders = UserFolders.FromEnvironment();
        string location = Path.Combine(folders.LocalAppData, "Example Vendor App");
        Assert.Equal(new RemnantLocation(location, false), Assert.Single(ProgramUninstaller.RemainingLocations(Program(location), [], folders)));
        Assert.Empty(ProgramUninstaller.RemainingLocations(Program(folders.LocalAppData), [], folders));
        Assert.Empty(ProgramUninstaller.RemainingLocations(Program(location), [Program(Path.Combine(location, "Other"))], folders));
        Assert.Empty(ProgramUninstaller.RemainingLocations(Program(location) with { RegisteredInstallLocation = null }, [], folders));
    }

    [Fact]
    public void Publisher_and_product_paths_have_exact_ownership_and_system_scope_is_review_only()
    {
        UserFolders folders = UserFolders.FromEnvironment();
        var program = Program(null) with { Publisher = "Example Publisher" };
        var locations = ProgramUninstaller.RemainingLocations(program, [], folders);
        Assert.Contains(new RemnantLocation(Path.Combine(folders.LocalAppData, "Example Publisher", program.Name), true), locations);
        Assert.Contains(new RemnantLocation(Path.Combine(folders.RoamingAppData, "Example Publisher", program.Name), true), locations);
        Assert.Contains(new RemnantLocation(Path.Combine(folders.ProgramData, "Example Publisher", program.Name), false), locations);
        Assert.Empty(ProgramUninstaller.RemainingLocations(program, [program with { Id = "HKLM\\different" }], folders));
        Assert.Empty(ProgramUninstaller.RemainingLocations(program with { Publisher = "..\\escape" }, [], folders));
    }

    private static InstalledProgram Program(string? path) => new("HKCU\\fixture", "Fixture", null, path, ProgramKind.Win32, null, null)
    {
        RegisteredInstallLocation = path,
    };
}
