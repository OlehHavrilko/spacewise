// Spike Q1 (Spacewise): can system IEmptyVolumeCache handlers be hosted from our own process?
//
// SAFETY CONTRACT (read-only spike):
//  * Purge and ShowProperties are NEVER called. Their vtable slots are declared only to keep the
//    interface layout correct and are named DoNotCall_*.
//  * The handler registry key is opened with KEY_READ only and passed to Initialize/InitializeEx.
//  * EVCF_OUTOFDISKSPACE (0x20) and EVCF_SETTINGSMODE (0x40) are NOT passed: input flags are 0.
//  * No registry writes; parent process snapshots the VolumeCaches tree before and after and compares.
//  * Every handler runs in a separate child process with a 60 s timeout.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

internal static class Program
{
    private const string VolumeCachesPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
    private const string Volume = @"C:\";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 2 && args[0] == "--one")
        {
            var r = Probe.Run(args[1]);
            Console.WriteLine(JsonSerializer.Serialize(r, Json));
            return 0;
        }

        return RunAll(args.Length == 1 ? args[0] : null);
    }

    private static int RunAll(string? jsonOut)
    {
        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        Console.WriteLine($"OS: {Environment.OSVersion.VersionString}; elevated={elevated}; volume={Volume}");
        if (elevated)
        {
            Console.Error.WriteLine("Refusing to run elevated: the spike must run as a normal user.");
            return 2;
        }

        string before = SnapshotRegistry();
        string[] names;
        using (var root = Registry.LocalMachine.OpenSubKey(VolumeCachesPath, writable: false)!)
        {
            names = root.GetSubKeyNames();
        }

        var results = new List<ProbeResult>();
        string self = Environment.ProcessPath!;
        foreach (var name in names)
        {
            var psi = new ProcessStartInfo(self)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("--one");
            psi.ArgumentList.Add(name);
            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            ProbeResult r;
            if (!p.WaitForExit(Timeout))
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit();
                r = new ProbeResult { Name = name, Error = $"TIMEOUT >{Timeout.TotalSeconds:0}s, child killed" };
            }
            else
            {
                string line = stdout.Result.Trim().Split('\n').LastOrDefault() ?? "";
                try
                {
                    r = JsonSerializer.Deserialize<ProbeResult>(line, Json) ?? new ProbeResult { Name = name, Error = "empty output" };
                }
                catch (JsonException)
                {
                    r = new ProbeResult { Name = name, Error = $"child exit 0x{p.ExitCode:X8}; stderr: {stderr.Result.Trim()}" };
                }
            }

            r.ChildTotalMs = sw.ElapsedMilliseconds;
            results.Add(r);
            Console.WriteLine($"{r.Name,-38} {r.Summary()}");
        }

        string after = SnapshotRegistry();
        Console.WriteLine($"Registry VolumeCaches tree unchanged: {before == after}");
        Console.WriteLine($"StateFlags values present after run: {after.Contains("StateFlags", StringComparison.OrdinalIgnoreCase)}");

        if (jsonOut is not null)
        {
            File.WriteAllText(jsonOut, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }

        return 0;
    }

    private static string SnapshotRegistry()
    {
        var sb = new StringBuilder();
        using var root = Registry.LocalMachine.OpenSubKey(VolumeCachesPath, writable: false)!;
        foreach (var sub in root.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            using var k = root.OpenSubKey(sub, writable: false);
            if (k is null)
            {
                continue;
            }

            sb.Append('[').Append(sub).Append(']').Append('\n');
            foreach (var v in k.GetValueNames().Order(StringComparer.OrdinalIgnoreCase))
            {
                object? val = k.GetValue(v, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                string s = val switch
                {
                    byte[] b => Convert.ToHexString(b),
                    string[] a => string.Join("|", a),
                    null => "",
                    _ => val.ToString() ?? "",
                };
                sb.Append(v).Append('=').Append(s).Append('\n');
            }
        }

        return sb.ToString();
    }
}

internal sealed class ProbeResult
{
    public string Name { get; set; } = "";
    public string? Clsid { get; set; }
    public string? RegFlags { get; set; }
    public bool? SupportsV2 { get; set; }
    public string? CreateHr { get; set; }
    public string? InitMethod { get; set; }
    public string? InitHr { get; set; }
    public string? InitOutFlags { get; set; }
    public string? DisplayName { get; set; }
    public string? GetSpaceHr { get; set; }
    public ulong? Bytes { get; set; }
    public string? DeactivateHr { get; set; }
    public string? DeactivateOutFlags { get; set; }
    public long CreateMs { get; set; }
    public long InitMs { get; set; }
    public long GetSpaceMs { get; set; }
    public long DeactivateMs { get; set; }
    public int ScanCallbacks { get; set; }
    public int NewVisibleWindows { get; set; }
    public string? Error { get; set; }
    public long ChildTotalMs { get; set; }

    public string Summary() =>
        Error is not null && InitHr is null
            ? $"ERROR {Error} ({ChildTotalMs} ms)"
            : $"v2={SupportsV2} create={CreateHr} {InitMethod}={InitHr} flags={InitOutFlags} getSpace={GetSpaceHr} bytes={Bytes} deact={DeactivateHr} t(i/g)={InitMs}/{GetSpaceMs}ms ui={NewVisibleWindows} {Error}";
}

internal static class Probe
{
    public static ProbeResult Run(string name)
    {
        var r = new ProbeResult { Name = name };
        RegistryKey? key = null;
        object? obj = null;
        try
        {
            key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\" + name, writable: false);
            if (key is null)
            {
                r.Error = "cannot open registry key read-only";
                return r;
            }

            r.Clsid = key.GetValue(null) as string;
            object? f = key.GetValue("Flags");
            r.RegFlags = f switch
            {
                int i => $"0x{i:X}",
                byte[] b => "bin:" + Convert.ToHexString(b),
                null => null,
                _ => f.ToString(),
            };
            if (r.Clsid is null || !Guid.TryParse(r.Clsid, out var clsid))
            {
                r.Error = "no CLSID";
                return r;
            }

            int windowsBefore = Ui.CountOwnVisibleWindows();
            var sw = Stopwatch.StartNew();
            var type = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
            try
            {
                obj = Activator.CreateInstance(type);
                r.CreateHr = "S_OK";
            }
            catch (COMException ex)
            {
                r.CreateHr = Hr(ex.HResult);
                r.Error = ex.Message;
                return r;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or Win32Exception)
            {
                r.CreateHr = Hr(ex.HResult);
                r.Error = ex.Message;
                return r;
            }
            finally
            {
                r.CreateMs = sw.ElapsedMilliseconds;
            }

            var v2 = obj as IEmptyVolumeCache2;
            var v1 = obj as IEmptyVolumeCache;
            r.SupportsV2 = v2 is not null;
            if (v1 is null)
            {
                r.Error = "IEmptyVolumeCache not supported (QI failed)";
                return r;
            }

            IntPtr hkey = key.Handle.DangerousGetHandle();
            uint flags = 0; // EVCF_OUTOFDISKSPACE and EVCF_SETTINGSMODE deliberately NOT set
            IntPtr pName = IntPtr.Zero, pDesc = IntPtr.Zero, pBtn = IntPtr.Zero;
            int hr;
            sw.Restart();
            if (v2 is not null)
            {
                r.InitMethod = "InitializeEx";
                hr = v2.InitializeEx(hkey, @"C:\", name, out pName, out pDesc, out pBtn, ref flags);
            }
            else
            {
                r.InitMethod = "Initialize";
                hr = v1.Initialize(hkey, @"C:\", out pName, out pDesc, ref flags);
            }

            r.InitMs = sw.ElapsedMilliseconds;
            r.InitHr = Hr(hr);
            r.InitOutFlags = $"0x{flags:X}";
            r.DisplayName = TakeString(pName);
            TakeString(pDesc);
            TakeString(pBtn);

            // S_FALSE from Initialize means "nothing to clean"; cleanmgr skips such handlers, but we still ask
            // for the size to see what the handler reports. Failure codes stop the probe here.
            if (hr >= 0)
            {
                var cb = new Callback();
                ulong used = 0;
                sw.Restart();
                hr = v1.GetSpaceUsed(out used, cb);
                r.GetSpaceMs = sw.ElapsedMilliseconds;
                r.GetSpaceHr = Hr(hr);
                r.Bytes = used;
                r.ScanCallbacks = cb.Calls;

                uint dflags = 0;
                sw.Restart();
                hr = v1.Deactivate(out dflags);
                r.DeactivateMs = sw.ElapsedMilliseconds;
                r.DeactivateHr = Hr(hr);
                r.DeactivateOutFlags = $"0x{dflags:X}";
            }

            r.NewVisibleWindows = Math.Max(0, Ui.CountOwnVisibleWindows() - windowsBefore);
        }
        catch (Exception ex)
        {
            r.Error = $"{ex.GetType().Name} {Hr(ex.HResult)}: {ex.Message}";
        }
        finally
        {
            if (obj is not null && Marshal.IsComObject(obj))
            {
                Marshal.FinalReleaseComObject(obj);
            }

            key?.Dispose();
        }

        return r;
    }

    private static string Hr(int hr) => hr switch
    {
        0 => "S_OK",
        1 => "S_FALSE",
        _ => $"0x{hr:X8}",
    };

    private static string? TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero)
        {
            return null;
        }

        string? s = Marshal.PtrToStringUni(p);
        Marshal.FreeCoTaskMem(p);
        return s;
    }
}

internal static class Ui
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    public static int CountOwnVisibleWindows()
    {
        uint me = (uint)Environment.ProcessId;
        int n = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == me && IsWindowVisible(h))
            {
                n++;
            }

            return true;
        }, IntPtr.Zero);
        return n;
    }
}

[ComVisible(true)]
internal sealed class Callback : IEmptyVolumeCacheCallBack
{
    public int Calls { get; private set; }

    public int ScanProgress(ulong dwlSpaceUsed, uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] string? pcwszStatus)
    {
        Calls++;
        return 0; // S_OK: continue scanning
    }

    public int PurgeProgress(ulong dwlSpaceFreed, ulong dwlSpaceToFree, uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] string? pcwszStatus)
    {
        return unchecked((int)0x80004004); // E_ABORT: purge must never happen in this spike
    }
}

[ComImport]
[Guid("6E793361-73C6-11D0-8469-00AA00442901")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEmptyVolumeCacheCallBack
{
    [PreserveSig]
    int ScanProgress(ulong dwlSpaceUsed, uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] string? pcwszStatus);

    [PreserveSig]
    int PurgeProgress(ulong dwlSpaceFreed, ulong dwlSpaceToFree, uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] string? pcwszStatus);
}

[ComImport]
[Guid("8FCE5227-04DA-11d1-A004-00805F8ABE06")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEmptyVolumeCache
{
    [PreserveSig]
    int Initialize(IntPtr hkRegKey, [MarshalAs(UnmanagedType.LPWStr)] string pcwszVolume, out IntPtr ppwszDisplayName, out IntPtr ppwszDescription, ref uint pdwFlags);

    [PreserveSig]
    int GetSpaceUsed(out ulong pdwlSpaceUsed, IEmptyVolumeCacheCallBack picb);

    // vtable slot only. NEVER CALL.
    [PreserveSig]
    int DoNotCall_Purge(ulong dwlSpaceToFree, IntPtr picb);

    // vtable slot only. NEVER CALL.
    [PreserveSig]
    int DoNotCall_ShowProperties(IntPtr hwnd);

    [PreserveSig]
    int Deactivate(out uint pdwFlags);
}

[ComImport]
[Guid("02b7e3ba-4db3-11d2-b2d9-00c04f8eec8c")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEmptyVolumeCache2
{
    // Base interface methods must be redeclared in vtable order for classic COM interop.
    [PreserveSig]
    int Initialize(IntPtr hkRegKey, [MarshalAs(UnmanagedType.LPWStr)] string pcwszVolume, out IntPtr ppwszDisplayName, out IntPtr ppwszDescription, ref uint pdwFlags);

    [PreserveSig]
    int GetSpaceUsed(out ulong pdwlSpaceUsed, IEmptyVolumeCacheCallBack picb);

    // vtable slot only. NEVER CALL.
    [PreserveSig]
    int DoNotCall_Purge(ulong dwlSpaceToFree, IntPtr picb);

    // vtable slot only. NEVER CALL.
    [PreserveSig]
    int DoNotCall_ShowProperties(IntPtr hwnd);

    [PreserveSig]
    int Deactivate(out uint pdwFlags);

    [PreserveSig]
    int InitializeEx(IntPtr hkRegKey, [MarshalAs(UnmanagedType.LPWStr)] string pcwszVolume, [MarshalAs(UnmanagedType.LPWStr)] string pcwszKeyName, out IntPtr ppwszDisplayName, out IntPtr ppwszDescription, out IntPtr ppwszBtnText, ref uint pdwFlags);
}
