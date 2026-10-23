using System.Text;
using System.Text.RegularExpressions;

namespace Spacewise.Catalog;

// Case-insensitive glob over a relative path with '\' separators.
public sealed class Glob
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
    private readonly Regex regex;

    public Glob(string pattern)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                // "**\" matches zero or more whole segments; a trailing "**" matches the rest.
                if (i + 1 < pattern.Length && pattern[i + 1] == '\\') { i++; sb.Append(@"(?:[^\\]*\\)*"); }
                else sb.Append(".*");
            }
            else if (c == '*') sb.Append(@"[^\\]*");
            else if (c == '?') sb.Append(@"[^\\]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        regex = new Regex(sb.Append('$').ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout);
    }

    public bool IsMatch(string relativePath) => regex.IsMatch(relativePath);
}
