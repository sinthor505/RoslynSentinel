using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class MissingTargetKindsTests
{
    private static List<string> ConstantValues() =>
        typeof(MissingTargetKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    private static (string Name, IReadOnlySet<string> Set)[] NamedSets() =>
    [
        (nameof(MissingTargetKinds.FileKinds), MissingTargetKinds.FileKinds),
        (nameof(MissingTargetKinds.SymbolKinds), MissingTargetKinds.SymbolKinds),
        (nameof(MissingTargetKinds.UnmappedKinds), MissingTargetKinds.UnmappedKinds),
    ];

    [Test]
    public void EveryConstant_IsInExactlyOneSet()
    {
        var offenders = new List<string>();
        foreach (var value in ConstantValues())
        {
            var memberships = NamedSets()
                .Where(s => s.Set.Contains(value))
                .Select(s => s.Name)
                .ToList();
            if (memberships.Count != 1)
            {
                offenders.Add($"'{value}' is in {memberships.Count} sets [{string.Join(", ", memberships)}]");
            }
        }

        Assert.That(offenders, Is.Empty,
            "Each MissingTargetKinds constant must be in exactly one of FileKinds, SymbolKinds, UnmappedKinds:\n"
            + string.Join("\n", offenders));
    }

    [Test]
    public void EverySetMember_HasAConstant()
    {
        var values = new HashSet<string>(ConstantValues(), StringComparer.OrdinalIgnoreCase);
        var offenders = new List<string>();
        foreach (var (name, set) in NamedSets())
        {
            foreach (var member in set)
            {
                if (!values.Contains(member))
                {
                    offenders.Add($"{name} member '{member}' has no matching MissingTargetKinds constant");
                }
            }
        }

        Assert.That(offenders, Is.Empty, string.Join("\n", offenders));
    }

    [Test]
    public void Classifier_AgreesWithTheSets()
    {
        var offenders = new List<string>();
        foreach (var kind in MissingTargetKinds.FileKinds)
        {
            var actual = ErrorPolicyClassifier.MapKind(ToolErrorCode.NotFound, kind);
            if (actual != ErrorPolicyTargetKind.File)
            {
                offenders.Add($"FileKinds '{kind}' mapped to {(actual?.ToString() ?? "null")}, expected File");
            }
        }

        foreach (var kind in MissingTargetKinds.SymbolKinds)
        {
            var actual = ErrorPolicyClassifier.MapKind(ToolErrorCode.NotFound, kind);
            if (actual != ErrorPolicyTargetKind.Symbol)
            {
                offenders.Add($"SymbolKinds '{kind}' mapped to {(actual?.ToString() ?? "null")}, expected Symbol");
            }
        }

        foreach (var kind in MissingTargetKinds.UnmappedKinds)
        {
            var actual = ErrorPolicyClassifier.MapKind(ToolErrorCode.NotFound, kind);
            if (actual is not null)
            {
                offenders.Add($"UnmappedKinds '{kind}' mapped to {actual}, expected null");
            }
        }

        Assert.That(offenders, Is.Empty,
            "ErrorPolicyClassifier.MapKind disagrees with MissingTargetKinds:\n" + string.Join("\n", offenders));
    }
}
