namespace ComputeWarden.Core;

/// <summary>Inexpensive counters for observability.</summary>
public sealed record WardenStats(
    long TotalAcquisitions,
    long FailedAcquisitions,
    long ExpiredReservations,
    int CurrentReservations,
    int CurrentProcessBlockers,
    TimeSpan Uptime);
