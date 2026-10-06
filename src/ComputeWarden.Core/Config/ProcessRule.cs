using ComputeWarden.Core.Model;

namespace ComputeWarden.Core.Config;

/// <summary>A rule identifying a process whose presence should block intensive work.</summary>
public sealed class ProcessRule
{
    public string Name { get; init; } = string.Empty;
    public string Executable { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public string? Description { get; init; }

    /// <summary>Resources the process occupies while running. Defaults to all (blocks everything).</summary>
    public ResourceSet Resources { get; init; } = ResourceSet.All;
}
