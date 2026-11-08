using Meep2.SDK.Messages;

namespace Meep2.Runtime;

/// <summary>How a verb with several inflections chooses between them by message type: the closest fit wins (Q20).</summary>
public static class InflectionOrder
{
    /// <summary>
    /// How closely an inflection's input type fits a message type, where lower is closer: 0 for the message's own
    /// type, then its base classes, nearest first, then interfaces, and <see cref="Message"/> last. Null if the
    /// inflection doesn't take the type at all. Inflections that fit equally well are tried in the order they were
    /// given, and their parameters decide (<see cref="SDK.Modules.IChosenByParameters{TParameters}"/>).
    /// </summary>
    public static int? Rank(Type messageType, Type inputType)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(inputType);
        if (!inputType.IsAssignableFrom(messageType))
            return null;
        if (inputType == messageType)
            return 0;
        if (inputType == typeof(Message))
            return int.MaxValue;
        if (inputType.IsInterface)
            return int.MaxValue - 1;
        var distance = 0;
        for (var current = messageType; current is not null; current = current.BaseType, distance++)
        {
            if (current == inputType)
                return distance;
        }
        return int.MaxValue - 1;
    }
}
