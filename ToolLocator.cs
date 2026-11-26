namespace MkvCensor;

/// <summary>
/// Finds ffmpeg/ffprobe: an explicit --ffmpeg-path/--ffprobe-path always wins,
/// otherwise we look for a bundled copy in the Assets folder next to the
/// executable, and only fall back to PATH if that's not there.
/// </summary>
internal static class ToolLocator
{
    private const string AssetsFolderName = "Assets";

    public static string ResolveFfmpeg(string? explicitPath) => Resolve(explicitPath, "ffmpeg");
    public static string ResolveFfprobe(string? explicitPath) => Resolve(explicitPath, "ffprobe");

    private static string Resolve(string? explicitPath, string toolName)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        var exeName = OperatingSystem.IsWindows() ? $"{toolName}.exe" : toolName;
        var bundled = Path.Combine(AppContext.BaseDirectory, AssetsFolderName, exeName);

        if (File.Exists(bundled))
        {
            EnsureExecutable(bundled);
            return bundled;
        }

        // Nothing bundled — fall back to PATH. If that's not resolvable
        // either, FfmpegService.VerifyToolsAvailable() will fail with a
        // clear error before any real work happens.
        return toolName;
    }

    /// <summary>
    /// Zips, git checkouts, and some CI artifacts drop the executable bit
    /// from bundled binaries on Linux/macOS. Restore it before we try to
    /// launch the process.
    /// </summary>
    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode executeBits =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            if ((mode & executeBits) != executeBits)
            {
                File.SetUnixFileMode(path, mode | executeBits);
            }
        }
        catch
        {
            // Best effort — a read-only filesystem or permission issue here
            // will surface as a clear process-launch error a moment later.
        }
    }
}
