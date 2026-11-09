using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Meep2.Daemon;

/// <summary>
/// Which user is at the other end of a local socket, as the operating system reports it, so the daemon only lets in
/// the user running it (Q40). macOS and the BSDs report it through <c>LOCAL_PEERCRED</c>, Linux through
/// <c>SO_PEERCRED</c>. Windows has no equivalent for these sockets; there, the socket's folder in your profile keeps
/// other users out.
/// </summary>
internal static class PeerCredentials
{
    /// <summary>The user running this process, or null on Windows.</summary>
    public static uint? CurrentUser => OperatingSystem.IsWindows() ? null : GetEffectiveUserId();

    /// <summary>The user at the other end of a local socket, or null where the system can't say.</summary>
    public static uint? UserOf(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (OperatingSystem.IsLinux())
        {
            // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
            Span<byte> credentials = stackalloc byte[12];
            return socket.GetRawSocketOption(1 /* SOL_SOCKET */, 17 /* SO_PEERCRED */, credentials) >= 8
                ? BitConverter.ToUInt32(credentials[4..8])
                : null;
        }
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            // struct xucred { u_int cr_version; uid_t cr_uid; short cr_ngroups; gid_t cr_groups[16]; }
            Span<byte> credentials = stackalloc byte[76];
            return socket.GetRawSocketOption(0 /* SOL_LOCAL */, 1 /* LOCAL_PEERCRED */, credentials) >= 8
                ? BitConverter.ToUInt32(credentials[4..8])
                : null;
        }
        return null;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
