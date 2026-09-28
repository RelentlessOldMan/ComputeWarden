namespace ComputeWarden.Core;

/// <summary>Inexpensive counters for observability (spec §31).</summary>
public sealed record WardenStats(
    long TotalAcquisitions,
    long FailedAcquisitions,
    long ExpiredReservations,
    int CurrentReservations,
    int CurrentProcessBlockers,
    TimeSpan Uptime);
