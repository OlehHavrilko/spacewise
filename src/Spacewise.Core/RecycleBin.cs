namespace Spacewise.Core;

public sealed record RecycleBinInfo(long Items, long Bytes);

// The Recycle Bin is not a folder Spacewise may touch: its contents are removed only through the shell's own API.
// Emptying is permanent (those files were already deleted once by the user), so it never goes through quarantine.
public interface IRecycleBin
{
    // Null when the shell cannot answer.
    RecycleBinInfo? Query();

    // Empties every drive's bin without prompts. Returns false when Windows refused.
    bool Empty();
}
