using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

/// <summary>Outcome of one slip site after grading the end-state file.</summary>
internal enum SiteOutcome { Correct, Unchanged, Wrong, Missing }

/// <summary>Graded result for one slip site, with its end-state text when present.</summary>
internal sealed record SiteGrade(string SiteName, SiteRole Role, SiteOutcome Outcome, string? EndStateText);

/// <summary>Grades slip-first sites against an end-state file and reads or writes the per-step sidecar.</summary>
internal static class SiteGrader
{
    public const string SidecarFileNameFormat = "step-{0}.sites.json";

    private static readonly JsonSerializerOptions SidecarJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<SiteGrade> Grade(string? fileText, SlipSiteSet set)
    {
        var expanded = SlipFamilyBuilder.Expand(set);
        if (fileText == null)
        {
            return expanded.Select(site => new SiteGrade(site.Name, site.Role, SiteOutcome.Missing, null)).ToList();
        }

        var root = ParseText(fileText).GetRoot();
        var named = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(cls => cls.Identifier.ValueText == set.ClassName)
            .SelectMany(cls => NamedMembers(cls))
            .ToList();

        var grades = new List<SiteGrade>(expanded.Count);
        foreach (var site in expanded)
        {
            var matches = named.Where(m => m.Name == site.Name).Select(m => m.Node).ToList();
            if (matches.Count == 0)
            {
                grades.Add(new SiteGrade(site.Name, site.Role, SiteOutcome.Missing, null));
                continue;
            }

            if (matches.Count > 1)
            {
                var joined = string.Join("\n---\n", matches.Select(node => node.ToFullString().Trim()));
                grades.Add(new SiteGrade(site.Name, site.Role, SiteOutcome.Missing, joined));
                continue;
            }

            var endNode = matches[0];
            var endElements = Elements(endNode);
            var endText = endNode.ToFullString().Trim();
            if (endElements.SequenceEqual(Elements(ParseSingleMember(SlipFamilyBuilder.ApplyChanges(site), site.Name))))
            {
                grades.Add(new SiteGrade(site.Name, site.Role, SiteOutcome.Correct, null));
            }
            else if (endElements.SequenceEqual(Elements(ParseSingleMember(site.SeedText, site.Name))))
            {
                grades.Add(new SiteGrade(site.Name, site.Role, SiteOutcome.Unchanged, endText));
            }
            else
            {
                grades.Add(new SiteGrade(site.Name, site.Role, SiteOutcome.Wrong, endText));
            }
        }

        return grades;
    }

    public static bool AllIntact(IReadOnlyList<SiteGrade> grades)
    {
        return grades.All(grade => grade.Outcome == SiteOutcome.Correct);
    }

    public static string Summarize(IReadOnlyList<SiteGrade> grades)
    {
        int Tally(SiteRole role, SiteOutcome outcome) => grades.Count(grade => grade.Role == role && grade.Outcome == outcome);

        var changedCorrect = Tally(SiteRole.Changed, SiteOutcome.Correct);
        var changedUnchanged = Tally(SiteRole.Changed, SiteOutcome.Unchanged);
        var changedWrong = Tally(SiteRole.Changed, SiteOutcome.Wrong);
        var changedMissing = Tally(SiteRole.Changed, SiteOutcome.Missing);
        var controlWrong = Tally(SiteRole.Control, SiteOutcome.Wrong);
        var controlMissing = Tally(SiteRole.Control, SiteOutcome.Missing);
        var separatorBroken = Tally(SiteRole.Separator, SiteOutcome.Wrong) + Tally(SiteRole.Separator, SiteOutcome.Missing);

        return FormattableString.Invariant(
            $"sites: changed correct={changedCorrect} unchanged={changedUnchanged} wrong={changedWrong} missing={changedMissing}; control wrong={controlWrong} missing={controlMissing}; separator broken={separatorBroken}");
    }

    public static void WriteSidecar(string path, IReadOnlyList<SiteGrade> grades)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(grades, SidecarJsonOptions));
    }

    public static IReadOnlyList<SiteGrade> ReadSidecar(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<SiteGrade>>(json, SidecarJsonOptions) ?? new List<SiteGrade>();
    }

    private static SyntaxTree ParseText(string text)
    {
        return CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithDocumentationMode(DocumentationMode.Parse));
    }

    private static MemberDeclarationSyntax ParseSingleMember(string memberText, string siteName)
    {
        var root = ParseText("class C\r\n{\r\n" + memberText + "\r\n}\r\n").GetRoot();
        if (root is CompilationUnitSyntax unit
            && unit.Members.Count == 1
            && unit.Members[0] is ClassDeclarationSyntax wrapper
            && wrapper.Members.Count == 1)
        {
            return wrapper.Members[0];
        }

        throw new InvalidOperationException($"Slip site '{siteName}': golden or seed text must parse to exactly one member.");
    }

    private static IEnumerable<(string Name, MemberDeclarationSyntax Node)> NamedMembers(ClassDeclarationSyntax cls)
    {
        foreach (var member in cls.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    yield return (method.Identifier.ValueText, member);
                    break;
                case PropertyDeclarationSyntax property:
                    yield return (property.Identifier.ValueText, member);
                    break;
                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                    {
                        yield return (variable.Identifier.ValueText, member);
                    }

                    break;
            }
        }
    }

    private static List<(SyntaxKind Kind, string Text)> Elements(SyntaxNode node)
    {
        var elements = new List<(SyntaxKind Kind, string Text)>();
        var run = new StringBuilder();
        var inRun = false;
        foreach (var token in node.DescendantTokens(descendIntoTrivia: true))
        {
            var kind = token.Kind();
            if (kind == SyntaxKind.DocumentationCommentExteriorTrivia)
            {
                continue;
            }

            if (kind == SyntaxKind.XmlTextLiteralNewLineToken)
            {
                if (inRun)
                {
                    run.Append(' ');
                }

                continue;
            }

            if (kind == SyntaxKind.XmlTextLiteralToken || kind == SyntaxKind.XmlEntityLiteralToken)
            {
                inRun = true;
                run.Append(token.Text);
                continue;
            }

            FlushRun(elements, run, ref inRun);
            elements.Add((kind, token.Text));
        }

        FlushRun(elements, run, ref inRun);
        return elements;
    }

    private static void FlushRun(List<(SyntaxKind Kind, string Text)> elements, StringBuilder run, ref bool inRun)
    {
        if (inRun)
        {
            var collapsed = Regex.Replace(run.ToString(), @"\s+", " ").Trim();
            if (collapsed.Length > 0)
            {
                elements.Add((SyntaxKind.XmlTextLiteralToken, collapsed));
            }
        }

        run.Clear();
        inRun = false;
    }
}
