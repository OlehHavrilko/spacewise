using Spacewise.Core;
using Spacewise.Platform.Windows;

namespace Spacewise.Tests.Fs;

public sealed class UsageSourcesTests : IDisposable
{
    private readonly FsFixture fx = new();

    public void Dispose() => fx.Dispose();

    [Fact]
    public void Steam_manifest_becomes_a_program_under_steamapps_common()
    {
        const string text = "\"AppState\"\n{\n\t\"appid\"\t\t\"620\"\n\t\"name\"\t\t\"Portal 2\"\n\t\"installdir\"\t\t\"Portal 2\"\n\t\"SizeOnDisk\"\t\t\"12345678\"\n}";
        InstalledProgram? game = ProgramInventory.ParseManifest(text, @"D:\Steam\steamapps");
        Assert.NotNull(game);
        Assert.Equal(("Portal 2", ProgramKind.Steam, 12345678L), (game.Name, game.Kind, game.SizeBytes));
        Assert.Equal(@"D:\Steam\steamapps\common\Portal 2", game.InstallLocation);
    }

    [Theory]
    [InlineData("\"name\" \"X\" \"installdir\" \"..\\..\\Windows\"")]
    [InlineData("\"name\" \"X\" \"installdir\" \"a\\b\"")]
    [InlineData("\"installdir\" \"OnlyDir\"")]
    public void Manifest_with_a_path_escape_or_missing_name_is_rejected(string text) =>
        Assert.Null(ProgramInventory.ParseManifest(text, @"D:\Steam\steamapps"));

    [Fact]
    public void Measure_sums_files_below_a_folder_and_reports_when_the_cap_stopped_it()
    {
        string root = fx.Dir("app");
        fx.File(@"app\a.bin", 100);
        fx.File(@"app\b.bin", 50);
        Assert.Equal((150L, true), ProgramInventory.Measure(root, new FileSystemTree(), CancellationToken.None));
        (long bytes, bool complete) = ProgramInventory.Measure(root, new FileSystemTree(), CancellationToken.None, maximumFiles: 1);
        Assert.False(complete);
        Assert.True(bytes is 100 or 50);
    }

    [Fact]
    public void Inventory_and_user_assist_can_be_read_on_this_machine()
    {
        IReadOnlyList<InstalledProgram> programs = ProgramInventory.Read();
        Assert.NotEmpty(programs);
        Assert.All(programs, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
        IReadOnlyList<UsageObservation>? observations = UserAssistReader.Read();
        Assert.All(observations ?? [], o => Assert.True(UserAssistParser.IsExecutablePath(o.ExePath)));
    }
}
