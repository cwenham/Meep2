namespace Meep2.Modules;

/// <summary>A <see cref="BayesTree"/> as it's saved: each class's document count and word counts.</summary>
internal sealed record SavedTree(Dictionary<string, SavedClass> Classes);

/// <summary>One class of a saved tree.</summary>
internal sealed record SavedClass(long Documents, Dictionary<string, long> Words);
