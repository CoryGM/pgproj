namespace PgProj.Core;

/// <summary>
/// Whether a directory's file system tells file names apart by case (G-212), found by asking it: a temporary file is created with a lower-case name,
/// and the file system is case-insensitive when the same name in upper case finds it. The operating system is not a reliable guide: macOS's
/// default file system is case-insensitive, a Windows directory can be made case-sensitive, and a macOS volume can be formatted case-sensitive.
/// </summary>
public static class FileSystemCase
{
    /// <summary>
    /// True when <paramref name="directory"/> tells <c>T.sql</c> and <c>t.sql</c> apart. A directory that does not exist yet is asked through its
    /// nearest existing parent, whose setting a new directory takes. When the temporary file cannot be written (no permission to write there, or
    /// another I/O error), the operating system's usual file system decides: case-insensitive on Windows and macOS, case-sensitive elsewhere.
    /// </summary>
    public static bool IsCaseSensitive(string directory)
        => IsCaseSensitive(directory, static path => File.Create(path, 1, FileOptions.DeleteOnClose));

    /// <summary><see cref="IsCaseSensitive(string)"/>, with the temporary file created by <paramref name="createProbe"/> (a test stands in a failing one).</summary>
    internal static bool IsCaseSensitive(string directory, Func<string, Stream> createProbe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(createProbe);

        var existing = Path.GetFullPath(directory);
        while (!Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || parent == existing)
            {
                return DefaultForOperatingSystem();
            }

            existing = parent;
        }

        var probe = Path.Combine(existing, $"pgproj-case-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using (createProbe(probe))
            {
                // While the file is open, look for it by its upper-case name.
                return !File.Exists(Path.Combine(existing, Path.GetFileName(probe).ToUpperInvariant()));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return DefaultForOperatingSystem();
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The file is deleted when it is closed; this only covers a file system that does not honor that.
            }
        }
    }

    /// <summary>The comparer for file paths in <paramref name="directory"/>: exact on a case-sensitive file system, ignoring case on any other.</summary>
    public static StringComparer PathComparer(string directory)
        => IsCaseSensitive(directory) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    internal static bool DefaultForOperatingSystem()
        => !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
}
