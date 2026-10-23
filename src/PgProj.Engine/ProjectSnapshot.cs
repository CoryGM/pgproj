using PgProj.Core;

namespace PgProj.Engine;

/// <summary>
/// Everything a run reads from the project's files, read once at its start (G-309a): the settings, the pre- and post-deployment scripts with
/// their includes, the tenant file, and the text and version of every schema file and tenant template file. A run then works from the snapshot
/// alone, so a file saved while it runs cannot make its result inconsistent, and the result can say which version of each document it describes.
/// </summary>
internal sealed record ProjectSnapshot(PgProject Project, Workspace Workspace)
{
    /// <summary>
    /// Reads the project through <paramref name="source"/>: from the project file at <paramref name="projectFilePath"/>, or from
    /// <paramref name="settings"/> resolved against <paramref name="rootDirectory"/>. Throws what the loader throws:
    /// <see cref="FileNotFoundException"/> for a project file that is not there, <see cref="PgProjectValidationException"/> for a project that is not valid.
    /// </summary>
    public static ProjectSnapshot Read(IProjectFileSource source, string? projectFilePath, ProjectSettings? settings, string? rootDirectory, CancellationToken cancellationToken)
    {
        var loader = new PgProjectLoader();
        var project = settings is not null
            ? loader.Load(source, settings, rootDirectory!)
            : loader.Load(source, projectFilePath!);
        cancellationToken.ThrowIfCancellationRequested();
        return new ProjectSnapshot(project, loader.LoadWorkspace(project, source));
    }

    /// <summary>The schema files read, each with the version the source gave it.</summary>
    public IReadOnlyList<DocumentInfo> Documents => [.. Workspace.Documents.Select(static document => new DocumentInfo(document.FilePath, document.Version))];

    /// <summary>The tenant template's files read, each with its version.</summary>
    public IReadOnlyList<DocumentInfo> TemplateDocuments => [.. Workspace.TemplateDocuments.Select(static document => new DocumentInfo(document.FilePath, document.Version))];
}

/// <summary>A file the run read, and the version of its content when the source has one (an editor's document version).</summary>
/// <param name="Path">The file's full path.</param>
/// <param name="Version">The version the source gave it, or null.</param>
public sealed record DocumentInfo(string Path, int? Version);
