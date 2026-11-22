using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Meep2.Daemon;

/// <summary>
/// Who a daemon is: an ID made the first time it runs and kept in <c>daemon/id</c> in Meep's folder, so it stays the
/// same across restarts. Tools that watch several daemons tell them apart by the ID, and place them by the machine's
/// names and addresses (<see cref="MachineAddresses"/>).
/// </summary>
public static class HostIdentity
{
    /// <summary>The file that keeps a daemon's ID, in Meep's folder (<c>~/.meep</c> or <c>MEEP_HOME</c>).</summary>
    public static string IdFile(string home) => Path.Combine(home, "daemon", "id");

    /// <summary>
    /// The daemon's ID for Meep's folder <paramref name="home"/>: the one kept in <see cref="IdFile"/>, or a new one,
    /// written there, if there isn't one yet. It's a UUID, as OpenTelemetry's <c>service.instance.id</c> suggests.
    /// The file also keeps the machine it was made on (<see cref="MachineId"/>), and a folder copied to another
    /// machine gets an ID of its own there, so two daemons never claim to be one (Q208).
    /// </summary>
    public static string IdOf(string home) => IdOf(home, MachineId());

    /// <summary>
    /// <see cref="IdOf(string)"/> as though on the machine <paramref name="machine"/>. Where the machine has no ID to
    /// compare (null), the ID kept is used as it is, without checking where it was made.
    /// </summary>
    internal static string IdOf(string home, string? machine)
    {
        ArgumentNullException.ThrowIfNull(home);
        var file = IdFile(home);
        if (Read(file) is { } kept)
        {
            if (machine is null || kept.Machine == machine)
                return kept.Id;
            if (kept.Machine is null)
            {
                // Kept before the machine was: it was made here, as far as anyone can tell.
                Replace(file, kept.Id, machine);
                return kept.Id;
            }
            // Made on another machine. The new ID comes from the old one and this machine, so two daemons starting
            // here at once from the same copy (one of which will find the socket taken) agree on it.
            var moved = Derived(kept.Id, machine);
            Replace(file, moved, machine);
            return moved;
        }
        Meep2.Shared.PrivateFolders.Create(Path.GetDirectoryName(file)!);
        var made = Guid.NewGuid().ToString();
        try
        {
            // CreateNew, so two daemons starting at once (one of which will find the socket taken) agree on one ID.
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream);
            writer.Write(Contents(made, machine));
            return made;
        }
        catch (IOException) when (Read(file) is { } other)
        {
            return other.Id;
        }
    }

    /// <summary>
    /// This machine's ID, as macOS (its hardware UUID) or Linux (<c>/etc/machine-id</c>) gives it, hashed with a key
    /// of Meep's own so the file doesn't hold the ID itself, as machine-id(5) asks; or null where there's none.
    /// </summary>
    public static string? MachineId()
    {
        string? raw = null;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var uuid = new byte[16];
                var wait = new Timespec { Seconds = 5 };
                if (gethostuuid(uuid, ref wait) == 0)
                    raw = new Guid(uuid, bigEndian: true).ToString();
            }
            else if (OperatingSystem.IsLinux())
            {
                raw = new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" }
                    .Where(File.Exists)
                    .Select(f => File.ReadAllText(f).Trim())
                    .FirstOrDefault(id => id.Length > 0);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            raw = null;
        }
        return raw is null ? null : Convert.ToHexStringLower(HMACSHA256.HashData("meep daemon"u8, Encoding.UTF8.GetBytes(raw)), 0, 16);
    }

    // A UUID (version 8, RFC 9562's own kind) made from an ID and a machine, the same each time for the same two.
    private static string Derived(string id, string machine)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{id}\n{machine}")).AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }

    private static string Contents(string id, string? machine) => machine is null ? $"{id}\n" : $"{id}\nmachine {machine}\n";

    // Written beside it and moved over it, so a reader never sees half a file.
    private static void Replace(string file, string id, string machine)
    {
        var temporary = $"{file}.{Environment.ProcessId}.new";
        File.WriteAllText(temporary, Contents(id, machine));
        File.Move(temporary, file, overwrite: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public nint Seconds;
        public nint Nanoseconds;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int gethostuuid(byte[] id, ref Timespec wait);

    // The ID kept in the file, and the machine it was made on, if it says.
    private static KeptId? Read(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;
            var lines = File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines is not [var id, ..])
                return null;
            var machine = lines.Skip(1).FirstOrDefault(l => l.StartsWith("machine ", StringComparison.Ordinal))?["machine ".Length..].Trim();
            return new KeptId(id, machine is { Length: > 0 } ? machine : null);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed record KeptId(string Id, string? Machine);
}
