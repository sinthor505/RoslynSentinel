using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Core-level rows of the ChangeNamespace work (plan Step 31, T31.17). MoveFileCopyAndObsolete.WithDiskBomsAsync is internal to RoslynSentinel.Tools.Basic and
// visible only to RoslynSentinel.Tests.Tools.Basic (InternalsVisibleTo), so the row lives here rather than in RoslynSentinel.Tests.Basic.
[TestFixture]
[Category("ChangeNamespace")]
public sealed class ChangeNamespaceCoreTests
{
    [Test]
    public async Task RewrittenCaller_KeepsDiskBom()
    {
        var directory = Path.Combine(ScratchRoot.Resolve(), "ChangeNamespaceCoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = new FilePathWrapper(Path.Combine(directory, "User.cs"));
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            const string rewritten = "using Demo.Other;\r\n\r\npublic class User { }\r\n";
            File.WriteAllBytes(path.Absolute, bom.Concat(System.Text.Encoding.UTF8.GetBytes(rewritten)).ToArray());

            var changes = new Dictionary<FilePathWrapper, string> { [path] = rewritten };
            var restored = await MoveFileCopyAndObsolete.WithDiskBomsAsync(changes, [path], CancellationToken.None);

            Assert.That(restored[path], Is.EqualTo("﻿" + rewritten), "The file on disk starts with the UTF-8 BOM, so the BOM is put back in front of the text.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
