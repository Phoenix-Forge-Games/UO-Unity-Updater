namespace Updater.Core;

/// <summary>Minimal semantic-version compare: "1.2.3" and "1.2.3-beta.1". Prerelease sorts before release.</summary>
public static class SemVer
{
    public static bool IsDev(string v) => v.Contains("-dev", StringComparison.OrdinalIgnoreCase) || v.StartsWith("0.0.0");

    public static int Compare(string a, string b)
    {
        Split(a, out var na, out var pa);
        Split(b, out var nb, out var pb);
        for (int i = 0; i < 3; i++)
        {
            int c = na[i].CompareTo(nb[i]);
            if (c != 0) return c;
        }
        if (pa == pb) return 0;
        if (pa.Length == 0) return 1;
        if (pb.Length == 0) return -1;
        var xa = pa.Split('.'); var xb = pb.Split('.');
        for (int i = 0; i < Math.Min(xa.Length, xb.Length); i++)
        {
            bool ia = int.TryParse(xa[i], out var ya), ib = int.TryParse(xb[i], out var yb);
            int c = ia && ib ? ya.CompareTo(yb) : ia ? -1 : ib ? 1 : string.CompareOrdinal(xa[i], xb[i]);
            if (c != 0) return c;
        }
        return xa.Length.CompareTo(xb.Length);
    }

    private static void Split(string v, out int[] nums, out string pre)
    {
        v = v.Trim().TrimStart('v', 'V');
        var plus = v.IndexOf('+'); if (plus >= 0) v = v[..plus];
        var dash = v.IndexOf('-');
        pre = dash >= 0 ? v[(dash + 1)..] : "";
        var core = dash >= 0 ? v[..dash] : v;
        var p = core.Split('.');
        nums = new int[3];
        for (int i = 0; i < 3 && i < p.Length; i++)
            if (!int.TryParse(p[i], out nums[i])) throw new UpdaterException($"Bad version '{v}'.");
    }
}
