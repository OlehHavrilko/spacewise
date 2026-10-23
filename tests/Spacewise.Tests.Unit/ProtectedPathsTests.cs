using Spacewise.Core;

namespace Spacewise.Tests.Unit;

public class ProtectedPathsTests
{
    internal static readonly UserFolders Folders = new(@"C:\Users\u", @"C:\Users\u\AppData\Local", @"C:\Users\u\AppData\Roaming",
        @"C:\Users\u\AppData\Local\Temp", @"C:\Windows", @"C:\ProgramData", @"C:\Program Files", @"C:\Program Files (x86)",
        @"C:\Users\u\AppData\Local\Spacewise", [@"C:\Users\u\OneDrive"]);

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\Temp\a.tmp")]
    [InlineData(@"C:\Users\u\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data\f_000001")]
    [InlineData(@"C:\Windows\Temp\x.tmp")]
    [InlineData(@"C:\Windows\Minidump\1.dmp")]
    [InlineData(@"c:\users\U\appdata\local\temp\A.TMP")]
    [InlineData(@"C:\Users\u\Spacewise-notes.txt")]
    public void Allows_files_under_allowed_roots(string path) => Assert.True(ProtectedPaths.Check(path, Folders).Allowed);

    public static readonly TheoryData<string> Protected =
    [
        @"C:\Windows\System32\kernel32.dll",
        @"C:\Windows\Prefetch\X.pf",
        @"C:\Windows\WinSxS\a",
        @"C:\Windows\Temp",
        @"C:\Program Files\App\app.exe",
        @"C:\Program Files (x86)\App\app.exe",
        @"C:\ProgramData\Package Cache\x.msi",
        @"C:\Users\other\AppData\Local\Temp\a.tmp",
        @"C:\Users\Public\Documents\a.txt",
        @"C:\Users\u\OneDrive\Documents\a.docx",
        @"C:\Users\u\AppData\Local\Spacewise\cache.db",
        @"C:\Users\u\AppData\Local\Spacewise\quarantine\1\a",
        @"C:\System Volume Information\x",
        @"C:\$Recycle.Bin\S-1-5-21-1\$R1.txt",
        @"C:\pagefile.sys",
        @"C:\hiberfil.sys",
        @"D:\data\a.txt",
        @"C:\Users\u",
        @"C:\",
    ];

    [Theory]
    [MemberData(nameof(Protected))]
    public void Denies_protected_paths(string path) => Assert.False(ProtectedPaths.Check(path, Folders).Allowed);

    [Theory]
    [InlineData(@"\\server\share\Temp\a.tmp")]
    [InlineData(@"\\?\C:\Users\u\AppData\Local\Temp\a.tmp")]
    [InlineData(@"\\.\C:\Users\u\AppData\Local\Temp\a.tmp")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume3\Users\u\a")]
    [InlineData(@"Users\u\AppData\Local\Temp\a.tmp")]
    [InlineData(@"C:Users\u\a.tmp")]
    [InlineData(@"C:/Users/u/AppData/Local/Temp/a.tmp")]
    [InlineData(@"C:\Users\u\AppData\Local\Temp\a.tmp:stream")]
    [InlineData(@"C:\Users\u\AppData\Local\Temp\..\..\..\..\..\Windows\System32\x.dll")]
    [InlineData(@"C:\Users\u\AppData\Local\Temp\.\a.tmp")]
    [InlineData(@"C:\Users\u\AppData\Local\Temp\\a.tmp")]
    [InlineData(@"C:\Users\u\AppData\Local\Temp\a.tmp.")]
    [InlineData(@"C:\Users\u\AppData\Local\Temp\a.tmp ")]
    [InlineData(@"C:\Users\u\APPDAT~1\Local\Temp\a.tmp")]
    public void Denies_non_canonical_forms(string path) => Assert.False(ProtectedPaths.Check(path, Folders).Allowed);

    // FR-SEC-003 (property): no variant of a protected path passes, whatever the case, 8.3 name,
    // trailing dot or space, ADS suffix, alternate separator, or traversal that lands back on it.
    [Fact]
    public void No_variant_of_a_protected_path_is_allowed()
    {
        var random = new Random(20261023);
        foreach (string path in Protected.Select(row => row.Data))
            for (int round = 0; round < 200; round++)
            {
                string variant = Mutate(path, random);
                Assert.False(ProtectedPaths.Check(variant, Folders).Allowed, $"{path} -> {variant}");
            }
    }

    [Fact]
    public void Root_match_respects_segment_boundary()
    {
        Assert.True(ProtectedPaths.IsUnder(@"C:\Temp\a", @"C:\Temp"));
        Assert.True(ProtectedPaths.IsUnder(@"C:\TEMP", @"c:\temp\"));
        Assert.False(ProtectedPaths.IsUnder(@"C:\Temp2\a", @"C:\Temp"));
    }

    private static string Mutate(string path, Random random)
    {
        char[] chars = path.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (random.Next(2) == 0) chars[i] = char.IsUpper(chars[i]) ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
        string s = new(chars);
        string[] segments = s.Split('\\');
        return random.Next(7) switch
        {
            0 => s,
            1 => s + ".",
            2 => s + " ",
            3 => s + ":Zone.Identifier",
            4 => s.Replace('\\', '/'),
            5 when segments.Length > 2 => string.Join('\\', segments.Take(segments.Length - 1).Append("x").Append("..").Append(segments[^1])),
            6 when segments.Length > 2 => string.Join('\\', segments.Select((seg, i) => i == 1 && seg.Length > 6 ? seg[..6].ToUpperInvariant() + "~1" : seg)),
            _ => s,
        };
    }
}
