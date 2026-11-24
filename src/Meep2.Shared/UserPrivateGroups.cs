using System.Runtime.InteropServices;

namespace Meep2.Shared;

/// <summary>
/// Whether a file or folder its group can write to is still its owner's alone, because the group is the owner's own
/// (Q284). Debian and its relatives give each user a group of their own, with a umask of 002, so what Meep makes there
/// is group-writable; Debian's OpenSSH allows that in what it trusts, and so does Meep. Only on Linux, where it's the
/// custom; elsewhere a user's group is shared, as macOS's <c>staff</c> is.
/// </summary>
/// <remarks>
/// The rule is Debian's OpenSSH's: the path belongs to the user who runs Meep, and its group has exactly one member,
/// that user, whether by it being their primary group or by their being listed in it. Anything that can't be found out
/// counts as shared.
/// </remarks>
internal static class UserPrivateGroups
{
    // .NET's own wrapper of stat (System.Native), whose FileStatus starts Flags, Mode, Uid and Gid, each 32 bits, on
    // every Unix. The buffer is larger than the whole structure.
    private const int StatusSize = 512;

    // Where the fields read are in glibc's and musl's struct passwd and struct group on 64-bit Linux.
    private const int PasswdName = 0;
    private const int PasswdUid = 16;
    private const int PasswdGid = 20;
    private const int GroupMembers = 24;

    // getpwent and getgrgid share static buffers, so only one thread asks at a time.
    private static readonly Lock s_lock = new();

    // What was found for each group, for a minute, since the trust store asks of every file in it each time it loads.
    private static readonly Dictionary<uint, (bool Alone, long At)> s_found = [];
    private static readonly TimeSpan s_keep = TimeSpan.FromMinutes(1);

    /// <summary>Whether <paramref name="path"/> is the user's, and its group has no member but them.</summary>
    public static bool IsOwnersAlone(string path)
    {
        if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess)
            return false;
        try
        {
            var status = new byte[StatusSize];
            if (Stat(path, status) != 0)
                return false;
            var owner = BitConverter.ToUInt32(status, 8);
            var group = BitConverter.ToUInt32(status, 12);
            var user = GetUid();
            return owner == user && IsAlone(group, user);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool IsAlone(uint group, uint user)
    {
        lock (s_lock)
        {
            var now = Environment.TickCount64;
            if (s_found.TryGetValue(group, out var found) && now - found.At < s_keep.TotalMilliseconds)
                return found.Alone;
            var alone = HasOnlyMember(group, user);
            s_found[group] = (alone, now);
            return alone;
        }
    }

    // Debian's secure_permissions: every user whose primary group it is must be this one, its listed members must be
    // none or this one alone, and it must have a member.
    private static bool HasOnlyMember(uint group, uint user)
    {
        var passwd = GetPwUid(user);
        if (passwd == 0 || Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(passwd, PasswdName)) is not { } name)
            return false;

        var entry = GetGrGid(group);
        if (entry == 0)
            return false;
        var listed = new List<string>();
        var members = Marshal.ReadIntPtr(entry, GroupMembers);
        for (var at = 0; members != 0; at += IntPtr.Size)
        {
            var member = Marshal.ReadIntPtr(members, at);
            if (member == 0)
                break;
            listed.Add(Marshal.PtrToStringUTF8(member) ?? "");
        }

        var count = 0;
        SetPwEnt();
        try
        {
            for (var next = GetPwEnt(); next != 0; next = GetPwEnt())
            {
                if (unchecked((uint)Marshal.ReadInt32(next, PasswdGid)) != group)
                    continue;
                if (unchecked((uint)Marshal.ReadInt32(next, PasswdUid)) != user)
                    return false;
                count++;
            }
        }
        finally
        {
            EndPwEnt();
        }
        if (listed.Count > 0)
        {
            if (listed.Count > 1 || listed[0] != name)
                return false;
            count++;
        }
        return count > 0;
    }

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int Stat(string path, byte[] status);

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    [DllImport("libc", EntryPoint = "getpwuid")]
    private static extern nint GetPwUid(uint uid);

    [DllImport("libc", EntryPoint = "getgrgid")]
    private static extern nint GetGrGid(uint gid);

    [DllImport("libc", EntryPoint = "setpwent")]
    private static extern void SetPwEnt();

    [DllImport("libc", EntryPoint = "getpwent")]
    private static extern nint GetPwEnt();

    [DllImport("libc", EntryPoint = "endpwent")]
    private static extern void EndPwEnt();
}
