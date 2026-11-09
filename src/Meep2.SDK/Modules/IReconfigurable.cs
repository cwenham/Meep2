namespace Meep2.SDK.Modules;

/// <summary>
/// For a module that can take new parameters while its pipeline runs, keeping its state, such as a cache's contents
/// or an open connection (Q31, Q55). Without it, a module whose parameters change is replaced by a new instance made
/// with them.
/// </summary>
/// <typeparam name="TParameters">The module's parameters record.</typeparam>
/// <remarks>
/// <para>
/// A module that handles messages is asked between two messages, so it never handles one while it changes. A source
/// is asked while it runs, so it should only change what its loop reads afresh each time round, such as a period.
/// </para>
/// <para>
/// Parameters that are worked out for each message, such as a template, arrive already compiled: keep the new record
/// and read from it from now on.
/// </para>
/// </remarks>
public interface IReconfigurable<in TParameters>
    where TParameters : class
{
    /// <summary>The module's parameters have changed, as when its line in the pipeline file was edited.</summary>
    /// <param name="old">The parameters it was made with, or last took.</param>
    /// <param name="next">The new parameters.</param>
    /// <returns>
    /// True if the module has taken the new parameters itself and carries on with its state; false to be replaced by
    /// a new instance made with them.
    /// </returns>
    bool ParametersChanged(TParameters old, TParameters next);
}
