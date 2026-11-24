using System.Security.Cryptography;
using Meep2.SDK.Security;

namespace Meep2.Security;

/// <summary>
/// An OpenSSH key revocation list (KRL), as <c>ssh-keygen -k</c> makes it (OpenSSH's PROTOCOL.krl): certificates
/// revoked by their authority and serial, serials in a range or a bitmap, or key ID, and keys revoked outright or by
/// their SHA-1 or SHA-256 hash.
/// </summary>
/// <remarks>
/// A KRL's own signature, which OpenSSH no longer checks either, isn't checked: a list in the trust store can only
/// refuse things, so one someone else wrote there can do no more than one you wrote.
/// </remarks>
public sealed class SshRevocationList
{
    private const ulong Magic = 0x5353484b524c0a00UL; // "SSHKRL\n\0"

    private readonly List<CertificateSection> _certificates = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sha1 = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sha256 = new(StringComparer.Ordinal);

    private SshRevocationList(ReadOnlySpan<byte> data)
    {
        var reader = new SshWireReader(data);
        if (reader.ReadUInt64() != Magic)
            throw new FormatException("it isn't an OpenSSH key revocation list");
        if (reader.ReadUInt32() != 1)
            throw new FormatException("it's a version of OpenSSH's key revocation list Meep doesn't know");
        Version = reader.ReadUInt64();
        reader.ReadUInt64(); // when it was made
        reader.ReadUInt64(); // flags
        reader.ReadString().ToArray(); // reserved
        Comment = reader.ReadText();
        while (!reader.AtEnd)
        {
            var type = reader.ReadByte();
            var section = reader.ReadString().ToArray();
            switch (type)
            {
                case 1:
                    _certificates.Add(CertificateSection.Read(section));
                    break;
                case 2:
                    ReadHashes(section, _keys);
                    break;
                case 3:
                    ReadHashes(section, _sha1);
                    break;
                case 4:
                    break; // a signature, which isn't checked (see the remarks)
                case 5:
                    ReadHashes(section, _sha256);
                    break;
                default:
                    throw new FormatException($"it has a section of a type Meep doesn't know, {type}");
            }
        }
    }

    /// <summary>The list's version, which <c>ssh-keygen -k -u</c> raises each time it's updated.</summary>
    public ulong Version { get; }

    /// <summary>The comment it was made with, if any.</summary>
    public string Comment { get; }

    /// <summary>Reads a KRL. Throws <see cref="FormatException"/>, saying why, if it can't.</summary>
    public static SshRevocationList Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            return new SshRevocationList(data);
        }
        catch (FormatException exception)
        {
            throw new FormatException($"This key revocation list can't be read: {exception.Message.TrimEnd('.')}.", exception);
        }
    }

    /// <summary>
    /// Why the list revokes <paramref name="credential"/>, or null if it doesn't: by its authority and serial or key
    /// ID, or because its key or its authority's key is revoked, as OpenSSH checks a certificate.
    /// </summary>
    public string? WhyRevoked(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        foreach (var section in _certificates.Where(s => s.Authority.Length == 0 || credential.AuthorityKey.Span.SequenceEqual(s.Authority)))
        {
            if (credential.Serial != 0 && section.RevokesSerial(credential.Serial))
                return $"its serial, {credential.Serial}, is revoked";
            if (section.KeyIds.Contains(credential.KeyId))
                return $"its key ID, {credential.KeyId}, is revoked";
        }
        if (WhyKeyRevoked(credential.Key.Span) is { } key)
            return $"its key is revoked{key}";
        if (WhyKeyRevoked(credential.AuthorityKey.Span) is { } authority)
            return $"its authority's key is revoked{authority}";
        return null;
    }

    /// <summary>Whether the list revokes this key, given in SSH's wire format.</summary>
    public bool RevokesKey(ReadOnlySpan<byte> key) => WhyKeyRevoked(key) is not null;

    // Null if the key isn't revoked; otherwise how, as words to add to a sentence (empty when it's listed whole).
    private string? WhyKeyRevoked(ReadOnlySpan<byte> key)
    {
        if (_keys.Contains(Base64(key)))
            return "";
        if (_sha256.Count > 0 && _sha256.Contains(Base64(SHA256.HashData(key))))
            return " by its SHA-256 hash";
#pragma warning disable CA5350 // SHA-1 is what the list names keys by; it's matched, not trusted.
        if (_sha1.Count > 0 && _sha1.Contains(Base64(SHA1.HashData(key))))
#pragma warning restore CA5350
            return " by its SHA-1 hash";
        return null;
    }

    private static string Base64(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes);

    // Keys, or their hashes, one after another, kept as base64 to look up.
    private static void ReadHashes(ReadOnlySpan<byte> section, HashSet<string> into)
    {
        var reader = new SshWireReader(section);
        while (!reader.AtEnd)
            into.Add(Base64(reader.ReadString().ToArray()));
    }

    // The certificates one authority (or, with no key, any authority) has revoked.
    private sealed class CertificateSection
    {
        private readonly HashSet<ulong> _serials = [];
        private readonly List<(ulong Low, ulong High)> _ranges = [];

        public byte[] Authority { get; private init; } = [];

        public HashSet<string> KeyIds { get; } = new(StringComparer.Ordinal);

        public static CertificateSection Read(ReadOnlySpan<byte> data)
        {
            var reader = new SshWireReader(data);
            var section = new CertificateSection { Authority = reader.ReadString().ToArray() };
            reader.ReadString().ToArray(); // reserved
            while (!reader.AtEnd)
            {
                var type = reader.ReadByte();
                var part = new SshWireReader(reader.ReadString().ToArray());
                switch (type)
                {
                    case 0x20: // serials
                        while (!part.AtEnd)
                            section._serials.Add(part.ReadUInt64());
                        break;
                    case 0x21: // a range of serials, inclusive
                        var low = part.ReadUInt64();
                        var high = part.ReadUInt64();
                        part.End();
                        section._ranges.Add((low, high));
                        break;
                    case 0x22: // a bitmap of serials from an offset, bit 0 being the offset itself
                        var offset = part.ReadUInt64();
                        var bitmap = part.ReadMpint();
                        part.End();
                        for (var i = 0; i < bitmap.Length; i++)
                        {
                            for (var bit = 0; bit < 8; bit++)
                            {
                                if ((bitmap[bitmap.Length - 1 - i] & (1 << bit)) != 0)
                                    section._serials.Add(offset + (ulong)(i * 8 + bit));
                            }
                        }
                        break;
                    case 0x23: // key IDs
                        while (!part.AtEnd)
                            section.KeyIds.Add(part.ReadText());
                        break;
                    default:
                        throw new FormatException($"it has a certificate section of a type Meep doesn't know, {type}");
                }
            }
            return section;
        }

        public bool RevokesSerial(ulong serial) =>
            _serials.Contains(serial) || _ranges.Any(range => serial >= range.Low && serial <= range.High);
    }
}
