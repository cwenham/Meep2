# Refactoring, phase 2: file splits

Written 22 November 2026. Phase 2 of the plan in `refactoring.md`: section 9's rule (Q226), as pure moves in one commit, 25cd87b, on the branch `refactor-files`, made from `main` at f6697ae, not merged or pushed. No type, namespace or code body changed. Only `using` lines differ: each new file has just the ones its types need, worked out by the compiler, and files that kept a type lost the ones only the moved types needed. `git diff -M --color-moved main` shows it as moves.

## Results

- 55 files split into 186 (`src/` went from 141 files to 274 counting the new ones; 6 files removed, the rest kept under the name of the class they still hold).
- The build has no warnings, with warnings as errors.
- The whole suite passes: 679 of 679. (Phase 1's doc said 677; the commits after it on `main` added 2.) None of the three flaky tests failed this time.
- `editors/vscode` isn't affected: it talks to `meep lsp` and doesn't refer to any source file.
- The design docs still name some old files (`Send.cs`, `Contracts.cs` and so on) in earlier answers. I left them as written, since they record what was true then.

## What was split

Paths are within each project's folder under `src/`. Types in brackets are the ones a file holds besides the one it's named after.

| Old file | New files |
|---|---|
| `Meep2.Cli/HostSettings.cs` | `HostSettings.cs`; `MeepExecutable.cs` |
| `Meep2.Daemon/Capabilities.cs` | `Capabilities.cs`; `CapabilityNames.cs`; `DaemonTokens.cs` |
| `Meep2.Daemon/Contracts.cs` | `Contracts/Daemon.cs` (DaemonInfo, ListenInfo, SendInfo); `Contracts/Pipelines.cs` (StartRequest, PipelineSummary, PipelineDetail, ModuleDetail, LinkDetail); `Contracts/Changes.cs` (EditItem, EditRequest, SavedCommit, ChangeResult, SourceText); `Contracts/Graph.cs` (PipelineGraph, GraphModule, GraphParameter, GraphModifier, GraphTemplateUse, GraphInclude, GraphLink, GraphSpan, GraphNotice); `Contracts/Replies.cs` (Problem, Injected); `Contracts/DaemonJson.cs`; `Contracts/DaemonException.cs` |
| `Meep2.Daemon/DaemonClient.cs` | `DaemonClient.cs`; `DaemonSocket.cs` |
| `Meep2.Daemon/DaemonService.cs` | `ServicePlan.cs` (ServiceCommand); `DaemonService.cs` |
| `Meep2.Daemon/HostedPipeline.cs` | `HostedPipeline.cs`; `PipelineStates.cs` |
| `Meep2.Daemon/PipelineSources.cs` | `PipelineOrigin.cs`; `PipelineAddress.cs`; `PipelineSources.cs` (PipelineSourceException); `ReadPipeline.cs` |
| `Meep2.Daemon/SocketSession.cs` | `ApiMessages.cs` (ApiRequest, ApiReply, Answer, Events, SubscribeReply, ApiEvent); `SocketSession.cs` |
| `Meep2.Daemon/Subscriptions/Subscription.cs` | `Subscriptions/HostInfo.cs`; `Subscriptions/ISubscriptionHost.cs`; `Subscriptions/Subscription.cs` |
| `Meep2.Daemon/Subscriptions/UpdateWriter.cs` | `Subscriptions/UpdateWriter.cs`; `Subscriptions/JsonUpdateWriter.cs`; `Subscriptions/CborUpdateWriter.cs`; `Subscriptions/Envelope.cs` |
| `Meep2.Git/PendingBranch.cs` | `PendingCommit.cs`; `PendingBranch.cs` (PendingBranchException) |
| `Meep2.Meeplang/Compilation/Binder.cs` | `Compilation/ExpressionCompiler.cs`; `Compilation/ExpressionBinder.cs` |
| `Meep2.Meeplang/Compilation/CompileScope.cs` | `Compilation/CompileScope.cs`; `Compilation/ConstantValue.cs`; `Compilation/FamTarget.cs`; `Compilation/BindException.cs`; `Compilation/Suggestions.cs` |
| `Meep2.Meeplang/Compilation/CompiledComputed.cs` | `Compilation/ICheckedComputed.cs`; `Compilation/CheckedComputed.cs`; `Compilation/CompiledComputed.cs` |
| `Meep2.Meeplang/Compilation/Numbers.cs` | `Compilation/NumberKind.cs`; `Compilation/Numbers.cs` (BeyondDecimalException) |
| `Meep2.Meeplang/Editing/DraftEditor.cs` | `Editing/DraftEdits.cs` (AddedModule); `Editing/RegistryLookup.cs`; `Editing/DraftEditor.cs` (DraftEditException) |
| `Meep2.Meeplang/Editing/ModuleMatcher.cs` | `Editing/MatchedBy.cs`; `Editing/ModuleMatch.cs`; `Editing/ModuleMatcher.cs` |
| `Meep2.Meeplang/Editing/SourceEditor.cs` | `Editing/EditKind.cs`; `Editing/SourceEdit.cs`; `Editing/EditedText.cs`; `Editing/SourceEditor.cs` (EditException) |
| `Meep2.Meeplang/Hosting/HostingModules.cs` | `Hosting/HostingMessages.cs` (ParsedPipeline, BuiltPipeline, PipelineRestarting, PipelineText, MeeplangErrorMessage, PipelineStoppedMessage); `Hosting/Parse.cs`; `Hosting/Build.cs`; `Hosting/Host.cs` |
| `Meep2.Meeplang/Loading/ErrorPolicyParser.cs` | `Loading/ErrorPolicyParts.cs`; `Loading/ErrorPolicyParser.cs` |
| `Meep2.Meeplang/Loading/ModuleRegistry.cs` | `Loading/ModuleRegistry.cs`; `Loading/ModuleRegistration.cs` |
| `Meep2.Meeplang/Loading/PipelineLoader.cs` | `Loading/PipelineLoader.cs` (LoadResult); `Loading/LoadedModules.cs` (LoadedModule); `Loading/Candidate.cs`; `Loading/LoadNode.cs`; `Loading/Load.cs`; `Loading/StandIn.cs` |
| `Meep2.Meeplang/Loading/VerbCatalogue.cs` | `Loading/CatalogueEntries.cs` (VerbEntry, ParameterEntry, EmittedType, SourceLocation); `Loading/VerbCatalogue.cs` |
| `Meep2.Meeplang/Parsing/ExpressionParser.cs` | `Parsing/ExpressionParser.cs`; `Parsing/TemplateParser.cs` |
| `Meep2.Meeplang/Parsing/Lexer.cs` | `Parsing/TokenKind.cs`; `Parsing/Token.cs`; `Parsing/Lexer.cs`; `Parsing/Diagnostics.cs` |
| `Meep2.Meeplang/Parsing/MeeplangParser.cs` | `Parsing/MeeplangParser.cs`; `Parsing/IIncludeReader.cs` (IncludeException); `Parsing/LineParser.cs` |
| `Meep2.Meeplang/Settings.cs` | `SettingsLine.cs` (SettingValue); `MeeplangSettings.cs` |
| `Meep2.Meeplang/Syntax/PipelineSyntax.cs` | `Syntax/PipelineSyntax.cs`; `Syntax/ModuleSyntax.cs`; `Syntax/ModuleParts.cs` (ArgumentSyntax, ModifierSyntax); `Syntax/ConstantSyntax.cs`; `Syntax/Directives.cs` (PluginOrigin, PluginDirective, IncludeDirective); `Syntax/TemplateBlock.cs` (TemplateParameter) |
| `Meep2.Meeplang/Syntax/Source.cs` | `Syntax/Source.cs` (SourceFile, SourceSpan, SourceRange); `Syntax/Diagnostic.cs` (DiagnosticSeverity) |
| `Meep2.Modules/Bayes.cs` | `BayesTree.cs`; `SavedTree.cs` (SavedClass); `TreeJson.cs` |
| `Meep2.Modules/Bodies.cs` | `OpenResponse.cs`; `StreamedBody.cs`; `CountingStream.cs`; `Bodies.cs` (BodyTooLargeException); `Endpoints.cs` |
| `Meep2.Modules/Classifying.cs` | `Learn.cs` (LearnParameters); `Classify.cs` (ClassifyParameters); `Forget.cs` (ForgetParameters); `Trees.cs` |
| `Meep2.Modules/Clients.cs` | `SharedClient.cs`; `Subscription.cs`; `ClientLease.cs`; `WebSocketClient.cs`; `TcpServerClient.cs` |
| `Meep2.Modules/Combining.cs` | `Messages/Combined.cs` (Batched, Zipped); `Batch.cs` (BatchParameters); `Zip.cs` (ZipParameters); `Distribute.cs` (DistributeParameters) |
| `Meep2.Modules/Enumerate.cs` | `Enumerate.cs` (EnumerateParameters); `Library.cs` |
| `Meep2.Modules/Fetch.cs` | `Fetch.cs` (HttpFetch, FileFetch, FetchParameters); `Addresses.cs` |
| `Meep2.Modules/Flow.cs` | `Where.cs` (WhereParameters); `Tap.cs` (TapParameters) |
| `Meep2.Modules/HttpListen.cs` | `HttpListen.cs` (HttpListenParameters); `HttpRequestLease.cs`; `HttpServer.cs`; `HttpAddress.cs`; `Certificates.cs` |
| `Meep2.Modules/Messages.cs` | `Messages/General.cs` (Tick, TextMessage, FileText, ClassifiedMessage, JsonMessage, BookItem); `Messages/Http.cs` (HttpResponse, HttpRequest, HttpErrorMessage); `Messages/Tcp.cs` (TcpMessage, TcpConnected, TcpDisconnected); `Messages/WebSockets.cs` (WebSocketText, WebSocketBinary, WebSocketConnected, WebSocketDisconnected) |
| `Meep2.Modules/Output.cs` | `WriteLine.cs` (WriteLineParameters); `Save.cs` (SaveParameters); `SaveMode.cs`; `CoreModules.cs` |
| `Meep2.Modules/Sources.cs` | `Interval.cs` (IntervalParameters); `Read.cs` (ReadParameters); `Paths.cs` (PathRefusedException) |
| `Meep2.Modules/TcpListen.cs` | `TcpListen.cs` (TcpListenParameters); `TcpConnectionLease.cs`; `Framing.cs` (FramingException) |
| `Meep2.Modules/WebSockets.cs` | `IConnectionLease.cs`; `Frame.cs`; `Frames.cs`; `WebSocketLink.cs` (QueueFullException); `WebSocketConnectionLease.cs` |
| `Meep2.Plugins/PluginCatalog.cs` | `PluginCatalog.cs`; `PluginHold.cs`; `LoadedPlugin.cs`; `PluginLoadContext.cs` |
| `Meep2.Plugins/PluginOptions.cs` | `PluginOptions.cs`; `PluginException.cs` (UnsignedPluginException, UntrustedSignatureException) |
| `Meep2.Plugins/Signatures.cs` | `Signatures/PublicKey.cs`; `Signatures/TrustedKeys.cs`; `Signatures/SshSignature.cs`; `Signatures/SshKeyPair.cs`; `Signatures/PluginSignature.cs`; `Signatures/SshReader.cs`; `Signatures/SshWriter.cs`; `Signatures/PipelineSignature.cs` |
| `Meep2.Runtime/Internal/Contexts.cs` | `Internal/InflectionContext.cs`; `Internal/SourceContext.cs`; `Internal/NodeOutlet.cs` |
| `Meep2.Runtime/Internal/Node.cs` | `Internal/NodeSpecs.cs` (InflectionSpec, NodeSpec); `Internal/Fanout.cs`; `Internal/LinkCount.cs`; `Internal/Node.cs` |
| `Meep2.Runtime/Internal/Tracking.cs` | `Internal/OutcomeScope.cs`; `Internal/ErrorScope.cs`; `Internal/ScopeSet.cs`; `Internal/InFlightCounter.cs` |
| `Meep2.Runtime/PipelineBuilder.cs` | `VerbInflection.cs`; `SourceSetup.cs`; `PipelineBuilder.cs` |
| `Meep2.Runtime/PipelineChange.cs` | `PipelineChange.cs`; `PipelineChangeReport.cs`; `PipelineDefinition.cs` |
| `Meep2.SDK/Modules/AInflection.cs` | `Modules/AInflection.cs`; `Modules/IRouteMessages.cs` |
| `Meep2.SDK/Modules/Peers.cs` | `Modules/Tally.cs`; `Modules/ISendsTo.cs`; `Modules/Peers.cs` |
| `Meep2.SDK/Modules/TextPieces.cs` | `Modules/TextPiece.cs`; `Modules/IComputedPieces.cs`; `Modules/ComputedText.cs` |

## Left as they are, and why

- **Families of small records**, as the rule allows: `ExpressionSyntax.cs` (13 syntax nodes), `Attributes.cs` (the attributes and the two records they use), `HandlingTimes.cs`, `ErrorPolicy.cs`, `ContentInterfaces.cs` (four interfaces), `PipelineShape.cs` and `Source.cs` (the three source positions). The new family files are listed in the table: `Contracts/`, `Messages/`, `ApiMessages.cs`, `HostingMessages.cs`, `CatalogueEntries.cs`, `Directives.cs` and so on.
- **`AInflection.cs`** keeps `AInflection` and `AInflection<TIn>`, since a type and its generic form share a name.
- **`PluginException.cs`** keeps the two one-line exceptions that derive from it.
- **Files with two types where one is the other's small companion** (a JSON context, a result record, a parameters record), such as `LanguageServer.cs`, `DaemonCommands.cs`, `Highlighter.cs`, `ParameterBinder.cs` and `PluginInstaller.cs`. Where both were classes in their own right, I split them too, though section 9 only counted files with more than two: `ModuleRegistry.cs`, `ExpressionParser.cs`, `Binder.cs` (now `ExpressionCompiler.cs` and `ExpressionBinder.cs`), `DaemonClient.cs`, `HostedPipeline.cs`, `HostSettings.cs` and `ErrorPolicyParser.cs`.
- **Tests** are left as they are. Their files with several types hold test doubles and helpers (`Support/MeeplangSupport.cs`, `Support/TestModules.cs`, `TestPlugin/Shout.cs`), which read best as a set. `DaemonLab` in `DaemonTests.cs` could move to `Support/` later.
- **`Send.cs`, and Fetch's two inflections in `Fetch.cs`.** I didn't split these, though the plan said `Send/`. Only `Addresses` moved out of `Fetch.cs`. The reason is under Q238.

## Found in passing: inflection order follows the files

`ModuleRegistry.AddAssembly` takes a verb's inflections in the order `assembly.GetExportedTypes()` gives them. That order follows the order the files are compiled in, then the order of the types within each file. The loader puts inflections of equal rank in that same order. It tries them one by one, and the first one that takes a message gets it. Error messages list the inflections in that order too.

When I first split `Fetch.cs`, `FileFetch.cs` sorted before `HttpFetch.cs`, so File came before HTTP. Three tests failed on the wording of their error messages. Splitting `Send.cs` into `Send/` would likewise change Send's order from Reply, Broadcast, WebSocket, TCP, HTTP to alphabetical order. No test notices that, but it could change which inflection takes a message whenever two of them would both take it. I checked the built assemblies: after this commit every verb's inflections come in the same order as on `main`.

The catalogue's list of verbs (`meep lsp`'s verb catalogue) also follows this order, so it now lists verbs in a different order. That's only how the list is shown.

Listen's three inflections already sit in three files, so their order depends on how the build lists files. On another file system, such as Linux's, that order isn't guaranteed.

## Questions

**Q238.** Should the order of a verb's inflections be stated outright, instead of following the files? For example, an order on `[Verb]` (`[Verb("Send", Order = 1)]`), or by rank and then name, with the catalogue sorted by verb name. Once it is, `Send.cs` can become `Send/` and Fetch's two inflections can each have a file, in a small commit after it. I'd recommend an explicit order on `[Verb]`, made in phase 3 along with H3's base records.

**Answer:** Not at this time.

**Q239.** I split some files with two classes in them as well (listed above), since your rule is one class to a file. Is that what you wanted, or should section 9's count of "more than two" have been the limit?

**Answer:** That's fine. I'll bring up any exceptions if I think it's necessary.

## After your answers

- **Q238 (inflection order):** not now. A verb's inflections keep following the order of the files, so `Send.cs` stays whole and Fetch's two inflections stay together in `Fetch.cs`. Anyone splitting either should first check that the order stays the same, or the loader may try a different inflection first.
- **Q239 (files with two classes):** the splits stand. You'll raise any exceptions as you find them.
