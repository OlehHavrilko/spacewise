using Spacewise.Core;

namespace Spacewise.Tests.Unit;

public class UsageTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static InstalledProgram Program(string name, string? location, long? size = null) =>
        new(name, name, null, location, ProgramKind.Win32, size, null);

    private static UsageObservation Seen(string exe, int daysAgo, int runs = 1) => new(exe, Now.AddDays(-daysAgo), runs);

    [Fact]
    public void Rot13_names_decode_and_leave_other_characters()
    {
        Assert.Equal(@"C:\Program Files\App\app.exe", UserAssistParser.DecodeName(@"P:\Cebtenz Svyrf\Ncc\ncc.rkr"));
        Assert.Equal("{6D809377-6AF0}", UserAssistParser.DecodeName("{6Q809377-6NS0}"));
    }

    [Fact]
    public void Record_parses_run_count_and_last_run_and_rejects_short_or_empty_data()
    {
        byte[] data = new byte[72];
        BitConverter.GetBytes(7u).CopyTo(data, 4);
        BitConverter.GetBytes(Now.ToFileTimeUtc()).CopyTo(data, 60);
        Assert.Equal((7, Now), UserAssistParser.ParseValue(data));
        Assert.Null(UserAssistParser.ParseValue(new byte[10]));
        Assert.Null(UserAssistParser.ParseValue(new byte[72]));
        byte[] tiny = new byte[72];
        BitConverter.GetBytes(1L).CopyTo(tiny, 60);
        Assert.Null(UserAssistParser.ParseValue(tiny));
    }

    [Fact]
    public void Known_folder_prefix_is_expanded_and_unknown_guid_is_left_alone()
    {
        var folders = new Dictionary<string, string> { ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = @"C:\Program Files (x86)" };
        Assert.Equal(@"C:\Program Files (x86)\Steam\steam.exe",
            UserAssistParser.ExpandKnownFolder(@"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Steam\steam.exe", folders));
        Assert.Equal(@"{11111111-0000-0000-0000-000000000000}\x.exe",
            UserAssistParser.ExpandKnownFolder(@"{11111111-0000-0000-0000-000000000000}\x.exe", folders));
        Assert.True(UserAssistParser.IsExecutablePath(@"C:\a\b.exe"));
        Assert.False(UserAssistParser.IsExecutablePath("Microsoft.Windows.Explorer"));
    }

    [Fact]
    public void Longest_install_location_wins()
    {
        InstalledProgram suite = Program("Suite", @"C:\Apps\Suite"), tool = Program("Tool", @"C:\Apps\Suite\Tool");
        Assert.Same(tool, UsageAnalyzer.Match([suite, tool], @"C:\Apps\Suite\Tool\tool.exe"));
        Assert.Same(suite, UsageAnalyzer.Match([suite, tool], @"C:\Apps\Suite\main.exe"));
        Assert.Null(UsageAnalyzer.Match([suite, tool], @"C:\Apps\Suite2\x.exe"));
    }

    [Fact]
    public void Recent_launch_is_active_old_launch_is_no_activity_with_its_last_date()
    {
        InstalledProgram a = Program("A", @"C:\A"), b = Program("B", @"C:\B");
        var usage = UsageAnalyzer.Analyze([a, b], [Seen(@"C:\A\a.exe", 3, 5), Seen(@"C:\B\b.exe", 200)], Now);
        Assert.Equal((ObservationStatus.ObservedActive, 5), (usage[0].Status, usage[0].RunCount));
        Assert.Equal(ObservationStatus.NoActivityObservedWithinCoveredWindow, usage[1].Status);
        Assert.Equal(Now.AddDays(-200), usage[1].LastSeenUtc);
        Assert.Equal(EvidenceLevel.Heuristic, usage[1].Evidence);
    }

    [Fact]
    public void Several_records_for_one_program_are_merged()
    {
        InstalledProgram a = Program("A", @"C:\A");
        var usage = UsageAnalyzer.Analyze([a], [Seen(@"C:\A\one.exe", 10, 2), Seen(@"C:\A\two.exe", 4, 3)], Now);
        Assert.Equal((5, Now.AddDays(-4)), (usage.Single().RunCount, usage.Single().LastSeenUtc!.Value));
    }

    [Fact]
    public void Absence_is_only_no_activity_when_the_source_reaches_back_far_enough()
    {
        InstalledProgram quiet = Program("Quiet", @"C:\Q");
        // The oldest record anywhere bounds the window from below.
        Assert.Equal(ObservationStatus.NoActivityObservedWithinCoveredWindow,
            UsageAnalyzer.Analyze([quiet], [Seen(@"C:\Other\x.exe", 120)], Now).Single().Status);
        Assert.Equal(ObservationStatus.InsufficientCoverage,
            UsageAnalyzer.Analyze([quiet], [Seen(@"C:\Other\x.exe", 30)], Now).Single().Status);
        Assert.Equal(ObservationStatus.UnknownHistory,
            UsageAnalyzer.Analyze([quiet], [Seen(@"C:\Other\x.exe", 2)], Now).Single().Status);
        Assert.Equal(ObservationStatus.UnknownHistory, UsageAnalyzer.Analyze([quiet], [], Now).Single().Status);
    }

    [Fact]
    public void Unreadable_source_or_missing_install_path_is_unknown_never_unused()
    {
        InstalledProgram noPath = Program("NoPath", null), withPath = Program("WithPath", @"C:\W");
        Assert.All(UsageAnalyzer.Analyze([noPath, withPath], null, Now), u => Assert.Equal(ObservationStatus.UnknownHistory, u.Status));
        Assert.Equal(ObservationStatus.UnknownHistory,
            UsageAnalyzer.Analyze([noPath], [Seen(@"C:\Other\x.exe", 400)], Now).Single().Status);
    }

    [Fact]
    public void Summary_counts_only_programs_backed_by_coverage_and_sums_known_sizes()
    {
        InstalledProgram old = Program("Old", @"C:\Old", 1000), older = Program("Older", @"C:\Older"), fresh = Program("Fresh", @"C:\Fresh", 5000);
        var usage = UsageAnalyzer.Analyze([old, older, fresh], [Seen(@"C:\Old\o.exe", 150), Seen(@"C:\Fresh\f.exe", 1), Seen(@"C:\Older\x.exe", 200)], Now);
        UnusedSummary summary = UsageAnalyzer.Unused(usage);
        Assert.Equal((2, 1000, 2 - 1), (summary.Programs, summary.Bytes, summary.WithoutSize));
    }

    [Fact]
    public void Descriptions_follow_the_wording_of_the_spec()
    {
        InstalledProgram a = Program("A", @"C:\A");
        Assert.Equal("Запускалась 3 дн. назад · UserAssist", UsageAnalyzer.Analyze([a], [Seen(@"C:\A\a.exe", 3)], Now).Single().Describe(Now));
        Assert.Equal("История запусков неизвестна", UsageAnalyzer.Analyze([a], null, Now).Single().Describe(Now));
        Assert.Equal("Данных пока мало", UsageAnalyzer.Analyze([a], [Seen(@"C:\Z\z.exe", 30)], Now).Single().Describe(Now));
        Assert.Equal("Запуски не замечены за 120 дн. наблюдения · UserAssist", UsageAnalyzer.Analyze([a], [Seen(@"C:\Z\z.exe", 120)], Now).Single().Describe(Now));
    }
}
