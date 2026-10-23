using System.Runtime.InteropServices;
using Npgsql;

namespace PgProj.Engine;

/// <summary>
/// The connection string pgproj opens, adjusted to the machine (G-324). Npgsql tries GSSAPI encryption by default (<c>GSS Encryption Mode=Prefer</c>),
/// which makes .NET load the Kerberos library; on a Linux system without it, .NET prints <c>Cannot load library libgssapi_krb5.so.2</c> on every
/// connection, though the connection works. When the library cannot be loaded, GSSAPI cannot be used anyway, so the mode is turned off, unless the
/// connection string asks for one other than the default. Windows and macOS always have what GSSAPI needs.
/// </summary>
internal static class KerberosLibrary
{
    private const string LinuxLibrary = "libgssapi_krb5.so.2";

    private static readonly Lazy<bool> s_available = new(static () => !OperatingSystem.IsLinux() || CanLoad(LinuxLibrary));

    /// <summary>Whether GSSAPI can be used on this machine: on Linux, whether the Kerberos library loads.</summary>
    public static bool IsAvailable => s_available.Value;

    /// <summary><paramref name="connectionString"/> as it is opened on this machine.</summary>
    public static string ForThisMachine(string connectionString) => Adjust(connectionString, IsAvailable);

    /// <summary>
    /// <paramref name="connectionString"/> with GSSAPI encryption turned off when <paramref name="gssAvailable"/> is false and it leaves the mode at its
    /// default (<c>Prefer</c>); otherwise as it is. A stated <c>Disable</c> or <c>Require</c> is kept.
    /// </summary>
    public static string Adjust(string connectionString, bool gssAvailable)
    {
        if (gssAvailable)
        {
            return connectionString;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (builder.GssEncryptionMode != GssEncryptionMode.Prefer)
        {
            return connectionString;
        }

        builder.GssEncryptionMode = GssEncryptionMode.Disable;
        return builder.ConnectionString;
    }

    private static bool CanLoad(string library)
    {
        if (!NativeLibrary.TryLoad(library, out var handle))
        {
            return false;
        }

        NativeLibrary.Free(handle);
        return true;
    }
}
