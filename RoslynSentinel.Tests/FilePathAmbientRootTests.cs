// FilePathWrapper.UseSolutionRoot: while a tool call is in scope, the implicit string -> wrapper
// conversion and the JSON converter resolve a RELATIVE path against the solution root instead of
// building an unrooted wrapper whose Absolute is still relative (which a path lookup can never
// match). Outside a scope, and for rooted paths, behavior is unchanged.

using System.Text.Json;

namespace RoslynSentinel.Tests;

[TestFixture]
public class FilePathAmbientRootTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "FilePathAmbientRootTests_root");

    [Test]
    public void ImplicitConversion_NoScope_RelativePath_StaysUnrooted()
    {
        FilePathWrapper wrapper = "Sub/Foo.cs";

        Assert.That(wrapper.Absolute, Is.EqualTo(Path.Combine("Sub", "Foo.cs")));
        Assert.That(Path.IsPathRooted(wrapper.Absolute), Is.False);
    }

    [Test]
    public void ImplicitConversion_InScope_RelativePath_ResolvesAgainstRoot()
    {
        using (FilePathWrapper.UseSolutionRoot(Root))
        {
            FilePathWrapper wrapper = "Sub/Foo.cs";

            Assert.That(wrapper.Absolute, Is.EqualTo(Path.GetFullPath(Path.Combine(Root, "Sub", "Foo.cs"))));
            Assert.That(wrapper.Validated, Is.True);
        }
    }

    [Test]
    public void ImplicitConversion_InScope_RootedPath_IsNotRewritten()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "elsewhere", "Foo.cs");

        using (FilePathWrapper.UseSolutionRoot(Root))
        {
            FilePathWrapper wrapper = rooted;

            Assert.That(wrapper.Absolute, Is.EqualTo(rooted));
        }
    }

    [Test]
    public void Scope_Dispose_RestoresPreviousRoot()
    {
        using (FilePathWrapper.UseSolutionRoot(Root))
        {
            using (FilePathWrapper.UseSolutionRoot(null))
            {
                FilePathWrapper inner = "Foo.cs";
                Assert.That(Path.IsPathRooted(inner.Absolute), Is.False, "a null root clears the scope");
            }

            FilePathWrapper outer = "Foo.cs";
            Assert.That(outer.Absolute, Is.EqualTo(Path.Combine(Root, "Foo.cs")), "disposing restores the enclosing scope");
        }

        FilePathWrapper after = "Foo.cs";
        Assert.That(Path.IsPathRooted(after.Absolute), Is.False, "disposing the outermost scope restores no root");
    }

    [Test]
    public void JsonConverter_InScope_RelativePath_ResolvesAgainstRoot()
    {
        using (FilePathWrapper.UseSolutionRoot(Root))
        {
            var wrapper = JsonSerializer.Deserialize<FilePathWrapper>("\"Sub/Foo.cs\"");

            Assert.That(wrapper.Absolute, Is.EqualTo(Path.GetFullPath(Path.Combine(Root, "Sub", "Foo.cs"))));
        }
    }

    [Test]
    public void JsonConverter_NoScope_RelativePath_StaysUnrooted()
    {
        var wrapper = JsonSerializer.Deserialize<FilePathWrapper>("\"Sub/Foo.cs\"");

        Assert.That(Path.IsPathRooted(wrapper.Absolute), Is.False);
    }

    [Test]
    public async Task Scope_DoesNotLeakIntoConcurrentUnscopedWorkAsync()
    {
        using var scope = FilePathWrapper.UseSolutionRoot(Root);

        // A task started from an unscoped context before the scope existed cannot observe it; a
        // fresh ExecutionContext-free thread-pool callback stands in for "another request".
        var seenByOtherRequest = await Task.Run(() =>
        {
            using (ExecutionContext.SuppressFlow())
            {
                return Task.Run(() =>
                {
                    FilePathWrapper w = "Foo.cs";
                    return w.Absolute;
                }).Result;
            }
        });

        Assert.That(Path.IsPathRooted(seenByOtherRequest), Is.False);
    }
}
