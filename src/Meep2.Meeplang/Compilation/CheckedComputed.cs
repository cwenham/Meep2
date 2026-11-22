using Meep2.SDK.Messages;

namespace Meep2.Meeplang.Compilation;

/// <summary>Checks expressions against message types, minding which messages each is worked out for.</summary>
internal static class CheckedComputed
{
    /// <summary>Whether the parameter is worked out for messages of this type.</summary>
    public static bool IsUsedFor(this ICheckedComputed computed, Type messageType) =>
        computed.ForErrors is not { } errors || errors == typeof(ErrorMessage).IsAssignableFrom(messageType);

    /// <summary>Whether messages of this type can go into the module as far as this parameter is concerned.</summary>
    public static bool Fits(this ICheckedComputed computed, Type messageType) =>
        !computed.IsUsedFor(messageType) || computed.AppliesTo(messageType);
}
