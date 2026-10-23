using System.Security.Cryptography;
using System.Text;

namespace Spacewise.Core;

public sealed record PlanItem(string RuleId, int RuleVersion, string ActionId, string Path, long ExpectedSize, DateTime ExpectedMtimeUtc);

// A plan is what the executor will be allowed to run: the digest binds the exact item list (spec §8, risk "confused deputy").
public sealed record CleanupPlan(DateTime CreatedUtc, DateTime ExpiresUtc, string Origin, IReadOnlyList<PlanItem> Items, byte[] Digest)
{
    public long ExpectedBytes => Items.Sum(i => i.ExpectedSize);

    public static CleanupPlan Create(IEnumerable<PlanItem> items, DateTime createdUtc, TimeSpan lifetime, string origin)
    {
        PlanItem[] ordered = [.. items.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.RuleId, StringComparer.Ordinal)];
        return new(createdUtc, createdUtc + lifetime, origin, ordered, ComputeDigest(ordered));
    }

    public static byte[] ComputeDigest(IReadOnlyList<PlanItem> items)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (PlanItem item in items)
        {
            // Length-prefixed fields: no field boundary can be shifted to produce the same bytes.
            Append(hash, item.RuleId);
            Append(hash, item.RuleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, item.ActionId);
            Append(hash, item.Path);
            Append(hash, item.ExpectedSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, item.ExpectedMtimeUtc.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return hash.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    public bool DigestMatches() => CryptographicOperations.FixedTimeEquals(Digest, ComputeDigest(Items));
}
