using System.Text;
using System.Text.RegularExpressions;

namespace Spacewise.Core;

// POSIX-relative glob. No absolute paths, traversal or regex syntax is accepted.
public sealed class RelativeGlob
{
    private readonly Regex expression;
    public RelativeGlob(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 512 || pattern.Contains('\\')
            || pattern.Split('/').Any(p => p is "" or "." or "..")) throw new ArgumentException("Недопустимый относительный glob.");
        var regex = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/') { i++; regex.Append("(?:[^/]+/)*"); }
                else regex.Append(".*");
            }
            else if (pattern[i] == '*') regex.Append("[^/]*");
            else if (pattern[i] == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(pattern[i].ToString()));
        }
        expression = new(regex.Append('$').ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
    }
    public bool Matches(string relativePath) => expression.IsMatch(relativePath);
}
