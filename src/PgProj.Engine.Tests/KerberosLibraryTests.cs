using Npgsql;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-324: on a Linux system without the Kerberos library, .NET printed <c>Cannot load library libgssapi_krb5.so.2</c> on every connection, because Npgsql
/// tries GSSAPI encryption by default. Where the library cannot be loaded, pgproj's connections do not try it, unless the connection string asks for a mode.
/// </summary>
[TestClass]
public sealed class KerberosLibraryTests
{
    private const string Connection = "Host=localhost;Port=5432;Database=shop;Username=app;Password=secret";

    [TestMethod]
    public void WithoutTheLibraryTheDefaultModeIsTurnedOff()
    {
        var adjusted = new NpgsqlConnectionStringBuilder(KerberosLibrary.Adjust(Connection, gssAvailable: false));

        Assert.AreEqual(GssEncryptionMode.Disable, adjusted.GssEncryptionMode);
        Assert.AreEqual("localhost", adjusted.Host);
        Assert.AreEqual("shop", adjusted.Database);
        Assert.AreEqual("app", adjusted.Username);
        Assert.AreEqual("secret", adjusted.Password);
    }

    [TestMethod]
    public void WithoutTheLibraryAStatedPreferIsTurnedOffToo()
    {
        // Prefer cannot use GSSAPI without the library either, so turning it off changes nothing but the noise.
        var adjusted = new NpgsqlConnectionStringBuilder(KerberosLibrary.Adjust(Connection + ";GSS Encryption Mode=Prefer", gssAvailable: false));

        Assert.AreEqual(GssEncryptionMode.Disable, adjusted.GssEncryptionMode);
    }

    [TestMethod]
    [DataRow("Disable")]
    [DataRow("Require")]
    public void AModeTheConnectionStringStatesIsKept(string mode)
    {
        var connection = $"{Connection};GSS Encryption Mode={mode}";

        Assert.AreSame(connection, KerberosLibrary.Adjust(connection, gssAvailable: false));
    }

    [TestMethod]
    public void WithTheLibraryTheConnectionStringIsUnchanged()
    {
        Assert.AreSame(Connection, KerberosLibrary.Adjust(Connection, gssAvailable: true));
    }

    [TestMethod]
    public void OffLinuxGssapiIsAlwaysAvailable()
    {
        // Windows (SSPI) and macOS (its GSS framework) always have what GSSAPI needs; only a Linux system can lack the Kerberos library.
        Assert.IsTrue(OperatingSystem.IsLinux() || KerberosLibrary.IsAvailable);
    }
}
