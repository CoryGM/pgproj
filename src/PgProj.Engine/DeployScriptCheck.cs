using PgProj.Core;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>What <c>build</c> checks in the pre- and post-deployment scripts (G-124, G-290e): their syntax, and their use of the tenant variables.</summary>
internal static class DeployScriptCheck
{
    // The diagnostics say where they come from, and always have: this class was DeployScripts in the CLI.
    private const string Source = "DeployScripts";

    /// <summary>
    /// Syntax-checks the pre- and post-deployment scripts and every file they include (G-124), each on its own so a position is a position
    /// in its file. Only a syntax error is reported: a script is meant to hold statements pgproj does not model (an <c>INSERT</c>, a
    /// <c>DO</c> block), so the "not modeled" warnings the schema files get would be noise here. A script is never modeled. The files are parsed in parallel,
    /// as the schema files are (G-312), with the project's parse (through its cache, G-311), and their diagnostics come in the files' order.
    /// </summary>
    public static IEnumerable<SemanticDiagnostic> CheckSyntax(PgProject project, Func<ScriptSourceFile, SqlSyntaxTree> parse)
    {
        var files = project.PreDeployScriptFiles.Concat(project.PostDeployScriptFiles).ToArray();
        var trees = ProjectModelBuilder.ParseAll(files, parse, ProjectModelBuilder.ParseParallelism, CancellationToken.None);
        return files.Zip(trees).SelectMany(static pair => SyntaxErrors(pair.First, pair.Second)).ToArray();
    }

    /// <summary>A script file's syntax errors, from its parse (G-124): what <see cref="CheckSyntax"/> reports for it, and a check of that file alone (G-313).</summary>
    public static IEnumerable<SemanticDiagnostic> SyntaxErrors(ScriptSourceFile file, SqlSyntaxTree tree)
        => tree.Diagnostics
            .Where(static d => d.Severity == SqlDiagnosticSeverity.Error)
            .Select(diagnostic => new SemanticDiagnostic(SemanticDiagnosticSeverity.Error, diagnostic.Message, SemanticLocation.FromSql(file.FilePath, diagnostic), diagnostic.Source));

    /// <summary>
    /// <c>build</c>'s check of the scripts' variables and syntax (G-290e, G-336): the project's own scripts may use any variable but a tenant's
    /// (<c>tenant_schema</c>, <c>tenant_id</c>); a tenant's are checked with the stand-in's values for those two, any other variable being one the
    /// environment gives <c>publish --execute</c>, then syntax-checked as the project's are. Each problem names its file. No value is read.
    /// </summary>
    public static IEnumerable<SemanticDiagnostic> Check(PgProject project, PgQueryParser parser)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(parser);

        foreach (var file in project.PreDeployScriptFiles.Concat(project.PostDeployScriptFiles))
        {
            if (ProjectVariables(file) is { } problem)
            {
                yield return problem;
            }
        }

        if (project.Tenants is not { } tenants)
        {
            yield break;
        }

        foreach (var file in (tenants.PreDeploy?.Files ?? []).Concat(tenants.PostDeploy?.Files ?? []))
        {
            foreach (var problem in TenantScript(file, tenants, parser))
            {
                yield return problem;
            }
        }
    }

    /// <summary>
    /// The error when one of the project's own script files uses a tenant's variable (G-290e: <c>tenant_schema</c>, <c>tenant_id</c>, which only a tenant's
    /// scripts have), or null. Any other variable is the environment's (G-336).
    /// </summary>
    public static SemanticDiagnostic? ProjectVariables(ScriptSourceFile file)
        => ScriptVariables.Names(file.Text).Where(PublishTexts.IsTenantVariable).ToArray() is { Length: > 0 } tenantOnly
            ? SemanticDiagnostics.Error(
                $"The project's deploy script uses {string.Join(", ", tenantOnly.Select(static name => $":'{name}'"))}: tenant_schema and tenant_id are only for a tenant's scripts (tenants.predeploy and tenants.postdeploy), which run once per tenant.",
                new SemanticLocation(file.FilePath, null),
                Source)
            : null;

    /// <summary>
    /// A tenant's script file checked with the stand-in's values (G-290e) for the tenant's variables, any other variable being the environment's (G-336), then
    /// the text's syntax errors, the environment's variables replaced by stand-ins of the same length.
    /// </summary>
    public static IEnumerable<SemanticDiagnostic> TenantScript(ScriptSourceFile file, PgProjectTenants tenants, PgQueryParser parser)
    {
        var standIn = new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant_schema"] = tenants.StandInSchema, ["tenant_id"] = tenants.StandInSchema };
        var substituted = ScriptVariables.StandIn(ScriptVariables.Substitute(file.Text, standIn).Text);
        foreach (var diagnostic in parser.Parse(substituted).Diagnostics.Where(static d => d.Severity == SqlDiagnosticSeverity.Error))
        {
            yield return new SemanticDiagnostic(SemanticDiagnosticSeverity.Error, diagnostic.Message, SemanticLocation.FromSql(file.FilePath, diagnostic), diagnostic.Source);
        }
    }
}
