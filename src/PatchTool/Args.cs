namespace PatchTool;

/// <summary>"--name value", repeated options, and "--flag" switches.</summary>
internal sealed class Args
{
    private readonly Dictionary<string, List<string>> _opts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    public Args(IEnumerable<string> argv, params string[] flags)
    {
        var flagSet = new HashSet<string>(flags, StringComparer.OrdinalIgnoreCase);
        var list = argv.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (!a.StartsWith("--")) throw new ArgumentException($"Unexpected argument '{a}'.");
            var name = a[2..];
            if (flagSet.Contains(name)) { _flags.Add(name); continue; }
            if (i + 1 >= list.Count) throw new ArgumentException($"--{name} needs a value.");
            if (!_opts.TryGetValue(name, out var v)) _opts[name] = v = [];
            v.Add(list[++i]);
        }
    }

    public bool Flag(string name) { _used.Add(name); return _flags.Contains(name); }

    public string? Opt(string name) { _used.Add(name); return _opts.TryGetValue(name, out var v) ? v[^1] : null; }

    public string Req(string name) => Opt(name) ?? throw new ArgumentException($"--{name} is required.");

    public List<string> Many(string name) { _used.Add(name); return _opts.TryGetValue(name, out var v) ? v : []; }

    public int Int(string name, int def) => Opt(name) is { } s ? int.Parse(s) : def;

    public void EnsureAllUsed()
    {
        var unknown = _opts.Keys.Concat(_flags).Where(k => !_used.Contains(k)).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"Unknown option(s): {string.Join(", ", unknown.Select(u => "--" + u))}");
    }
}
