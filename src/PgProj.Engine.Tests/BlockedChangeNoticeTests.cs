using PgProj.Deploy;
using PgProj.Diff;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-346: when publish refuses a change it cannot make because something uses the object, the refusal's line says why and what uses it, as a destructive
/// item gives its reasons; it used to name only the object, the reason being only in a dry run's <c>-- TODO</c>. A plain unsupported operation is unchanged.
/// </summary>
[TestClass]
public sealed class BlockedChangeNoticeTests
{
    [TestMethod]
    public void ABlockedChangeSaysWhatUsesTheObject()
    {
        var blocked = new BlockedOperation
        {
            NodeId = "public.ft",
            NodeKind = ChangeNodeKind.Table,
            Message = "The foreign table public.ft moves to another server, which PostgreSQL cannot do in place, and public.v uses it.",
        };

        var item = PublishItems.Unsupported([(null, blocked)]).Single();

        Assert.AreEqual(PublishNoticeKind.BlockedChange, item.Kind);
        Assert.AreEqual("[Table] public.ft: The foreign table public.ft moves to another server, which PostgreSQL cannot do in place, and public.v uses it.", item.Text);
    }

    [TestMethod]
    public void ATenantsPartIsNamedBeforeTheReason()
    {
        var blocked = new BlockedOperation { NodeId = "public.ft", NodeKind = ChangeNodeKind.Table, Message = "Why." };

        Assert.AreEqual("tenant acme: [Table] public.ft: Why.", PublishItems.Unsupported([("tenant acme", blocked)]).Single().Text);
    }
}
