namespace Meep2.Runtime;

/// <summary>Whether a module receives error messages.</summary>
public enum ErrorDelivery
{
    /// <summary>Errors reach the module if it accepts them, as any module that takes <c>Message</c> does.</summary>
    Include = 0,

    /// <summary>Only errors reach the module. Meeplang's <c>@errors</c>.</summary>
    Only = 1,

    /// <summary>Errors skip the module. Meeplang's <c>@no-errors</c>.</summary>
    Exclude = 2,
}
