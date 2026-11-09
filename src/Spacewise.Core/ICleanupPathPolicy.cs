namespace Spacewise.Core;

// Each platform owns path syntax, case sensitivity and protected locations.
public interface ICleanupPathPolicy
{
    PolicyDecision Check(string path);
}
