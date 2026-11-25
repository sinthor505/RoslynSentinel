using System.Text;
using NUnit.Framework;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// CsprojEdgeWriter.Splice on in-memory csproj bytes.
/// Fixture justification: Splice is bytes in, text out, so no workspace or disk is needed; the rows pin BOM and EOL
/// preservation and idempotence on the exact bytes a caller would write back.
/// </summary>
[TestFixture]
public sealed class CsprojEdgeWriterTests
{
    private const string Csproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n" +
        "  <PropertyGroup>\r\n" +
        "    <TargetFramework>net10.0</TargetFramework>\r\n" +
        "  </PropertyGroup>\r\n" +
        "</Project>\r\n";

    private const string ProjectReferenceItem = "<ProjectReference Include=\"..\\Core\\Core.csproj\" />";
    private const string InternalsVisibleToItem = "<InternalsVisibleTo Include=\"App\" />";

    private static readonly NeededProjectEdge[] Edges =
    [
        new(ProjectEdgeKind.ProjectReference, "App", "Core", "reason"),
        new(ProjectEdgeKind.InternalsVisibleTo, "Core", "App", "reason"),
    ];

    [Test]
    public void Splice_PreservesCrlfAndBom_Idempotent()
    {
        // Pins: a CRLF+BOM csproj keeps its BOM and CRLF, only the added ItemGroup differs, and splicing the result again is a no-op.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Csproj)).ToArray();
        var prefix = "﻿" + Csproj.Substring(0, Csproj.LastIndexOf("</Project>", StringComparison.Ordinal));
        const string tail = "</Project>\r\n";

        var spliced = CsprojEdgeWriter.Splice(bytes, Edges);

        Assert.That(spliced.StartsWith('﻿'), Is.True);
        Assert.That(spliced, Does.StartWith(prefix));
        Assert.That(spliced, Does.EndWith(tail));
        var added = spliced.Substring(prefix.Length, spliced.Length - prefix.Length - tail.Length);
        Assert.That(added, Does.Contain("<ItemGroup>"));
        Assert.That(added, Does.Contain(ProjectReferenceItem));
        Assert.That(added, Does.Contain(InternalsVisibleToItem));
        Assert.That(spliced.Replace("\r\n", string.Empty).Contains('\n'), Is.False);

        var again = CsprojEdgeWriter.Splice(Encoding.UTF8.GetBytes(spliced), Edges);
        Assert.That(again, Is.EqualTo(spliced));
    }

    [Test]
    public void Splice_LfFileWithoutBom_KeepsLfAndIsIdempotent()
    {
        // Pins: an LF file without a BOM gets LF-only added lines, gains no BOM, and splicing the result again is a no-op.
        var lf = Csproj.Replace("\r\n", "\n");

        var spliced = CsprojEdgeWriter.Splice(Encoding.UTF8.GetBytes(lf), Edges);

        Assert.That(spliced.StartsWith('﻿'), Is.False);
        Assert.That(spliced.Contains('\r'), Is.False);
        Assert.That(spliced, Does.Contain(ProjectReferenceItem));
        Assert.That(CsprojEdgeWriter.Splice(Encoding.UTF8.GetBytes(spliced), Edges), Is.EqualTo(spliced));
    }
}
