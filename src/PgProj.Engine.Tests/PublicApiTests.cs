using System.Reflection;
using System.Text;

namespace PgProj.Engine.Tests;

/// <summary>
/// G-309a: the engine's public surface is the library's promise to its shells, so a change to it must be deliberate (the user's decision 4 on
/// #213: a plain test, no package). The expected surface is <c>PublicApi.txt</c>, beside this file; when this test fails, read the difference,
/// and if the change is meant, replace the file's text with the actual surface (the failure message prints it) and say so in <c>CHANGELOG.md</c>.
/// </summary>
[TestClass]
public sealed class PublicApiTests
{
    [TestMethod]
    public void TheEnginesPublicSurfaceIsTheOneListed()
    {
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PublicApi.txt")).ReplaceLineEndings("\n").TrimEnd();
        var actual = Describe(typeof(PgProjEngine).Assembly).ReplaceLineEndings("\n").TrimEnd();
        // The surface as it is now, beside the test, to copy over PublicApi.txt when a change is meant.
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "PublicApi.actual.txt"), actual + "\n");

        Assert.AreEqual(expected, actual, "The public surface of PgProj.Engine changed. If that is meant, replace PublicApi.txt with:\n" + actual);
    }

    [TestMethod]
    public void TheEngineDoesNotReferenceTheConsoleOrTheCommandLine()
    {
        var references = typeof(PgProjEngine).Assembly.GetReferencedAssemblies().Select(static name => name.Name).ToArray();

        CollectionAssert.DoesNotContain(references, "System.CommandLine");
        CollectionAssert.DoesNotContain(references, "System.Console");
    }

    /// <summary>What the compiler writes for a record (copying, equality, printing, deconstruction): not part of what a person designed.</summary>
    private static bool IsRecordPlumbing(MemberInfo member)
        => member.Name is "<Clone>$" or "Equals" or "GetHashCode" or "ToString" or "Deconstruct" or "PrintMembers" or "EqualityContract";

    internal static string Describe(Assembly assembly)
    {
        var text = new StringBuilder();
        foreach (var type in assembly.GetExportedTypes().OrderBy(static type => type.FullName, StringComparer.Ordinal))
        {
            text.Append(type.IsEnum ? "enum " : type.IsInterface ? "interface " : type.IsValueType ? "struct " : type.IsAbstract && type.IsSealed ? "static class " : type.IsSealed ? "sealed class " : "class ").AppendLine(type.FullName);
            if (type.IsEnum)
            {
                foreach (var name in Enum.GetNames(type))
                {
                    text.Append("  ").AppendLine(name);
                }

                continue;
            }

            var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(static member => member is not MethodInfo { IsSpecialName: true } && member is not Type && !IsRecordPlumbing(member))
                .Select(static member => member.ToString()!)
                .Order(StringComparer.Ordinal);
            foreach (var member in members)
            {
                text.Append("  ").AppendLine(member);
            }
        }

        return text.ToString();
    }
}
