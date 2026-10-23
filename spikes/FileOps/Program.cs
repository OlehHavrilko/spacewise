using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using static Spacewise.Spikes.FileOps.Native;

namespace Spacewise.Spikes.FileOps;

// Spike: ТЗ §7.4 / Q7 / Q8. All experiments run inside %TEMP%\SpacewiseFileOps-<guid>
// plus exactly one directory D:\.SpacewiseQuarantine (created only if absent, removed only if empty).
// Cleanup is non-recursive by known names.
internal static unsafe class Program
{
    private const string Q8Dir = @"D:\.SpacewiseQuarantine";

    private static int Main()
    {
        PrintEnvironment();
        var work = Path.Combine(Path.GetTempPath(), "SpacewiseFileOps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Console.WriteLine($"work dir: {work}");
        var q8Created = false;
        try
        {
            Step1And2(work);
            Step3(work);
            q8Created = Step6CreateQ8();
            Step4(work, q8Created);
            Step5(work);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UNEXPECTED: {ex}");
        }
        finally
        {
            if (q8Created)
            {
                var ok = RemoveDir(Q8Dir);
                Console.WriteLine($"[Q8] RemoveDirectoryW({Q8Dir}) -> {ok}");
            }
            var removed = TryDeleteDir(work);
            Console.WriteLine($"cleanup work dir removed: {removed}");
        }
        return 0;
    }

    private static void PrintEnvironment()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        Console.WriteLine($"OS: {key?.GetValue("DisplayVersion")} build {key?.GetValue("CurrentBuild")}.{key?.GetValue("UBR")} ({RuntimeInformation.OSDescription})");
        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        Console.WriteLine($"Elevated (Administrators active in token): {admin}; runtime {RuntimeInformation.FrameworkDescription}");
    }

    // ---------- Steps 1-2: parent by path, child relative via NtCreateFile ----------
    private static void Step1And2(string work)
    {
        var dir = Path.Combine(work, "s12");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "child.bin");
        File.WriteAllBytes(file, new byte[4096]);
        var link = Path.Combine(dir, "child-link.bin");
        var linkOk = CreateHardLink(link, file);

        var hParent = OpenDir(dir);
        Console.WriteLine($"[S1] CreateFileW(parent, BACKUP_SEMANTICS|OPEN_REPARSE_POINT) -> {(hParent != INVALID_HANDLE_VALUE ? "OK" : "ERR " + Marshal.GetLastPInvokeError())}");
        Console.WriteLine($"[S1] parent FileIdInfo: {IdInfo(hParent)}; attrs: 0x{Basic(hParent).FileAttributes:X}");

        var st = NtOpenRelative(hParent, "child.bin", DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_OPEN_REPARSE_POINT | FILE_SYNCHRONOUS_IO_NONALERT | FILE_NON_DIRECTORY_FILE, out var hChild);
        Console.WriteLine($"[S2] NtCreateFile(Root=parent, \"child.bin\", DELETE|READ_ATTR|SYNC, OPEN_REPARSE_POINT) -> {Status(st)}");
        if (st >= 0)
        {
            var hPath = OpenFile(file, FILE_READ_ATTRIBUTES);
            var a = IdInfo(hChild);
            var b = IdInfo(hPath);
            var std = Standard(hChild);
            Console.WriteLine($"[S2] child FileIdInfo: {a}; by-path FileIdInfo equal: {a == b}");
            Console.WriteLine($"[S2] FileBasicInfo.attrs=0x{Basic(hChild).FileAttributes:X}; FileStandardInfo: EOF={std.EndOfFile} Alloc={std.AllocationSize} NumberOfLinks={std.NumberOfLinks} (hard link created: {linkOk})");
            CloseHandle(hPath);
            CloseHandle(hChild);
        }
        CloseHandle(hParent);
        File.Delete(link);
        File.Delete(file);
        Directory.Delete(dir, false);
    }

    // ---------- Step 3: POSIX delete while another handle is open ----------
    private static void Step3(string work)
    {
        var dir = Path.Combine(work, "s3");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "del.bin");
        File.WriteAllText(file, "payload-s3");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var hOther = OpenFile(file, GENERIC_READ);
        var hParent = OpenDir(dir);
        var st = NtOpenRelative(hParent, "del.bin", DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_OPEN_REPARSE_POINT | FILE_SYNCHRONOUS_IO_NONALERT | FILE_NON_DIRECTORY_FILE, out var hDel);
        Console.WriteLine($"[S3] other handle open: {hOther != INVALID_HANDLE_VALUE}; NtCreateFile -> {Status(st)}; readonly attr: {(Basic(hDel).FileAttributes & FILE_ATTRIBUTE_READONLY) != 0}");

        var r1 = SetDispositionEx(hDel, FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS);
        Console.WriteLine($"[S3] DispositionEx(DELETE|POSIX) on readonly file -> {r1}");
        var r2 = SetDispositionEx(hDel, FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE);
        Console.WriteLine($"[S3] DispositionEx(DELETE|POSIX|IGNORE_READONLY) -> {r2}");
        Console.WriteLine($"[S3] before close of deleting handle: GetFileAttributesW -> {Attr(file)}; listed in dir: {Listed(dir, "del.bin")}");

        CloseHandle(hDel);
        Console.WriteLine($"[S3] after close of deleting handle (other still open): GetFileAttributesW -> {Attr(file)}; listed: {Listed(dir, "del.bin")}");
        File.WriteAllText(file, "new-file-same-name");
        Console.WriteLine($"[S3] create new file with same name -> OK; listed: {Listed(dir, "del.bin")}");
        var buf = new byte[32];
        using (var sh = new SafeFileHandle(hOther, ownsHandle: false))
        {
            var n = RandomAccess.Read(sh, buf, 0);
            Console.WriteLine($"[S3] read via surviving other handle: \"{System.Text.Encoding.ASCII.GetString(buf, 0, n)}\"; its NumberOfLinks={Standard(hOther).NumberOfLinks} DeletePending={Standard(hOther).DeletePending}");
        }
        CloseHandle(hOther);
        CloseHandle(hParent);
        File.Delete(file);
        Directory.Delete(dir, false);
    }

    // ---------- Step 4: rename into quarantine by handle ----------
    private static void Step4(string work, bool q8Available)
    {
        var src = Path.Combine(work, "s4src");
        var quar = Path.Combine(work, "s4quarantine");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(quar);
        var file = Path.Combine(src, "q.bin");
        File.WriteAllText(file, "payload-s4");
        var existing = Path.Combine(quar, "q-moved.bin");
        File.WriteAllText(existing, "old-occupant");

        var hOther = OpenFile(file, GENERIC_READ);
        var hSrc = OpenDir(src);
        var hQuar = OpenDir(quar);
        var st = NtOpenRelative(hSrc, "q.bin", DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_OPEN_REPARSE_POINT | FILE_SYNCHRONOUS_IO_NONALERT | FILE_NON_DIRECTORY_FILE, out var h);
        var idBefore = IdInfo(h);
        Console.WriteLine($"[S4] NtCreateFile -> {Status(st)}; other handle open: {hOther != INVALID_HANDLE_VALUE}");

        var r1 = Rename(h, hQuar, "q-moved.bin", FILE_RENAME_FLAG_POSIX_SEMANTICS);
        Console.WriteLine($"[S4] RenameInfoEx(Root=quarantine, POSIX) onto existing name -> {r1}");
        var r2 = Rename(h, hQuar, "q-moved.bin", FILE_RENAME_FLAG_POSIX_SEMANTICS | FILE_RENAME_FLAG_REPLACE_IF_EXISTS);
        Console.WriteLine($"[S4] RenameInfoEx(Root=quarantine, POSIX|REPLACE_IF_EXISTS) -> {r2}");
        var n1 = Rename(h, hQuar, "q-moved.bin", FILE_RENAME_FLAG_POSIX_SEMANTICS, native: true);
        Console.WriteLine($"[S4] NtSetInformationFile(FileRenameInformationEx, Root=quarantine, POSIX) onto existing name -> {n1}");
        var n2 = Rename(h, hQuar, "q-moved.bin", FILE_RENAME_FLAG_POSIX_SEMANTICS | FILE_RENAME_FLAG_REPLACE_IF_EXISTS, native: true);
        Console.WriteLine($"[S4] NtSetInformationFile(FileRenameInformationEx, Root=quarantine, POSIX|REPLACE_IF_EXISTS) -> {n2}; other handle still open");
        Console.WriteLine($"[S4] FileId same after move: {IdInfo(h) == idBefore}; src listed: {Listed(src, "q.bin")}; quarantine content: \"{ReadShared(existing)}\"");

        var r3 = Rename(h, 0, Path.Combine(src, "q-dospath.bin"), FILE_RENAME_FLAG_POSIX_SEMANTICS);
        Console.WriteLine($"[S4] RenameInfoEx(Root=NULL, DOS full path) -> {r3}; listed: {Listed(src, "q-dospath.bin")}");
        var r4 = Rename(h, 0, @"\??\" + Path.Combine(quar, "q-ntpath.bin"), FILE_RENAME_FLAG_POSIX_SEMANTICS);
        Console.WriteLine($"[S4] RenameInfoEx(Root=NULL, \\??\\ NT path) -> {r4}; listed: {Listed(quar, "q-ntpath.bin")}");

        if (q8Available)
        {
            var hD = OpenDir(Q8Dir);
            var x1 = Rename(h, hD, "cross.bin", FILE_RENAME_FLAG_POSIX_SEMANTICS);
            Console.WriteLine($"[S4] cross-volume C:->D: RenameInfoEx(Root=D:\\.SpacewiseQuarantine) -> {x1}");
            var x3 = Rename(h, hD, "cross.bin", FILE_RENAME_FLAG_POSIX_SEMANTICS, native: true);
            Console.WriteLine($"[S4] cross-volume NtSetInformationFile(FileRenameInformationEx, Root=D:\\.SpacewiseQuarantine) -> {x3}");
            var x2 = Rename(h, 0, Path.Combine(Q8Dir, "cross.bin"), FILE_RENAME_FLAG_POSIX_SEMANTICS);
            Console.WriteLine($"[S4] cross-volume RenameInfoEx(Root=NULL, D:\\ full path) -> {x2}; D: dir empty: {!Directory.EnumerateFileSystemEntries(Q8Dir).Any()}");
            CloseHandle(hD);
        }

        // Restore by handle back to source.
        var r5 = Rename(h, hSrc, "q.bin", FILE_RENAME_FLAG_POSIX_SEMANTICS, native: true);
        Console.WriteLine($"[S4] restore NtSetInformationFile(Root=src, \"q.bin\") -> {r5}; src listed: {Listed(src, "q.bin")}");

        CloseHandle(h);
        CloseHandle(hOther);
        CloseHandle(hSrc);
        CloseHandle(hQuar);
        foreach (var f in new[] { file, existing, Path.Combine(src, "q-dospath.bin"), Path.Combine(quar, "q-ntpath.bin") })
        {
            File.Delete(f);
        }
        Directory.Delete(src, false);
        Directory.Delete(quar, false);
    }

    // ---------- Step 5: intermediate directory swapped for a junction ----------
    private static void Step5(string work)
    {
        var a = Path.Combine(work, "s5A");
        var b = Path.Combine(a, "B");
        var bOrig = Path.Combine(a, "B_orig");
        var decoy = Path.Combine(work, "s5decoy");
        Directory.CreateDirectory(b);
        Directory.CreateDirectory(decoy);
        var victim = Path.Combine(b, "victim.txt");
        File.WriteAllText(victim, "original");
        File.WriteAllText(Path.Combine(decoy, "victim.txt"), "decoy");

        // "Plan" time: remember identities.
        var hB = OpenDir(b);
        var planB = IdInfo(hB);
        var hTmp = OpenFile(victim, FILE_READ_ATTRIBUTES);
        var planVictim = IdInfo(hTmp);
        CloseHandle(hTmp);
        var hA = OpenDir(a);

        // Attack: rename B away (our handle has FILE_SHARE_DELETE) and put junction B -> decoy.
        Directory.Move(b, bOrig);
        var mk = RunCmd($"mklink /J \"{b}\" \"{decoy}\"");
        Console.WriteLine($"[S5] B renamed while handle open; mklink /J -> exit {mk.code}: {mk.output}");

        // (a) relative to the already-open parent handle -> original object.
        var st = NtOpenRelative(hB, "victim.txt", DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_OPEN_REPARSE_POINT | FILE_SYNCHRONOUS_IO_NONALERT | FILE_NON_DIRECTORY_FILE, out var hRel);
        Console.WriteLine($"[S5] (a) NtCreateFile(Root=hB opened before swap, \"victim.txt\") -> {Status(st)}; FileId == plan: {IdInfo(hRel) == planVictim}");
        CloseHandle(hRel);

        // (b) naive path-based open -> decoy.
        var hPath = OpenFile(victim, FILE_READ_ATTRIBUTES);
        Console.WriteLine($"[S5] (b) CreateFileW(path A\\B\\victim.txt) FileId == plan: {IdInfo(hPath) == planVictim} (follows junction to decoy)");
        CloseHandle(hPath);

        // (c) re-open parent by path with OPEN_REPARSE_POINT -> junction itself, detectable.
        var hB2 = OpenDir(b);
        var attrs = Basic(hB2).FileAttributes;
        Console.WriteLine($"[S5] (c) re-open parent by path: REPARSE_POINT attr={(attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0}; FileId == plan: {IdInfo(hB2) == planB} -> IdentityMismatch detectable");
        CloseHandle(hB2);

        // (d) multi-component relative name from grandparent: intermediate junction IS followed.
        st = NtOpenRelative(hA, @"B\victim.txt", FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_OPEN_REPARSE_POINT | FILE_SYNCHRONOUS_IO_NONALERT | FILE_NON_DIRECTORY_FILE, out var hMulti);
        Console.WriteLine($"[S5] (d) NtCreateFile(Root=hA, \"B\\victim.txt\") -> {Status(st)}; FileId == plan: {(st >= 0 ? (IdInfo(hMulti) == planVictim).ToString() : "n/a")} (OPEN_REPARSE_POINT protects only the last component)");
        if (st >= 0)
        {
            CloseHandle(hMulti);
        }

        CloseHandle(hA);
        CloseHandle(hB);
        Directory.Delete(b, false); // removes the junction only
        File.Delete(Path.Combine(bOrig, "victim.txt"));
        Directory.Delete(bOrig, false);
        Directory.Delete(a, false);
        File.Delete(Path.Combine(decoy, "victim.txt"));
        Directory.Delete(decoy, false);
    }

    // ---------- Step 6 (Q8): .SpacewiseQuarantine in root of D: ----------
    private static bool Step6CreateQ8()
    {
        if (!Directory.Exists(@"D:\"))
        {
            Console.WriteLine("[Q8] D:\\ absent, skipped");
            return false;
        }
        if (Attr(Q8Dir) != "INVALID (err 2)")
        {
            Console.WriteLine($"[Q8] {Q8Dir} already exists ({Attr(Q8Dir)}); not touched");
            return false;
        }
        bool ok;
        fixed (char* p = Q8Dir)
        {
            ok = CreateDirectoryW(p, 0);
        }
        var err = ok ? 0 : Marshal.GetLastPInvokeError();
        Console.WriteLine($"[Q8] CreateDirectoryW({Q8Dir}) as non-elevated user -> {(ok ? "OK" : "ERR " + err)}");
        if (ok)
        {
            var hD = OpenDir(Q8Dir);
            Console.WriteLine($"[Q8] FileIdInfo: {IdInfo(hD)}; owner: {new DirectoryInfo(Q8Dir).GetAccessControl().GetOwner(typeof(NTAccount))}");
            CloseHandle(hD);
        }
        return ok;
    }

    // ---------- helpers ----------
    private static nint OpenDir(string path)
    {
        fixed (char* p = path)
        {
            return CreateFileW(p, FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE, FILE_SHARE_ALL, 0,
                OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, 0);
        }
    }

    private static nint OpenFile(string path, uint access)
    {
        fixed (char* p = path)
        {
            return CreateFileW(p, access, FILE_SHARE_ALL, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
        }
    }

    private static int NtOpenRelative(nint root, string name, uint access, uint options, out nint handle)
    {
        fixed (char* p = name)
        {
            var us = new UNICODE_STRING { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = p };
            var oa = new OBJECT_ATTRIBUTES
            {
                Length = (uint)sizeof(OBJECT_ATTRIBUTES),
                RootDirectory = root,
                ObjectName = &us,
                Attributes = OBJ_CASE_INSENSITIVE,
            };
            IO_STATUS_BLOCK io;
            nint h;
            var st = NtCreateFile(&h, access, &oa, &io, null, 0, FILE_SHARE_ALL, FILE_OPEN, options, null, 0);
            handle = st >= 0 ? h : 0;
            return st;
        }
    }

    private static string SetDispositionEx(nint h, uint flags)
    {
        var ok = SetFileInformationByHandle(h, FileDispositionInfoEx, &flags, sizeof(uint));
        return ok ? "OK" : "ERR " + Marshal.GetLastPInvokeError();
    }

    // FILE_RENAME_INFO layout for FileRenameInfoEx (x64): Flags@0, RootDirectory@8, FileNameLength@16, FileName@20.
    private static string Rename(nint h, nint root, string name, uint flags, bool native = false)
    {
        var nameBytes = name.Length * 2;
        var size = 24 + nameBytes;
        var buf = stackalloc byte[size];
        new Span<byte>(buf, size).Clear();
        *(uint*)buf = flags;
        *(nint*)(buf + 8) = root;
        *(uint*)(buf + 16) = (uint)nameBytes;
        fixed (char* p = name)
        {
            Buffer.MemoryCopy(p, buf + 20, nameBytes, nameBytes);
        }
        if (native)
        {
            IO_STATUS_BLOCK io;
            return Status(NtSetInformationFile(h, &io, buf, (uint)size, FileRenameInformationEx));
        }
        var ok = SetFileInformationByHandle(h, FileRenameInfoEx, buf, (uint)size);
        return ok ? "OK" : "ERR " + Marshal.GetLastPInvokeError();
    }

    private static string IdInfo(nint h)
    {
        FILE_ID_INFO info;
        if (!GetFileInformationByHandleEx(h, FileIdInfo, &info, (uint)sizeof(FILE_ID_INFO)))
        {
            return "ERR " + Marshal.GetLastPInvokeError();
        }
        var id = new ReadOnlySpan<byte>(info.FileId, 16).ToArray();
        Array.Reverse(id);
        return $"vol={info.VolumeSerialNumber:X16} id={Convert.ToHexString(id)}";
    }

    private static FILE_BASIC_INFO Basic(nint h)
    {
        FILE_BASIC_INFO info;
        return GetFileInformationByHandleEx(h, FileBasicInfo, &info, (uint)sizeof(FILE_BASIC_INFO)) ? info : default;
    }

    private static FILE_STANDARD_INFO Standard(nint h)
    {
        FILE_STANDARD_INFO info;
        return GetFileInformationByHandleEx(h, FileStandardInfo, &info, (uint)sizeof(FILE_STANDARD_INFO)) ? info : default;
    }

    private static string Status(int st) =>
        st >= 0 ? "OK" : $"NTSTATUS 0x{st:X8} (win32 {RtlNtStatusToDosError(st)})";

    private static string Attr(string path)
    {
        fixed (char* p = path)
        {
            var a = GetFileAttributesW(p);
            return a == INVALID_FILE_ATTRIBUTES ? $"INVALID (err {Marshal.GetLastPInvokeError()})" : $"0x{a:X}";
        }
    }

    // Readers must pass FILE_SHARE_DELETE while our DELETE handle is open.
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    private static bool Listed(string dir, string name) =>
        Directory.EnumerateFileSystemEntries(dir).Any(e => string.Equals(Path.GetFileName(e), name, StringComparison.OrdinalIgnoreCase));

    private static bool CreateHardLink(string link, string existing)
    {
        fixed (char* l = link)
        fixed (char* e = existing)
        {
            return CreateHardLinkW(l, e, 0);
        }
    }

    private static bool RemoveDir(string path)
    {
        fixed (char* p = path)
        {
            return RemoveDirectoryW(p);
        }
    }

    private static bool TryDeleteDir(string path)
    {
        try
        {
            Directory.Delete(path, false);
            return true;
        }
        catch (IOException ex)
        {
            Console.WriteLine($"cleanup: {ex.Message}");
            return false;
        }
    }

    private static (int code, string output) RunCmd(string args)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c " + args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var o = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
        p.WaitForExit();
        return (p.ExitCode, o.Length > 0 ? "<output>" : "");
    }
}
