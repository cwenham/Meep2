using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Meep2.Meeplang.Loading;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.Runtime.Verifying;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;
using Meep2.Security;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>@permission gates, verbs that require permissions, checks in code, and Listen's signature=required (permissions phase 4).</summary>
public class GateTests
{
    private const string Definitions = "#permission ReadNotes\n#permission WriteNotes\n#permission Other\n";

    private static PermissionName Name(string text) => PermissionName.Parse(text);

    private static ModuleInfo Module(TestRun run, string name) =>
        run.Result.Pipeline!.Modules.Single(m => m.Name == name || (m.Name is null && m.Verb == name));

    private static IReadOnlyList<PermissionDenied> Denials(TestRun run, string module) => [.. run.At(module).OfType<PermissionDenied>()];

    // ---- Gates ----

    [Test]
    public async Task A_gate_lets_through_a_message_from_a_verified_credential_and_refuses_one_without()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await GateLab.CreateAsync("ReadNotes");

        var run = await TestRun.Start($"""
            Read "{lab.File}"
                Verify bearer=true
                    Derive
                        Sink @permission=ReadNotes
                            Sink as Denied @errors
            Numbers to=2
                Sink as Anonymous @permission=ReadNotes
                    Sink as Refusals @errors
            """, trust: TrustStoreSource.Of(lab.Store()));

        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(run.At("Sink").Single()).IsTypeOf<TextMessage>();
        await Assert.That(run.At("Denied")).IsEmpty();
        await Assert.That(run.At("Anonymous")).IsEmpty();
        var refusals = Denials(run, "Refusals");
        await Assert.That(refusals.Count).IsEqualTo(2);
        await Assert.That(refusals.All(r => r.Needed.SequenceEqual([Name("ReadNotes")]))).IsTrue();
        await Assert.That(refusals[0].Reasons.Single()).Contains("Nothing on one of the lines of this message's family is a verified credential");
        await Assert.That(refusals[0].Parent).IsTypeOf<Number>();
        await Assert.That(refusals[0].Gate.Name).IsEqualTo("Anonymous");
        await Assert.That(refusals[0].Error).StartsWith("Anonymous needs ReadNotes, which this message doesn't have.");
        await Assert.That(run.Result.Pipeline!.GetStats(Module(run, "Anonymous")).Denied).IsEqualTo(2);
        await Assert.That(run.Result.Pipeline!.GetStats(Module(run, "Sink")).Denied).IsEqualTo(0);
        await Assert.That(run.Unhandled).IsEmpty();
    }

    [Test]
    public async Task Refusals_nothing_handles_are_logged_but_never_stop_the_pipeline()
    {
        // No trust store at all: every message is refused, saying so.
        var run = await TestRun.Start("""
            Numbers to=40
                Sink @permission=ReadNotes
            """);

        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(run.Unhandled.Count).IsEqualTo(40);
        await Assert.That(run.Unhandled.First()).IsTypeOf<PermissionDenied>();
        await Assert.That(run.Unhandled.First().Error).Contains("There's no trust store to check against");
        await Assert.That(run.Notices.Any(n => n.Contains("Stopping", StringComparison.Ordinal))).IsFalse();
        await Assert.That(run.Result.Pipeline!.GetStats(Module(run, "Sink")).Denied).IsEqualTo(40);
        await Assert.That(run.Result.Pipeline!.GetStats(Module(run, "Sink")).Received).IsEqualTo(0);
    }

    [Test]
    public async Task A_permission_the_trust_store_doesnt_define_is_held_by_a_credential_carrying_it()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await GateLab.CreateAsync("Fresh");
        var pipeline = $"""
            Read "{lab.File}"
                Verify bearer=true
                    Sink as Held @permission=Fresh
                        Sink as HeldDenied @errors
                    Sink as Missing @permission=Missing
                        Sink as MissingDenied @errors
            """;

        // Q289: any trusted authority confers a name the store doesn't define, and it requires nothing.
        var run = await TestRun.Start(pipeline, trust: TrustStoreSource.Of(lab.Store()));
        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(run.At("Held").Count).IsEqualTo(1);
        await Assert.That(Denials(run, "MissingDenied").Single().Reasons.Single()).IsEqualTo("alice's credential doesn't hold Missing.");

        // The pipeline's own directive can still narrow it.
        var narrowed = await TestRun.Start("#permission Fresh authority=granter\n" + pipeline, trust: TrustStoreSource.Of(lab.Store()));
        await Assert.That(narrowed.At("Held")).IsEmpty();
        await Assert.That(Denials(narrowed, "HeldDenied").Single().Reasons.Single()).IsEqualTo("alice's credential holds Fresh, conferred by ca, but only granter may confer it.");
    }

    [Test]
    public async Task Every_permission_written_is_needed_and_scopes_must_be_covered()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await GateLab.CreateAsync("ReadNotes", "WriteNotes:~/notes/**");

        var run = await TestRun.Start($"""
            Read "{lab.File}"
                Verify bearer=true
                    Sink as Both @permission=ReadNotes @permission="WriteNotes:~/notes/**"
                    Sink as Three @permission=ReadNotes @permission=Other @permission=WriteNotes
                        Sink as ThreeDenied @errors
                    Sink as Inside @permission="WriteNotes:~/notes/drafts/**"
                    Sink as Wider @permission="WriteNotes:~/**"
                        Sink as WiderDenied @errors
            """, trust: TrustStoreSource.Of(lab.Store()));

        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(run.At("Both").Count).IsEqualTo(1);
        await Assert.That(run.At("Inside").Count).IsEqualTo(1);
        var three = Denials(run, "ThreeDenied").Single();
        await Assert.That(three.Needed).IsEquivalentTo([Name("Other"), Name("WriteNotes")]);
        await Assert.That(three.Reasons[0]).IsEqualTo("alice's credential doesn't hold Other.");
        await Assert.That(three.Reasons[1]).IsEqualTo("alice's credential doesn't hold WriteNotes.");
        await Assert.That(Denials(run, "WiderDenied").Single().Reasons.Single()).IsEqualTo("alice's credential doesn't hold WriteNotes:~/**.");
    }

    [Test]
    public async Task A_message_with_several_parents_needs_the_permission_on_every_line()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await GateLab.CreateAsync("ReadNotes");
        var credential = File.ReadAllText(lab.File);
        var store = lab.Store();

        // Two lines into a Zip, each a credential verified or not, and a gate after it.
        async Task<(IReadOnlyList<Message> Passed, IReadOnlyList<Message> Denied)> ZipAsync(Message left, Message right, bool verifyRight)
        {
            var passed = new Collect<Message>();
            var denied = new Collect<Message>();
            var builder = new PipelineBuilder("lines", new PipelineOptions { Trust = TrustStoreSource.Of(store), UnhandledError = _ => { } });
            var leftSource = builder.AddSource(() => new Messages(left), name: "Left");
            var verifyLeft = builder.AddVerb("Verify", VerifyLab.Inflections(new VerifyParameters { Bearer = true }), name: "VerifyLeft");
            var rightSource = builder.AddSource(() => new Messages(right), name: "Right");
            var zip = builder.Add(() => new Zip(new ZipParameters()), name: "Zip");
            var gated = builder.Add(() => passed, name: "Gated", options: new NodeOptions { Permissions = [Name("ReadNotes")] });
            var errors = builder.Add(() => denied, name: "Denied", options: new NodeOptions { Errors = ErrorDelivery.Only });
            builder.Connect(leftSource, verifyLeft).Connect(verifyLeft, zip).Connect(zip, gated).Connect(gated, errors);
            if (verifyRight)
            {
                var verifyRightModule = builder.AddVerb("Verify", VerifyLab.Inflections(new VerifyParameters { Bearer = true }), name: "VerifyRight");
                builder.Connect(rightSource, verifyRightModule).Connect(verifyRightModule, zip);
            }
            else
            {
                builder.Connect(rightSource, zip);
            }
            await using var pipeline = builder.Build();
            await Wait.For(pipeline.RunAsync());
            return (passed.Received, denied.Received);
        }

        var mixed = await ZipAsync(new TextMessage(credential), new TextMessage("anonymous"), verifyRight: false);
        await Assert.That(mixed.Passed).IsEmpty();
        await Assert.That(((PermissionDenied)mixed.Denied.Single()).Reasons.Single()).Contains("Nothing on one of the lines");

        var both = await ZipAsync(new TextMessage(credential), new TextMessage(credential), verifyRight: true);
        await Assert.That(both.Denied).IsEmpty();
        await Assert.That(both.Passed.Count).IsEqualTo(1);
        await Assert.That(both.Passed[0].Parents.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_credential_revoked_after_it_was_verified_is_refused_at_the_next_gate()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await GateLab.CreateAsync("ReadNotes");
        var trust = new SwitchableTrust(lab.Store());
        var passed = new Collect<Message>();
        var denied = new Collect<Message>();

        // Between Verify and the gate, the credential is revoked and the store reloaded, as the watcher would.
        var revoker = new Revoker(() =>
        {
            lab.Lab.Folder.Revoke(Revocation.Of(lab.Credential), "alice's");
            trust.Store = trust.Store.Reload();
        });
        var builder = new PipelineBuilder("revoked", new PipelineOptions { Trust = trust, UnhandledError = _ => { } });
        var source = builder.AddSource(() => new Messages(new TextMessage(File.ReadAllText(lab.File))));
        var verify = builder.AddVerb("Verify", VerifyLab.Inflections(new VerifyParameters { Bearer = true }));
        var revoke = builder.Add(() => revoker, name: "Revoke");
        var gated = builder.Add(() => passed, name: "Gated", options: new NodeOptions { Permissions = [Name("ReadNotes")] });
        var errors = builder.Add(() => denied, name: "Denied", options: new NodeOptions { Errors = ErrorDelivery.Only });
        builder.Chain(source, verify, revoke, gated, errors);
        await using (var pipeline = builder.Build())
            await Wait.For(pipeline.RunAsync());

        await Assert.That(passed.Received).IsEmpty();
        await Assert.That(((PermissionDenied)denied.Received.Single()).Reasons.Single()).Contains("revoked");
    }

    [Test]
    public async Task A_gates_answer_is_kept_on_the_message_and_one_made_from_it_stops_there()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await GateLab.CreateAsync("ReadNotes");
        var store = lab.Store();
        var verified = await VerifyLab.VerifyAsync(store, new VerifyParameters { Bearer = true }, new TextMessage(File.ReadAllText(lab.File)));
        await Assert.That(verified.Success).IsTrue();
        var keeper = new Gatekeeper(() => new PipelineOptions { Trust = TrustStoreSource.Of(store) });
        var gate = new Gate(keeper, [Name("ReadNotes")]);
        var requirement = keeper.Resolve(Name("ReadNotes"), store);

        var child = new TextMessage("child") { Parents = new Parents(verified) };
        await Assert.That(gate.Check(child)).IsNull();
        await Assert.That(GateAnswers.Find(child, requirement)).IsEqualTo(new LineAnswer(null, verified.ExpiresTimestamp));
        await Assert.That(GateAnswers.Find(verified, requirement)?.WhyNot).IsNull();
        await Assert.That(GateAnswers.Find(verified, requirement)).IsNotNull();

        // A grandchild's walk stops at the child's kept answer: here, one put there to tell.
        GateAnswers.Add(child, requirement, new LineAnswer("kept on the child", 0));
        var grandchild = new TextMessage("grandchild") { Parents = new Parents(child) };
        await Assert.That(gate.Check(grandchild)!.Reasons.Single()).IsEqualTo("kept on the child");

        // A yes that has lapsed is worked out again.
        var other = new TextMessage("other") { Parents = new Parents(verified) };
        GateAnswers.Add(other, requirement, new LineAnswer(null, Stopwatch.GetTimestamp() - 1));
        await Assert.That(GateAnswers.Find(other, requirement)).IsNull();

        // A reloaded store makes new requirements, so nothing kept for the old one is used.
        File.AppendAllText(lab.Lab.Folder.Permissions, "#permission Another\n");
        var reloaded = store.Reload();
        await Assert.That(ReferenceEquals(keeper.Resolve(Name("ReadNotes"), reloaded), requirement)).IsFalse();
    }

    [Test]
    public async Task An_error_refused_at_a_gate_passes_it_by_and_a_passthrough_gate_passes_nothing_refused_on()
    {
        var run = await TestRun.Start("""
            Numbers to=1
                Boom
                    Sink as Gated @permission=ReadNotes
                        Sink as Errors @errors
            Numbers to=2
                Glance @permission=ReadNotes
                    Sink as After
            """);

        await Assert.That(run.Diagnostics).IsEmpty();
        var error = (ErrorMessage)run.At("Errors").Single();
        await Assert.That(error).IsNotTypeOf<PermissionDenied>();
        await Assert.That(error.Error).IsEqualTo("boom");
        await Assert.That(run.At("Gated")).IsEmpty();
        await Assert.That(run.Result.Pipeline!.GetStats(Module(run, "Gated")).Denied).IsEqualTo(1);
        await Assert.That(run.At("After").Count).IsEqualTo(2);
        await Assert.That(run.At("After").All(m => m is PermissionDenied)).IsTrue();
    }

    [Test]
    [NotInParallel("tracing")]
    public async Task Every_refusal_is_a_short_span_of_its_own_and_its_error_hangs_under_it()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MeepTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if ((string?)activity.GetTagItem("meep.pipeline") is "Pipeline" or "test")
                    stopped.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);

        // One refusal straight after a source, which has no handling span to put it on, and one after a module. The
        // names are this test's own, since other tests' pipelines refuse things too.
        var run = await TestRun.Start("""
            Numbers to=1 as SourceQ298
                Sink as StraightQ298 @permission=ReadNotes
                    Sink as AnswerQ298 @errors
                Derive as DeriveQ298
                    Sink as GatedQ298 @permission=ReadNotes
            """);

        await Assert.That(run.Diagnostics).IsEmpty();
        Activity Named(string module, string? suffix = null) => stopped.Single(a => (string?)a.GetTagItem("meep.module") == module
            && (suffix is null || a.DisplayName.EndsWith(suffix, StringComparison.Ordinal)));
        var straight = Named("StraightQ298");
        var gated = Named("GatedQ298");
        foreach (var refusal in new[] { straight, gated })
        {
            await Assert.That(refusal.DisplayName).StartsWith("Sink");
            await Assert.That(refusal.DisplayName).EndsWith(".denied");
            await Assert.That(refusal.Status).IsEqualTo(ActivityStatusCode.Unset);
            await Assert.That((string?)refusal.GetTagItem("meep.outcome")).IsEqualTo("denied");
            await Assert.That((string?)refusal.GetTagItem("meep.permission")).IsEqualTo("ReadNotes");
            await Assert.That((string?)refusal.GetTagItem("meep.permission.reasons")).Contains("There's no trust store to check against");
            var denied = refusal.Events.Single(e => e.Name == "meep.permission.denied");
            await Assert.That(denied.Tags.Single(t => t.Key == "meep.permission").Value).IsEqualTo("ReadNotes");
        }

        // The one after the source hangs under the source's span; the other under Derive's.
        var source = Named("SourceQ298");
        await Assert.That(straight.ParentSpanId).IsEqualTo(source.SpanId);
        await Assert.That(gated.ParentSpanId).IsEqualTo(Named("DeriveQ298").SpanId);

        // The @errors branch answering the refusal is its child: request, refused at Straight, answered.
        var answer = Named("AnswerQ298");
        await Assert.That(answer.ParentSpanId).IsEqualTo(straight.SpanId);
        await Assert.That(answer.TraceId).IsEqualTo(source.TraceId);
    }

    // ---- Gates that need a kind of proof ----

    [Test]
    public async Task A_gate_with_proof_refuses_a_bearer_credential_and_lets_a_signed_request_through()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await GateLab.CreateAsync("ReadNotes");
        var port = TestServer.FreePort();
        var url = $"http://127.0.0.1:{port}/notes/";
        await using var running = TestRun.Begin($$"""
            Listen "{{url}}"
                Verify from=msg.Headers["Meep-Credential"] bearer=true
                    Send text="Welcome." @permission=ReadNotes @proof=signed
                        Send error-status=403 error-reply="{msg.Error}" @errors
            """, TrustStoreSource.Of(lab.Store()));
        await Assert.That(running.Run.Diagnostics).IsEmpty();
        using var client = new HttpClient();

        async Task<(HttpStatusCode Status, string Text)> PostAsync(Dictionary<string, string> headers)
        {
            var deadline = DateTime.UtcNow + Wait.Limit;
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("hello", Encoding.UTF8, "text/plain") };
                foreach (var (name, value) in headers)
                {
                    if (!request.Headers.TryAddWithoutValidation(name, value))
                        request.Content.Headers.TryAddWithoutValidation(name, value);
                }
                try
                {
                    using var response = await client.SendAsync(request);
                    return (response.StatusCode, await response.Content.ReadAsStringAsync());
                }
                catch (HttpRequestException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }
            }
        }

        var signer = new RequestSigner(RequestSigner.With(lab.Lab.Subject), lab.Credential) { Url = url };
        var signed = await PostAsync(signer.Headers());
        await Assert.That(signed.Status).IsEqualTo(HttpStatusCode.OK);

        // The same credential, unsigned: Verify takes it as a bearer credential, which passes, but the gate wants proof.
        var bearer = await PostAsync(new Dictionary<string, string> { ["Meep-Credential"] = Convert.ToBase64String(lab.Credential.Blob.Span) });
        await Assert.That(bearer.Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(bearer.Text).Contains("alice's credential was verified with none, as a bearer credential, and this needs a signed request, a connection's proof or TLS.");
    }

    [Test]
    [Arguments("Numbers to=1\n    Sink @permission=ReadNotes @proof=sometimes", "@proof needs the kind of proof")]
    [Arguments("Numbers to=1\n    Sink @proof=signed", "isn't a gate: give it @permission too")]
    public async Task Proof_modifiers_that_cant_be_honoured_are_reported(string text, string expected)
    {
        var run = await TestRun.Start(text.Replace("\\n", "\n", StringComparison.Ordinal), run: false);
        await Assert.That(run.Diagnostics).Contains(expected);
    }

    [Test]
    public async Task Proof_modifiers_load_and_say_which_kinds_they_accept()
    {
        var run = await TestRun.Start("""
            Numbers to=1
                Sink as Signed @permission=ReadNotes @proof=signed
                Sink as Some @permission=ReadNotes @proof=tls @proof=request
            """, run: false);
        await Assert.That(run.Diagnostics).IsEmpty();

        var signed = ProofSet.Of([Proof.RequestSignature, Proof.Connection, Proof.Tls]);
        await Assert.That(signed.Allows(Proof.Bearer)).IsFalse();
        await Assert.That(signed.Allows(Proof.None)).IsFalse();
        await Assert.That(signed.Allows(Proof.Connection)).IsTrue();
        await Assert.That(signed.ToString()).IsEqualTo("a signed request, a connection's proof or TLS");
        await Assert.That(ProofSet.Of([Proof.Tls, Proof.RequestSignature]).ToString()).IsEqualTo("a signed request or TLS");
        await Assert.That(ProofSet.Of([]).Allows(Proof.Bearer)).IsTrue();
    }

    // ---- The pipeline's own directives ----

    [Test]
    public async Task A_pipelines_permission_directive_narrows_its_gates_and_its_Verify_and_can_never_widen()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await GateLab.CreateAsync("ReadNotes");
        var pipeline = $"""
            #permission ReadNotes authority=granter
            Read "{lab.File}"
                Verify bearer=true
                    Sink @permission=ReadNotes
                        Sink as Denied @errors
                    Verify from="ReadNotes" as Ahead
                        Sink as Answer
            """;

        var narrowed = await TestRun.Start(pipeline, trust: TrustStoreSource.Of(lab.Store()));
        await Assert.That(narrowed.Diagnostics).IsEmpty();
        await Assert.That(narrowed.At("Sink")).IsEmpty();
        await Assert.That(Denials(narrowed, "Denied").Single().Reasons.Single()).IsEqualTo("alice's credential holds ReadNotes, conferred by ca, but only granter may confer it.");
        await Assert.That(((VerifiedMessage)narrowed.At("Answer").Single()).Success).IsFalse();

        var plain = await TestRun.Start(pipeline.Replace("#permission ReadNotes authority=granter\n", "", StringComparison.Ordinal), trust: TrustStoreSource.Of(lab.Store()));
        await Assert.That(plain.At("Sink").Count).IsEqualTo(1);
        await Assert.That(((VerifiedMessage)plain.At("Answer").Single()).Success).IsTrue();

        lab.Lab.Define("#permission ReadNotes authority=ca\n");
        var widened = await TestRun.Start(pipeline, trust: TrustStoreSource.Of(lab.Store()));
        await Assert.That(Denials(widened, "Denied").Single().Reasons.Single()).StartsWith("ReadNotes isn't well defined here: granter may not confer ReadNotes: only ca may.");
    }

    [Test]
    [Arguments("Numbers to=1\n#permission ReadNotes\n", "#permission goes before the pipeline's modules.")]
    [Arguments("  #permission ReadNotes\n", "#permission goes at the left margin.")]
    [Arguments("#permission ReadNotes\n#permission ReadNotes requires=Other\n", "This pipeline already narrows ReadNotes. Write everything for it on one #permission line.")]
    [Arguments("#permission ReadNotes colour=blue\n", "colour isn't something #permission takes. It takes authority=name and requires=permission.")]
    [Arguments("#permit ReadNotes\n", "There's no #permit directive. Meeplang has #plugin, #include, #permission and #holds.")]
    public async Task Permission_directives_that_cant_be_read_are_reported(string text, string expected)
    {
        var syntax = Script.Parse(text);
        await Assert.That(Script.Diagnostics(syntax)).Contains(expected);
    }

    [Test]
    public async Task Permission_directives_and_modifiers_are_read_into_the_pipelines_options()
    {
        var run = await TestRun.Start("""
            #permission WriteNotes:~/notes/** authority=home requires=ReadNotes
            const Scope = "Other:~/x/**"
            Numbers to=1 as N
                Sink @permission=ReadNotes @permission=meep.files.read @permission="WriteNotes:~/notes/**" @permission=Scope @permission=ReadNotes
            """, run: false);

        await Assert.That(run.Diagnostics).IsEmpty();
        var directive = run.Result.Pipeline!.Options.Permissions.Single();
        await Assert.That(directive.ToDirective()).IsEqualTo("#permission WriteNotes:~/notes/** authority=home requires=ReadNotes");
        var bad = await TestRun.Start("""
            Numbers to=1
                Sink @permission="not a name" @permission
            """, run: false);
        await Assert.That(bad.Diagnostics).Contains("not a name isn't a permission's name.");
        await Assert.That(bad.Diagnostics).Contains("@permission needs a permission's name, as in @permission=ReadNotes");
    }

    // ---- Sources, verbs that require permissions, and checks in code ----

    [Test]
    [Arguments("Numbers to=1 @permission=ReadNotes\n    Sink\n", "Numbers #1 can't start: it needs ReadNotes")]
    [Arguments("NotesSource\n    Sink\n", "NotesSource #1 can't start: it needs ReadNotes")]
    public async Task A_gated_source_stops_its_pipeline_starting(string text, string expected)
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => TestRun.Start(text));
        await Assert.That(failure!.Message).StartsWith(expected);
        await Assert.That(failure.Message).Contains("a pipeline's own credential holds only Meep's own permissions");
    }

    [Test]
    public async Task A_verb_that_requires_a_permission_keeps_it_and_permission_adds_to_it()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await GateLab.CreateAsync("ReadNotes");

        var run = await TestRun.Start($"""
            Read "{lab.File}"
                Verify bearer=true
                    NeedsNotes
                        Sink
                    NeedsNotes as Both @permission=WriteNotes
                        Sink as Denied @errors
            Numbers to=1
                NeedsNotes as Anonymous
                    Sink as Refused @errors
            """, trust: TrustStoreSource.Of(lab.Store()));

        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(run.At("Sink").Single()).IsTypeOf<VerifiedMessage>();
        await Assert.That(Denials(run, "Denied").Single().Needed).IsEquivalentTo([Name("WriteNotes")]);
        await Assert.That(Denials(run, "Refused").Single().Needed).IsEquivalentTo([Name("ReadNotes")]);

        var registry = new ModuleRegistry().Add(typeof(NeedsNotes));
        await Assert.That(registry.TryGetVerb("NeedsNotes", out var inflections)).IsTrue();
        await Assert.That(inflections![0].RequiredPermissions).IsEquivalentTo([Name("ReadNotes")]);
        await Assert.That(VerbCatalogue.Describe(inflections).Single().Requires).IsEquivalentTo(["ReadNotes"]);
        await Assert.That(() => new ModuleRegistry().Add(typeof(BadlyGated))).Throws<ArgumentException>();

        // Q293: inflections chosen for each message are chosen after the gate, so they must need the same permissions.
        var mismatched = await Assert.That(() => new ModuleRegistry().Add(typeof(ChosenRead)).Add(typeof(ChosenWrite))).Throws<ArgumentException>();
        await Assert.That(mismatched!.Message).StartsWith("Chosen.Read and Chosen.Write require different permissions ([RequiresPermission]), but they're chosen for each message");
        await Assert.That(() => new ModuleRegistry().Add(typeof(ChosenRead)).Add(typeof(ChosenAlsoRead))).ThrowsNothing();
    }

    [Test]
    public async Task A_check_in_code_refuses_a_message_without_counting_it_as_a_failure()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await GateLab.CreateAsync("WriteNotes:~/notes/**");

        var run = await TestRun.Start($"""
            Read "{lab.File}"
                Verify bearer=true
                    WritesNote "~/notes/today.md" as Inside
                        Sink as Wrote
                    WritesNote "~/secrets/key" as Outside
                        Sink as Denied @errors
            """, trust: TrustStoreSource.Of(lab.Store()));

        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(run.At("Wrote").Count).IsEqualTo(1);
        var denied = Denials(run, "Denied").Single();
        await Assert.That(denied.Reasons.Single()).IsEqualTo("alice's credential doesn't hold WriteNotes for ~/secrets/key.");
        await Assert.That(denied.Gate.Name).IsEqualTo("Outside");
        var stats = run.Result.Pipeline!.GetStats(Module(run, "Outside"));
        await Assert.That(stats.Denied).IsEqualTo(1);
        await Assert.That(stats.Failed).IsEqualTo(0);
        await Assert.That(stats.Restarts).IsEqualTo(0);
    }

    // ---- Listen ----

    [Test]
    public async Task Listen_with_proof_required_refuses_a_request_without_a_signature_and_credential()
    {
        var port = TestServer.FreePort();
        var url = $"http://127.0.0.1:{port}/notes/";
        await using var running = TestRun.Begin($$"""
            Listen "{{url}}" signature=required
                Send text="in"
            """);
        await Assert.That(running.Run.Diagnostics).IsEmpty();
        using var client = new HttpClient();

        async Task<(HttpStatusCode Status, string Text)> PostAsync(params string[] headers)
        {
            var deadline = DateTime.UtcNow + Wait.Limit;
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("hello", Encoding.UTF8, "text/plain") };
                foreach (var header in headers)
                    request.Headers.TryAddWithoutValidation(header, "x");
                try
                {
                    using var response = await client.SendAsync(request);
                    return (response.StatusCode, await response.Content.ReadAsStringAsync());
                }
                catch (HttpRequestException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }
            }
        }

        var none = await PostAsync();
        await Assert.That(none.Status).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(none.Text).IsEqualTo("This server needs requests signed with a Meep credential, and this one has no Signature-Input, Signature or Meep-Credential.");
        var one = await PostAsync("Signature-Input", "Signature");
        await Assert.That(one.Text).EndsWith("has no Meep-Credential.");
        var all = await PostAsync("Signature-Input", "Signature", "Meep-Credential");
        await Assert.That(all.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(all.Text).IsEqualTo("in");
        // Q296: the two refused are counted as the Listen's own denied.
        await Assert.That(running.Pipeline.GetStats(running.Pipeline.Modules[0]).Denied).IsEqualTo(2);
    }

    [Test]
    public async Task A_signed_request_with_the_permission_is_answered_and_others_get_a_403()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await GateLab.CreateAsync("ReadNotes");
        var port = TestServer.FreePort();
        var url = $"http://127.0.0.1:{port}/notes/";
        // The example in docs/verbs.md, under Gates.
        await using var running = TestRun.Begin($$"""
            Listen "{{url}}" signature=required
                Verify from=msg.Headers["Meep-Credential"]
                    Send text="Welcome." @permission=ReadNotes
                        Send error-status=403 error-reply="{msg.Error}" @errors
            """, TrustStoreSource.Of(lab.Store()));
        await Assert.That(running.Run.Diagnostics).IsEmpty();
        using var client = new HttpClient();

        async Task<(HttpStatusCode Status, string Text)> PostAsync(Dictionary<string, string> headers)
        {
            var deadline = DateTime.UtcNow + Wait.Limit;
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("hello", Encoding.UTF8, "text/plain") };
                foreach (var (name, value) in headers)
                {
                    if (!request.Headers.TryAddWithoutValidation(name, value))
                        request.Content.Headers.TryAddWithoutValidation(name, value);
                }
                try
                {
                    using var response = await client.SendAsync(request);
                    return (response.StatusCode, await response.Content.ReadAsStringAsync());
                }
                catch (HttpRequestException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }
            }
        }

        var signer = new RequestSigner(RequestSigner.With(lab.Lab.Subject), lab.Credential) { Url = url };
        var signed = await PostAsync(signer.Headers());
        await Assert.That(signed.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(signed.Text).IsEqualTo("Welcome.");

        // Signed by a key that isn't the credential's: Verify fails it, and the gate refuses it.
        var forged = await PostAsync(new RequestSigner(RequestSigner.With(Meep2.Security.SshKeyPair.CreateEd25519("mallory")), lab.Credential) { Url = url }.Headers());
        await Assert.That(forged.Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(forged.Text).StartsWith("Send #3 needs ReadNotes, which this message doesn't have. alice's credential didn't verify:");
    }

    /// <summary>A verb whose required permission isn't a permission's name.</summary>
    [Verb("BadlyGated")]
    [RequiresPermission("not a name")]
    private sealed class BadlyGated : AInflection<Message>
    {
        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>Parameters for the Chosen verbs, which take every message.</summary>
    public sealed record ChosenParameters;

    /// <summary>One of three inflections of Chosen, chosen for each message, needing ReadNotes.</summary>
    [Verb("Chosen", Inflection = "Read")]
    [ChosenForEachMessage]
    [RequiresPermission("ReadNotes")]
    private sealed class ChosenRead(ChosenParameters parameters) : AInflection<Message>, IChosenByParameters<ChosenParameters>
    {
        public ChosenParameters Parameters => parameters;

        public static string? WhyNot(ChosenParameters parameters, Message message) => null;

        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>Another, needing WriteNotes, which can't join ChosenRead.</summary>
    [Verb("Chosen", Inflection = "Write")]
    [ChosenForEachMessage]
    [RequiresPermission("WriteNotes")]
    private sealed class ChosenWrite(ChosenParameters parameters) : AInflection<Message>, IChosenByParameters<ChosenParameters>
    {
        public ChosenParameters Parameters => parameters;

        public static string? WhyNot(ChosenParameters parameters, Message message) => null;

        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>Another, needing ReadNotes as ChosenRead does, which can.</summary>
    [Verb("Chosen", Inflection = "AlsoRead")]
    [ChosenForEachMessage]
    [RequiresPermission("ReadNotes")]
    private sealed class ChosenAlsoRead(ChosenParameters parameters) : AInflection<Message>, IChosenByParameters<ChosenParameters>
    {
        public ChosenParameters Parameters => parameters;

        public static string? WhyNot(ChosenParameters parameters, Message message) => null;

        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>Does something, then passes the message on.</summary>
    private sealed class Revoker(Action action) : AInflection<Message>
    {
        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
        {
            action();
            context.Emit(message);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A trust store a test can replace, as the watcher replaces one on a reload.</summary>
    private sealed class SwitchableTrust(TrustStore store) : TrustStoreSource
    {
        public TrustStore Store { get; set; } = store;

        public override TrustStore? Current => Store;
    }

    /// <summary>
    /// A trust store with ReadNotes, WriteNotes and Other defined, and alice's credential holding some of them, which
    /// she may use without proof, in a file.
    /// </summary>
    private sealed class GateLab : IDisposable
    {
        private GateLab(VerifyLab lab, Credential credential, string file)
        {
            Lab = lab;
            Credential = credential;
            File = file;
        }

        public VerifyLab Lab { get; }

        public Credential Credential { get; }

        /// <summary>The credential's file, for Read.</summary>
        public string File { get; }

        public TrustStore Store() => Lab.Store();

        public static async Task<GateLab> CreateAsync(params string[] permissions)
        {
            var lab = await VerifyLab.CreateAsync();
            lab.Define(Definitions);
            var options = new List<string> { "-z", "7", "-O", "extension:permit-bearer@meep" };
            foreach (var permission in permissions)
            {
                var colon = permission.IndexOf(':', StringComparison.Ordinal);
                options.AddRange(["-O", colon < 0 ? $"extension:{permission}@meep" : $"extension:{permission[..colon]}@meep={permission[(colon + 1)..]}"]);
            }
            var credential = await lab.IssueAsync([.. options]);
            var file = Path.Combine(lab.Home, "alice.credential");
            System.IO.File.WriteAllText(file, credential.ToText() + " alice@laptop\n");
            return new GateLab(lab, credential, file);
        }

        public void Dispose() => Lab.Dispose();
    }
}
