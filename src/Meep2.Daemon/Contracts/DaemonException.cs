namespace Meep2.Daemon;

/// <summary>A request the daemon refused, or a call to it that failed. The message says why, for people.</summary>
public sealed class DaemonException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DaemonException(int status, string message, IReadOnlyList<string>? details = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
        Details = details ?? [];
    }

    /// <summary>The HTTP status the daemon answered with, or 0 if it couldn't be reached.</summary>
    public int Status { get; }

    /// <summary>More, such as a pipeline file's errors.</summary>
    public IReadOnlyList<string> Details { get; }
}
