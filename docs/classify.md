# Learn and Classify

Written 11 November 2026. Three new core verbs that share naive Bayes classification trees by name: Learn and Classify, replacing the original Meep's `BayesTrain` and `Bayes`, and Forget. This covers what was built, the decisions I made while building it, and questions numbered from 142 with answer lines. Your answers are applied, as described at the end.

## What's built

```
+ Read "examples/science.txt"
  Learn "topics" class="Science"
+ Read "examples/business.txt"
  Learn "topics" class="Business"
Batch 2
    Classify "topics" min-training=1 text="Telescope finds water on a distant planet"
        WriteLine "{msg.Class} ({msg.Score:P0})"
```

- **`Learn "tree" class=…`** teaches the named tree that each message belongs to a class, then passes the message on. `class` is worked out for each message, so `class=msg.section` or `class="{msg.Url}"` work, as `BayesTrain Class="{msg.URL}"` did.
- **`Classify "tree"`** emits a `ClassifiedMessage` for each message: `Tree`, `Class` (the best class), `Score` (its probability, 0 to 1), `Scores` (every class's probability) and `Text` (what was classified). Its parent is the message classified, so `fam` reaches it. To keep only confident results, follow it with `Where msg.Score > 0.9`.
- **Until a class has learned `min-training` documents** (10 unless set), Classify leaves it out. A message no class can be chosen for comes out unclassified: `Class` is null, `IsClassified` false and `Score` 0.
- **`against="Ham"`** on Classify scores each other class against the classes named (separated by commas) alone, as the original's `Bayes Against` did for spam filtering. The `against` classes are never the class emitted.
- **`Forget "tree"`** makes a tree forget everything, or one class with `class=…`, and saves it straight away. A tree that has forgotten everything has its file deleted.
- **All three take `text=…`** (Learn and Classify) to say what to learn from or classify, such as `text=msg.title` on JSON. Without it, they use the message's text (`IHasText`), or its printed form for other messages.
- **A tree belongs to its pipeline** unless its name starts with `shared:`, as in `Learn "shared:topics"`. Modules find a tree by its file: `~/.meep/trees/<pipeline>_<tree>.json`, or `shared_<tree>.json` for a shared tree, in `MEEP_HOME` when that's set. `file=…` on any of the three verbs names another file, and modules naming the same file share the tree.
- **Learn saves the tree after every 5 documents** it has learned (`save-every=…` to change it, or 0 to keep it in memory only), and again when the pipeline finishes. A tree is loaded from its file the first time a module opens it.
- **Errors pass through all three untouched**, so Learn never learns from an error's text and the error still reaches whatever handles it.
- **Modules can now read their pipeline's name**, as `ModuleContext.PipelineName`: the pipeline file's name without `.meep`.

The classifier is in [src/Meep2.Modules/Bayes.cs](../src/Meep2.Modules/Bayes.cs), the verbs in [src/Meep2.Modules/Classifying.cs](../src/Meep2.Modules/Classifying.cs), the tests in [tests/Meep2.Tests/Meeplang/ClassifyTests.cs](../tests/Meep2.Tests/Meeplang/ClassifyTests.cs), and the example in [examples/classify.meep](../examples/classify.meep).

## Decisions I made while building

- **Multinomial naive Bayes with add-one smoothing**, the textbook version for text. Scores are added as logarithms and turned into probabilities at the end. The original multiplied them back out with `Math.Pow(Math.E, score)`, which underflows to 0 for anything longer than a few hundred words; a test covers an 8,000-word text.
- **Words** are runs of letters and digits, lower-cased, keeping an apostrophe inside a word ("don't"). One-letter words are left out. There are no stop words: common words appear in every class, so they barely move the scores.
- **Words the tree has never seen are ignored**, since they say nothing about any class.
- **Learn isn't a passthrough module.** It passes each message on after learning from it, so a Classify below it in the same chain always sees what it just learned. Classify in a separate chain has no such guarantee: the first version of the example classified before learning on some runs, so it now waits with `Batch 2`.
- **The score is the probability among the classes compared.** With one class it's always 1, and naive Bayes is overconfident on long texts, so scores bunch up near 0 and 1.

## Questions

142. What a tree has learned is in memory only, so it's lost when the pipeline's process or the daemon stops. Should trees be saved, for example to `~/.meep/trees/<name>.json` after each change (or every few seconds), and loaded when first named? Or should saving and loading be messages, as the original's `BayesClass` exported and imported its classes?

     **Answer:** Save it to a file in `~/.meep/trees/<pipeline>_<name>.json` after every 5 training messages, with optional parameters to change both.

143. Every pipeline in a process shares a tree with the same name, as the original's static dictionary did. In the daemon, that means any pipeline can teach or read any tree. Should trees instead belong to one pipeline unless named with a prefix such as `shared:topics`, or wait for pipeline permissions (Q40)?

     **Answer:** They should belong to one pipeline unless named with a prefix.

144. Classify turns each message into an error until the tree has learned something, so a pipeline that starts classifying before learning may hit its error policy's limit and stop. Would you rather those messages were dropped quietly, passed on unclassified, or kept as errors?

     **Answer:** Pass them as unclassified.

145. `min-training` defaults to 1, where the original's default was 10. 1 means a fresh tree works straight away, at the cost of confident guesses from classes taught once or twice. Should the default be higher?

     **Answer:** Yes, set the default to 10. But if you know of a better number for Bayes classifiers, then use that instead.

146. There's no way to unlearn a document or forget a tree from Meeplang yet (C# has `BayesTree.Forget`). Is either wanted, for example `Learn … forget=true` to correct a mistake, or a `Forget "tree"` verb?

     **Answer:** A `Forget` verb.

147. Classify picks the best class, as you described. Should it also take `against="Ham"` (or a list) to score some classes only against others, as the original's `Bayes` did for spam filtering?

     **Answer:** Yes, let's make that an optional parameter.


## After your answers

- **Q142:** Learn saves after every 5 documents, and when the pipeline finishes, to `~/.meep/trees/<pipeline>_<name>.json`. `save-every` changes the count (0 never saves), and `file` changes the path; Classify and Forget take `file` too, to find a tree saved elsewhere. A save writes a new file and moves it into place, so a crash can't leave half a tree. Trees are saved as JSON: each class's document count and word counts.
- **Q143:** Trees belong to their pipeline unless the name starts with `shared:`. Since a tree is known by its file, the prefix only changes the file's name (`shared_topics.json`), so two pipelines that give the same `file` share a tree too. Characters that can't be in a file name, including `/` and `:`, become `_`. One catch: `news` with a tree called `a_b` and `news_a` with one called `b` would both be `news_a_b.json`.
- **Q144:** An unclassified message is a `ClassifiedMessage` with a null `Class`, `IsClassified` false and `Score` 0, rather than the original message passed on. That keeps one type coming out of Classify, so `{msg.Class}` and `Where msg.Score > 0.9` work on everything it emits, and the message's text and parent are still there.
- **Q145:** `min-training` is 10. There's no better standard number for naive Bayes: what's enough depends on how long the documents are and how alike the classes are. 10 is a common rule of thumb, and it's the original's default.
- **Q146:** `Forget "tree"` forgets everything, and `Forget "tree" class="Spam"` one class. Each message it takes makes it forget, so it's meant to sit under a source such as an HTTP request or a message sent in by hand.
- **Q147:** `against` takes class names separated by commas. Each other class is scored against those classes alone, so `Scores` then holds each class's probability against them, and they don't add up to 1.
