namespace Meep2.Modules;

/// <summary>
/// The Bayes trees in memory, by file, and which are open: each loaded once, however many modules hold it, and
/// dropped when the last lets go, saved first if one that saves it held it (M2). A tree opened with
/// <see cref="BayesTree.Open"/> stays for the life of the process.
/// </summary>
internal static class TreeStore
{
    private static readonly Dictionary<string, Entry> s_trees = new(StringComparer.Ordinal);
    private static readonly Lock s_opening = new();

    /// <summary>The tree saved in this file, kept in memory for good.</summary>
    public static BayesTree Open(string path)
    {
        var entry = Find(path, hold: false);
        lock (s_opening)
            entry.Kept = true;
        return entry.Tree;
    }

    /// <summary>A module's hold on the tree saved in this file, which saves it when dropped if <paramref name="saves"/>.</summary>
    public static BayesTree.TreeHold Hold(string path, bool saves)
    {
        var entry = Find(path, hold: true);
        lock (s_opening)
            entry.SaveWhenDropped |= saves;
        return new BayesTree.TreeHold(entry.Tree);
    }

    /// <summary>Whether the tree saved in this file is in memory now.</summary>
    public static bool IsInMemory(string path)
    {
        lock (s_opening)
            return s_trees.ContainsKey(Path.GetFullPath(path));
    }

    /// <summary>A module let go: the last saves the tree, if it should, then drops it, unless it was taken again meanwhile.</summary>
    public static async ValueTask LetGoAsync(BayesTree tree)
    {
        bool save;
        lock (s_opening)
        {
            if (!s_trees.TryGetValue(tree.Path, out var entry) || !ReferenceEquals(entry.Tree, tree) || --entry.Holders > 0 || entry.Kept)
                return;
            save = entry.SaveWhenDropped;
        }
        if (save && tree.Unsaved > 0)
            await tree.SaveAsync().ConfigureAwait(false);
        lock (s_opening)
        {
            if (s_trees.TryGetValue(tree.Path, out var entry) && ReferenceEquals(entry.Tree, tree) && entry.Holders == 0 && !entry.Kept)
                s_trees.Remove(tree.Path);
        }
    }

    // The tree in memory for a file, loaded if it isn't, counting a holder if asked.
    private static Entry Find(string path, bool hold)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        lock (s_opening)
        {
            if (s_trees.TryGetValue(path, out var entry))
            {
                entry.Holders += hold ? 1 : 0;
                return entry;
            }
        }

        var loaded = BayesTree.LoadFrom(path);
        lock (s_opening)
        {
            // Another may have loaded it in the meantime.
            if (!s_trees.TryGetValue(path, out var entry))
                s_trees[path] = entry = new Entry(loaded);
            entry.Holders += hold ? 1 : 0;
            return entry;
        }
    }

    // Guarded by s_opening: the modules holding the tree, whether it was opened for good with Open, and whether a module
    // that saves it has held it since it was loaded.
    private sealed class Entry(BayesTree tree)
    {
        public BayesTree Tree { get; } = tree;

        public int Holders { get; set; }

        public bool Kept { get; set; }

        public bool SaveWhenDropped { get; set; }
    }
}
