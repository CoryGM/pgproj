namespace PgProj.Core;

public sealed class PgProjectValidationException : Exception
{
    public PgProjectValidationException(string message)
        : base(message)
    {
    }

    public PgProjectValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
