using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PS2Iso.Core;

/// <summary>A file the builder needed is held open elsewhere with a conflicting share mode. If the
/// file is in a folder this PC shares over SMB, the usual holder is a client that disconnected
/// without closing it (e.g. a PS2 switched off mid-game): the SMB server keeps that dead session's
/// handle open until it times the connection out. <see cref="SmbOpenFiles.Close"/> frees it.</summary>
public sealed class FileInUseException : IOException
{
    private const int SharingViolation = unchecked((int)0x80070020); // ERROR_SHARING_VIOLATION

    public FileInUseException(string path, IOException inner)
        : base($"{path} is in use by another program, or by an SMB client that disconnected " +
               "without closing it.", inner) => Path = path;

    public string Path { get; }

    /// <summary>Open a file, reporting a sharing violation as a <see cref="FileInUseException"/>
    /// that names it (so the caller can offer to release it).</summary>
    internal static T Guard<T>(string path, Func<string, T> open)
    {
        try { return open(path); }
        catch (IOException ex) when (ex.HResult == SharingViolation)
        {
            throw new FileInUseException(path, ex);
        }
    }
}

/// <summary>Closes this PC's SMB-server handles on a local file. A handle left by a client that
/// vanished belongs to the kernel SMB server, not to any process, so it can't be killed from Task
/// Manager — closing the server-side open is the only way to free it early. That needs
/// administrator rights, so it runs in an elevated PowerShell (one UAC prompt). Any client still
/// actively using the file loses its handle.</summary>
public static class SmbOpenFiles
{
    private const int ErrorCancelled = 1223; // the UAC prompt was declined

    public static (bool Closed, string Message) Close(string path)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Releasing SMB handles is only supported on Windows.");

        string fullPath = Path.GetFullPath(path);
        string name = Path.GetFileName(fullPath);
        string quoted = fullPath.Replace("'", "''");
        // Exit codes: 0 = closed, 3 = no SMB client has the file open, anything else = failed.
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                $open = @(Get-SmbOpenFile -IncludeHidden | Where-Object Path -eq '{{quoted}}')
                if ($open.Count -eq 0) { exit 3 }
                $open | Close-SmbOpenFile -Force
                exit 0
            } catch { exit 1 }
            """;
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
        };

        int exitCode;
        try
        {
            using var ps = Process.Start(psi)!;
            ps.WaitForExit();
            exitCode = ps.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return (false, "Administrator prompt declined; the file is still locked.");
        }

        return exitCode switch
        {
            0 => (true, $"Closed the stale SMB handle(s) on {name}."),
            3 => (false, $"No SMB client has {name} open, so a program on this PC is holding it " +
                         "(e.g. PCSX2 with the ISO loaded). Close it and try again."),
            _ => (false, $"Could not close the SMB handles on {name} (PowerShell exit code {exitCode})."),
        };
    }
}
