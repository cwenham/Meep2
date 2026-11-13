# Step 8: write-back

Written 9 November 2026. This covers what step 8 built, the decisions I made while building it, benchmark numbers, and questions numbered from 118 with answer lines. Q118 and Q119 come from applying step 7's answers.

## What's built

- **Changing a running pipeline** (goal 4): `Pipeline.ApplyAsync` changes a running pipeline into a new set of modules and connections, without stopping it.
  - Modules it keeps carry on with their buffers, counts, taps and state, even if they've moved, been renamed or renumbered.
  - New modules start, and modules left out stop taking messages, finish the ones in their buffers, and are disposed.
  - A module whose parameters changed is changed between two messages, never in the middle of one.
- **Keeping state when parameters change** (Q31, Q55): a module can implement `IReconfigurable<TParameters>`, whose `ParametersChanged(old, next)` returns true if it took the new parameters itself. A module that doesn't is replaced by a new instance made with them.
- **Which module is which** (Q30, Q61): when a pipeline's text changes, its modules are matched by name, then by structure (the same verb under the same module, with the most similar parameters), then by position. There are no IDs in the file.
  - Inserting a module mid-chain keeps every module after it, with its state. A test checks that a counter under the insertion carries on counting.
  - Position and structure take turns, so once a moved module is matched by position, the modules under it match by structure.
  - A module whose verb changed is a new module.
- **Live pipelines:** `LivePipeline` holds a running pipeline with the text it runs from. Give it new text and it loads it, matches its modules, and applies the change. Text that doesn't load leaves the pipeline as it was, and says why. A restart after a failure (Q112) rebuilds from the text as edited, not as first loaded.
- **Edits that keep comments:** `SourceEditor` changes a file's text the way a person would. Each edit touches only the text it has to, so comments, blank lines and layout stay as they were.
  - The edits: set or remove a parameter or the main value, set or remove a modifier, rename, add a branch, insert a module mid-chain, remove a module, and remove a branch.
  - A parameter goes where the module keeps its others: on its line, or on a line of its own if it uses parameter lines.
  - Inserting a module moves the ones under it in a level, comments included; removing one moves them out. New lines follow the file's own indentation, and its line endings.
  - A comment later on the line stays in its column where there's room, so comments lined up stay lined up.
  - Modules are named by their name, their number, their label as messages show it (`Where #3`), or a verb only one module has.
- **Commits to `pending`** (Q32): `Meep2.Git`, a new project using LibGit2Sharp 0.32.0, commits a pipeline's new text to `pending`, or the branch an edit names.
  - It doesn't touch the working tree or the branch that's checked out.
  - The commit goes on top of `pending`, or starts it from the commit that's checked out. Your repository's `user.name` and `user.email` sign it.
  - It refuses to commit to the branch that's checked out, since that would leave the working tree behind.
- **Reloading and watching:** `meep reload` changes a running pipeline to match its file. `meep start --watch` and `meep run --watch` do it whenever the file changes, once the file has settled for a quarter of a second. A file that doesn't load is reported, and the pipeline runs on as it was.
- **Reloading plugins** (Q107): a reload installs the pipeline's plugins again. A plugin on this machine that has been rebuilt loads afresh, and its modules are replaced by ones made from the new build. Plugins that haven't changed stay loaded, and their modules carry on. The old build unloads once nothing uses it.
- **In the daemon:** `GET /pipelines/{name}/source`, `POST /pipelines/{name}/reload` and `POST /pipelines/{name}/edits`, over HTTP or the WebSocket. Edits need the control capability.
- **New commands:** `meep reload`, `meep source`, `meep edit`, and `--watch` on `meep start` and `meep run`.
- **Tests:** 401 TUnit tests, 40 of them new. The suite passed 8 runs in a row.
  - I broke three things on purpose, and their tests failed: ignoring the hook, matching without structure, and changing a module mid-message. The code is restored.
  - The Git tests make real repositories with the `git` command.

## Using it

```
cd ~/pipelines                                   # a Git repository, on main
meep start --watch ticker.meep
meep edit ticker Interval set count=1000
Edited ticker:
  made again: Interval #1
Committed to pending as 50ff5a6, in /Users/you/pipelines/.

meep edit ticker Interval insert 'Where msg.Count % 2 == 0'
Edited ticker:
  added: Where #2
Committed to pending as b89149f, in /Users/you/pipelines/.

meep source ticker
// A ticker to edit while it runs.
Interval 500ms count=1000   // twice a second
    Where msg.Count % 2 == 0
        WriteLine "tick {msg.Count}"
```

`git log pending` shows the two commits on top of `main`, and `git status` is clean. Editing `ticker.meep` itself then reloads the pipeline from the file, which wins over the edits until you merge `pending` (Q32, Q120). I tried all of this by hand as well as in the tests.

`meep edit`'s edits are `set <name>=<value>`, `set <value>`, `set @<modifier>[=<value>]`, `unset [<name> | @<modifier>]`, `rename [<name>]`, `add <line>`, `insert <line>`, `remove` and `remove-branch`. `--no-save` skips the commit, and `--branch` and `--message` change it.

## Decisions I made while building

1. **Changes wait for a module to finish its message.** The module's own loop applies a change between two messages, or the change is applied directly while the module waits for messages. The loop pays for this with one check of a field per message, and one compare-and-swap each time it wakes from waiting.
2. **A parameter has changed when its text has.** Parameters include compiled expressions, which can't be compared, so the loader compares each module's text instead: its verb, main value and named parameters. Constants count as changed for every module, and so do the classes a module is made from, which differ after a plugin is rebuilt. Modifiers and names don't count, since they apply to a module without remaking it.
3. **A module without the hook is replaced.** Q31's answer had the runtime write new values to each property, but Q55 made parameters immutable records, so there's nothing to write to. The new instance starts with fresh state, and the old one finishes its message and is disposed.
4. **A source is asked while it runs.** Sources don't handle messages one at a time, so there's no gap between them. A source with the hook is asked while it runs, and one without it is stopped and started again with the new parameters (Q122).
5. **A module whose inflections or buffer size changed is replaced whole,** with fresh counts, since its buffer and routing are fixed when it's made.
6. **Messages in flight:** a message passing between two modules at the instant their connection changes may be abandoned. Messages in a removed module's buffer are handled and sent on to where they were going. A test makes 20 changes while messages flow at one a millisecond, and checks that none went missing.
7. **Edits are patches to the text, not a rewritten file.** The parse tree has every value's place in the text, so an edit replaces just those characters, and a file's comments never pass through a syntax tree that might drop them.
8. **An edit outside a Git repository still changes the running pipeline,** and says it couldn't be saved (Q124).
9. **The commit message** says what changed: "Edit ticker while it runs", then a line for each edit. An edit can give its own.
10. **Reloading after edits:** the file wins, as Q32 says, so a reload, or a change to a watched file, undoes edits made through the API until `pending` is merged (Q120).
11. **A rebuilt plugin is recognised by its main assembly's time,** which the plugin catalog now keys plugins on this machine by. A plugin from GitHub or NuGet is keyed by its version, as before.
12. **Watching uses .NET's `FileSystemWatcher`.** On macOS it can miss a change made in its first few moments, so the tests write their change again until it's seen.
13. **New projects and types:** `Meep2.Git`. In the runtime: `PipelineChange`, `PipelineChangeReport`, `SourceSetup`, and `Parameters` and `Reconfigure` on `VerbInflection`. In the SDK: `IReconfigurable<TParameters>`. In Meeplang: `LivePipeline`, `ModuleMatcher`, `SourceEditor`, `SourceEdit` and `BuiltPipeline.Live`. In the daemon: `Reloading`, `PipelineWatcher`, and the edit and source contracts.
14. **Found along the way:**
    - **Listen** started its server before registering its first path, so a request that arrived at once got a 404. It now registers the path first.
    - **Three tests had races of their own.** The plugin lab gave each WriteLine its own lock on one shared writer, so two branches writing at once could lose a line. Two lease tests counted on timing.
    - **Lease holds:** a lease's maker only holds it until its first emit (step 5), so a source that emits several messages with one lease must `Hold()` it while it does. One test didn't, and failed when the first message finished before the next went out (Q123).

## Benchmark numbers

Measured with BenchmarkDotNet. The chain benchmarks used its medium run, on step 7's commit and on step 8 in turn, twice each; the table gives the mean of the two rounds.

| Chain, per message | Step 7 | Step 8 |
|---|---:|---:|
| 1 step, passing messages on | 1.12 µs, 100 B | 1.03 µs, 99 B |
| 1 step, a new message each step | 1.34 µs, 192 B | 1.38 µs, 193 B |
| 4 steps, passing messages on | 1.53 µs, 104 B | 1.47 µs, 103 B |
| 4 steps, a new message each step | 2.17 µs, 459 B | 2.13 µs, 460 B |

What changing a running pipeline costs, for a pipeline of 20 modules (a source, four branches of four modules, and three more):

| Work | Time | Allocated |
|---|---:|---:|
| Applying text with one parameter changed: load, match and change | 10.0 ms | 866 KB |
| Of which loading the text | 8.8 ms | 614 KB |
| Matching the modules of two versions | 0.11 ms | 217 KB |
| Making one edit to the text, and parsing the result | 0.025 ms | 45 KB |

What they show:

- **Being changeable costs nothing measurable.** Step 8 is as fast as step 7, within about 5% either way, with the same allocations. A short run first showed one case 15% slower; the medium runs show that was noise.
- **A change costs about 10 milliseconds,** nearly all of it loading the new text, which compiles each expression. Matching and applying take about a millisecond.
- **An edit to the text costs 25 microseconds,** so a UI could preview every keystroke.

## Questions

118. (From Q111.) A pipeline someone stops with `meep stop` leaves the list at once, since whoever stopped it knows why. Only pipelines that end on their own stay listed for `keep-finished`. Is that right, or should stopped ones stay listed too?

     **Answer:** That's correct.

119. (From Q112.) The daemon's settings file is JSON, which may have comments, at `~/.meep/daemon/config.json`, with `start`, `otlp` and `sample`. Is JSON right, or would you rather it were Meeplang, or something else?

     **Answer:** Prefer Meeplang.

120. A pipeline edited through the API, and also watched, loses its edits when its file changes, because the file wins (Q32). Should a watched pipeline refuse edits through the API, warn before a reload undoes them, or carry them over by applying `pending`'s changes to the file's new text?

     **Answer:** Warn before a reload undoes them.

121. No core module implements `IReconfigurable` yet, so changing a parameter of any of them makes a new instance. Which should keep their state first? Interval's period and Listen's paths seem the most useful, since remaking them restarts a count or reopens a port.

     **Answer:** Interval and Listen are good starting points.

122. A source with the hook is asked while it runs, so its author has to make the change safe alongside its loop. The alternative is to always restart a source with its new parameters. Which do you prefer?

     **Answer:** Author needs to make the change safe.

123. A lease's maker holds it only until its first emit (step 5), so a module that emits several messages with one lease has to `Hold()` it meanwhile, or the lease can close between them. Should the maker's hold last until the module has finished handling the message it's working on, so several emits need no `Hold()`? A source would still need one, since it runs for as long as it likes.

     **Answer:** The maker's hold should last until the module has finished handling the message.

124. An edit to a pipeline whose file isn't in a Git repository changes the running pipeline but isn't saved, and says so. Should it write the file instead, or refuse the edit?

     **Answer:** Write to the file.

125. Edits only reach modules. Should there be edits for the `defaults` block, constants and `#plugin` lines too?

     **Answer:** Yes.

126. Write-back commits are signed by your repository's `user.name` and `user.email`, or "Meep" if it has none, and aren't signed cryptographically. Should they be marked as Meep's, say with a `Co-Authored-By` line, or signed with your SSH key as plugins are?

     **Answer:** Signed with the user's SSH key.

127. A rebuilt plugin is only picked up when its pipeline reloads: by `meep reload`, or by a change to a watched pipeline file. Should `--watch` also watch the plugins on this machine that a pipeline uses, so rebuilding one reloads it?

     **Answer:** No, it should require a reload or saved change to the file. Plugins should at least bump their version.

## After your answers

Applied on 9 November 2026. 410 tests pass, 9 of them new, and the suite passed 8 runs in a row. Breaking the lease hold and the warning before a reload on purpose made their tests fail, and the code is restored.

- **Q118:** kept as built.
- **Q119, the daemon's settings in Meeplang:** the file is now `~/.meep/daemon/daemon.meep`, or the one `--config` names:

  ```
  // What the daemon starts, and where it reports.
  defaults
      otlp = "http://localhost:4318"
      sample = 0.25
  const Pipelines = "~/pipelines"
  Start "{Pipelines}/ticker.meep" watch=true
  Start "feed.meep" dev=true
  ```

  - It's read with Meeplang's own parser, so comments, constants and templates work, and mistakes are reported with their line and column. Nothing in it runs.
  - `MeeplangSettings` reads such files, for whatever else wants settings in Meeplang later.
  - The JSON file is gone.
- **Q120, warning before a reload undoes edits:** `meep reload`, and a change to a watched file, refuse when they would undo edits made through `meep edit` that aren't in the file yet:

  ```
  Reloading greet from its file would undo edits made through meep edit that aren't in the file:
  set the constant Greeting to "hello". They're committed as 2d28f04; merge them into your branch
  to keep them. To reload anyway: meep reload greet --force
  ```

  - Once the commits are merged into the branch that's checked out, the reload goes ahead by itself.
  - A watched pipeline logs the warning and runs on as it was.
- **Q121, Interval and Listen:**
  - Interval takes a new period, count and payload while it runs, and carries on counting.
  - Listen over HTTP takes a new path on the same server, and lets the old one go once its open requests are answered, so the port never closes. Its other settings apply to new requests.
  - Listen over TCP takes new framing and settings for new connections.
  - A new host, port, scheme or certificate still means a new Listen.
- **Q122:** kept: a source is asked while it runs, so its author makes the change safe. Interval and Listen do.
- **Q123, lease holds:** a module now holds a lease it made until it has finished handling its message, so it can emit several messages with one lease and no `Hold()`. A source still lets go at the first emit, so one that emits several needs a `Hold()`. A test emits twice with a pause between, twenty times over.
- **Q124:** an edit to a file that isn't in a Git repository is written to the file. A reload then keeps it, and a watched pipeline sees its own write as no change.
- **Q125, editing the rest of the file:** new edits set and remove defaults and constants, and add and remove `#plugin` lines.
  - A new `defaults` block goes at the top, above any comment that describes the first module. New constants go after the last, and new plugins after the settings.
  - A plugin is removed by its `as` name, its address or its file's name.
  - An edit that changes the `#plugin` lines installs plugins before the change, as a reload does.
  - On the command line: `meep edit greet defaults set expires=5s`, `meep edit greet const set Greeting='"hello"'`, `meep edit greet plugin add nuget:Meep.Weather 1.2.0`, and `unset` and `remove` for each.
- **Q126, signed commits:** write-back's commits are signed with your SSH key, as Git signs them with `gpg.format = ssh`, so `git log --show-signature` checks them.
  - The key is the repository's `user.signingkey` when its `gpg.format` is `ssh`, and otherwise the one `meep sign` uses: the first of `~/.ssh/id_ed25519`, `id_ecdsa` and `id_rsa`. A key with a passphrase has to be in ssh-agent.
  - Without a key, the commit is made unsigned, and `meep edit` says why. The tests give each repository a key of their own and check the signature with `git log --format=%G?`.
  - Your Git config has no signing key, so `meep edit` would use `~/.ssh/id_rsa`.
- **Q127, plugin versions:** a plugin on this machine is now told apart by its version, not its build time.
  - A reload loads a rebuilt plugin afresh only if its version changed.
  - A plugin rebuilt with the same version stays as loaded, and the reload warns: "Shout has been rebuilt since it was loaded, but its version is still 1.0.0, so pipelines keep the build they loaded. Give it a new version to load the new build."
  - The tests build the test plugin a second time as 1.0.1 to check it.
- **Also:** a reload or edit that changes the `defaults` block now says so, where it used to say "nothing changed".

**Tried by hand:** a daemon started from `daemon.meep`, with a watched pipeline in a Git repository. Then edits to a constant and a default, both committed to `pending` and signed (`G`). Then a change to the file, which the daemon refused to reload with the warning, and `meep reload`, which refused the same way.
