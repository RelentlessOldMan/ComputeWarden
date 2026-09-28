namespace ComputeWarden.Core.Config;

/// <summary>A rule identifying a process whose presence should block intensive work.</summary>
public sealed class ProcessRule
{
    public string Name { get; init; } = string.Empty;
    public string Executable { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public string? Description { get; init; }
}
