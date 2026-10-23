using System.Text;

namespace PgProj.Deploy;

public sealed class ScriptWriter
{
    public string Write(OperationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var builder = new StringBuilder();

        AppendScripts(builder, batch.PreDeploymentScripts);

        foreach (var operation in batch.Operations)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(operation.Reason))
            {
                builder.Append("-- ").AppendLine(operation.Reason);
            }

            builder.AppendLine(operation.GenerateSql());
        }

        AppendScripts(builder, batch.PostDeploymentScripts);

        return builder.ToString().TrimEnd();
    }

    private static void AppendScripts(StringBuilder builder, IEnumerable<string> scripts)
    {
        foreach (var script in scripts)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine(script);
        }
    }
}
