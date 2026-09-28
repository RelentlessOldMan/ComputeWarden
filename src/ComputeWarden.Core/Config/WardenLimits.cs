namespace ComputeWarden.Core.Config;

/// <summary>
/// Input validation limits. The service must validate all client inputs and place caps on
/// string and metadata sizes (spec §26).
/// </summary>
public sealed class WardenLimits
{
    public int MaxOwnerLength { get; init; } = 256;
    public int MaxDescriptionLength { get; init; } = 1024;
    public int MaxReasonLength { get; init; } = 1024;
    public int MaxMetadataEntries { get; init; } = 32;
    public int MaxMetadataKeyLength { get; init; } = 128;
    public int MaxMetadataValueLength { get; init; } = 1024;
}
