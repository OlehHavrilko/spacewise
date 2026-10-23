using System.Runtime.InteropServices;
using System.Text;
using Spacewise.Core;

namespace Spacewise.Platform.Windows;

public sealed class VolumeCatalog : IVolumeCatalog
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int length);

    public IReadOnlyList<VolumeInfo> Fixed()
    {
        var result = new List<VolumeInfo>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var guid = new StringBuilder(64);
                string key = GetVolumeNameForVolumeMountPointW(drive.Name, guid, guid.Capacity) ? guid.ToString() : drive.Name;
                result.Add(new(drive.Name, key, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
}
