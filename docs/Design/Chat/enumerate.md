# Enumerate

Written 12 November 2026. A new core verb, Enumerate, ported from the original Meep's `Enumerate`, with its library of built-in books and `[LibraryBook]` for plugins to add more. This covers what was built, the decisions I made while building it, and questions numbered from 162 with answer lines.

## What's built

```
Interval 1s count=5
    Enumerate "Countries"
        WriteLine "{msg.Index}: {msg.Text}"
```

- **`Enumerate "Book"`** emits an item from a book for each message it receives, as a `BookItem`: `Book` (the name), `Index` (its place, from 0) and `Text` (the item). It's per message, as the original was, so Interval stays the way to pace it.
- **Without `index`**, it starts at item 0 and moves on one for each message, wrapping round at the end.
- **`index=…`** chooses the item, worked out for each message, as in `index=msg.Count`. It wraps both ways: 12 in a book of 5 is item 2, and -1 is the last item.
- **The built-in books**: Lorem Ipsum (6 paragraphs), Common Passwords (1,000), Common Last Names (1,000), Countries (196), US States (56), US Abbreviations (56), Reindeer (8) and Encodings (whatever .NET knows on the machine).
- **Plugins add books** with `[LibraryBook("Name")]` on a public static property or field that's a list of strings. A plugin's books join the library under their own name, and as `"Plugin.Book"`. Where two plugins have a book of the same name, only `"Plugin.Book"` reaches it. Where a plugin's book has the name of a built-in one, the built-in one keeps it.
- **A misspelt book is reported when the pipeline loads**, with a suggestion: `There's no book called Countrys in the library. Did you mean 'Countries'?`
- **Errors pass through untouched.**
- **SecLists is credited** for the passwords, with its MIT License, in [src/Meep2.Modules/Library/NOTICE.md](../../../src/Meep2.Modules/Library/NOTICE.md).

The verb and the `Library` class are in [src/Meep2.Modules/Enumerate.cs](../../../src/Meep2.Modules/Enumerate.cs), the books in [src/Meep2.Modules/Library/](../../../src/Meep2.Modules/Library/), and the tests in [tests/Meep2.Tests/Meeplang/EnumerateTests.cs](../../../tests/Meep2.Tests/Meeplang/EnumerateTests.cs), plus two in `PluginTests`.

## Decisions I made while building

- **Books are found by the registry, not by scanning the whole process.** The original searched every loaded assembly for `[LibraryBook]`. Here `ModuleRegistry` collects them from the assemblies it registers, as it does verbs. So a pipeline only sees the books of the plugins it loads, and `Meep2.Modules` stays AOT-compatible. `ModuleRegistry.AddBook` adds one by hand.
- **The loader finds the book.** Enumerate's parameter is a new SDK type, `Book`, which the parameter binder fills in from the name, much as `Tap` gets a `ModuleReference`. So the name has to be the same for every message, and an empty book is reported at load time too.
- **Books are read when a pipeline first names one**, not when Meep starts. Each built-in book is an embedded `.txt` resource, one item to a line, cached after its first read.
- **A book is an `IReadOnlyList<string>`** rather than the original's `List<string>`, so a plugin can't change one under a running pipeline. A property or a field will do.
- **Enumerate emits a `BookItem`**, not plain text, so the book and index go with the item. It has `IHasText`, so WriteLine, Learn and anything else that takes text treat it as its item.
- **No templates in items.** As you asked, items are plain text; `{…}` in one is left as it is.
- **The data was tidied:**
  - Swaziland is now Eswatini and Macedonia is now North Macedonia, each moved to its alphabetical place.
  - "District Of Columbia" is now "District of Columbia".
  - US Abbreviations used to be sorted by abbreviation (AK, AL, AZ…) and had only the 50 states. It now follows US States' order and adds DC and the five territories, so the same index gives a state and its abbreviation.

## Questions

162. Without `index`, Enumerate keeps its own count, so each Enumerate steps through its book on its own. The original used `{msg.Number}` by default. Is a count of its own right, or should it default to something on the message, such as a Tick's `Count`?

     **Answer:** It should have its own, which should be the index position of the underlying source. The original number from Tick would still be available in `fam`.

163. `index` counts from 0, but a Tick's `Count` counts from 1, so `index=msg.Count` skips the first item until it wraps round. Should `index` count from 1 to match, or stay at 0, like the original, and the item's `Index`?

     **Answer:** Enumerate's index should begin at 0. Can Interval have an optional `count_start=` parameter to begin at 0 or other number, too?

164. Sometimes one message should bring out a whole book, such as one Tick running through every common password. Would you like an option for that, such as `all=true`, that emits every item for each message?

     **Answer:** Yes.

165. The original let a pipeline list its own items in `<Text>` elements. Should Meeplang have a way to do that, such as `items=["a", "b", "c"]` on Enumerate, or a book declared once at the top of the file for several modules to share? Or is `Read` with a file enough?

     **Answer:** `Read` is enough for now.

166. Book names are matched exactly, case included, as verbs are, though a near miss gets a suggestion. Should they be matched ignoring case, since they're written as text and some have spaces ("Common Passwords")?

     **Answer:** Ignore case.

167. The Countries book is the original's 196, with the two renames above. Would you rather it was the ISO 3166 list (249, including territories, with their two-letter codes as a book of their own in the same order), or kept as it is?

     **Answer:** The original list of politically recognised countries (recognised by the UK and EU). The ISO 3166 list and 2-letter codes is also a good idea as a separate book.

168. Should the language server offer the library's book names as completions after `Enumerate "`, as it does for verbs?

     **Answer:** Yes.

## After your answers

- **Q162:** Unchanged: without `index`, Enumerate keeps its own place in the book. The message that asked for each item is its parent, so a Tick's `Count` is still there as `fam`'s, and a test covers that.
- **Q163:** Enumerate's index still counts from 0. Interval now takes `count-start`, as in `Interval 1s count-start=0`, which numbers its ticks from there. It doesn't change how many ticks `count` emits: `count=3 count-start=0` gives 0, 1 and 2. Parameter names ignore hyphens and underscores, so `count_start` works too, though Meeplang writes them with hyphens elsewhere. A count start changed in a live edit numbers the ticks after it from the new start.
- **Q164:** `all=true` brings out the whole book, in order, for each message. Giving `index` as well is reported when the pipeline loads. Emitting never waits, so a book larger than the next module's buffer (1024 unless set) would lose its first items. All the built-in books fit; Q169 asks about larger ones.
- **Q165:** No change; `Read` covers lists of your own.
- **Q166:** Book names are matched ignoring case, plugins' names included. The `BookItem`'s `Book` is the name as the library spells it, so `Enumerate "common passwords"` gives items from "Common Passwords". Two books in one assembly whose names differ only in case are refused.
- **Q167:** Countries stays the original's 196, with the two renames. Two new books follow ISO 3166-1: **ISO 3166 Countries** (249, by ISO's English short names, such as "Korea, Republic of", in ISO's order) and **ISO 3166 Codes** (their two-letter codes in the same order). I took the list from Wikipedia's copy of the ISO table and checked the codes against .NET's region data. All but EH (Western Sahara) were found, and EH is a real ISO code that .NET's data leaves out. Q170 asks about two entries in Countries.
- **Q168:** The language server offers the library's books after the opening quote of any verb whose value is a book, Enumerate's included, and `"` now triggers completion. Each suggestion replaces everything typed since the quote, so names with spaces complete properly.

## More questions

169. `all=true` emits the whole book at once, and emitting never waits, so a plugin's book of more than 1,024 items would lose its first items unless `buffer` is raised. Should Enumerate warn when the pipeline loads if the book is larger than the buffer of the module after it, or emit in steps of one buffer at a time, between messages, as Batch flushes?

     **Answer:** Warn on load.

170. You asked for countries recognised by the UK and the EU. The original list has Taiwan, which neither the UK nor any EU state formally recognises. It doesn't have Palestine, which the UK recognised in September 2025, as do most EU states. Should Taiwan come out and Palestine go in, or should the list stay as the original had it?

     **Answer:** The list should have both Palestine and Taiwan.

## After your second answers

- **Q169:** The loader warns when a module can emit more messages at once than a module after it holds, as in `Enumerate can emit 1,000 messages at once, but WriteLine only holds 10, so the earliest would be dropped. Give WriteLine @buffer=1000, or raise buffer under defaults.` It looks past passthrough modules, since they pass messages straight on, and takes each module's `@buffer`, or the pipeline's `buffer` under `defaults`. It's a hook any module can use: an inflection implements `IEmitsAtOnce<TParameters>` from the SDK, and says from its parameters how many it can emit at once. Enumerate says the book's size when `all=true`, and nothing otherwise.
- **Q170:** Countries now has Palestine as well as Taiwan, 197 in all. While adding it I found that Eswatini and North Macedonia hadn't gone to their alphabetical places, because "The Bahamas" and "The Gambia" are listed under B and G and threw the search off. All three are now where they belong.
