using Meep2.SDK.Messages;
using Meep2.SDK.Security;

namespace Meep2.SDK.Modules;

/// <summary>
/// What a module uses to talk to its pipeline. The runtime provides it.
/// </summary>
public abstract class ModuleContext
{
    internal ModuleContext()
    {
    }

    /// <summary>The module's place in the pipeline.</summary>
    public abstract ModuleInfo Module { get; }

    /// <summary>
    /// Cancelled when the pipeline is asked to stop. Messages already in the pipeline still get time to finish, so
    /// most modules can ignore this. A module that waits a long time, such as one running another pipeline, can use
    /// it to wind down.
    /// </summary>
    public abstract CancellationToken Stopping { get; }

    /// <summary>
    /// The name of the module's pipeline: its file's name without the extension, such as <c>nextbus</c> for
    /// <c>nextbus.meep</c>. Modules that keep things for their pipeline, such as Learn's classification trees, use it to
    /// tell pipelines apart.
    /// </summary>
    public virtual string PipelineName => "Pipeline";

    /// <summary>
    /// Sends a message to every module downstream. A module that handles messages can only emit while it's
    /// handling one; a new message becomes a child of the message being handled.
    /// </summary>
    /// <remarks>Emitting never waits: if a downstream module's buffer is full, its oldest message is dropped.</remarks>
    public abstract void Emit(Message message);

    /// <summary>
    /// Emits a new message carrying a lease, such as a request waiting for its reply. The lease stays open while the
    /// message, or any message made from it, is in the pipeline, and while anything holds it. A lease can go out
    /// with several messages, such as each line read from one connection.
    /// </summary>
    public abstract void Emit(Message message, Lease lease);

    /// <summary>
    /// Emits a new message and reports when it, and every message made from it, has left the pipeline.
    /// Sources use this to acknowledge or commit what they read.
    /// </summary>
    /// <remarks>
    /// Don't wait for the result inside <c>HandleAsync</c> if messages downstream can come back to this module:
    /// it would be waiting for itself.
    /// </remarks>
    public abstract Task<MessageOutcome> EmitTracked(Message message);

    /// <summary>
    /// Emits an error in place of the message being handled. Modules use this for failures they expect, such as a
    /// server that doesn't answer, so the runtime doesn't have to replace them.
    /// </summary>
    public void EmitError(string error, Exception? exception = null) =>
        Emit(new ErrorMessage { Error = error, Exception = exception });

    /// <summary>
    /// Which of the module's inputs the message being handled came in on, counting from 1 in the order the lines
    /// feeding the module are written (round 3, section 2). A module fed by one line always sees 1; a message sent in
    /// as a test, 0. Zip uses it to tell left from right.
    /// </summary>
    public virtual int Input => 1;

    /// <summary>
    /// Keeps a message after the module has finished handling it, as Batch and Zip do while they wait for the
    /// messages that go with it. It stays in flight, with its leases open, until the module emits a message made from
    /// it (one with it among its <see cref="Message.Parents"/>) or lets it go with <see cref="LetGo"/>.
    /// </summary>
    /// <remarks>
    /// When nothing else is left for the pipeline to do, as when its sources have finished or it's stopping, the
    /// runtime asks each module that keeps messages to flush them (<see cref="AInflection.FlushAsync"/>). Messages
    /// still kept after that are let go as the pipeline halts.
    /// </remarks>
    public virtual void Keep(Message message) =>
        throw new NotSupportedException("Only a module that handles messages can keep them.");

    /// <summary>Stops keeping a message, which then finishes, unless a message made from it is still in flight.</summary>
    public virtual void LetGo(Message message) =>
        throw new NotSupportedException("Only a module that handles messages can keep them.");

    /// <summary>
    /// Asks the runtime to flush the module after a while (<see cref="AInflection.FlushAsync"/>), between messages,
    /// as a batch that's due to go out after a second does. A later request replaces an earlier one.
    /// </summary>
    public virtual void FlushAfter(TimeSpan delay) =>
        throw new NotSupportedException("Only a module that handles messages can be flushed.");

    /// <summary>
    /// While flushing, whether it's because nothing else is left for the pipeline to do, as when its sources have
    /// finished or it's stopping: the last chance to emit the messages the module keeps. False for a flush the module
    /// asked for with <see cref="FlushAfter"/>, and while handling a message.
    /// </summary>
    public virtual bool Finishing => false;

    /// <summary>
    /// How many messages the module's buffer holds, as set by <c>buffer</c> under <c>defaults</c>. A module that keeps messages can take
    /// it as its limit on how many to keep, as Zip does (Q131).
    /// </summary>
    public virtual int BufferSize => 1024;

    /// <summary>
    /// Says the module has answered an error for whoever was waiting, such as by sending a reply to the request it
    /// came from. Taking the error then doesn't count as handling it: unless a module after this one handles it, the
    /// error policy logs it, but doesn't count it towards stopping the pipeline (Q100). So a server that answers bad
    /// requests keeps a record of them, and can't be stopped by them.
    /// </summary>
    public void Answered(ErrorMessage error)
    {
        ArgumentNullException.ThrowIfNull(error);
        error.AnsweredBy = Module;
    }

    /// <summary>
    /// Says the module left an error alone, as Send does when it has nowhere sensible to send one. Taking the error
    /// then doesn't count as handling it, even as the last module in its branch, so it still reaches the error policy
    /// unless a module after this one handles it.
    /// </summary>
    public void LeftAlone(ErrorMessage error)
    {
        ArgumentNullException.ThrowIfNull(error);
        error.LeftAloneBy = Module;
    }

    /// <summary>
    /// Refuses <paramref name="message"/> unless it holds <paramref name="permission"/>, for a check finer than
    /// <c>@permission</c>, such as Fetch.File only for files outside its <c>root</c>: every line of the message's
    /// family must pass through a verified credential that holds it, as at a gate. If it doesn't, this throws
    /// <see cref="PermissionDeniedException"/>, which the runtime turns into a <see cref="PermissionDenied"/> error in
    /// place of the message, without counting it as the module failing.
    /// </summary>
    /// <param name="permission">The permission's name, as in <c>meep.files.read</c>, or a name and scope.</param>
    /// <param name="message">The message whose credentials count, usually the one being handled.</param>
    /// <param name="target">
    /// What the module is about to touch, such as a file's full path, which a scoped permission the message holds must
    /// match (<see cref="PermissionName.Allows"/>). Null needs the permission as named.
    /// </param>
    /// <exception cref="PermissionDeniedException">The message doesn't hold it.</exception>
    /// <exception cref="FormatException"><paramref name="permission"/> isn't a permission's name.</exception>
    public void Demand(string permission, Message message, string? target = null)
    {
        if (WhyNotHeld(permission, message, target) is { } why)
            throw new PermissionDeniedException(PermissionName.Parse(permission), why);
    }

    /// <summary>
    /// Whether <paramref name="message"/> holds <paramref name="permission"/>, as <see cref="Demand"/> checks, without
    /// refusing it.
    /// </summary>
    /// <exception cref="FormatException"><paramref name="permission"/> isn't a permission's name.</exception>
    public bool Holds(string permission, Message message, string? target = null) => WhyNotHeld(permission, message, target) is null;

    /// <summary>
    /// Refuses unless <paramref name="message"/> may <paramref name="permission"/> the file at <paramref name="path"/>,
    /// as <see cref="Demand"/> does, matching a scoped permission against the path's real form: whole, with symbolic
    /// links and <c>..</c> followed, in the case it has on disk (Q269), and the scope's fixed start read the same way.
    /// Meep's own file verbs call it with <see cref="MeepPermissions.FilesRead"/> or <see cref="MeepPermissions.FilesWrite"/>.
    /// </summary>
    /// <param name="permission">The permission's name, as in <c>meep.files.read</c>.</param>
    /// <param name="message">The message whose credentials count, or null for the pipeline's own credential, as a source has.</param>
    /// <param name="path">The file, as the module will open it.</param>
    /// <exception cref="PermissionDeniedException">The message, or the pipeline, doesn't hold it for this file.</exception>
    /// <exception cref="FormatException"><paramref name="permission"/> isn't a permission's name.</exception>
    public void DemandFile(string permission, Message? message, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (WhyNotHeld(permission, message, PermissionTarget.File(Path.GetFullPath(path))) is { } why)
            throw new PermissionDeniedException(PermissionName.Parse(permission), why);
    }

    /// <summary>
    /// Refuses unless <paramref name="message"/> may <paramref name="permission"/> the network endpoint
    /// <paramref name="host"/> and <paramref name="port"/>, as <see cref="Demand"/> does, and says which addresses the
    /// connection may then reach. A scoped permission is matched against <c>host:port</c>, the host in lower case and its
    /// ASCII form, as in <c>meep.network.send:*.example.com:443</c>; a scope without a port, such as
    /// <c>meep.network.send:*.example.com</c>, allows any. The host is matched as it's written. The scopes
    /// <c>public</c> and <c>private</c> (Q313) are matched against the addresses connected to instead: then this returns
    /// <see cref="NetworkReach.Public"/> or <see cref="NetworkReach.Private"/>, and the module must connect only to an
    /// address in that reach, checked at connect time. Meep's own network verbs call it with
    /// <see cref="MeepPermissions.NetworkSend"/> or <see cref="MeepPermissions.NetworkListen"/>.
    /// </summary>
    /// <param name="permission">The permission's name, as in <c>meep.network.send</c>.</param>
    /// <param name="message">The message whose credentials count, or null for the pipeline's own credential, as a source has.</param>
    /// <param name="host">The host, as the address names it.</param>
    /// <param name="port">The port.</param>
    /// <returns>The addresses the connection may reach: <see cref="NetworkReach.Any"/>, unless only a keyword scope allowed it.</returns>
    /// <exception cref="PermissionDeniedException">The message, or the pipeline, doesn't hold it for this endpoint.</exception>
    /// <exception cref="FormatException"><paramref name="permission"/> isn't a permission's name.</exception>
    public NetworkReach DemandEndpoint(string permission, Message? message, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        var endpoint = PermissionTarget.Endpoint(host, port);
        var why = WhyNotHeld(permission, message, endpoint);
        if (why is null)
            return NetworkReach.Any;

        // Not by name: perhaps by the kind of address it's at, which an address written as one shows now, and a name
        // only when connecting.
        var reach = NetworkReach.None;
        if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            var of = NetworkAddresses.ReachOf(literal);
            if (of != NetworkReach.None && WhyNotHeld(permission, message, PermissionTarget.Addresses(of, port)) is null)
                return NetworkReach.Any;
        }
        else
        {
            foreach (var each in new[] { NetworkReach.Public, NetworkReach.Private })
            {
                if (WhyNotHeld(permission, message, PermissionTarget.Addresses(each, port)) is null)
                    reach |= each;
            }
        }
        return reach != NetworkReach.None ? reach : throw new PermissionDeniedException(PermissionName.Parse(permission), why);
    }

    /// <summary>
    /// Counts something the module refused for lack of a permission or a proof, without a message to refuse, as a
    /// Listen does when it answers a request with 401 before it reaches the pipeline (Q296). It's added to the module's
    /// <c>denied</c> count, beside the messages its gate refused.
    /// </summary>
    public virtual void CountDenied()
    {
    }

    /// <summary>Why <paramref name="message"/> doesn't hold a permission, or null if it does. The runtime provides it.</summary>
    internal string? WhyNotHeld(string permission, Message message, string? target) =>
        WhyNotHeld(permission, message, target is null ? null : PermissionTarget.Of(target));

    /// <summary>
    /// Why <paramref name="message"/>, or the pipeline's own credential if it's null, doesn't hold a permission on a
    /// target, or null if it does. The runtime provides it.
    /// </summary>
    internal virtual string? WhyNotHeld(string permission, Message? message, PermissionTarget? target) =>
        throw new NotSupportedException("This context isn't part of a pipeline, so there are no credentials to check.");

    /// <summary>
    /// The credentials through which <paramref name="message"/> holds a permission, one for each line of its family, or
    /// none if it doesn't hold it. The runtime provides it, for <c>Authorise</c> to name who authorised a grant.
    /// </summary>
    internal virtual IReadOnlyList<Credential> HeldBy(string permission, Message message) =>
        throw new NotSupportedException("This context isn't part of a pipeline, so there are no credentials to check.");

    /// <summary>
    /// Emits at any time, from any thread, for a module that handles messages, such as one passing on what arrives on a
    /// connection it opened. Take it while handling a message, so what it emits records the inflection doing the work.
    /// Sources emit through the context itself at any time, so they don't need one.
    /// </summary>
    public virtual Outlet Outlet => throw new NotSupportedException("Only a module that handles messages has an outlet.");

    /// <summary>
    /// Counts bytes the module read from the network or wrote to it, for a module that talks to other machines, as
    /// Listen, Fetch and Send do. Count what the module itself reads and writes: the bodies of requests and replies, the
    /// payloads of WebSocket messages, the bytes of a TCP stream. Clients use the counts to show how much goes over each
    /// connection. Safe from any thread, at any time while the pipeline runs.
    /// </summary>
    /// <param name="received">Bytes read.</param>
    /// <param name="sent">Bytes written.</param>
    public virtual void CountBytes(long received, long sent)
    {
    }

    /// <summary>
    /// Says where the module is listening now, such as <c>http://0.0.0.0:8080/hooks/</c>, so tools can tell which of a
    /// host's addresses reach it; null when it stops. A Listen says so once it's bound, and again if it moves.
    /// </summary>
    public virtual void Listening(string? address)
    {
    }

    /// <summary>
    /// The lasting ID of the Meep host the module runs in, such as the daemon's, or null if nothing has given it one.
    /// A Listen answers another Meep with it (<see cref="Peers.HostHeader"/>).
    /// </summary>
    public virtual string? HostId => null;

    /// <summary>
    /// Where messages the module sends come from, for another Meep host to count them by (<see cref="Peers"/>): its
    /// host's ID, its pipeline and its key, each escaped as in a URL. Null when the module has no key, as in a pipeline
    /// built without Meeplang, or its host has no ID.
    /// </summary>
    public virtual string? Origin => null;

    /// <summary>
    /// The W3C <c>traceparent</c> for a request made while handling the message being handled, naming the module's own
    /// span, so the host that receives it can continue the trace (<see cref="ContinueTrace"/>). While tracing, a
    /// message whose trace isn't recorded gets one that says so, so the other host doesn't record its part either.
    /// Null when nothing is tracing.
    /// </summary>
    public virtual string? TraceParent => null;

    /// <summary>
    /// Says a message a source is about to emit continues a trace begun elsewhere, as a request from another Meep
    /// host does, by its W3C <c>traceparent</c>. Its span then has that one as its parent, so a collector joins the
    /// two hosts' spans into one trace. Call it before emitting the message. Does nothing unless tracing.
    /// </summary>
    public virtual void ContinueTrace(Message message, string traceParent)
    {
    }

    /// <summary>
    /// The counts for messages arriving from one place, for a module that receives them over the network, as Listen
    /// does: <c>meep://</c> and the origin another Meep host gave (<see cref="Peers.OriginIn"/>), or else the client's
    /// address (<see cref="Peers.ClientOf"/>).
    /// </summary>
    public virtual Tally Arrival(string from) => Tally.None;

    /// <summary>
    /// The counts for messages going to one place, for a module that sends them over the network, as Fetch and Send
    /// do: where it connected, as <see cref="Peers.EndpointOf(Uri)"/> writes it.
    /// </summary>
    public virtual Tally Destination(string endpoint) => Tally.None;

    /// <summary>
    /// Things the modules of one pipeline share, by name, such as the connections a Listen has open, which Send reaches
    /// by the Listen's name. A plugin should start its names with its own, so they don't clash.
    /// </summary>
    public virtual System.Collections.Concurrent.ConcurrentDictionary<string, object> Shared =>
        throw new NotSupportedException("This context isn't part of a pipeline.");
}
