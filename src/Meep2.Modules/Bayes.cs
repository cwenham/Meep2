using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meep2.Modules;

/// <summary>
/// A naive Bayes classifier that <see cref="Learn"/> trains, <see cref="Classify"/> reads and <see cref="Forget"/>
/// clears. A tree is known by the file it's saved in, so modules that name the same file share one tree.
/// </summary>
/// <remarks>
/// <para>
/// It's a multinomial classifier over words: for each class it counts the documents it has learned and how often
/// each word appeared in them. A document's score for a class is the class's share of the documents times the
/// chance of each of its words appearing in that class, with add-one (Laplace) smoothing so a word never seen in a
/// class doesn't rule the class out. Scores are worked out as logarithms, so long documents don't underflow, then
/// turned into probabilities that add up to 1 across the classes compared.
/// </para>
/// <para>
/// Learn, Classify and Forget find a tree's file with <see cref="PathFor"/>: a tree belongs to its pipeline, unless
/// its name starts with <c>shared:</c> (Q143). A tree is loaded from its file the first time it's opened, if the file
/// is there, and saved by <see cref="SaveAsync"/> (Q142).
/// </para>
/// <para>Safe to use from several threads: each tree takes a lock while it learns, classifies or saves.</para>
/// </remarks>
public sealed class BayesTree
{
    /// <summary>The prefix that makes a tree shared by every pipeline, as in <c>shared:topics</c>.</summary>
    public const string SharedPrefix = "shared:";

    private static readonly ConcurrentDictionary<string, BayesTree> s_trees = new(StringComparer.Ordinal);

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _saving = new(1, 1);
    private readonly Dictionary<string, ClassCounts> _classes = new(StringComparer.Ordinal);

    // How many classes each word has appeared in, so the vocabulary's size is known without counting it each time.
    private readonly Dictionary<string, int> _vocabulary = new(StringComparer.Ordinal);

    // Documents learned, or classes forgotten, since the tree was last saved.
    private int _unsaved;

    private BayesTree(string path) => Path = path;

    /// <summary>The file the tree is saved in, which is also how it's known.</summary>
    public string Path { get; }

    /// <summary>How many documents have been learned, or classes forgotten, since the tree was last saved or loaded.</summary>
    public int Unsaved
    {
        get
        {
            lock (_lock)
                return _unsaved;
        }
    }

    /// <summary>The classes the tree has learned, and how many documents it has learned for each.</summary>
    public IReadOnlyDictionary<string, long> Classes
    {
        get
        {
            lock (_lock)
                return _classes.ToDictionary(c => c.Key, c => c.Value.Documents, StringComparer.Ordinal);
        }
    }

    /// <summary>Meep's own folder for trees: <c>trees</c> in <c>MEEP_HOME</c>, or in <c>~/.meep</c>.</summary>
    public static string DefaultFolder =>
        System.IO.Path.Combine(
            Environment.GetEnvironmentVariable("MEEP_HOME") is { Length: > 0 } home
                ? home
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".meep"),
            "trees");

    /// <summary>
    /// The file for a tree that's named in a pipeline: <c>&lt;pipeline&gt;_&lt;tree&gt;.json</c> in
    /// <paramref name="folder"/>, or <c>shared_&lt;tree&gt;.json</c> for a name starting with <c>shared:</c>.
    /// Characters that can't be in a file's name become <c>_</c>.
    /// </summary>
    public static string PathFor(string pipeline, string tree, string? folder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);
        var name = tree.StartsWith(SharedPrefix, StringComparison.Ordinal)
            ? $"shared_{tree[SharedPrefix.Length..]}"
            : $"{pipeline}_{tree}";
        return System.IO.Path.Combine(folder ?? DefaultFolder, SafeFileName(name) + ".json");
    }

    /// <summary>
    /// The tree saved in this file. The first time it's asked for, it's loaded from the file if the file is there,
    /// and is otherwise empty.
    /// </summary>
    /// <exception cref="IOException">The file is there but couldn't be read.</exception>
    /// <exception cref="JsonException">The file isn't a saved tree.</exception>
    public static BayesTree Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = System.IO.Path.GetFullPath(path);
        if (s_trees.TryGetValue(path, out var tree))
            return tree;

        var loaded = new BayesTree(path);
        if (File.Exists(path))
            loaded.Load(File.ReadAllText(path));
        return s_trees.GetOrAdd(path, loaded);
    }

    /// <summary>Learns that <paramref name="text"/> belongs to <paramref name="class"/>.</summary>
    /// <returns>False if the text has no words, so there was nothing to learn.</returns>
    public bool Learn(string @class, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@class);
        ArgumentNullException.ThrowIfNull(text);
        var words = Count(text);
        if (words.Count == 0)
            return false;

        lock (_lock)
        {
            if (!_classes.TryGetValue(@class, out var counts))
                _classes.Add(@class, counts = new ClassCounts());
            counts.Documents++;
            foreach (var (word, count) in words)
                Add(counts, word, count);
            _unsaved++;
        }
        return true;
    }

    /// <summary>Forgets one class, or everything when <paramref name="class"/> is null.</summary>
    /// <returns>False if there was nothing to forget.</returns>
    public bool Forget(string? @class = null)
    {
        lock (_lock)
        {
            if (@class is null)
            {
                if (_classes.Count == 0)
                    return false;
                _classes.Clear();
                _vocabulary.Clear();
            }
            else
            {
                if (!_classes.Remove(@class, out var counts))
                    return false;
                foreach (var word in counts.Words.Keys)
                {
                    if (--_vocabulary[word] == 0)
                        _vocabulary.Remove(word);
                }
            }
            _unsaved++;
            return true;
        }
    }

    /// <summary>
    /// Works out how likely <paramref name="text"/> is to belong to each class that has learned at least
    /// <paramref name="minTraining"/> documents.
    /// </summary>
    /// <param name="text">The text to classify.</param>
    /// <param name="minTraining">How many documents a class must have learned to be considered.</param>
    /// <param name="against">
    /// Classes to score the others against, as a spam filter scores spam against ham (Q147). With them, each other
    /// class is scored against these classes only, not against each other, and these classes aren't scored
    /// themselves. A class named here that the tree hasn't learned enough of is left out.
    /// </param>
    /// <returns>
    /// Each class's probability, highest first; empty if no class could be scored. Without <paramref name="against"/>
    /// they add up to 1, and with only one class its probability is always 1.
    /// </returns>
    public IReadOnlyList<KeyValuePair<string, double>> Scores(string text, int minTraining = 1, IReadOnlyCollection<string>? against = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = Count(text);
        Dictionary<string, double> logs;

        lock (_lock)
        {
            double vocabulary = _vocabulary.Count;
            logs = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (name, counts) in _classes)
            {
                if (counts.Documents < Math.Max(1, minTraining))
                    continue;

                // The prior is left as a document count; dividing by the total would cancel out below.
                var score = Math.Log(counts.Documents);
                var denominator = Math.Log(counts.TotalWords + vocabulary);
                foreach (var (word, count) in words)
                {
                    // Words the tree has never seen say nothing about any class, so they're left out.
                    if (!_vocabulary.ContainsKey(word))
                        continue;
                    score += count * (Math.Log(counts.Words.GetValueOrDefault(word) + 1) - denominator);
                }
                logs.Add(name, score);
            }
        }

        List<KeyValuePair<string, double>> scores;
        if (against is null || against.Count == 0)
        {
            scores = [.. Probabilities(logs)];
        }
        else
        {
            var opponents = logs.Where(l => against.Contains(l.Key)).ToList();
            if (opponents.Count == 0)
                return [];
            scores = [];
            foreach (var candidate in logs.Where(l => !against.Contains(l.Key)))
            {
                var probability = Probabilities([candidate, .. opponents]).First(p => p.Key == candidate.Key).Value;
                scores.Add(new(candidate.Key, probability));
            }
        }

        return [.. scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Saves the tree to its file, or deletes the file if the tree is empty, then counts it as saved. The file is
    /// written whole and then moved into place, so a reader never sees half of it.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _saving.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SavedTree snapshot;
            int unsaved;
            lock (_lock)
            {
                unsaved = _unsaved;
                snapshot = new SavedTree(_classes.ToDictionary(
                    c => c.Key,
                    c => new SavedClass(c.Value.Documents, new Dictionary<string, long>(c.Value.Words, StringComparer.Ordinal)),
                    StringComparer.Ordinal));
            }

            if (snapshot.Classes.Count == 0)
            {
                File.Delete(Path);
            }
            else
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var temporary = Path + ".saving";
                await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(snapshot, TreeJson.Default.SavedTree), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, Path, overwrite: true);
            }

            lock (_lock)
                _unsaved -= unsaved;
        }
        finally
        {
            _saving.Release();
        }
    }

    /// <summary>
    /// Splits text into words: runs of letters and digits, lower-cased, keeping apostrophes inside a word
    /// ("don't"). Words of one character are left out.
    /// </summary>
    public static IEnumerable<string> Words(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var word = new StringBuilder();
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            }
            else if (c is '\'' or '’' && word.Length > 0 && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]))
            {
                word.Append('\'');
            }
            else if (word.Length > 0)
            {
                if (word.Length > 1)
                    yield return word.ToString();
                word.Clear();
            }
        }
    }

    // Turns logarithms into probabilities that add up to 1, subtracting the highest first so Exp doesn't underflow.
    private static IEnumerable<KeyValuePair<string, double>> Probabilities(IEnumerable<KeyValuePair<string, double>> logs)
    {
        var list = logs.ToList();
        if (list.Count == 0)
            return [];
        var highest = list.Max(l => l.Value);
        var total = list.Sum(l => Math.Exp(l.Value - highest));
        return list.Select(l => new KeyValuePair<string, double>(l.Key, Math.Exp(l.Value - highest) / total));
    }

    private void Load(string json)
    {
        var saved = JsonSerializer.Deserialize(json, TreeJson.Default.SavedTree)
            ?? throw new JsonException($"{Path} doesn't hold a classification tree.");
        foreach (var (name, savedClass) in saved.Classes)
        {
            var counts = new ClassCounts { Documents = savedClass.Documents };
            foreach (var (word, count) in savedClass.Words)
                Add(counts, word, count);
            _classes.Add(name, counts);
        }
    }

    private void Add(ClassCounts counts, string word, long count)
    {
        if (!counts.Words.TryGetValue(word, out var before))
            _vocabulary[word] = _vocabulary.GetValueOrDefault(word) + 1;
        counts.Words[word] = before + count;
        counts.TotalWords += count;
    }

    private static Dictionary<string, int> Count(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in Words(text))
            counts[word] = counts.GetValueOrDefault(word) + 1;
        return counts;
    }

    private static string SafeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new StringBuilder(name.Length);
        foreach (var c in name)
            safe.Append(Array.IndexOf(invalid, c) >= 0 || c is ':' or '/' or '\\' ? '_' : c);
        return safe.ToString();
    }

    private sealed class ClassCounts
    {
        public long Documents;
        public long TotalWords;
        public readonly Dictionary<string, long> Words = new(StringComparer.Ordinal);
    }
}

/// <summary>A <see cref="BayesTree"/> as it's saved: each class's document count and word counts.</summary>
internal sealed record SavedTree(Dictionary<string, SavedClass> Classes);

/// <summary>One class of a saved tree.</summary>
internal sealed record SavedClass(long Documents, Dictionary<string, long> Words);

[JsonSerializable(typeof(SavedTree))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class TreeJson : JsonSerializerContext;
