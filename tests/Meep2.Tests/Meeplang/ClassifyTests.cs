using System.Text.Json;
using Meep2.Modules;
using Meep2.SDK.Messages;
using Meep2.Tests.Support;

namespace Meep2.Tests.Meeplang;

/// <summary>Learn, Classify and Forget, and the naive Bayes trees they share.</summary>
/// <remarks>Trees are shared across the process by their file, so each test uses files of its own.</remarks>
public sealed class ClassifyTests : IDisposable
{
    private readonly TemporaryFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string NewFile() => Path.Combine(_folder.Path, $"tree-{Guid.NewGuid():N}.json");

    private BayesTree Topics()
    {
        var tree = BayesTree.Open(NewFile());
        tree.Learn("Science", "The experiment measured the quantum state of electrons in the laboratory.");
        tree.Learn("Science", "Researchers published a study on photosynthesis and plant cells.");
        tree.Learn("Science", "Astronomers observed a distant galaxy with the new telescope.");
        tree.Learn("Business", "The company reported quarterly profits and its shares rose on the market.");
        tree.Learn("Business", "Investors expect the merger to raise revenue and cut costs.");
        tree.Learn("Business", "The bank raised interest rates as the market slowed.");
        tree.Learn("History", "The empire fell after centuries of war and the king was exiled.");
        tree.Learn("History", "Medieval castles were built to defend the kingdom from invaders.");
        return tree;
    }

    [Test]
    public async Task Words_are_lower_cased_runs_of_letters_and_digits_keeping_apostrophes_inside_them()
    {
        var words = BayesTree.Words("Don't PANIC: it's 42, a 3D-printed ‘towel’!");
        await Assert.That(string.Join(" ", words)).IsEqualTo("don't panic it's 42 3d printed towel");
    }

    [Test]
    public async Task A_tree_scores_the_class_whose_words_fit_best_highest()
    {
        var tree = Topics();

        var scores = tree.Scores("A study of electrons using a telescope in the laboratory");
        await Assert.That(scores[0].Key).IsEqualTo("Science");
        await Assert.That(scores.Sum(s => s.Value)).IsEqualTo(1.0).Within(1e-9);

        await Assert.That(tree.Scores("Shares and profits on the market")[0].Key).IsEqualTo("Business");
        await Assert.That(tree.Scores("The king defended his castle in the war")[0].Key).IsEqualTo("History");
    }

    [Test]
    public async Task A_long_text_scores_without_underflowing()
    {
        var tree = Topics();
        var scores = tree.Scores(string.Join(" ", Enumerable.Repeat("market profits shares revenue", 2000)));

        await Assert.That(scores[0].Key).IsEqualTo("Business");
        await Assert.That(double.IsFinite(scores[0].Value)).IsTrue();
        await Assert.That(scores[0].Value).IsGreaterThan(0.99);
    }

    [Test]
    public async Task Classes_with_too_little_training_are_left_out()
    {
        var tree = Topics();
        await Assert.That(tree.Scores("castles and kings", minTraining: 3).Select(s => s.Key)).IsEquivalentTo(["Science", "Business"]);
        await Assert.That(tree.Scores("castles and kings", minTraining: 4)).IsEmpty();
    }

    [Test]
    public async Task Against_scores_each_other_class_against_those_classes_alone()
    {
        var tree = BayesTree.Open(NewFile());
        tree.Learn("Spam", "Win a free prize now, click to claim your cash reward");
        tree.Learn("Copyright", "This video infringes our copyright, please take it down");
        tree.Learn("Ham", "Lunch on Thursday? Let me know what time suits you");

        var scores = tree.Scores("Claim your free cash prize", against: ["Ham"]);

        await Assert.That(scores.Select(s => s.Key)).IsEquivalentTo(["Spam", "Copyright"]);
        await Assert.That(scores[0].Key).IsEqualTo("Spam");
        await Assert.That(scores[0].Value).IsGreaterThan(0.9);
        await Assert.That(tree.Scores("anything", against: ["Nothing learned"])).IsEmpty();
    }

    [Test]
    public async Task A_tree_belongs_to_its_pipeline_unless_its_name_is_shared()
    {
        await Assert.That(BayesTree.PathFor("news", "topics", "/trees")).IsEqualTo("/trees/news_topics.json");
        await Assert.That(BayesTree.PathFor("news", "shared:topics", "/trees")).IsEqualTo("/trees/shared_topics.json");
        await Assert.That(BayesTree.PathFor("news", "a/b:c", "/trees")).IsEqualTo("/trees/news_a_b_c.json");
        await Assert.That(BayesTree.PathFor("news", "topics")).StartsWith(BayesTree.DefaultFolder);
    }

    [Test]
    public async Task A_saved_tree_is_loaded_when_it_is_first_opened()
    {
        var tree = Topics();
        await tree.SaveAsync();
        await Assert.That(tree.Unsaved).IsEqualTo(0);

        var copy = NewFile();
        File.Copy(tree.Path, copy);
        var loaded = BayesTree.Open(copy);

        await Assert.That(loaded.Classes).IsEquivalentTo(tree.Classes);
        var text = "The king defended his castle in the war";
        await Assert.That(loaded.Scores(text)[0].Value).IsEqualTo(tree.Scores(text)[0].Value).Within(1e-12);
    }

    [Test]
    public async Task Classify_emits_the_best_class_and_its_score()
    {
        var tree = Topics();
        var run = await TestRun.Start($$"""
            Interval 10ms count=1 payload="Astronomers built a telescope to study the galaxy"
                Classify "topics" file="{{tree.Path}}" min-training=1 text=msg.Payload
                    Sink
            """);

        await Assert.That(run.Diagnostics).IsEqualTo("");
        var classified = (ClassifiedMessage)run.At("Sink").Single();
        await Assert.That(classified.Tree).IsEqualTo("topics");
        await Assert.That(classified.Class).IsEqualTo("Science");
        await Assert.That(classified.IsClassified).IsTrue();
        await Assert.That(classified.Score).IsGreaterThan(0.5);
        await Assert.That(classified.Scores.Keys).IsEquivalentTo(["Science", "Business", "History"]);
        await Assert.That(classified.Text).IsEqualTo("Astronomers built a telescope to study the galaxy");
        await Assert.That(classified.Parents.Count).IsEqualTo(1);
        await Assert.That(classified.Parents[0]).IsTypeOf<Tick>();
    }

    [Test]
    public async Task Classify_passes_messages_on_unclassified_until_a_class_has_learned_enough()
    {
        var tree = Topics(); // three documents at most, and min-training is 10 unless set
        var run = await TestRun.Start($$"""
            Interval 10ms count=1 payload="A telescope"
                Classify "topics" file="{{tree.Path}}" text=msg.Payload
                    Sink
            """);

        var classified = (ClassifiedMessage)run.At("Sink").Single();
        await Assert.That(classified.IsClassified).IsFalse();
        await Assert.That(classified.Class).IsNull();
        await Assert.That(classified.Score).IsEqualTo(0);
        await Assert.That(run.Unhandled).IsEmpty();
    }

    [Test]
    public async Task Classify_takes_classes_to_score_against()
    {
        var tree = BayesTree.Open(NewFile());
        tree.Learn("Spam", "Win a free prize now, click to claim your cash reward");
        tree.Learn("Ham", "Lunch on Thursday? Let me know what time suits you");
        var run = await TestRun.Start($$"""
            Interval 10ms count=1 payload="Free cash prize"
                Classify "mail" file="{{tree.Path}}" min-training=1 against="Ham" text=msg.Payload
                    Sink
            """);

        var classified = (ClassifiedMessage)run.At("Sink").Single();
        await Assert.That(classified.Class).IsEqualTo("Spam");
        await Assert.That(classified.Scores.Keys).IsEquivalentTo(["Spam"]);
    }

    [Test]
    public async Task Learn_saves_after_every_few_documents_and_when_the_pipeline_finishes()
    {
        var file = NewFile();
        using var texts = new TemporaryFolder();
        var science = texts.Write("science.txt", "Physicists measured gravity waves in the laboratory experiment.");
        var business = texts.Write("business.txt", "The firm's quarterly revenue beat the market forecast.");

        var run = await TestRun.Start($$"""
            Read "{{science}}"
                Learn "topics" class="Science" file="{{file}}" save-every=1
                    Sink
            Read "{{business}}"
                Learn "topics" class="Business" file="{{file}}"
            """);
        await Assert.That(run.Diagnostics).IsEqualTo("");
        await Assert.That(run.At("Sink").Single()).IsTypeOf<FileText>(); // passed on after learning

        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        var classes = saved.RootElement.GetProperty("classes");
        await Assert.That(classes.GetProperty("Science").GetProperty("documents").GetInt64()).IsEqualTo(1);
        await Assert.That(classes.GetProperty("Business").GetProperty("documents").GetInt64()).IsEqualTo(1); // saved as it finished
        await Assert.That(classes.GetProperty("Business").GetProperty("words").GetProperty("revenue").GetInt64()).IsEqualTo(1);
    }

    [Test]
    public async Task Learn_and_Classify_find_their_pipelines_tree_by_name()
    {
        var name = $"tree-{Guid.NewGuid():N}";
        await TestRun.Start($$"""
            Interval 1ms count=10 payload="gravity experiment in the laboratory"
                Learn "{{name}}" class="Science" text=msg.Payload save-every=0
                    Classify "{{name}}" text=msg.Payload as Classified
                        Sink
            """);

        // Test pipelines are all called "test", after test.meep. Nothing was saved, so this only opens the tree in memory.
        var tree = BayesTree.Open(BayesTree.PathFor("test", name));
        await Assert.That(tree.Classes).IsEquivalentTo(new Dictionary<string, long> { ["Science"] = 10 });
        await Assert.That(File.Exists(tree.Path)).IsFalse();
    }

    [Test]
    public async Task Learn_can_take_its_class_from_each_message()
    {
        var file = NewFile();
        await TestRun.Start($$"""
            Interval 1ms count=3 payload="some words"
                Learn "topics" class="Batch {msg.Count}" text=msg.Payload file="{{file}}" save-every=0
            """);

        await Assert.That(BayesTree.Open(file).Classes.Keys).IsEquivalentTo(["Batch 1", "Batch 2", "Batch 3"]);
        await Assert.That(File.Exists(file)).IsFalse();
    }

    [Test]
    public async Task Forget_forgets_one_class_or_the_whole_tree_and_saves_it()
    {
        var tree = Topics();
        await TestRun.Start($$"""
            Interval 10ms count=1
                Forget "topics" class="History" file="{{tree.Path}}"
            """);
        await Assert.That(tree.Classes.Keys).IsEquivalentTo(["Science", "Business"]);
        await Assert.That(File.Exists(tree.Path)).IsTrue();
        await Assert.That(tree.Scores("castles and the king").Select(s => s.Key)).IsEquivalentTo(["Science", "Business"]);

        var run = await TestRun.Start($$"""
            Interval 10ms count=1
                Forget "topics" file="{{tree.Path}}"
                    Sink
            """);
        await Assert.That(run.At("Sink").Single()).IsTypeOf<Tick>();
        await Assert.That(tree.Classes).IsEmpty();
        await Assert.That(File.Exists(tree.Path)).IsFalse();
    }

    [Test]
    public async Task Learn_and_Classify_pass_errors_on_without_learning_from_them()
    {
        var file = NewFile();
        var run = await TestRun.Start($$"""
            Interval 10ms count=1
                Boom
                    Learn "topics" class="Science" file="{{file}}"
                        Classify "topics" file="{{file}}"
                            Sink
            """);

        await Assert.That(run.At("Sink").Single()).IsTypeOf<ErrorMessage>();
        await Assert.That(BayesTree.Open(file).Classes).IsEmpty();
    }

    [Test]
    public async Task A_missing_tree_name_is_reported_when_the_pipeline_loads()
    {
        var run = await TestRun.Start("""
            Interval 10ms count=1
                Learn "shared:" class="Science"
            """, run: false);

        await Assert.That(run.Diagnostics).Contains("Learn needs the name of a classification tree.");
    }
}
