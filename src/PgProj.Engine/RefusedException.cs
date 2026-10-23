namespace PgProj.Engine;

/// <summary>
/// A run the engine will not do because of what the user asked for or gave it, not because of a defect (G-309b): a missing connection string, the source
/// being the target, <c>--tenants</c> on a project without tenants, a tenant query that returns bad rows. An entry point turns it into a status with this
/// message; any other exception is a defect and is thrown.
/// </summary>
internal sealed class RefusedException : InvalidOperationException
{
    public RefusedException(string message)
        : base(message)
    {
    }

    public RefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
