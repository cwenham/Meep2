using Meep2.Modules;
using Meep2.Runtime.Verifying;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;
using Meep2.Security;
using Meep2.Shared;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>
/// Meep's own permissions, a pipeline's own credential, <c>#holds</c>, and scopes matched against real paths and
/// endpoints (permissions phase 6).
/// </summary>
public class PipelineCredentialTests
{
    private static PermissionName Name(string text) => PermissionName.Parse(text);

    private static IReadOnlyList<PermissionDenied> Denials(TestRun run, string module) => [.. run.At(module).OfType<PermissionDenied>()];

    private static IEnumerable<Message> Ancestors(Message message)
    {
        for (var parent = message.Parent; parent is not null; parent = parent.Parent)
            yield return parent;
    }

    private static PipelineCredential Nobody => PipelineCredential.None("it came from nowhere in particular");

    // ---- A pipeline's own credential ----

    [Test]
    public async Task An_owners_pipeline_reads_and_writes_files_without_a_trust_store()
    {
        using var folder = new TemporaryFolder();
        var input = folder.Write("in.txt", "hello");

        var run = await TestRun.Start($"""
            Read "{input}"
                Save "{folder.Path}/out.txt" mode=overwrite
                    Sink @no-errors
            Numbers to=2
                Save "{folder.Path}/numbers.txt"
            """);

        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(run.Unhandled).IsEmpty();
        await Assert.That(File.ReadAllText(Path.Combine(folder.Path, "out.txt"))).IsEqualTo("hello");
        await Assert.That(File.ReadAllLines(Path.Combine(folder.Path, "numbers.txt")).Length).IsEqualTo(2);

        // Every message a source emits descends from the pipeline's own verification.
        var verified = Ancestors(run.At("Sink").Single()).OfType<VerifiedMessage>().Single();
        await Assert.That(verified.Kind).IsEqualTo(VerificationKind.Pipeline);
        await Assert.That(verified.Proof).IsEqualTo(Proof.Pipeline);
        await Assert.That(verified.IsGenuine).IsTrue();
        await Assert.That(verified.Success).IsTrue();
        await Assert.That(verified.Expires).IsNull();
        await Assert.That(verified.Permissions).IsEquivalentTo(MeepPermissions.ForPipelines);
        var credential = (PipelineCredential)verified.Subject;
        await Assert.That(credential.Vouch).IsEqualTo(PipelineVouch.Owner);
        await Assert.That(credential.Holder).IsEqualTo(Environment.UserName);
        await Assert.That(verified.Parents.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_pipeline_nobody_vouches_for_can_compute_but_not_touch_the_machine()
    {
        using var folder = new TemporaryFolder();
        var input = folder.Write("in.txt", "hello");

        var run = await TestRun.Start($"""
            Numbers to=2
                Save "{folder.Path}/out.txt"
                    Sink as Refusals @errors
                Sink as Plain @no-errors
            Read "{input}"
                Sink as ReadErrors @errors
            """, credential: Nobody);

        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(File.Exists(Path.Combine(folder.Path, "out.txt"))).IsFalse();
        await Assert.That(run.At("Plain").Count).IsEqualTo(2);
        var refusals = Denials(run, "Refusals");
        await Assert.That(refusals.Count).IsEqualTo(2);
        await Assert.That(refusals[0].Needed.Single()).IsEqualTo(Name("meep.files.write"));
        await Assert.That(refusals[0].Reasons.Single()).Contains("This pipeline has no credential of its own, as it came from nowhere in particular, so it doesn't hold meep.files.write for");

        // A source is refused once, and stops rather than being restarted.
        var read = Denials(run, "ReadErrors").Single();
        await Assert.That(read.Needed.Single()).IsEqualTo(Name("meep.files.read"));
        await Assert.That(run.Notices.Any(n => n.StartsWith("Read #", StringComparison.Ordinal) && n.Contains("stopped", StringComparison.Ordinal))).IsTrue();
        var stats = run.Result.Pipeline!.GetStats(run.Result.Pipeline.Modules.Single(m => m.Verb == "Read"));
        await Assert.That(stats.Denied).IsEqualTo(1);
        await Assert.That(stats.Restarts).IsEqualTo(0);
    }

    [Test]
    public async Task Holds_narrows_what_a_pipeline_starts_with()
    {
        using var folder = new TemporaryFolder();
        var input = folder.Write("in.txt", "hello");

        var run = await TestRun.Start($"""
            #holds "meep.files.write:{folder.Path}/notes/**"
            Numbers to=1
                Save "{folder.Path}/notes/a.txt"
                    Sink as Inside @errors
                Save "{folder.Path}/b.txt"
                    Sink as Outside @errors
            Read "{input}"
                Sink as ReadErrors @errors
            """);

        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(File.Exists(Path.Combine(folder.Path, "notes", "a.txt"))).IsTrue();
        await Assert.That(run.At("Inside")).IsEmpty();
        await Assert.That(File.Exists(Path.Combine(folder.Path, "b.txt"))).IsFalse();
        await Assert.That(Denials(run, "Outside").Single().Reasons.Single()).Contains("doesn't hold meep.files.write for");
        await Assert.That(Denials(run, "ReadErrors").Single().Reasons.Single()).Contains("doesn't hold meep.files.read");
        await Assert.That(run.Result.Pipeline!.Options.Holds).IsEquivalentTo([Name($"meep.files.write:{folder.Path}/notes/**")]);
    }

    [Test]
    public async Task Holds_none_holds_nothing_and_holds_lines_report_their_mistakes()
    {
        using var folder = new TemporaryFolder();
        var none = await TestRun.Start($"""
            #holds none
            Numbers to=1
                Save "{folder.Path}/out.txt"
                    Sink as Refusals @errors
            """);
        await Assert.That(none.Diagnostics).IsEmpty();
        await Assert.That(Denials(none, "Refusals").Count).IsEqualTo(1);
        await Assert.That(none.Result.Pipeline!.Options.Holds).IsEmpty();

        foreach (var (text, expected) in new[]
        {
            ("#holds ReadNotes\nNumbers to=1\n", "ReadNotes isn't one of Meep's own permissions"),
            ("#holds meep.file.read\nNumbers to=1\n", "Meep has no permission called meep.file.read"),
            ("#holds\nNumbers to=1\n", "#holds lists which of Meep's own permissions the pipeline starts with"),
            ("Numbers to=1\n#holds none\n", "#holds goes before the pipeline's modules."),
            ("  #holds none\nNumbers to=1\n", "#holds goes at the left margin."),
        })
        {
            var syntax = Script.Parse(text);
            await Assert.That(Script.Diagnostics(syntax)).Contains(expected);
        }

        // What #holds lists but the credential doesn't hold is noted, not held.
        var noted = await TestRun.Start("#holds meep.files.read\nNumbers to=1\n    Sink\n", credential: Nobody);
        var verified = Ancestors(noted.At("Sink").Single()).OfType<VerifiedMessage>().Single();
        await Assert.That(verified.Permissions).IsEmpty();
        await Assert.That(verified.Notes.Single()).Contains("#holds lists meep.files.read, but this pipeline has no credential of its own");
    }

    [Test]
    public async Task A_gated_source_starts_when_the_pipeline_holds_what_it_needs()
    {
        var holds = await TestRun.Start("Numbers to=1 @permission=meep.files.read\n    Sink\n");
        await Assert.That(holds.At("Sink").Count).IsEqualTo(1);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => TestRun.Start("Numbers to=1 @permission=meep.files.read\n    Sink\n", credential: Nobody));
        await Assert.That(failure!.Message).Contains("Numbers #1 can't start: it needs meep.files.read, which the pipeline must hold for its sources to start.");
        await Assert.That(failure.Message).Contains("has no credential of its own");
    }

    [Test]
    public async Task A_message_sent_in_from_outside_holds_nothing_until_clients_present_credentials()
    {
        using var folder = new TemporaryFolder();
        await using (var running = TestRun.Begin($"""
            Interval 1h
                Save "{folder.Path}/sent.txt" as Keep
            """))
        {
            running.Pipeline.Inject(running.Pipeline.Modules.Single(m => m.Name == "Keep"), new TextMessage("sent"));
            await Wait.Until(() => running.Run.Unhandled.OfType<PermissionDenied>().Any());
            var denial = running.Run.Unhandled.OfType<PermissionDenied>().Single();
            await Assert.That(denial.Needed.Single()).IsEqualTo(Name("meep.files.write"));
            await Assert.That(denial.Parent!.Parents.Count).IsEqualTo(0).Because("it doesn't descend from the pipeline's own credential");
        }
        await Assert.That(File.ReadAllText(Path.Combine(folder.Path, "sent.txt"))).DoesNotContain("sent");
    }

    [Test]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task An_unsigned_file_earns_the_owners_credential_only_if_nobody_else_can_change_it()
    {
        Skip.When(OperatingSystem.IsWindows(), "File modes are Unix's.");
        using var folder = new TemporaryFolder();
        var file = folder.Write("mine.meep", "Interval 1s\n");
        await Assert.That(Meep2.Daemon.PipelineSources.CredentialForFile(file).Vouch).IsEqualTo(PipelineVouch.Owner);

        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
        var writable = Meep2.Daemon.PipelineSources.CredentialForFile(file);
        await Assert.That(writable.Vouch).IsEqualTo(PipelineVouch.Nobody);
        await Assert.That(writable.Why).IsEqualTo("its file is on this machine, but it can be changed by others");
        await Assert.That(Meep2.Shared.PrivateFolders.WhyNotUsersAlone("/etc/hosts")).IsEqualTo("it belongs to someone else");

        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(folder.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        await Assert.That(Meep2.Daemon.PipelineSources.CredentialForFile(file).Why).IsEqualTo($"its file is on this machine, but the folder {folder.Path} can be changed by others");
        // And every folder above it, up to the root (Q320).
        var deeper = Directory.CreateDirectory(Path.Combine(folder.Path, "deeper")).FullName;
        var nested = Path.Combine(deeper, "nested.meep");
        File.WriteAllText(nested, "Interval 1s\n");
        File.SetUnixFileMode(nested, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(Meep2.Daemon.PipelineSources.CredentialForFile(nested).Why).IsEqualTo($"its file is on this machine, but the folder {folder.Path} can be changed by others");
        // A sticky folder, as /tmp is, is fine: only the file's owner can replace it there.
        File.SetUnixFileMode(folder.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute | UnixFileMode.StickyBit);
        await Assert.That(Meep2.Daemon.PipelineSources.CredentialForFile(file).Vouch).IsEqualTo(PipelineVouch.Owner);
        File.SetUnixFileMode(folder.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // A pipeline built without saying who vouches for it holds nothing.
        await Assert.That(new Meep2.Runtime.PipelineOptions().Credential.Vouch).IsEqualTo(PipelineVouch.Nobody);
    }

    [Test]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A_trust_store_that_cant_be_read_holds_back_even_meeps_own_permissions()
    {
        Skip.When(OperatingSystem.IsWindows(), "File modes are Unix's.");
        using var home = new TemporaryFolder();
        var trust = TrustFolder.In(home.Path);
        trust.Create();
        // A file in it others can change makes the store one Meep won't trust.
        File.WriteAllText(trust.Permissions, "");
        File.SetUnixFileMode(trust.Permissions, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var source = TrustStoreSource.Watching(trust, logged.Enqueue);

        var run = await TestRun.Start($"""
            Numbers to=1
                Save "{home.Path}/out.txt"
                    Sink as Refused @errors
            """, trust: source);

        await Assert.That(Denials(run, "Refused").Single().Reasons.Single()).Contains("trust store can't be read");
        await Assert.That(File.Exists(Path.Combine(home.Path, "out.txt"))).IsFalse();
        await Assert.That(logged.Any(line => line.Contains("can't be read", StringComparison.Ordinal))).IsTrue();
    }

    // ---- Real paths ----

    [Test]
    public async Task Scoped_files_are_matched_by_their_real_form()
    {
        Skip.When(OperatingSystem.IsWindows(), "Symbolic links need privileges on Windows.");
        using var folder = new TemporaryFolder();
        var notes = Directory.CreateDirectory(Path.Combine(folder.Path, "notes")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(folder.Path, "outside")).FullName;
        File.CreateSymbolicLink(Path.Combine(notes, "out"), outside);
        File.CreateSymbolicLink(Path.Combine(notes, "gone"), Path.Combine(outside, "new.txt"));

        var run = await TestRun.Start($"""
            #holds "meep.files.write:{notes}/**"
            Numbers to=1
                Save "{notes}/out/linked.txt"
                    Sink as Linked @errors
                Save "{notes}/gone"
                    Sink as Dangling @errors
                Save "{notes}/sub/../climbed.txt"
                    Sink as Climbed @errors
            """);

        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(Denials(run, "Linked").Count).IsEqualTo(1).Because("the link leads outside notes");
        await Assert.That(File.Exists(Path.Combine(outside, "linked.txt"))).IsFalse();
        await Assert.That(Denials(run, "Dangling").Count).IsEqualTo(1).Because("writing through a dangling link would create its target outside");
        await Assert.That(File.Exists(Path.Combine(outside, "new.txt"))).IsFalse();
        await Assert.That(run.At("Climbed")).IsEmpty();
        await Assert.That(File.Exists(Path.Combine(notes, "climbed.txt"))).IsTrue();
    }

    [Test]
    public async Task Real_paths_follow_links_and_take_the_case_on_disk()
    {
        Skip.When(OperatingSystem.IsWindows(), "Real paths are worked out with realpath on macOS and Linux.");
        using var folder = new TemporaryFolder();
        var notes = Directory.CreateDirectory(Path.Combine(folder.Path, "notes")).FullName;
        var real = RealPaths.Of(notes);
        File.CreateSymbolicLink(Path.Combine(folder.Path, "link"), notes);

        await Assert.That(RealPaths.Of(Path.Combine(folder.Path, "link", "a.txt"))).IsEqualTo(Path.Combine(real, "a.txt"));
        await Assert.That(RealPaths.Of(Path.Combine(notes, "new", "deeper.txt"))).IsEqualTo(Path.Combine(real, "new", "deeper.txt"));
        await Assert.That(RealPaths.Of(Path.Combine(notes, "x", "..", "y.txt"))).IsEqualTo(Path.Combine(real, "y.txt"));
        await Assert.That(RealPaths.Of("~")).IsEqualTo(RealPaths.Of(HomeFolder.Path));

        // On a disk that ignores case, as a Mac's usually does, the case written gives way to the case on disk.
        var shouted = Path.Combine(folder.Path, "NOTES", "a.txt");
        if (Directory.Exists(Path.Combine(folder.Path, "NOTES")))
            await Assert.That(RealPaths.Of(shouted)).IsEqualTo(Path.Combine(real, "a.txt"));

        // A scope's fixed start is read the same way, so a scope written through a link, or in the wrong case, matches.
        var scope = PermissionScope.Parse($"{folder.Path}/link/**");
        await Assert.That(Targets.Matches(scope, PermissionTarget.File(Path.Combine(notes, "a.txt")))).IsTrue();
        await Assert.That(Targets.Matches(scope, PermissionTarget.File(Path.Combine(folder.Path, "other.txt")))).IsFalse();
    }

    // ---- Endpoints ----

    [Test]
    [Arguments("*.example.com:443", "API.Example.com", 443, true)]
    [Arguments("*.example.com:443", "api.example.com", 8443, false)]
    [Arguments("*.example.com", "api.example.com", 8443, true)]
    [Arguments("example.com", "example.com.", 80, true)]
    [Arguments("example.com", "api.example.com", 80, false)]
    [Arguments("localhost:*", "localhost", 0, true)]
    [Arguments("[::1]", "::1", 8080, true)]
    [Arguments("[::1]:8080", "[::1]", 8080, true)]
    [Arguments("xn--bcher-kva.example", "bücher.example", 443, true)]
    [Arguments("*:8080", "0.0.0.0", 8080, true)]
    [Arguments("127.0.0.1", "localhost", 80, false)]
    public async Task Network_scopes_match_hosts_as_written_and_ports(string scope, string host, int port, bool matches)
    {
        await Assert.That(Targets.Matches(PermissionScope.Parse(scope), PermissionTarget.Endpoint(host, port))).IsEqualTo(matches);
    }

    [Test]
    public async Task Fetch_and_Send_need_where_they_send_and_where_a_redirect_leads()
    {
        await using var server = new TestServer(async context =>
        {
            if (context.Request.Url!.AbsolutePath == "/away")
            {
                context.Response.StatusCode = 302;
                context.Response.RedirectLocation = context.Request.Url.ToString().Replace("127.0.0.1", "localhost", StringComparison.Ordinal).Replace("/away", "/ok", StringComparison.Ordinal);
                context.Response.Close();
                return;
            }
            await TestServer.Reply(context, 200, "fine", "text/plain");
        });
        var local = server.Url.Replace("127.0.0.1", "localhost", StringComparison.Ordinal);

        var run = await TestRun.Start($"""
            #holds meep.network.send:127.0.0.1
            Numbers to=1
                Fetch "{server.Url}/ok"
                    Sink as Fetched @no-errors
                Fetch "{server.Url}/away"
                    Sink as Redirected @errors
                Fetch "{local}/ok"
                    Sink as ByName @errors
                Send "{local}/ok"
                    Sink as Posted @errors
                Send "tcp://localhost:9" text="hi"
                    Sink as Tcp @errors
                Send "ws://localhost:9/" text="hi"
                    Sink as WebSocket @errors
            """);

        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(run.At("Fetched").Single().ToText()).IsEqualTo("fine");
        foreach (var refused in new[] { "Redirected", "ByName", "Posted", "Tcp", "WebSocket" })
        {
            var denial = Denials(run, refused).SingleOrDefault();
            await Assert.That(denial).IsNotNull().Because(refused);
            await Assert.That(denial!.Needed.Single()).IsEqualTo(Name("meep.network.send"));
            await Assert.That(denial.Reasons.Single()).Contains("doesn't hold meep.network.send for localhost:");
        }
    }

    [Test]
    public async Task A_listen_needs_the_pipeline_to_hold_its_address()
    {
        var run = await TestRun.Start("""
            #holds meep.network.listen:localhost:1
            Listen "http://localhost:0/"
                Sink as Requests @no-errors
                Sink as Refused @errors
            """);

        await Assert.That(run.Diagnostics).IsEmpty();
        var denial = Denials(run, "Refused").Single();
        await Assert.That(denial.Needed.Single()).IsEqualTo(Name("meep.network.listen"));
        await Assert.That(denial.Reasons.Single()).Contains("doesn't hold meep.network.listen for localhost:0");
        await Assert.That(run.Result.Pipeline!.GetListening()).IsEmpty();
    }

    // ---- Trees ----

    [Test]
    public async Task Trees_need_to_read_the_file_they_name_and_to_write_the_file_they_save()
    {
        using var folder = new TemporaryFolder();
        var run = await TestRun.Start($"""
            #holds "meep.files.read:{folder.Path}/**"
            Numbers to=1
                Learn "saving" class="a" file="{folder.Path}/saving.json"
                    Sink as Saving @errors
                Learn "reading" class="a" file="{folder.Path}/reading.json" save-every=0
                    Sink as Reading @errors
                Classify "reading" file="{folder.Path}/reading.json"
                    Sink as Classifying @errors
                Forget "reading" file="{folder.Path}/reading.json"
                    Sink as Forgetting @errors
            """);

        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(Denials(run, "Saving").Single().Needed.Single()).IsEqualTo(Name("meep.files.write"));
        await Assert.That(run.At("Reading")).IsEmpty();
        await Assert.That(run.At("Classifying")).IsEmpty();
        await Assert.That(Denials(run, "Forgetting").Single().Needed.Single()).IsEqualTo(Name("meep.files.write"));
    }

    // ---- Meep's definitions, the trust store and proof ----

    [Test]
    public async Task Meeps_permissions_are_defined_without_a_store_and_the_store_can_only_narrow_them()
    {
        await Assert.That(PermissionDefinitions.None.Find("meep.files.read")).IsNotNull();
        await Assert.That(PermissionDefinitions.None.Find("meep.daemon.control")).IsNotNull();
        await Assert.That(PermissionDefinitions.None.Find("meep.file.read")).IsNull();
        var misspelt = PermissionDefinitions.Parse("#permission meep.file.read\n", []);
        await Assert.That(misspelt.Problems.Single()).Contains("Meep has no permission called meep.file.read");
        await Assert.That(MeepPermissions.All.Count).IsEqualTo(8);
    }

    [Test]
    public async Task A_definition_in_the_store_narrows_what_pipelines_hold()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        using var folder = new TemporaryFolder();
        var options = new List<string> { "-z", "7", "-O", "extension:permit-bearer@meep", "-O", "extension:WriteNotes@meep", "-O", "extension:meep.files.write@meep" };
        var alice = await lab.IssueAsync([.. options]);
        var file = Path.Combine(lab.Home, "alice.credential");
        File.WriteAllText(file, alice.ToText() + " alice@laptop\n");

        // Writing needs WriteNotes too: the pipeline alone can't, but a request from alice, who holds both, can.
        lab.Define("#permission WriteNotes\n#permission meep.files.write requires=WriteNotes\n");
        var run = await TestRun.Start($"""
            Numbers to=1
                Save "{folder.Path}/alone.txt"
                    Sink as Alone @errors
            Read "{file}"
                Verify bearer=true
                    Save "{folder.Path}/alice.txt"
                        Sink as Alice @errors
            """, trust: TrustStoreSource.Of(lab.Store()));
        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(Denials(run, "Alone").Single().Reasons.Single()).Contains("meep.files.write requires WriteNotes");
        await Assert.That(run.At("Alice")).IsEmpty();
        await Assert.That(File.Exists(Path.Combine(folder.Path, "alice.txt"))).IsTrue();

        // Only the ca may confer it, and the pipeline's own credential is conferred by this machine's authority.
        lab.Define("#permission meep.files.write authority=ca\n");
        var authority = await TestRun.Start($"""
            Numbers to=1
                Save "{folder.Path}/ca.txt"
                    Sink as Refused @errors
            Numbers to=1
                Sink as Misspelt @permission=meep.file.read
                    Sink as Unknown @errors
            """, trust: TrustStoreSource.Of(lab.Store()));
        await Assert.That(Denials(authority, "Refused").Single().Reasons.Single()).Contains("holds meep.files.write, but only ca may confer it, and not pipelines: add authority=pipeline to the definition to let them.");
        await Assert.That(Denials(authority, "Unknown").Single().Reasons.Single()).Contains("Meep has no permission called meep.file.read");
    }

    [Test]
    public async Task Proof_says_whether_the_pipelines_own_credential_will_do()
    {
        var run = await TestRun.Start("""
            Numbers to=1
                Sink as Signed @permission=meep.files.read @proof=signed
                    Sink as Refused @errors
                Sink as Pipeline @permission=meep.files.read @proof=pipeline @no-errors
            """);

        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(Denials(run, "Refused").Single().Reasons.Single()).Contains("was verified with the pipeline's own proof, when it loaded, and this needs a signed request, a connection's proof or TLS");
        await Assert.That(run.At("Pipeline").Count).IsEqualTo(1);
    }

    // ---- After the answers ----

    [Test]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Meeps_own_folder_is_out_of_reach_but_its_trees()
    {
        using var meep = new TemporaryFolder();
        using var elsewhere = new TemporaryFolder();
        Directory.CreateDirectory(Path.Combine(meep.Path, "trust"));
        File.CreateSymbolicLink(Path.Combine(elsewhere.Path, "into-trust"), Path.Combine(meep.Path, "trust"));
        var authorities = meep.Write(Path.Combine("trust", "authorities"), "");

        var run = await TestRun.Start($"""
            Numbers to=1
                Save "{authorities}"
                    Sink as Trust @errors
                Save "{elsewhere.Path}/into-trust/grants"
                    Sink as Linked @errors
                Save "{meep.Path}/trees/mine.txt"
                    Sink as Trees @errors
                Fetch "{meep.Path}/trust/authorities"
                    Sink as Read @errors
            """, meepFolder: meep.Path);

        await Assert.That(run.Diagnostics).IsEmpty();
        await Assert.That(Denials(run, "Trust").Single().Reasons.Single()).Contains("is in Meep's own folder");
        await Assert.That(Denials(run, "Linked").Single().Reasons.Single()).Contains("is in Meep's own folder");
        await Assert.That(Denials(run, "Read").Single().Needed.Single()).IsEqualTo(Name("meep.files.read"));
        await Assert.That(run.At("Trees")).IsEmpty();
        await Assert.That(File.ReadAllText(authorities)).IsEqualTo("");
        await Assert.That(File.Exists(Path.Combine(meep.Path, "trees", "mine.txt"))).IsTrue();

        // A tree in its own place needs no permission at all (Q314), even in a pipeline that holds none.
        var trees = await TestRun.Start("""
            #holds none
            Numbers to=1
                Learn "nothing-held" class="a" save-every=0
                    Sink as Learned @errors
            """);
        await Assert.That(trees.At("Learned")).IsEmpty();
    }

    [Test]
    [Arguments("8.8.8.8", NetworkReach.Public)]
    [Arguments("127.0.0.1", NetworkReach.Private)]
    [Arguments("10.1.2.3", NetworkReach.Private)]
    [Arguments("172.20.0.1", NetworkReach.Private)]
    [Arguments("172.32.0.1", NetworkReach.Public)]
    [Arguments("192.168.1.1", NetworkReach.Private)]
    [Arguments("169.254.169.254", NetworkReach.Private)]
    [Arguments("100.100.1.1", NetworkReach.Private)]
    [Arguments("224.0.0.1", NetworkReach.None)]
    [Arguments("0.0.0.0", NetworkReach.None)]
    [Arguments("::", NetworkReach.None)]
    [Arguments("::1", NetworkReach.Private)]
    [Arguments("fd00::1", NetworkReach.Private)]
    [Arguments("fe80::1", NetworkReach.Private)]
    [Arguments("2606:4700::1111", NetworkReach.Public)]
    [Arguments("::ffff:192.168.0.1", NetworkReach.Private)]
    public async Task Addresses_are_public_or_private(string address, NetworkReach reach)
    {
        await Assert.That(NetworkAddresses.ReachOf(System.Net.IPAddress.Parse(address))).IsEqualTo(reach);
    }

    [Test]
    public async Task Public_and_private_scopes_are_checked_against_the_address_connected_to()
    {
        // Keyword scopes never match a host by name, only the kind of address.
        await Assert.That(Targets.Matches(PermissionScope.Parse("public"), PermissionTarget.Endpoint("public", 80))).IsFalse();
        await Assert.That(Targets.Matches(PermissionScope.Parse("public"), PermissionTarget.Addresses(NetworkReach.Public, 80))).IsTrue();
        await Assert.That(Targets.Matches(PermissionScope.Parse("private:443"), PermissionTarget.Addresses(NetworkReach.Private, 80))).IsFalse();
        await Assert.That(Targets.Matches(PermissionScope.Parse("*.example.com"), PermissionTarget.Addresses(NetworkReach.Public, 80))).IsFalse();

        await using var server = new TestServer(context => TestServer.Reply(context, 200, "fine", "text/plain"));
        var port = new Uri(server.Url).Port;
        var byName = $"http://localhost:{port}/ok";
        var run = await TestRun.Start($"""
            #holds meep.network.send:private
            Numbers to=1
                Fetch "{byName}"
                    Sink as Private @errors
                Fetch "{server.Url}/ok"
                    Sink as Literal
            """);
        await Assert.That(run.Diagnostics).IsEmpty();
        // It reaches the server, which answers 400 only because it serves 127.0.0.1 by that name alone.
        await Assert.That(run.At("Private").Single()).IsTypeOf<HttpErrorMessage>();
        await Assert.That(run.At("Literal").Single().ToText()).IsEqualTo("fine");
        var everywhere = await TestRun.Start("""
            #holds meep.network.listen:private
            Listen "http://0.0.0.0:0/"
                Sink as Everywhere @errors
            """);
        await Assert.That(Denials(everywhere, "Everywhere").Single().Reasons.Single()).Contains("doesn't hold meep.network.listen for 0.0.0.0:0");

        var publicOnly = await TestRun.Start($"""
            #holds meep.network.send:public
            Numbers to=1
                Fetch "{byName}"
                    Sink as Fetched @errors
                Fetch "{server.Url}/ok"
                    Sink as Literal @errors
                Send "tcp://localhost:{port}" text="hi"
                    Sink as Tcp @errors
            """);
        await Assert.That(publicOnly.Diagnostics).IsEmpty();
        await Assert.That(Denials(publicOnly, "Fetched").Single().Reasons.Single()).Contains("and the message may send only to public addresses there");
        await Assert.That(Denials(publicOnly, "Literal").Single().Reasons.Single()).Contains("doesn't hold meep.network.send for 127.0.0.1");
        await Assert.That(Denials(publicOnly, "Tcp").Single().Reasons.Single()).Contains("and the message may send only to public addresses there");
    }

    [Test]
    public async Task Pipelines_are_bound_by_authority_only_unless_named_as_pipeline()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        using var folder = new TemporaryFolder();
        lab.Define("#permission meep.files.write authority=ca authority=pipeline\n");
        var run = await TestRun.Start($"""
            Numbers to=1
                Save "{folder.Path}/ok.txt"
                    Sink as Saved @errors
            """, trust: TrustStoreSource.Of(lab.Store()));
        await Assert.That(run.At("Saved")).IsEmpty();
        await Assert.That(lab.Store().Permissions.Problems).IsEmpty().Because("pipeline needs no authority in the store");
        await Assert.That(() => lab.Folder.AddAuthority("pipeline", PublicKey.Parse("x", File.ReadAllText(lab.Credentials.AuthorityPublicKeyFile)), [TrustStore.CredentialNamespace]))
            .Throws<FormatException>().WithMessageContaining("pipeline is kept for pipelines' own credentials");
    }

    [Test]
    public async Task A_listens_certificate_and_key_need_meep_files_read()
    {
        using var folder = new TemporaryFolder();
        var certificate = folder.Write("server.pem", "not really a certificate");
        var run = await TestRun.Start($"""
            #holds meep.network.listen
            Listen "https://localhost:0/" certificate="{certificate}" key="{certificate}"
                Sink as Refused @errors
            """);
        await Assert.That(run.Diagnostics).IsEmpty();
        var denial = Denials(run, "Refused").Single();
        await Assert.That(denial.Needed.Single()).IsEqualTo(Name("meep.files.read"));
        await Assert.That(denial.Reasons.Single()).Contains("server.pem");
    }

    [Test]
    public async Task A_revoked_signers_pipelines_hold_nothing()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        using var folder = new TemporaryFolder();
        var signer = PipelineCredential.SignedBy("publisher", lab.Granter.Public.Fingerprint);
        var text = $"""
            Numbers to=1
                Save "{folder.Path}/signed.txt"
                    Sink as Refused @errors
            """;

        var before = await TestRun.Start(text, trust: TrustStoreSource.Of(lab.Store()), credential: signer);
        await Assert.That(before.At("Refused")).IsEmpty();

        lab.Folder.Revoke(Revoking.Key(lab.Granter.Public.Fingerprint));
        var after = await TestRun.Start(text, trust: TrustStoreSource.Of(lab.Store()), credential: signer);
        var denial = Denials(after, "Refused").Single();
        await Assert.That(denial.Reasons.Single()).Contains("This pipeline's own credential, publisher's, no longer counts: its key");
        var verified = Ancestors(denial).OfType<VerifiedMessage>().Single(v => v.Kind == VerificationKind.Pipeline);
        await Assert.That(verified.Notes.Single()).Contains("It's signed by publisher, but its key");
    }

    [Test]
    public async Task A_running_source_stops_when_what_it_started_with_is_taken_away()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await VerifyLab.CreateAsync();
        var trust = TrustStoreSource.Of(lab.Store());
        await using var running = TestRun.Begin("""
            Listen "http://localhost:0/"
                Sink as Requests @no-errors
                Sink as Refused @errors
            """, trust: trust);
        await Wait.Until(() => running.Pipeline.GetListening().Count == 1);

        // The store now lets only the ca confer meep.network.listen, so the pipeline no longer holds it.
        lab.Define("#permission meep.network.listen authority=ca\n");
        trust.Reload();
        await Wait.Until(() => running.Run.At("Refused").Count == 1);
        var denial = (PermissionDenied)running.Run.At("Refused").Single();
        await Assert.That(denial.Needed.Single()).IsEqualTo(Name("meep.network.listen"));
        await Assert.That(running.Run.Notices.Any(n => n.Contains("stopped, as the pipeline no longer holds meep.network.listen", StringComparison.Ordinal))).IsTrue();
        await Wait.Until(() => running.Pipeline.GetListening().Count == 0);
    }

    // ---- Authorities' keys ----

    [Test]
    public async Task An_authoritys_key_is_read_from_a_pub_line()
    {
        Skip.Unless(SshKeygen.Available, "This needs OpenSSH's ssh-keygen.");
        using var lab = await CredentialLab.CreateAsync();
        var line = File.ReadAllText(lab.AuthorityPublicKeyFile).Trim();

        var key = AuthorityKey.Parse(line);
        await Assert.That(key.KeyType).IsEqualTo("ssh-ed25519");
        await Assert.That(key.Fingerprint).IsEqualTo(PublicKey.Parse("ca", line).Fingerprint);
        await Assert.That(key.ToText()).IsEqualTo(line);
        await Assert.That(SecurityText.KindOf(line)).IsEqualTo(SecurityTextKind.AuthorityKey);
        await Assert.That(SecurityText.Read<AuthorityKey>(line).Fingerprint).IsEqualTo(key.Fingerprint);

        var certificate = await lab.IssueTextAsync("-I", "alice");
        await Assert.That(() => AuthorityKey.Parse(certificate)).Throws<FormatException>().WithMessageContaining("This is a certificate, which is a credential");
        await Assert.That(() => AuthorityKey.Parse("ssh-ed25519")).Throws<FormatException>().WithMessageContaining("as a .pub file has it");
        await Assert.That(() => AuthorityKey.Parse("ssh-dss AAAA")).Throws<FormatException>().WithMessageContaining("isn't a kind of public key Meep reads");
        await Assert.That(() => AuthorityKey.Parse("ssh-ed25519 not-base64!")).Throws<FormatException>().WithMessageContaining("base64");
        var rsaBytes = Convert.ToBase64String(new SshWireWriter().Text("ssh-rsa").ToArray());
        await Assert.That(() => AuthorityKey.Parse($"ssh-ed25519 {rsaBytes}")).Throws<FormatException>().WithMessageContaining("but its bytes are ssh-rsa");
    }
}
