using System.Buffers.Binary;
using System.Text;
using Meep2.SDK.Modules;

namespace Meep2.SDK.Messages;

/// <summary>
/// The base type for everything that flows through a pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Messages don't change once they're made. To change one, copy it with <c>with</c>: the copy is a new message,
/// with its own <see cref="Id"/>, whose parent is the original. To turn a message into a different type, create the
/// new message while handling the old one; when it's emitted, the runtime makes it a child of the message being
/// handled.
/// </para>
/// <para>
/// Two messages are equal only when they're the same message, meaning they have the same <see cref="Id"/>.
/// Compare their contents yourself if that's what you need.
/// </para>
/// </remarks>
public abstract record Message
{
    private Parents _parents;
    private long _expires; // a Deadline timestamp, or 0 for never

    /// <summary>Runtime state, such as whether an error has been handled.</summary>
    internal int RuntimeFlags;

    /// <summary>Creates a message with a new <see cref="Id"/> and no parents.</summary>
    protected Message()
    {
        Created = DateTime.UtcNow;
        Id = NewId(Created);
    }

    /// <summary>Used by <c>with</c>. The copy is a new message whose parent is <paramref name="original"/>.</summary>
    protected Message(Message original)
    {
        ArgumentNullException.ThrowIfNull(original);
        Created = DateTime.UtcNow;
        Id = NewId(Created);
        _parents = new Parents(original);
        _expires = original._expires;
    }

    /// <summary>Identifies this message. Ids sort by creation time (UUID version 7).</summary>
    public Guid Id { get; init; }

    /// <summary>When the message was made, in UTC.</summary>
    public DateTime Created { get; init; }

    /// <summary>When the message expires, or null if it never does.</summary>
    /// <remarks>
    /// A message expires no later than its parents. When it's emitted, the runtime sets this to the earliest of its
    /// own value and its parents' values, so a module can bring expiry forward but can't put it back.
    /// </remarks>
    public Deadline? Expires
    {
        get => _expires == 0 ? null : new Deadline(_expires);
        init => _expires = value?.Timestamp ?? 0;
    }

    /// <summary>
    /// The messages this one was made from. Most messages have one. Messages from sources have none, and messages
    /// that combine others, such as a batch, have several.
    /// </summary>
    public Parents Parents
    {
        get => _parents;
        init => _parents = value;
    }

    /// <summary>The first parent, or null if there are none.</summary>
    public Message? Parent => _parents.First;

    /// <summary>The module that emitted this message, or null if it hasn't been emitted.</summary>
    public ModuleInfo? EmittedBy { get; internal set; }

    /// <summary>Finds this message's ancestors by the name of the module that emitted them.</summary>
    public Family Fam => new(this);

    /// <summary>Whether the runtime has stamped this message with its emitter, expiry and tracking.</summary>
    internal bool IsStamped => EmittedBy is not null;

    internal long ExpiresTimestamp
    {
        get => _expires;
        set => _expires = value;
    }

    /// <summary>The runtime's tracking scopes that this message counts towards, if any.</summary>
    internal object? Tracking { get; set; }

    internal void SetParents(Parents parents) => _parents = parents;

    /// <summary>
    /// Makes a UUID version 7 (RFC 9562): 48 bits of Unix milliseconds, then 74 random bits.
    /// </summary>
    /// <remarks>
    /// <see cref="Guid.CreateVersion7()"/> draws on the operating system's secure random generator, which on macOS
    /// costs about 280 ns and goes through a lock, so modules making messages on different threads queue for it.
    /// Ids only need to be unique, not unpredictable, so this uses .NET's per-thread generator instead.
    /// </remarks>
    internal static Guid NewId(DateTime createdUtc)
    {
        var milliseconds = (createdUtc.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;
        var randomA = Random.Shared.Next(1 << 12);
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, milliseconds << 16);
        BinaryPrimitives.WriteInt64BigEndian(bytes[8..], Random.Shared.NextInt64());
        bytes[6] = (byte)(0x70 | (randomA >> 8)); // version 7
        bytes[7] = (byte)randomA;
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F)); // RFC 9562 variant
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>True only when <paramref name="other"/> is the same message.</summary>
    public virtual bool Equals(Message? other) =>
        ReferenceEquals(this, other)
        || (other is not null && EqualityContract == other.EqualityContract && Id == other.Id);

    /// <inheritdoc/>
    public override int GetHashCode() => Id.GetHashCode();

    /// <summary>Prints the message's identity, leaving out its ancestors.</summary>
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append("Id = ").Append(Id);
        if (EmittedBy is not null)
            builder.Append(", EmittedBy = ").Append(EmittedBy);
        return true;
    }
}
