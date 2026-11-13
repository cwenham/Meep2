# Step 6: plugins

Written 9 November 2026. This covers what step 6 built, the decisions I made while building it, benchmark numbers, and questions numbered from 100 with answer lines.

## What's built

- **`#plugin` lines** name the plugins a pipeline needs. They come after `defaults` and `const`, and before the modules (Q28):
  - `#plugin github.com/owner/repo 1.2.0 sha256:…`: a GitHub release, pinned by its SHA-256 (Q34).
  - `#plugin nuget:Package.Id 1.2.0`: a NuGet package and its dependencies (Q62). A SHA-256 is optional.
  - `#plugin "path/to/Plugin.dll"`: a plugin on this machine, as a DLL or a folder.
  - Any of them can end with `as Name`, the name its verbs can be reached by (Q105).
- **Install,** a new stage in the bootstrap pipeline, sits between Parse and Build: Read → Parse → Install → Build → Host (goal 8).
  - It downloads each plugin, checks it, and gives the pipeline its own verbs: the core ones plus its plugins'.
  - A plugin that fails is a load error on its `#plugin` line, like any other mistake in the file.
- **Load contexts that unload** (Q35, Q47):
  - Each plugin loads into a context of its own.
  - It shares `Meep2.SDK`, the core modules and .NET's own assemblies with the host, so its messages are ordinary messages. Everything else comes from its own folder, so two plugins can use different versions of a library.
  - Pipelines that use the same plugin share one copy. It unloads once the last of them has finished.
- **A cache** in `~/.meep/plugins`, or under `MEEP_HOME` if that's set. A plugin is downloaded once, and checked again each time it loads.
- **SSH signatures** (Q36, Q101):
  - A plugin carries `meep-files.txt`, listing each of its files with its SHA-256 as `shasum -a 256` does, and `meep-files.txt.sig`, an SSH signature of that list made by `ssh-keygen -Y sign -n meep-plugin`.
  - Authors sign with the SSH keys they have: Ed25519, ECDSA or RSA.
- **Trust** (Q102):
  - A GitHub plugin is trusted because its line pins its SHA-256. It needn't be signed.
  - A NuGet plugin must be signed by a key you trust, and its packages are recorded in a lock file beside the pipeline (Q103).
  - A plugin on this machine must be signed by a key you trust, unless meep is in developer mode.
- **New commands:**
  - `meep keys trust <name> ssh-ed25519 AAAA…`, `meep keys trust <name> <file.pub>`, or `meep keys trust github:<user>` trusts someone's key.
  - `meep keys list` shows the keys you trust.
  - `meep sign <folder or files> [--key <private key>]` signs a plugin, using `ssh-keygen`.
  - `meep run --dev` turns on developer mode, which loads unsigned plugins from this machine, with a warning.
- **A test plugin,** `tests/Meep2.TestPlugin`. It has a verb (`Shout`), a message type (`Shouted`), and a library of its own to depend on. The build makes it as a folder and as NuGet packages, and the tests install it all three ways.
- **Tests:** 314 TUnit tests, 38 of them new. The GitHub tests use a local server, and the NuGet tests a local feed. Signatures are checked against the real `ssh-keygen` both ways: Meep accepts what it signs, and it accepts what Meep signs. The suite passed 8 runs in a row. Breaking the file hash check, the catalog's letting go of a plugin, the Ed25519 check and the lock file on purpose made their tests fail, and the code is restored.

## In Meeplang

```
const Stop = "Hove Train Station"

#plugin github.com/cwenham/MeepTransit 1.2.0 sha256:3f9a… as Transit
#plugin nuget:Meep.Weather 2.0.1
#plugin "../MyPlugin/bin/Debug/net11.0/MyPlugin.dll"

Interval 1m
    Transit.Departures Stop
        Where msg is Departure and msg.minutesAway < 30
            WriteLine "{msg.route} in {msg.minutesAway} min"
```

Making and sharing a plugin:

```
dotnet publish -c Release -o publish
meep sign publish --key ~/.ssh/id_ed25519            # writes publish/meep-files.txt and its .sig, with ssh-keygen
cd publish && zip -r ../MeepTransit-1.2.0.zip .      # attach to the v1.2.0 release
shasum -a 256 ../MeepTransit-1.2.0.zip               # for the #plugin line
```

Someone using it from GitHub needs nothing more: the SHA on the `#plugin` line is what they trust. For a NuGet plugin, or one shared as a folder, they run `meep keys trust github:alice` once, if the key is on Alice's GitHub account, or `meep keys trust alice ssh-ed25519 AAAA…` with the key `meep sign` printed.

## Decisions I made while building

1. **Plugins have a project of their own,** `Meep2.Plugins`.
   - It isn't AOT-compatible, because a NativeAOT program can't load assemblies (Q5).
   - It uses NuGet's own client libraries, `NuGet.Protocol` 7.9.0.
   - That package asks for a version of `System.Security.Cryptography.Pkcs` with known vulnerabilities, so the project pins the .NET 11 version instead.
   - Meeplang only knows the `#plugin` syntax. A parsed pipeline can carry its own verbs, and holds that keep its plugins loaded.
2. **`#plugin` lines have a grammar of their own:** words, not Meeplang expressions. A quoted path is a plugin on this machine, and a relative path starts from the pipeline file's folder.
3. **The SHA is required for GitHub, but optional for NuGet.** A GitHub release's assets can be deleted and uploaded again, but a version on nuget.org can't be changed once it's published. The SHA is also what makes a GitHub plugin trusted (Q102).
4. **GitHub releases** follow a convention (Q104):
   - The plugin is the asset `{repo}-{version}.zip`, in the release tagged `v{version}`, or `{version}`.
   - It holds the output of `dotnet publish`, whose `.deps.json` says which assembly is the plugin's own.
   - Meep downloads it with a plain URL, not GitHub's API, so there's no rate limit.
5. **NuGet:**
   - **Choosing versions:** each dependency gets the lowest version its range allows, as NuGet does. When two packages ask for the same one, the higher wins.
   - **Packages the host provides are left out:** `Meep2.SDK`, the core modules, and .NET's own assemblies, whose package IDs match their assembly names.
   - **Each package's files for .NET 11** go into one folder, and the cache records each package's SHA-256.
   - **NuGet's own package signatures aren't checked.** The lock file does that job instead (Q103).
6. **Signatures are SSH signatures** (Q101; see "After your answers"):
   - The signature lists the plugin's files, not the archive they came in, so one signature works for a zip, a NuGet package and a folder.
   - **For a GitHub zip or a folder,** every file must be listed, apart from hidden ones such as macOS's `.DS_Store`. An extra DLL slipped in is an error.
   - **For a NuGet package,** the list and signature go in `lib/net11.0` beside the assembly, and cover what's there. The package's dependencies aren't covered: the lock file is for them.
7. **Trust:**
   - The keys you trust are listed in `~/.meep/allowed_signers`, in the form `ssh-keygen -Y verify` reads, so you can check a plugin with OpenSSH's own tools too.
   - A key is matched by the key itself. Its name is only a label, so a key can't pass as another by taking its name.
8. **Developer mode is for plugins on this machine** (Q36, Q102):
   - With `--dev`, an unsigned plugin, or one whose signature fails, loads with a warning, as when you're rebuilding it.
   - NuGet plugins still need signatures with `--dev`, and GitHub plugins their SHAs.
9. **Sharing and unloading** (Q35):
   - The process keeps a catalog of loaded plugins. A pipeline holds each plugin it uses until it finishes, or until it fails to load.
   - Two pipelines using the same plugin load it once.
   - When the last hold goes, Meep asks the plugin's context to unload. .NET finishes the job once nothing refers to the plugin's types.
   - **A fix for unloading:** Meeplang's cache of member lookups held on to every type it had seen, which would have kept plugins loaded forever. It no longer keeps types alive.
10. **Installs are safe to interrupt:**
    - A download is checked in a staging folder, and only moved into the cache once it has passed.
    - A zip entry whose path would land outside its folder is refused.
11. **Names** (Q105): a plugin's verbs and message types join the core ones, and can also be reached by the plugin's name. A plugin can add an inflection to a core verb, such as `Fetch.SFTP`. See "After your answers" for what happens when names clash.
12. **New in Meeplang:**
    - `PluginDirective` (with `Alias`), `PipelineSyntax.Plugins`, `ModuleSyntax.Plugin`
    - `ParsedPipeline.Registry`, `Holds` and `Warnings`; `BuiltPipeline.Holds`
    - `ModuleRegistry.Clone()`, `AddPlugin`, `WhyNotPluginName`, `SharedBy`, `TryGetPluginVerb`
13. **New in Meep2.Plugins:**
    - Installing: `PluginOptions`, `PluginInstaller`, `InstalledPlugin`, `PluginLock`
    - Loading: `PluginCatalog`, `PluginHold`, `LoadedPlugin`
    - Signing: `PublicKey`, `TrustedKeys`, `SshSignature`, `PluginSignature`
    - The bootstrap module `Install`
14. **New in the SDK and runtime:** `ModuleContext.Answered` and `ErrorMessage.AnsweredBy` (Q100).

## Benchmark numbers

Measured with BenchmarkDotNet's short run. This Mac was slower today than in step 2, so I ran the chain benchmarks on step 5's commit as well, back to back with step 6. They came out the same, within the noise of a short run: one module took 1.24 µs on step 5's commit and 1.18 µs now.

| What | Time | Allocated |
|---|---:|---:|
| Source → Shout → sink, Shout loaded as a plugin | 1.35 µs a message | 253 B |
| The same, with Shout's assembly referenced as usual | 1.54 µs a message | 261 B |
| Checking a cached plugin's SSH signature (Ed25519, two assemblies) | 0.64 ms | 25.0 KB |
| Checking it, loading it into its own context, and letting it go | 0.91 ms | 27.0 KB |

What they show:
- **A module from a plugin runs as fast as a built-in one.** A plugin's code is compiled by the same JIT, and the difference is within the noise.
- **Starting a pipeline's plugin takes about a millisecond** once it's in the cache. Most of that is checking its signature: hashing its files and verifying the signature. It happens once, when the pipeline loads. With Meep's own ECDSA signatures it took 0.55 ms; Ed25519 through BouncyCastle is about the same, within the noise of a busy machine.
- **Against nuget.org,** a real package downloaded and resolved, and was then refused for having no signature, as it should be. A missing GitHub release gives "Couldn't find Meep-9.9.9.zip in the v9.9.9 release of github.com/cwenham/Meep."

## Questions

100. Since step 5, an error that reaches Respond is answered there, and counts as handled, so the error policy doesn't log it (Q98). A server whose clients send bad JSON would answer them quietly. Which would you like?
     - **(a)** Keep it: answered and not logged. Authors who want a log add a branch with `WriteLine`.
     - **(b)** Logged, but not counted towards `stop-pipeline`, so bad requests can't stop a server.
     - **(c)** Left unhandled, so the error policy logs it and counts it.

     **Answer:** b.

101. Signatures are Meep's own format, with ECDSA P-256 keys. The alternatives:
     - **SSH signatures** (`ssh-keygen -Y sign`) would let authors use the SSH keys GitHub already knows. Most of those are Ed25519, which .NET doesn't have, so Meep would need another library.
     - **Sigstore** signs with a GitHub login instead of a key, and logs every signature publicly. It's more to build, and it needs the network to check.
     - **NuGet's own signing** needs a code-signing certificate, which costs money.

     Keep Meep's own?

     **Answer:** SSH signatures. I'll accept the extra library.

102. Trust works like this now:
     - Plugins from GitHub and NuGet must be signed by a key you trust, even with `--dev`.
     - A plugin on this machine must be signed too, unless `--dev` is on.

     Is that the policy you want? One alternative: a GitHub plugin pinned by its SHA in a pipeline file you trust counts as trusted, signed or not. Then plugins whose authors don't sign could still be used, by pinning them.

     **Answer:** The alternative; GitHub plugins pinned by SHA.

103. A NuGet plugin's signature covers its own package, not its dependencies. Those are trusted because they come from the same feed, and nuget.org never changes a published version. Should Meep also do either of these?
     - Check NuGet's own signatures. nuget.org signs every package it serves.
     - Write the versions and SHAs it picked to a lock file beside the pipeline, and refuse anything different next time.

     **Answer:** Write the versions and SHAs it picked to a lock file beside the pipeline, refuse anything different.

104. A GitHub plugin is the asset `{repo}-{version}.zip` in the release tagged `v{version}`. Is that convention all right, or should a `#plugin` line be able to name the asset, for repositories with several plugins or other naming?

     **Answer:** That convention is fine. One plugin per repo.

105. A plugin's verbs share one namespace with the core verbs and other plugins' verbs, so two plugins with a `Send` verb would clash. Should plugin verbs be reachable by the plugin's name too, as in `Transit.Departures`, or would you rather keep one namespace and have clashes be load errors, as now?

     **Answer:** Plugin version can be reachable by name, e.g.: 'Transit.Departures'.

106. Meep keeps its plugin cache, your keys and the keys you trust in `~/.meep`, or `MEEP_HOME`. Is that all right, or would you prefer each system's usual folders: `~/Library/Application Support` on macOS, `%LOCALAPPDATA%` on Windows, and the XDG folders on Linux?

     **Answer:** Use MEEP_HOME or ~/.meep.

107. Unloadable contexts make it possible to reload a plugin while a pipeline runs, for instance when you rebuild one in developer mode. That needs the host to swap modules in a running pipeline, which is step 7's and step 8's ground. Should reloading wait for those steps, or do you want it sooner?

     **Answer:** It can wait for Step 7 and 8.

## After your answers

- **Q100, errors Respond answers (b):**
  - Modules have a new call, `context.Answered(error)`, and Respond makes it after it has answered an error. Taking the error then doesn't count as handling it.
  - If nothing after Respond handles the error, the error policy logs it, as `error from Extract #2, answered by Respond #3: …`, but doesn't count it towards `stop-pipeline`. A test sends 30 bad requests, and the server keeps running.
  - If a module after Respond does handle it, such as a `WriteLine` branch, it isn't logged, since the author has dealt with it.
  - `ErrorMessage.AnsweredBy` says which module answered, for anyone writing their own log.
- **Q101, SSH signatures:**
  - A plugin carries `meep-files.txt`, listing its files and their SHA-256s in `shasum -a 256`'s form, and `meep-files.txt.sig`, an SSH signature of the list in the namespace `meep-plugin`.
  - **Signing:** `meep sign <folder> --key ~/.ssh/id_ed25519` writes the list and runs `ssh-keygen -Y sign`, so passphrases, ssh-agent and hardware-backed keys all work as they do for SSH. Without `--key`, it uses the first of `~/.ssh/id_ed25519`, `id_ecdsa` and `id_rsa`.
  - **Checking** is Meep's own code, so running a pipeline doesn't need OpenSSH. It reads the SSHSIG format and checks Ed25519 (with BouncyCastle 2.7.0, the extra library), ECDSA on P-256, P-384 and P-521, and RSA of 2048 bits or more (with .NET). Security keys (`sk-…`) aren't supported yet.
  - **Trusting:** `meep keys trust` takes a key as `name ssh-ed25519 AAAA…`, as a `.pub` file, or as `github:<user>`, which trusts the keys on that GitHub account, read from `github.com/<user>.keys`. `meep keys list` shows them with their fingerprints, as `ssh-keygen -l` would.
  - The trusted keys are in `~/.meep/allowed_signers`, in OpenSSH's own form. A key restricted there to other namespaces, such as `namespaces="git"`, isn't trusted for plugins.
  - `meep keys new` is gone, since people use `ssh-keygen` to make keys. Meep2.Plugins keeps a key pair class for tests, which signs without OpenSSH.
  - Tests check Meep against `ssh-keygen` both ways: Meep accepts plugins signed by `ssh-keygen` with Ed25519, ECDSA (256, 384, 521) and RSA keys, and `ssh-keygen -Y verify` accepts plugins Meep signed.
- **Q102, GitHub plugins pinned by SHA:**
  - A GitHub plugin loads on the strength of the SHA its line pins, signed or not.
  - If it's signed by a key you trust, that's recorded. If its signature is there but doesn't hold, it still loads, with a warning, so the author can be told.
  - A cached copy used to be checked against its signature each time it loaded. Now the cache records each file's SHA-256 when it's installed and checks them each time. If anything changed, the plugin is downloaded again, and the SHA pin checks the new copy.
- **Q103, a lock file for NuGet:**
  - The first time a pipeline installs a NuGet plugin, its packages are written to a lock file beside the pipeline, `name.meep.lock`: each package's ID, version and SHA-256.
  - After that, a package that's different, missing or new is refused with what changed, as in `Meep2.TestPlugin.Words 1.0.0 sha256:3f9a… is now … sha256:b71c…`. To accept a change, delete the plugin's lines from the lock file.
  - A pipeline that isn't read from a file has no lock file.
- **Q104:** kept as built: `{repo}-{version}.zip`, one plugin per repository.
- **Q105, plugin names:**
  - A plugin's verbs can be written as `Plugin.Verb` or `Plugin.Verb.Inflection`, and its message types as `is Plugin.Type`.
  - A plugin's name is the last part of its assembly's name, so `Meep.Transit.dll` is `Transit`, unless its `#plugin` line ends with `as Name`.
  - A plugin's name can't be a verb's, or `Fetch.HTTP` could mean two things. Two plugins can't share a name either.
  - **Clashes:** where a plugin uses a name a core module has, the core one keeps the short name, and the plugin's is reached by its plugin's name. Where two plugins use one name, the short name is an error that says which to write, as in `Shout is in both One and Two, so write One.Shout or Two.Shout.`
  - Plugins' message types used to be able to replace each other's, or a core one, silently. Now they can't.
- **Q106:** kept as built: `MEEP_HOME`, or `~/.meep`.
- **Q107:** reloading waits for steps 7 and 8.
- **Also fixed:** the tests' small HTTP server sometimes failed with "Address already in use" as it shut down. It stopped and then closed .NET's HttpListener, and the second call bound its port again. It now closes once.
