using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-347: <c>--apply-secrets</c> with a name no user mapping's options use (a typo) sent nothing and reported success; it is now refused before anything runs
/// with <c>--execute</c>, and warned about in a plan, naming the variables the mappings do use. No names (every variable) is never refused.
/// </summary>
[TestClass]
public sealed class ApplySecretsNameTests
{
    private static readonly DatabaseModel Model = new SemanticPipeline().Execute([new PgQueryParser().Parse("""
        CREATE FOREIGN DATA WRAPPER w;
        CREATE SERVER wh FOREIGN DATA WRAPPER w OPTIONS (host :'wh_host');
        CREATE USER MAPPING FOR app SERVER wh OPTIONS (user 'u', password :'wh_password');
        CREATE USER MAPPING FOR PUBLIC SERVER wh OPTIONS (user 'anon', password :'anon_password');
        """)]);

    [TestMethod]
    public void ANameNoMappingUsesIsUnknownAndOneItUsesIsNot()
    {
        CollectionAssert.AreEqual(new[] { "wh_pasword" }, PublishTexts.UnknownSecrets(["wh_password", "wh_pasword", "wh_pasword"], Model).ToArray());
        Assert.IsEmpty(PublishTexts.UnknownSecrets([], Model), "No names means every variable the mappings use.");
        Assert.IsEmpty(PublishTexts.UnknownSecrets(null, Model), "Not given.");
        CollectionAssert.AreEqual(new[] { "wh_host" }, PublishTexts.UnknownSecrets(["wh_host"], Model).ToArray(), "A server's variable is not a mapping's secret.");
    }

    [TestMethod]
    public void TheRefusalNamesTheUnknownNamesAndTheOnesTheMappingsUse()
    {
        Assert.AreEqual(
            "Publish failed: --apply-secrets names :'wh_pasword', which no user mapping's options use, so nothing was run. The project's user mappings use :'anon_password', :'wh_password'.",
            PublishTexts.DescribeUnknownSecrets(["wh_pasword"], PublishTexts.SecretVariables(Model), executing: true));
        StringAssert.StartsWith(PublishTexts.DescribeUnknownSecrets(["x"], [], executing: false), "Warning: --apply-secrets names :'x', which no user mapping's options use, and publish --execute refuses it. The project's user mappings use no variable.");
    }
}
