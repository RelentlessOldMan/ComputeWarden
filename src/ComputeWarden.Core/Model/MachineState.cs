namespace ComputeWarden.Core.Model;

/// <summary>
/// The logical machine availability, derived from the current blocker set.
/// <para><see cref="Unknown"/> means the blocker set could not be reliably
/// computed (e.g. process enumeration failed). Agents must treat it as
/// "do not begin protected intensive work" — acquire refuses under Unknown.</para>
/// </summary>
public enum MachineState
{
    Available,
    Busy,
    Unknown,
}
