using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Meep2.Modules;

/// <summary>
/// Whether a path is a regular file, rather than a device such as <c>/dev/zero</c>, a named pipe or a socket, which
/// can be read without end or block whoever opens them (S3). .NET's <see cref="FileInfo"/> reports those as normal
/// files of length 0 on Unix, so this asks the system.
/// </summary>
internal static partial class FileKinds
{
    // The file type bits of a mode, as .NET's native layer reports them on every Unix.
    private const int TypeMask = 0xF000;
    private const int Regular = 0x8000;

    // .NET's own wrapper of stat and fstat (System.Native), which lays out its FileStatus the same on every Unix: two
    // 32-bit fields, Flags then Mode, start it. The buffer is larger than the whole structure.
    private const int StatusSize = 512;

    /// <summary>Whether the path names a regular file, following links. False if it doesn't exist.</summary>
    public static bool IsRegular(string path)
    {
        if (OperatingSystem.IsWindows())
            return File.Exists(path) && (File.GetAttributes(path) & (FileAttributes.Device | FileAttributes.Directory)) == 0;
        var status = new byte[StatusSize];
        return Stat(path, status) == 0 && IsRegularMode(status);
    }

    /// <summary>Whether an open file is a regular file.</summary>
    public static bool IsRegular(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
            return true;
        var status = new byte[StatusSize];
        return FStat(handle, status) == 0 && IsRegularMode(status);
    }

    private static bool IsRegularMode(byte[] status) => (BitConverter.ToInt32(status, 4) & TypeMask) == Regular;

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int Stat(string path, byte[] status);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_FStat")]
    private static extern int FStat(SafeFileHandle handle, byte[] status);
}
