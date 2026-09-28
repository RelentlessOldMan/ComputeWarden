namespace ComputeWarden.Core.Model;

/// <summary>The kind of reason the machine is considered busy.</summary>
public enum BlockerType
{
    Process,
    Reservation,
    Manual,
}
