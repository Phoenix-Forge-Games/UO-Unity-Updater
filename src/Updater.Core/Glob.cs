using System.Text;
using System.Text.RegularExpressions;

namespace Updater.Core;

/// <summary>Glob patterns over forward-slash relative paths: "*" (within a folder), "**" (any depth), "?".</summary>
public sealed class Glob
{
    private readonly Regex _re;
    public string Pattern { get; }

    public Glob(string pattern)
    {
        Pattern = pattern;
        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                bool slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                sb.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        _re = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public bool IsMatch(string relPath) => _re.IsMatch(relPath);
}
