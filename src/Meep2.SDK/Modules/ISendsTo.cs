namespace Meep2.SDK.Modules;

/// <summary>
/// A module that sends to an address its parameters give, such as Fetch or Send, says what it is, so tools can match
/// it with the Listen it reaches before anything runs.
/// </summary>
public interface ISendsTo
{
    /// <summary>The address, the same for every message (<see cref="Computed{T}.IsConstant"/>) or worked out for each.</summary>
    Computed<string> Address { get; }
}
