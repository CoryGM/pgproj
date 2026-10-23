using PgProj.Core;
using PgProj.Deploy;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Engine;

/// <summary>
/// The items a publish's warnings and refusals name (G-316): each with the object it is about, so a shell can go to it, and the line <c>pgproj publish</c> lists it
/// as. A method's <c>part</c> is the part of a tenants publish the item is in, or null.
/// </summary>
internal static class PublishItems
{
    public static PublishItem[] LockRewrites(IEnumerable<LockWarning> warnings, string? part = null)
        => [.. warnings.Select(warning => new PublishItem(PublishNoticeKind.LockRewrite, $"{warning.TableKey}: {warning.Operation}", ChangeNodeKind.Table, warning.TableKey, $"{warning.Operation}: {warning.Effect}", part))];

    public static PublishItem[] BodyNotes(IEnumerable<PlpgsqlBodyNote> notes)
        => [.. notes.Select(static note => new PublishItem(
            PublishNoticeKind.PlpgsqlBody,
            $"{note.RoutineKey}: {note.What}",
            note.RoutineKind == "procedure" ? ChangeNodeKind.Procedure : ChangeNodeKind.Function,
            note.RoutineKey,
            note.What))];

    public static PublishItem[] Schemas(PublishNoticeKind kind, IEnumerable<string> schemas)
        => [.. schemas.Select(schema => new PublishItem(kind, schema, ChangeNodeKind.Schema, schema))];

    public static PublishItem[] Features(PublishNoticeKind kind, IEnumerable<ServerFeature> features)
        => [.. features.Select(feature => new PublishItem(kind, feature.Description, Detail: $"needs PostgreSQL {feature.MinimumMajorVersion}"))];

    public static PublishItem[] Tablespaces(PublishNoticeKind kind, IEnumerable<MissingTablespace> missing)
        => [.. missing.Select(item => new PublishItem(kind, item.TablespaceName, NodeId: item.TablespaceName, Detail: "named by " + string.Join(", ", item.Tables)))];

    public static PublishItem[] Roles(PublishNoticeKind kind, IEnumerable<MissingRole> missing)
        => [.. missing.Select(item => new PublishItem(kind, item.RoleName, NodeId: item.RoleName, Detail: "named by " + string.Join(", ", item.NamedBy)))];

    public static PublishItem[] Superuser(IEnumerable<(string? Part, SuperuserStatement Statement)> statements)
        => [.. statements.Select(static item => new PublishItem(
            PublishNoticeKind.Superuser,
            (item.Part is null ? string.Empty : item.Part + ": ") + SuperuserStatements.Describe([item.Statement]).Single(),
            item.Statement.NodeKind,
            item.Statement.NodeId,
            item.Statement.Reason,
            item.Part))];

    /// <summary>The operations that emit a placeholder; a blocked change (an object in use that cannot be changed, G-148) is <see cref="PublishNoticeKind.BlockedChange"/>.</summary>
    public static PublishItem[] Unsupported(IEnumerable<(string? Part, SqlOperation Operation)> operations)
        => [.. operations.Select(static item => new PublishItem(
            item.Operation is BlockedOperation ? PublishNoticeKind.BlockedChange : PublishNoticeKind.Unsupported,
            (item.Part is null ? string.Empty : item.Part + ": ") + $"[{item.Operation.NodeKind}] {DatabaseModel.DisplayKey(item.Operation.NodeId)}",
            item.Operation.NodeKind,
            item.Operation.NodeId,
            item.Operation is BlockedOperation blocked ? blocked.Message : null,
            item.Part))];

    public static PublishItem[] Destructive(IEnumerable<(string? Part, DestructiveOperation Operation)> operations)
        => [.. operations.Select(static item => new PublishItem(
            PublishNoticeKind.Destructive,
            (item.Part is null ? string.Empty : item.Part + ": ") + $"[{item.Operation.Operation.NodeKind}] {DatabaseModel.DisplayKey(item.Operation.Operation.NodeId)}: {string.Join("; ", item.Operation.Reasons)}",
            item.Operation.Operation.NodeKind,
            item.Operation.Operation.NodeId,
            string.Join("; ", item.Operation.Reasons),
            item.Part))];

    /// <summary>The refusal's kind for placeholders: <see cref="PublishNoticeKind.BlockedChange"/> when every one is a blocked change.</summary>
    public static PublishNoticeKind UnsupportedKind(IReadOnlyList<PublishItem> items)
        => items.Count > 0 && items.All(static item => item.Kind == PublishNoticeKind.BlockedChange) ? PublishNoticeKind.BlockedChange : PublishNoticeKind.Unsupported;
}
