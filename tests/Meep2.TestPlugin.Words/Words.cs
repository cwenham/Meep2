namespace Meep2.TestPlugin.Words;

/// <summary>What the test plugin's Shout does to text.</summary>
public static class Words
{
    /// <summary>"hi" becomes "HI!".</summary>
    public static string Exclaim(string text) => text.ToUpperInvariant() + "!";
}
