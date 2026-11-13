namespace Meep2.SDK.Modules;

/// <summary>
/// For an inflection that can emit many messages for one it handles, such as Enumerate with <c>all=true</c>. Emitting
/// never waits, so when that's more than the next module's buffer holds, the earliest are dropped. The loader asks
/// this when the pipeline loads and warns about any buffer too small to take them (Q169).
/// </summary>
/// <typeparam name="TParameters">The inflection's parameters record.</typeparam>
public interface IEmitsAtOnce<in TParameters>
    where TParameters : class
{
    /// <summary>The most messages it can emit for one message with these parameters, or null if it can't say.</summary>
    static abstract long? MostAtOnce(TParameters parameters);
}
