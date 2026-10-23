namespace PgProj.Cli;

/// <summary>
/// How this copy of pgproj was installed (G-332), read from where it runs: a .NET tool runs from the tools store, the <c>.deb</c> and <c>.rpm</c> packages put it
/// in <c>/usr/lib/pgproj</c>, the MSI in <c>Program Files\pgproj</c>, and every other self-contained build is an unpacked archive. It describes the shell, not
/// the pipeline, so it is the CLI's.
/// </summary>
public static class InstallKind
{
    public static string Describe() => Describe(AppContext.BaseDirectory, IsSelfContained(AppContext.BaseDirectory), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    public static string Describe(string baseDirectory, bool selfContained, string? programFiles)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        var directory = baseDirectory.Replace('\\', '/').TrimEnd('/') + "/";
        if (directory.Contains("/.store/pgproj/", StringComparison.OrdinalIgnoreCase))
        {
            return "a .NET tool";
        }

        if (!selfContained)
        {
            return "a build that runs on the installed .NET (from source)";
        }

        if (directory.StartsWith("/usr/lib/pgproj/", StringComparison.Ordinal))
        {
            return "a .deb or .rpm package";
        }

        if (!string.IsNullOrEmpty(programFiles) && directory.StartsWith(programFiles.Replace('\\', '/').TrimEnd('/') + "/pgproj/", StringComparison.OrdinalIgnoreCase))
        {
            return "the Windows installer (.msi)";
        }

        return "an archive (.zip or .tar.gz)";
    }

    /// <summary>A self-contained build carries the runtime beside it.</summary>
    private static bool IsSelfContained(string baseDirectory) => File.Exists(Path.Combine(baseDirectory, "System.Private.CoreLib.dll"));
}
