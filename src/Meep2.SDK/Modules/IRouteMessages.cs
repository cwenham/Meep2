using Meep2.SDK.Messages;

namespace Meep2.SDK.Modules;

/// <summary>
/// For a module that sends each message down one of several copies of the branch below it, as Distribute does. The
/// runtime asks it which, on the thread delivering the message, so it mustn't keep state that isn't safe for that.
/// </summary>
public interface IRouteMessages
{
    /// <summary>How many copies of the branch there are.</summary>
    int Lanes { get; }

    /// <summary>Which copy a message goes down, from 0 to <see cref="Lanes"/> - 1.</summary>
    int LaneOf(Message message);
}
