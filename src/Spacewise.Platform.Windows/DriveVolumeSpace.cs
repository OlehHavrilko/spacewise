using Spacewise.Core;

namespace Spacewise.Platform.Windows;

public sealed class DriveVolumeSpace : IVolumeSpace
{
    public long? FreeBytes(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
}
