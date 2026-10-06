namespace ComputeWarden.Core.Model;

/// <summary>
/// The machine resources a piece of work saturates (or a blocker occupies). Reservations are
/// exclusive per resource: two holders conflict only if their sets overlap, so CPU+network
/// work can run alongside GPU+RAM work. <see cref="All"/> is the conservative default — it
/// conflicts with everything, which is exactly the pre-resource (exclusive machine) behavior.
/// </summary>
[Flags]
public enum ResourceSet
{
    None = 0,
    Cpu = 1,
    Gpu = 2,
    Ram = 4,
    Network = 8,
    Disk = 16,
    All = Cpu | Gpu | Ram | Network | Disk,
}

/// <summary>Parsing/formatting for <see cref="ResourceSet"/> using lower-case wire names.</summary>
public static class Resources
{
    private static readonly (ResourceSet Flag, string Name)[] Names =
    {
        (ResourceSet.Cpu, "cpu"),
        (ResourceSet.Gpu, "gpu"),
        (ResourceSet.Ram, "ram"),
        (ResourceSet.Network, "network"),
        (ResourceSet.Disk, "disk"),
    };

    /// <summary>Valid resource names, for error messages and docs.</summary>
    public static string ValidNames => string.Join(", ", Names.Select(n => n.Name)) + ", all";

    /// <summary>
    /// Parses resource names (case-insensitive; "all" and "memory" accepted). Null or empty
    /// means <see cref="ResourceSet.All"/>. Throws <see cref="ArgumentException"/> on an
    /// unknown name so typos fail loudly instead of silently under-reserving.
    /// </summary>
    public static ResourceSet Parse(IEnumerable<string>? names)
    {
        if (names is null) return ResourceSet.All;

        var set = ResourceSet.None;
        foreach (var raw in names)
        {
            var name = raw?.Trim().ToLowerInvariant() ?? string.Empty;
            if (name.Length == 0) continue;
            set |= name switch
            {
                "all" => ResourceSet.All,
                "memory" => ResourceSet.Ram,
                _ => Lookup(name) ?? throw new ArgumentException(
                    $"Unknown resource '{raw}'. Valid: {ValidNames}"),
            };
        }
        return set == ResourceSet.None ? ResourceSet.All : set;
    }

    /// <summary>Formats a set as lower-case names, e.g. ["cpu", "network"].</summary>
    public static string[] ToNames(ResourceSet set)
        => Names.Where(n => (set & n.Flag) != 0).Select(n => n.Name).ToArray();

    public static bool Overlaps(ResourceSet a, ResourceSet b) => (a & b) != 0;

    private static ResourceSet? Lookup(string name)
    {
        foreach (var (flag, n) in Names)
            if (n == name) return flag;
        return null;
    }
}
