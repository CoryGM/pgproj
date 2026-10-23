namespace PgProj.Core;

public sealed record class Workspace
{
    public required string RootDirectory { get; init; }

    public required IReadOnlyList<WorkspaceDocument> Documents { get; init; }

    /// <summary>The tenant template's files (G-290), read once per tenant rather than with <see cref="Documents"/>; empty for a project without tenants.</summary>
    public IReadOnlyList<WorkspaceDocument> TemplateDocuments { get; init; } = [];
}

public sealed record class WorkspaceDocument
{
    public required string FilePath { get; init; }

    public required string Content { get; init; }

    /// <summary>The version of the content the source gave it, when the source has one (G-309a): an editor's document version.</summary>
    public int? Version { get; init; }
}
