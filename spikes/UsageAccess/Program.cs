// Spike: which usage-analytics sources can a standard (non-elevated) user read?
// Read-only. Prints only counts, versions, error codes and timestamps of the current user's own SID.
// See docs/adr/0001-usage-sources-without-admin.md.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace UsageAccess;

internal static unsafe class Program
{
    private static int Main()
    {
        var identity = WindowsIdentity.GetCurrent();
        var ownSid = identity.User?.Value ?? "?";
        Console.WriteLine($"OS: {Environment.OSVersion.VersionString}; build UBR={ReadUbr()}");
        Console.WriteLine($"User SID: {ownSid}; IsAdministrator(role): {new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)}; TokenElevated: {IsTokenElevated()}");
        Console.WriteLine();

        Section("Q2a Prefetch", () => ProbePrefetch());
        Section("Q2b BAM", () => ProbeBam(ownSid));
        Section("Q2c USN", ProbeUsn);
        Section("Q2d SRUM", ProbeSrum);
        Section("Q2e UserAssist", ProbeUserAssist);
        Section("Q13 Placeholder compatibility mode", ProbePlaceholderMode);
        return 0;
    }

    private static void Section(string name, Action body)
    {
        Console.WriteLine($"== {name} ==");
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  UNEXPECTED {ex.GetType().Name}: {ex.Message} (HResult 0x{ex.HResult:X8})");
        }

        Console.WriteLine();
    }

    private static string Err(int code) => code switch
    {
        1 => "1 ERROR_INVALID_FUNCTION",
        2 => "2 ERROR_FILE_NOT_FOUND",
        3 => "3 ERROR_PATH_NOT_FOUND",
        5 => "5 ERROR_ACCESS_DENIED",
        32 => "32 ERROR_SHARING_VIOLATION",
        87 => "87 ERROR_INVALID_PARAMETER",
        1314 => "1314 ERROR_PRIVILEGE_NOT_HELD",
        _ => $"{code} {new Win32Exception(code).Message.Trim()}",
    };

    private static string ReadUbr()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return $"{key?.GetValue("CurrentBuild")}.{key?.GetValue("UBR")} ({key?.GetValue("DisplayVersion")}, {key?.GetValue("ProductName")})";
    }

    private static bool IsTokenElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        int elevation;
        return Native.GetTokenInformation(identity.Token, 20 /* TokenElevation */, &elevation, 4, out _) && elevation != 0;
    }

    // ---------------- Prefetch ----------------

    private static void ProbePrefetch()
    {
        const string dir = @"C:\Windows\Prefetch";
        Console.WriteLine($"  Directory.Exists: {Directory.Exists(dir)}");

        using (var dh = Native.CreateFileW(dir, Native.FILE_LIST_DIRECTORY, Native.ShareAll, 0, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, 0))
        {
            Console.WriteLine(dh.IsInvalid
                ? $"  Open dir (FILE_LIST_DIRECTORY): FAIL {Err(Marshal.GetLastWin32Error())}"
                : "  Open dir (FILE_LIST_DIRECTORY): OK");
        }

        var names = new List<string>();
        var find = Native.FindFirstFileW(Path.Combine(dir, "*.pf"), out var data);
        if (find == -1)
        {
            Console.WriteLine($"  FindFirstFileW(*.pf): FAIL {Err(Marshal.GetLastWin32Error())}");
        }
        else
        {
            do
            {
                names.Add(data.cFileName);
            }
            while (Native.FindNextFileW(find, out data));
            Native.FindClose(find);
            Console.WriteLine($"  FindFirstFileW(*.pf): OK, {names.Count} files");
        }

        if (names.Count == 0)
        {
            SyntheticMamRoundTrip();
            return;
        }

        var openErrors = new Dictionary<string, int>();
        var attrOk = 0;
        var readOk = new List<string>();
        foreach (var name in names)
        {
            var path = Path.Combine(dir, name);
            using (var ah = Native.CreateFileW(path, Native.FILE_READ_ATTRIBUTES, Native.ShareAll, 0, Native.OPEN_EXISTING, 0, 0))
            {
                if (!ah.IsInvalid)
                {
                    attrOk++;
                }
            }

            using var h = Native.CreateFileW(path, Native.GENERIC_READ, Native.ShareAll, 0, Native.OPEN_EXISTING, 0, 0);
            if (h.IsInvalid)
            {
                var e = Err(Marshal.GetLastWin32Error());
                openErrors[e] = openErrors.GetValueOrDefault(e) + 1;
            }
            else
            {
                readOk.Add(path);
            }
        }

        Console.WriteLine($"  CreateFileW(FILE_READ_ATTRIBUTES): {attrOk}/{names.Count} OK");
        Console.WriteLine($"  CreateFileW(GENERIC_READ): {readOk.Count}/{names.Count} OK");
        foreach (var (e, n) in openErrors)
        {
            Console.WriteLine($"    FAIL x{n}: {e}");
        }

        if (readOk.Count > 0)
        {
            DecodePrefetch(readOk);
        }
    }

    private static void DecodePrefetch(List<string> paths)
    {
        var versions = new Dictionary<string, int>();
        var failures = new Dictionary<string, int>();
        string? sample = null;
        foreach (var path in paths)
        {
            byte[] raw;
            try
            {
                raw = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                var k = $"read: {ex.GetType().Name} 0x{ex.HResult:X8}";
                failures[k] = failures.GetValueOrDefault(k) + 1;
                continue;
            }

            var result = DecodeOne(raw, out var version, out var detail);
            if (result is null)
            {
                var k = $"SCCA v{version}";
                versions[k] = versions.GetValueOrDefault(k) + 1;
                sample ??= $"{Path.GetFileName(path)}: {detail}";
            }
            else
            {
                failures[result] = failures.GetValueOrDefault(result) + 1;
            }
        }

        Console.WriteLine("  Decode (RtlDecompressBufferEx, XPRESS_HUFF):");
        foreach (var (k, n) in versions)
        {
            Console.WriteLine($"    {k}: {n} files");
        }

        foreach (var (k, n) in failures)
        {
            Console.WriteLine($"    FAIL x{n}: {k}");
        }

        if (sample is not null)
        {
            Console.WriteLine($"    sample {sample}");
        }
    }

    // No real .pf is readable: prove the user-mode decode path on a synthetic MAM\x04 container
    // (RtlCompressBuffer XPRESS_HUFF of a fake SCCA v31 header), using the same DecodeOne as for real files.
    private static void SyntheticMamRoundTrip()
    {
        const ushort format = Native.COMPRESSION_FORMAT_XPRESS_HUFF;
        var plain = new byte[16 * 1024];
        BitConverter.GetBytes(31u).CopyTo(plain, 0);
        "SCCA"u8.CopyTo(plain.AsSpan(4));
        for (var i = 8; i < plain.Length; i++)
        {
            plain[i] = (byte)(i % 251);
        }

        var st = Native.RtlGetCompressionWorkSpaceSize(format, out var wsSize, out _);
        if (st != 0)
        {
            Console.WriteLine($"  Synthetic: RtlGetCompressionWorkSpaceSize NTSTATUS 0x{st:X8}");
            return;
        }

        var ws = new byte[wsSize];
        var packed = new byte[plain.Length + 1024];
        uint final;
        fixed (byte* pIn = plain)
        fixed (byte* pOut = packed)
        fixed (byte* pWs = ws)
        {
            st = Native.RtlCompressBuffer(format, pIn, (uint)plain.Length, pOut, (uint)packed.Length, 4096, out final, pWs);
        }

        if (st != 0)
        {
            Console.WriteLine($"  Synthetic: RtlCompressBuffer NTSTATUS 0x{st:X8}");
            return;
        }

        var mam = new byte[8 + final];
        "MAM\x04"u8.CopyTo(mam);
        BitConverter.GetBytes((uint)plain.Length).CopyTo(mam, 4);
        Array.Copy(packed, 0, mam, 8, final);
        var result = DecodeOne(mam, out _, out var detail);
        Console.WriteLine(result is null
            ? $"  Synthetic MAM round-trip (no real .pf readable): OK, {detail}"
            : $"  Synthetic MAM round-trip: FAIL {result}");
    }

    private static string? DecodeOne(byte[] raw, out uint version, out string detail)
    {
        version = 0;
        detail = "";
        byte[] scca;
        if (raw.Length >= 8 && raw[0] == 'M' && raw[1] == 'A' && raw[2] == 'M')
        {
            if ((raw[3] & 0x7F) != 0x04)
            {
                return $"MAM variant 0x{raw[3]:X2}";
            }

            var hasCrc = (raw[3] & 0x80) != 0;
            var uncompressed = BitConverter.ToUInt32(raw, 4);
            var offset = hasCrc ? 12 : 8;
            scca = new byte[uncompressed];
            const ushort format = Native.COMPRESSION_FORMAT_XPRESS_HUFF;
            var st = Native.RtlGetCompressionWorkSpaceSize(format, out var wsSize, out _);
            if (st != 0)
            {
                return $"RtlGetCompressionWorkSpaceSize NTSTATUS 0x{st:X8}";
            }

            var ws = new byte[wsSize];
            uint final;
            fixed (byte* pOut = scca)
            fixed (byte* pIn = &raw[offset])
            fixed (byte* pWs = ws)
            {
                st = Native.RtlDecompressBufferEx(format, pOut, uncompressed, pIn, (uint)(raw.Length - offset), out final, pWs);
            }

            if (st != 0)
            {
                return $"RtlDecompressBufferEx NTSTATUS 0x{st:X8}";
            }

            if (final != uncompressed)
            {
                return $"size mismatch {final} != {uncompressed}";
            }

            detail = $"MAM\\x{raw[3]:x2} {raw.Length} B -> {final} B";
        }
        else
        {
            scca = raw;
            detail = "uncompressed";
        }

        if (scca.Length < 8 || Encoding.ASCII.GetString(scca, 4, 4) != "SCCA")
        {
            return "no SCCA signature";
        }

        version = BitConverter.ToUInt32(scca, 0);
        detail += $", SCCA v{version}";
        return null;
    }

    // ---------------- BAM ----------------

    private static void ProbeBam(string ownSid)
    {
        foreach (var root in new[] { @"SYSTEM\CurrentControlSet\Services\bam\State\UserSettings", @"SYSTEM\CurrentControlSet\Services\bam\UserSettings" })
        {
            var rc = Native.RegOpenKeyExW(Native.HKEY_LOCAL_MACHINE, root, 0, Native.KEY_READ, out var hk);
            if (rc != 0)
            {
                Console.WriteLine($"  HKLM\\{root} (KEY_READ): FAIL {Err(rc)}");
                // The child key may carry its own ACL: try the own SID directly.
                rc = Native.RegOpenKeyExW(Native.HKEY_LOCAL_MACHINE, $@"{root}\{ownSid}", 0, Native.KEY_QUERY_VALUE, out hk);
                Console.WriteLine(rc == 0 ? "    own SID direct (KEY_QUERY_VALUE): OK" : $"    own SID direct (KEY_QUERY_VALUE): FAIL {Err(rc)}");
                if (rc == 0)
                {
                    Native.RegCloseKey(hk);
                }

                continue;
            }

            Native.RegCloseKey(hk);
            Console.WriteLine($"  HKLM\\{root} (KEY_READ): OK");
            using var key = Registry.LocalMachine.OpenSubKey(root);
            string[] sids;
            try
            {
                sids = key?.GetSubKeyNames() ?? [];
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    enumerate subkeys: {ex.GetType().Name} 0x{ex.HResult:X8}");
                sids = [];
            }

            Console.WriteLine($"    subkeys (SIDs): {sids.Length}");
            if (!sids.Contains(ownSid))
            {
                // Probe the own SID even if enumeration did not reveal it.
                sids = [.. sids, ownSid];
            }

            foreach (var sid in sids)
            {
                var own = sid == ownSid;
                var label = own ? "own SID" : $"other SID {Mask(sid)}";
                rc = Native.RegOpenKeyExW(Native.HKEY_LOCAL_MACHINE, $@"{root}\{sid}", 0, Native.KEY_QUERY_VALUE, out hk);
                if (rc != 0)
                {
                    Console.WriteLine($"    {label} (KEY_QUERY_VALUE): FAIL {Err(rc)}");
                    continue;
                }

                Native.RegCloseKey(hk);
                using var sk = Registry.LocalMachine.OpenSubKey($@"{root}\{sid}");
                var values = sk?.GetValueNames() ?? [];
                var line = $"    {label} (KEY_QUERY_VALUE): OK, {values.Length} values";
                if (own && sk is not null)
                {
                    var newest = values
                        .Select(v => sk.GetValue(v) as byte[])
                        .Where(b => b is { Length: >= 8 })
                        .Select(b => BitConverter.ToInt64(b!, 0))
                        .Where(ft => ft > 0)
                        .DefaultIfEmpty(0)
                        .Max();
                    line += newest > 0 ? $", newest FILETIME {DateTime.FromFileTimeUtc(newest):u}" : "";
                }

                Console.WriteLine(line);
            }
        }
    }

    private static string Mask(string sid) => sid.Length > 12 ? $"...{sid[^5..]}" : sid;

    // ---------------- USN ----------------

    private static void ProbeUsn()
    {
        var volumes = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.DriveFormat == "NTFS")
            .Select(d => d.Name[..2])
            .ToList();
        Console.WriteLine($"  Fixed NTFS volumes: {string.Join(", ", volumes)} (system: {Environment.GetEnvironmentVariable("SystemDrive")})");
        var modes = new (string Name, uint Access)[]
        {
            ("GENERIC_READ", Native.GENERIC_READ),
            ("FILE_READ_ATTRIBUTES|SYNCHRONIZE", Native.FILE_READ_ATTRIBUTES | Native.SYNCHRONIZE),
            ("0 (no access)", 0),
        };

        foreach (var vol in volumes)
        {
            foreach (var (name, access) in modes)
            {
                using var h = Native.CreateFileW($@"\\.\{vol}", access, Native.ShareAll, 0, Native.OPEN_EXISTING, 0, 0);
                if (h.IsInvalid)
                {
                    Console.WriteLine($"  \\\\.\\{vol} open {name}: FAIL {Err(Marshal.GetLastWin32Error())}");
                    continue;
                }

                Console.WriteLine($"  \\\\.\\{vol} open {name}: OK");
                UsnFsctls(h, "    ");
            }

            // Alternative: root directory handle (no volume open).
            using var root = Native.CreateFileW($@"{vol}\", Native.FILE_READ_ATTRIBUTES, Native.ShareAll, 0, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, 0);
            if (root.IsInvalid)
            {
                Console.WriteLine($"  {vol}\\ root dir handle: FAIL {Err(Marshal.GetLastWin32Error())}");
            }
            else
            {
                Console.WriteLine($"  {vol}\\ root dir handle (FILE_READ_ATTRIBUTES): OK");
                UsnFsctls(root, "    ");
            }
        }
    }

    private static void UsnFsctls(SafeFileHandle h, string indent)
    {
        var journal = stackalloc byte[80];
        ulong journalId = 0;
        long nextUsn = 0, firstUsn = 0;
        if (Native.DeviceIoControl(h, Native.FSCTL_QUERY_USN_JOURNAL, null, 0, journal, 80, out _, 0))
        {
            journalId = *(ulong*)journal;
            firstUsn = *(long*)(journal + 8);
            nextUsn = *(long*)(journal + 16);
            var maxSize = *(ulong*)(journal + 40);
            Console.WriteLine($"{indent}FSCTL_QUERY_USN_JOURNAL: OK (FirstUsn={firstUsn}, NextUsn={nextUsn}, MaximumSize={maxSize / (1024 * 1024)} MiB)");
        }
        else
        {
            Console.WriteLine($"{indent}FSCTL_QUERY_USN_JOURNAL: FAIL {Err(Marshal.GetLastWin32Error())}");
        }

        const int bufSize = 64 * 1024;
        var buf = (byte*)NativeMemory.Alloc(bufSize);
        try
        {
            // READ_USN_JOURNAL_DATA_V0: StartUsn, ReasonMask, ReturnOnlyOnClose, Timeout, BytesToWaitFor, UsnJournalID
            var read = stackalloc byte[40];
            new Span<byte>(read, 40).Clear();
            // StartUsn must be a record boundary; an arbitrary offset gives ERROR_INVALID_PARAMETER.
            *(long*)read = firstUsn;
            *(uint*)(read + 8) = 0xFFFFFFFF;
            *(ulong*)(read + 32) = journalId;
            if (Native.DeviceIoControl(h, Native.FSCTL_READ_USN_JOURNAL, read, 40, buf, bufSize, out var got, 0))
            {
                Console.WriteLine($"{indent}FSCTL_READ_USN_JOURNAL: OK ({CountRecords(buf, got)} records in first {bufSize / 1024} KiB)");
            }
            else
            {
                Console.WriteLine($"{indent}FSCTL_READ_USN_JOURNAL: FAIL {Err(Marshal.GetLastWin32Error())}");
            }

            // MFT_ENUM_DATA_V0: StartFileReferenceNumber, LowUsn, HighUsn
            var mft = stackalloc long[3];
            mft[0] = 0;
            mft[1] = 0;
            mft[2] = long.MaxValue;
            if (Native.DeviceIoControl(h, Native.FSCTL_ENUM_USN_DATA, mft, 24, buf, bufSize, out got, 0))
            {
                Console.WriteLine($"{indent}FSCTL_ENUM_USN_DATA: OK ({CountRecords(buf, got)} records in first {bufSize / 1024} KiB)");
            }
            else
            {
                Console.WriteLine($"{indent}FSCTL_ENUM_USN_DATA: FAIL {Err(Marshal.GetLastWin32Error())}");
            }
        }
        finally
        {
            NativeMemory.Free(buf);
        }
    }

    private static int CountRecords(byte* buf, uint size)
    {
        var count = 0;
        uint off = 8;
        while (off + 4 <= size)
        {
            var len = *(uint*)(buf + off);
            if (len == 0)
            {
                break;
            }

            count++;
            off += len;
        }

        return count;
    }

    // ---------------- SRUM ----------------

    private static void ProbeSrum()
    {
        const string path = @"C:\Windows\System32\sru\SRUDB.dat";
        var modes = new (string Name, uint Access)[]
        {
            ("FILE_READ_ATTRIBUTES", Native.FILE_READ_ATTRIBUTES),
            ("GENERIC_READ", Native.GENERIC_READ),
        };
        foreach (var (name, access) in modes)
        {
            using var h = Native.CreateFileW(path, access, Native.ShareAll, 0, Native.OPEN_EXISTING, 0, 0);
            Console.WriteLine(h.IsInvalid
                ? $"  CreateFileW({name}, share RWD): FAIL {Err(Marshal.GetLastWin32Error())}"
                : $"  CreateFileW({name}, share RWD): OK");
        }

        try
        {
            Console.WriteLine($"  File size via FileInfo: {new FileInfo(path).Length / (1024 * 1024)} MiB");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FileInfo: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
    }

    // ---------------- UserAssist ----------------

    private static void ProbeUserAssist()
    {
        const string root = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
        using var key = Registry.CurrentUser.OpenSubKey(root);
        if (key is null)
        {
            Console.WriteLine($"  HKCU\\{root}: not found");
            return;
        }

        var total = 0;
        var withTime = 0;
        long newest = 0;
        var guids = key.GetSubKeyNames();
        foreach (var guid in guids)
        {
            using var count = key.OpenSubKey($@"{guid}\Count");
            if (count is null)
            {
                continue;
            }

            foreach (var v in count.GetValueNames())
            {
                total++;
                if (count.GetValue(v) is byte[] { Length: 72 } b)
                {
                    var ft = BitConverter.ToInt64(b, 60);
                    if (ft > 0)
                    {
                        withTime++;
                        newest = Math.Max(newest, ft);
                    }
                }
            }
        }

        Console.WriteLine($"  HKCU\\...\\UserAssist: OK, {guids.Length} GUID keys, {total} values, {withTime} 72-byte entries with last-run FILETIME");
        if (newest > 0)
        {
            Console.WriteLine($"  newest last-run FILETIME: {DateTime.FromFileTimeUtc(newest):u}");
        }
    }

    // ---------------- Placeholder compatibility mode ----------------

    private static void ProbePlaceholderMode()
    {
        static string Name(int m) => m switch
        {
            0 => "PHCM_APPLICATION_DEFAULT",
            1 => "PHCM_DISGUISE_PLACEHOLDER",
            2 => "PHCM_EXPOSE_PLACEHOLDERS",
            -1 => "PHCM_ERROR_INVALID_PARAMETER",
            -2 => "PHCM_ERROR_NO_TEB",
            _ => "unknown",
        };

        int process;
        try
        {
            process = Native.RtlQueryProcessPlaceholderCompatibilityMode();
        }
        catch (EntryPointNotFoundException)
        {
            Console.WriteLine("  RtlQueryProcessPlaceholderCompatibilityMode: entry point not found");
            return;
        }

        int thread = Native.RtlQueryThreadPlaceholderCompatibilityMode();
        Console.WriteLine($"  Query process: {process} {Name(process)}; query thread: {thread} {Name(thread)}");

        int prev = Native.RtlSetProcessPlaceholderCompatibilityMode(2);
        int after = Native.RtlQueryProcessPlaceholderCompatibilityMode();
        Console.WriteLine($"  Set process EXPOSE(2): returned {prev} {Name(prev)}; query after: {after} {Name(after)}");

        int restore = Native.RtlSetProcessPlaceholderCompatibilityMode((sbyte)process);
        Console.WriteLine($"  Restore process to {process}: returned {restore} {Name(restore)}; query: {Native.RtlQueryProcessPlaceholderCompatibilityMode()}");
    }
}

internal static unsafe class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint FILE_LIST_DIRECTORY = 0x0001;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint ShareAll = 0x7; // read | write | delete
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    public const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    public const ushort COMPRESSION_FORMAT_XPRESS_HUFF = 4;
    public const int KEY_READ = 0x20019;
    public const int KEY_QUERY_VALUE = 0x0001;
    public static readonly nint HKEY_LOCAL_MACHINE = unchecked((nint)(int)0x80000002);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint sa, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, uint inSize, void* outBuf, uint outSize, out uint returned, nint overlapped);

    // Pack = 4: native FILETIME is two DWORDs; 8-byte alignment of long would shift cFileName.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    public struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern nint FindFirstFileW(string pattern, out WIN32_FIND_DATAW data);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool FindNextFileW(nint h, out WIN32_FIND_DATAW data);

    [DllImport("kernel32.dll")]
    public static extern bool FindClose(nint h);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegOpenKeyExW(nint hKey, string subKey, int options, int sam, out nint result);

    [DllImport("advapi32.dll")]
    public static extern int RegCloseKey(nint hKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(nint token, int cls, void* info, int len, out int returned);

    [DllImport("ntdll.dll")]
    public static extern int RtlGetCompressionWorkSpaceSize(ushort format, out uint bufferWorkspace, out uint fragmentWorkspace);

    [DllImport("ntdll.dll")]
    public static extern int RtlCompressBuffer(ushort format, byte* uncompressed, uint uncompressedSize, byte* compressed, uint compressedSize, uint chunkSize, out uint finalSize, void* workspace);

    [DllImport("ntdll.dll")]
    public static extern int RtlDecompressBufferEx(ushort format, byte* uncompressed, uint uncompressedSize, byte* compressed, uint compressedSize, out uint finalSize, void* workspace);

    [DllImport("ntdll.dll")]
    public static extern sbyte RtlQueryProcessPlaceholderCompatibilityMode();

    [DllImport("ntdll.dll")]
    public static extern sbyte RtlQueryThreadPlaceholderCompatibilityMode();

    [DllImport("ntdll.dll")]
    public static extern sbyte RtlSetProcessPlaceholderCompatibilityMode(sbyte mode);
}
