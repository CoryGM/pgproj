using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using PgProj.Core;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>The project, parsed and bound (G-309a): what every command starts from.</summary>
internal sealed record ModelBuild(PgProject Project, Workspace Workspace, IReadOnlyList<SqlSyntaxTree> SyntaxTrees, DatabaseModel Model);

/// <summary>
/// Turns a project snapshot into the semantic model: parses every schema file and the tenant template, runs the semantic passes, and adds the diagnostics
/// that need the project (the deployment scripts, the template's rules, how many objects state no owner). This is what <c>CliContext.LoadProjectModel</c>
/// did before the engine (G-309a); every command reaches it through the engine (G-320).
/// </summary>
internal static class ProjectModelBuilder
{
    /// <summary>
    /// <paramref name="excludeGrants"/>, <paramref name="excludeRoleMemberships"/>, and <paramref name="excludeOwners"/> are a command's own
    /// <c>--exclude-*</c> option values (G-87, G-146), or null when not passed, in which case the project file's setting is used. With
    /// <paramref name="parseCache"/> (G-311) a file whose text is unchanged since the last build is not parsed again, and the cache is left holding this
    /// build's files only.
    /// </summary>
    public static ModelBuild Build(
        ProjectSnapshot snapshot,
        bool? excludeGrants,
        bool? excludeRoleMemberships,
        bool? excludeOwners,
        IReadOnlyList<SqlSyntaxTree>? likeSourceTrees,
        CancellationToken cancellationToken,
        ParseCache? parseCache = null)
    {
        var project = snapshot.Project;
        var workspace = snapshot.Workspace;

        // An unqualified declaration is created in the first schema of the project's search path (G-93, G-114).
        var defaultSchema = project.HasConfiguredSearchPath ? project.DefaultSchema : DefaultSchema.Name;
        var parser = new PgQueryParser(defaultSchema);
        var used = parseCache is null ? null : new ConcurrentDictionary<ParseCache.EntryKey, byte>();
        var parse = Parsing(parser, defaultSchema, parseCache, used);
        var syntaxTrees = ParseAll(workspace.Documents, document => parse(document.FilePath, document.Content) with { FilePath = document.FilePath }, ParseParallelism, cancellationToken);

        // A tenant template is checked once, in its stand-in schema (the prefix), beside the shared project (G-290).
        var templateTrees = project.Tenants is { } tenants ? ParseTemplate(project, workspace, tenants.StandInSchema, parseCache, used) : [];
        cancellationToken.ThrowIfCancellationRequested();
        var model = new SemanticBuilder().Build(
            [.. syntaxTrees, .. templateTrees],
            project.Schemas,
            excludeGrants ?? project.ExcludeGrants,
            excludeRoleMemberships ?? project.ExcludeRoleMemberships,
            project.SearchPath,
            excludeOwners ?? project.ExcludeOwners,
            likeSourceTrees);
        model.Diagnostics = [.. model.Diagnostics, .. DeployScriptCheck.CheckSyntax(project, file => parse(file.FilePath, file.Text)), .. DeployScriptCheck.Check(project, parser)];
        if (project.Tenants is { } checkedTenants)
        {
            model.Diagnostics = [.. model.Diagnostics, .. TenantTemplateCheck.Check(syntaxTrees, templateTrees, checkedTenants.Prefix)];
        }

        // A project that states some owners and not all is told how many it leaves out, so that is seen (G-146).
        if (!(excludeOwners ?? project.ExcludeOwners) && OwnerCoverage.Describe(model) is { } ownerNotice)
        {
            model.Diagnostics = [.. model.Diagnostics, ownerNotice];
        }

        parseCache?.KeepOnly(used!.Keys);
        return new ModelBuild(project, workspace, [.. syntaxTrees, .. templateTrees], model);
    }

    /// <summary>
    /// A file's parse with <paramref name="parser"/>: through <paramref name="parseCache"/> when there is one (G-311), the file noted in <paramref name="used"/>
    /// so the cache keeps it.
    /// </summary>
    private static Func<string, string, SqlSyntaxTree> Parsing(
        PgQueryParser parser,
        string defaultSchema,
        ParseCache? parseCache,
        ConcurrentDictionary<ParseCache.EntryKey, byte>? used)
    {
        if (parseCache is null)
        {
            return (_, text) => parser.Parse(text);
        }

        return (path, text) =>
        {
            used![new ParseCache.EntryKey(path, defaultSchema)] = 0;
            return parseCache.GetOrParse(path, defaultSchema, text, parser.Parse);
        };
    }

    /// <summary>
    /// A model of <paramref name="trees"/> under the project's settings, as <see cref="Build"/> builds the whole project: for a project with tenants
    /// (G-290), the shared files alone, or the shared files with the template read for one tenant.
    /// </summary>
    public static DatabaseModel BuildModel(PgProject project, IReadOnlyList<SqlSyntaxTree> trees, bool? excludeGrants, bool? excludeRoleMemberships, bool? excludeOwners)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(trees);

        return new SemanticBuilder().Build(
            trees,
            project.Schemas,
            excludeGrants ?? project.ExcludeGrants,
            excludeRoleMemberships ?? project.ExcludeRoleMemberships,
            project.SearchPath,
            excludeOwners ?? project.ExcludeOwners);
    }

    /// <summary>
    /// The tenant template's files read for <paramref name="schema"/> (G-290): its unqualified declarations are created in that schema, and each file's
    /// names resolve along that schema and then the project's <c>searchPath</c>, as PostgreSQL resolves them in the session that deploys the tenant.
    /// <c>build</c> reads them for the stand-in schema.
    /// </summary>
    public static IReadOnlyList<SqlSyntaxTree> ParseTemplate(
        PgProject project,
        Workspace workspace,
        string schema,
        ParseCache? parseCache = null,
        ConcurrentDictionary<ParseCache.EntryKey, byte>? used = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        var parse = Parsing(new PgQueryParser(schema), schema, parseCache, used);
        IReadOnlyList<string> searchPath = [schema, .. project.SearchPath.Where(path => !SqlIdentifiers.NameComparer.Equals(path, schema))];
        return ParseAll(
            workspace.TemplateDocuments,
            document => parse(document.FilePath, document.Content) with { FilePath = document.FilePath, SearchPath = searchPath },
            ParseParallelism,
            CancellationToken.None);
    }

    /// <summary>How many files are parsed at once (G-312): one per processor.</summary>
    internal static int ParseParallelism => Environment.ProcessorCount;

    /// <summary>
    /// Each file parsed by <paramref name="parse"/>, at most <paramref name="maxParallelism"/> at a time, the trees in the files' order (G-312). Files do not
    /// depend on each other when they are parsed, the parser keeps its state per thread, and libpg_query its memory, so the trees are those of a parse of one
    /// file after another. A defect in a parse is thrown as itself, the first file's when several fail, as when the files were parsed in turn.
    /// </summary>
    internal static SqlSyntaxTree[] ParseAll<TFile>(
        IReadOnlyList<TFile> files,
        Func<TFile, SqlSyntaxTree> parse,
        int maxParallelism,
        CancellationToken cancellationToken)
    {
        var trees = new SqlSyntaxTree[files.Count];
        var failures = new ExceptionDispatchInfo?[files.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = cancellationToken };
        Parallel.For(0, files.Count, options, (index, loop) =>
        {
            try
            {
                trees[index] = parse(files[index]);
            }
            catch (Exception exception)
            {
                // Break, not Stop: every file before this one is still parsed, so the first failure in the files' order is the one thrown.
                failures[index] = ExceptionDispatchInfo.Capture(exception);
                loop.Break();
            }
        });

        foreach (var failure in failures)
        {
            failure?.Throw();
        }

        return trees;
    }

    /// <summary>The lines a command's header adds for a project with tenants (G-290): the template, and where the tenants come from.</summary>
    public static IEnumerable<string> DescribeTenants(PgProject project, Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(workspace);

        if (project.Tenants is not { } tenants)
        {
            yield break;
        }

        yield return $"Tenant template: {tenants.TemplatePath} ({workspace.TemplateDocuments.Count} SQL file(s)), checked in the stand-in schema {tenants.StandInSchema}";
        yield return tenants.Source switch
        {
            TenantSource.Query => $"Tenants: the identifiers the tenant query returns, each in schema {tenants.Prefix}_<identifier>",
            TenantSource.File => $"Tenants: {tenants.FileTenants.Count} listed in {tenants.FilePath}, each in schema {tenants.Prefix}_<identifier>",
            _ => $"Tenants: the database's schemas named {tenants.Prefix}_...",
        };
    }
}
