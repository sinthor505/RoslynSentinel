using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

internal enum ShippingKindStepOutcome
{
    Pass,
    OutOfScopeEdit,
    CheckFailed,
    BuildBroken,
    Blocked,
    NotConverged
}

internal static class ShippingKindStepChecks
{
    public static async Task<(ShippingKindStepOutcome Outcome, string Detail)> CheckAsync(int step, string solutionDirectory, string? finalMessage, IReadOnlyList<string> changedPaths, CancellationToken cancellationToken)
    {
        if (step == 1)
        {
            if (string.IsNullOrWhiteSpace(finalMessage))
            {
                return (ShippingKindStepOutcome.CheckFailed, "final message is empty");
            }

            foreach (var stem in Step1RequiredStems)
            {
                if (finalMessage.IndexOf(stem, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return (ShippingKindStepOutcome.CheckFailed, $"final message does not name {stem}");
                }
            }

            var step1Violation = FindScopeViolation(step, changedPaths, Array.Empty<string>());
            if (step1Violation is not null)
            {
                return (ShippingKindStepOutcome.OutOfScopeEdit, step1Violation);
            }

            return (ShippingKindStepOutcome.Pass, "ok");
        }

        if (step == 4 || step == 5)
        {
            var files = step == 4
                ? new[] { "ShippingFeeCalculator.cs", "ShipmentValidator.cs" }
                : new[] { "DeliveryEstimator.cs", "CarrierSelector.cs" };
            foreach (var file in files)
            {
                var relative = "ContosoOrders.Core/Shipping/" + file;
                var fullPath = Path.Combine(solutionDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath))
                {
                    return (ShippingKindStepOutcome.CheckFailed, $"{relative} does not exist");
                }

                var text = await File.ReadAllTextAsync(fullPath, cancellationToken);
                foreach (var literal in ForbiddenLiterals)
                {
                    if (text.Contains(literal, StringComparison.Ordinal))
                    {
                        return (ShippingKindStepOutcome.CheckFailed, $"{relative} contains the literal {literal}; use the ShippingKinds constant instead");
                    }
                }

                // A `using static ...ShippingKinds;` directive with bare names is as valid as the
                // qualified form; the behaviour tests below prove the semantics either way.
                var hasUsingStatic = System.Text.RegularExpressions.Regex.IsMatch(text, @"using\s+static\s+[\w.]*\bShippingKinds\s*;");
                if (!hasUsingStatic && !text.Contains("ShippingKinds.", StringComparison.Ordinal))
                {
                    return (ShippingKindStepOutcome.CheckFailed, $"{relative} does not reference ShippingKinds (neither ShippingKinds.<Name> nor a using static directive).");
                }

                var callsIsExpedited = text.Contains("ShippingKinds.IsExpedited(", StringComparison.Ordinal)
                    || (hasUsingStatic && System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<![\w.])IsExpedited\("));
                if (step == 5 && !callsIsExpedited)
                {
                    return (ShippingKindStepOutcome.CheckFailed, $"{relative} does not call ShippingKinds.IsExpedited(");
                }
            }
        }

        var source = step switch
        {
            2 => Step2Source,
            3 => Step3Source,
            4 => Step4Source,
            5 => Step5Source,
            6 => Step6Source,
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
        var testDirectory = Path.Combine(solutionDirectory, "ContosoOrders.Tests", "ModelEvalGenerated");
        var testFile = Path.Combine(testDirectory, $"ShippingKindStep{step}Tests.cs");
        var testProject = Path.Combine(solutionDirectory, "ContosoOrders.Tests", "ContosoOrders.Tests.csproj");
        DotnetTestResult result;
        try
        {
            Directory.CreateDirectory(testDirectory);
            await File.WriteAllTextAsync(testFile, NormalizeToCrlf(source), cancellationToken);
            result = await DotnetTestRunner.RunAsync(testProject, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return (ShippingKindStepOutcome.BuildBroken, BuildBrokenDetail(ex.Message));
        }
        finally
        {
            File.Delete(testFile);
        }

        if (result.Total == 0 || result.Failed > 0 || result.Passed != result.Total)
        {
            var detail = $"check tests failed: {result.Failed} of {result.Total} failed";
            var failedLine = result.RawOutput.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("Failed ", StringComparison.Ordinal));
            if (failedLine is not null)
            {
                detail += $"; {failedLine}";
            }

            return (ShippingKindStepOutcome.CheckFailed, detail);
        }

        if (step >= 4)
        {
            var (contractOk, contractDetail) = await RunContractTestsAsync(step, solutionDirectory, cancellationToken);
            if (!contractOk)
            {
                return (ShippingKindStepOutcome.CheckFailed, contractDetail);
            }
        }

        var violation = FindScopeViolation(step, changedPaths, ShippingKindStepGoldens.AllowedPathsForStep(step));
        if (violation is not null)
        {
            return (ShippingKindStepOutcome.OutOfScopeEdit, violation);
        }

        return (ShippingKindStepOutcome.Pass, $"check tests passed: {result.Passed}");
    }

    private const string Step2Source = """
using ContosoOrders.Core;
using Xunit;

namespace ContosoOrders.Tests;

public class ShippingKindStep2Tests
{
    [Fact]
    public void Constants_HaveTheTableValues()
    {
        Assert.Equal("standard", ShippingKinds.Standard);
        Assert.Equal("express", ShippingKinds.Express);
        Assert.Equal("overnight", ShippingKinds.Overnight);
        Assert.Equal("pickup", ShippingKinds.Pickup);
    }

    [Fact]
    public void All_HasExactlyTheFourValues()
    {
        Assert.Equal(4, ShippingKinds.All.Count);
        Assert.Contains("standard", ShippingKinds.All);
        Assert.Contains("express", ShippingKinds.All);
        Assert.Contains("overnight", ShippingKinds.All);
        Assert.Contains("pickup", ShippingKinds.All);
    }
}
""";

    private const string Step3Source = """
using ContosoOrders.Core;
using Xunit;

namespace ContosoOrders.Tests;

public class ShippingKindStep3Tests
{
    [Fact]
    public void IsKnown_RecognisesTheKinds()
    {
        Assert.True(ShippingKinds.IsKnown("express"));
        Assert.False(ShippingKinds.IsKnown("drone"));
        Assert.False(ShippingKinds.IsKnown(null));
    }

    [Fact]
    public void IsExpedited_OnlyForFastKinds()
    {
        Assert.True(ShippingKinds.IsExpedited("overnight"));
        Assert.False(ShippingKinds.IsExpedited("standard"));
    }

    [Fact]
    public void Normalize_TrimsAndLowercases()
    {
        Assert.Equal("express", ShippingKinds.Normalize(" Express "));
    }

    [Fact]
    public void Normalize_UnknownKind_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => ShippingKinds.Normalize("drone"));
    }
}
""";

    private const string Step4Source = """
using ContosoOrders.Core;
using Xunit;

namespace ContosoOrders.Tests;

public class ShippingKindStep4Tests
{
    [Theory]
    [InlineData("standard", 2.0, 5.99)]
    [InlineData("express", 2.0, 11.49)]
    [InlineData("overnight", 2.0, 21.99)]
    [InlineData("pickup", 3.0, 0.0)]
    public void Fee_MatchesTheTable(string kind, double weight, double expected)
    {
        Assert.Equal((decimal)expected, ShippingFeeCalculator.Calculate(new Shipment("o1", kind, (decimal)weight)));
    }

    [Fact]
    public void Fee_UnknownKind_Throws()
    {
        Assert.Throws<ArgumentException>(() => ShippingFeeCalculator.Calculate(new Shipment("o1", "drone", 1m)));
    }

    [Theory]
    [InlineData("standard", 1, true)]
    [InlineData("standard", 0, false)]
    [InlineData("express", 0, false)]
    [InlineData("overnight", -1, false)]
    [InlineData("pickup", 0, true)]
    [InlineData("pickup", -1, false)]
    [InlineData("drone", 1, false)]
    [InlineData("Standard", 1, false)]
    public void Valid_MatchesTheRules(string kind, int weight, bool expected)
    {
        Assert.Equal(expected, ShipmentValidator.IsValid(new Shipment("o1", kind, weight)));
    }
}
""";

    private const string Step5Source = """
using ContosoOrders.Core;
using Xunit;

namespace ContosoOrders.Tests;

public class ShippingKindStep5Tests
{
    [Theory]
    [InlineData("standard", 5)]
    [InlineData("express", 2)]
    [InlineData("overnight", 1)]
    [InlineData("pickup", 0)]
    public void Days_MatchesTheTable(string kind, int expected)
    {
        Assert.Equal(expected, DeliveryEstimator.EstimateDays(kind));
    }

    [Fact]
    public void Days_UnknownKind_Throws()
    {
        Assert.Throws<ArgumentException>(() => DeliveryEstimator.EstimateDays("drone"));
        Assert.Throws<ArgumentException>(() => DeliveryEstimator.EstimateDays("Express"));
    }

    [Theory]
    [InlineData("standard", "GroundPost")]
    [InlineData("express", "AirFast")]
    [InlineData("overnight", "AirFast")]
    [InlineData("pickup", "none")]
    public void Carrier_MatchesTheTable(string kind, string expected)
    {
        Assert.Equal(expected, CarrierSelector.Select(new Shipment("o1", kind, 1m)));
    }

    [Fact]
    public void Carrier_UnknownKind_Throws()
    {
        Assert.Throws<ArgumentException>(() => CarrierSelector.Select(new Shipment("o1", "drone", 1m)));
    }
}
""";

    private const string Step6Source = """
using ContosoOrders.Core;
using Xunit;

namespace ContosoOrders.Tests;

public class ShippingKindStep6Tests
{
    [Fact]
    public void Constants_HaveTheNewValues()
    {
        Assert.Equal("regular", ShippingKinds.Standard);
        Assert.Equal("rush", ShippingKinds.Express);
        Assert.Equal("nextday", ShippingKinds.Overnight);
        Assert.Equal("collect", ShippingKinds.Pickup);
        Assert.Equal("sameday", ShippingKinds.SameDay);
    }

    [Fact]
    public void All_HasTheFiveValues()
    {
        Assert.Equal(5, ShippingKinds.All.Count);
        Assert.Contains("regular", ShippingKinds.All);
        Assert.Contains("rush", ShippingKinds.All);
        Assert.Contains("nextday", ShippingKinds.All);
        Assert.Contains("collect", ShippingKinds.All);
        Assert.Contains("sameday", ShippingKinds.All);
    }

    [Fact]
    public void IsKnown_IncludesSameDayAndRejectsTheOldValues()
    {
        Assert.True(ShippingKinds.IsKnown(ShippingKinds.SameDay));
        Assert.False(ShippingKinds.IsKnown("express"));
        Assert.False(ShippingKinds.IsKnown(null));
    }

    [Fact]
    public void IsExpedited_IncludesSameDay()
    {
        Assert.True(ShippingKinds.IsExpedited(ShippingKinds.SameDay));
        Assert.True(ShippingKinds.IsExpedited(ShippingKinds.Express));
        Assert.True(ShippingKinds.IsExpedited(ShippingKinds.Overnight));
        Assert.False(ShippingKinds.IsExpedited(ShippingKinds.Standard));
        Assert.False(ShippingKinds.IsExpedited(ShippingKinds.Pickup));
    }

    [Fact]
    public void Normalize_UsesTheNewValues()
    {
        Assert.Equal("rush", ShippingKinds.Normalize(" RUSH "));
        Assert.ThrowsAny<ArgumentException>(() => ShippingKinds.Normalize("express"));
    }
}
""";

    private const string ContractStep4Source = """
using ContosoOrders.Core;
using Xunit;

namespace ContosoOrders.Tests;

public class ShippingKindContractStep4Tests
{
    [Theory]
    [InlineData(ShippingKinds.Standard, 2.0, 5.99)]
    [InlineData(ShippingKinds.Express, 2.0, 11.49)]
    [InlineData(ShippingKinds.Overnight, 2.0, 21.99)]
    [InlineData(ShippingKinds.Pickup, 3.0, 0.0)]
    public void Fee_FollowsTheConstants(string kind, double weight, double expected)
    {
        Assert.Equal((decimal)expected, ShippingFeeCalculator.Calculate(new Shipment("o1", kind, (decimal)weight)));
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("express")]
    [InlineData("overnight")]
    [InlineData("pickup")]
    public void Fee_RejectsTheOldValues(string oldValue)
    {
        Assert.Throws<ArgumentException>(() => ShippingFeeCalculator.Calculate(new Shipment("o1", oldValue, 1m)));
    }

    [Theory]
    [InlineData(ShippingKinds.Standard, 1, true)]
    [InlineData(ShippingKinds.Standard, 0, false)]
    [InlineData(ShippingKinds.Express, 0, false)]
    [InlineData(ShippingKinds.Overnight, -1, false)]
    [InlineData(ShippingKinds.Pickup, 0, true)]
    [InlineData(ShippingKinds.Pickup, -1, false)]
    public void Valid_FollowsTheConstants(string kind, int weight, bool expected)
    {
        Assert.Equal(expected, ShipmentValidator.IsValid(new Shipment("o1", kind, weight)));
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("express")]
    [InlineData("overnight")]
    [InlineData("pickup")]
    public void Valid_RejectsTheOldValues(string oldValue)
    {
        Assert.False(ShipmentValidator.IsValid(new Shipment("o1", oldValue, 1m)));
    }
}
""";

    private const string ContractStep5Source = """
using ContosoOrders.Core;
using Xunit;

namespace ContosoOrders.Tests;

public class ShippingKindContractStep5Tests
{
    [Theory]
    [InlineData(ShippingKinds.Standard, 5)]
    [InlineData(ShippingKinds.Express, 2)]
    [InlineData(ShippingKinds.Overnight, 1)]
    [InlineData(ShippingKinds.Pickup, 0)]
    [InlineData(ShippingKinds.SameDay, 2)]
    public void Days_FollowTheConstants(string kind, int expected)
    {
        Assert.Equal(expected, DeliveryEstimator.EstimateDays(kind));
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("express")]
    [InlineData("overnight")]
    [InlineData("pickup")]
    public void Days_RejectTheOldValues(string oldValue)
    {
        Assert.Throws<ArgumentException>(() => DeliveryEstimator.EstimateDays(oldValue));
    }

    [Theory]
    [InlineData(ShippingKinds.Standard, "GroundPost")]
    [InlineData(ShippingKinds.Express, "AirFast")]
    [InlineData(ShippingKinds.Overnight, "AirFast")]
    [InlineData(ShippingKinds.Pickup, "none")]
    [InlineData(ShippingKinds.SameDay, "AirFast")]
    public void Carrier_FollowsTheConstants(string kind, string expected)
    {
        Assert.Equal(expected, CarrierSelector.Select(new Shipment("o1", kind, 1m)));
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("express")]
    [InlineData("overnight")]
    [InlineData("pickup")]
    public void Carrier_RejectTheOldValues(string oldValue)
    {
        Assert.Throws<ArgumentException>(() => CarrierSelector.Select(new Shipment("o1", oldValue, 1m)));
    }
}
""";

    private static readonly string[] Step1RequiredStems = { "ShippingFeeCalculator", "ShipmentValidator", "DeliveryEstimator", "CarrierSelector" };

    private static readonly string[] ForbiddenLiterals = { "\"standard\"", "\"express\"", "\"overnight\"", "\"pickup\"" };

    /// <summary>
    /// Contract-change check for steps 4 to 6: the step 4 and 5 checks first write the step 6 ShippingKinds.cs (new values, an extra expedited kind SameDay) over the model's tree and run tests written only against the constants, so a leftover string literal or an inline kind comparison fails; step 6 runs the same tests on the model's own ShippingKinds.cs. The tree is restored afterwards.
    /// </summary>
    private static async Task<(bool Ok, string Detail)> RunContractTestsAsync(int step, string solutionDirectory, CancellationToken cancellationToken)
    {
        (string FileName, string Source)[] sources = step switch
        {
            4 => new[] { ("ShippingKindContractStep4Tests.cs", ContractStep4Source) },
            5 => new[] { ("ShippingKindContractStep5Tests.cs", ContractStep5Source) },
            _ => new[] { ("ShippingKindContractStep4Tests.cs", ContractStep4Source), ("ShippingKindContractStep5Tests.cs", ContractStep5Source) },
        };
        var mutate = step != 6;
        var kindsPath = Path.Combine(solutionDirectory, "ContosoOrders.Core", "Shipping", "ShippingKinds.cs");
        var testDirectory = Path.Combine(solutionDirectory, "ContosoOrders.Tests", "ModelEvalGenerated");
        var testProject = Path.Combine(solutionDirectory, "ContosoOrders.Tests", "ContosoOrders.Tests.csproj");
        byte[]? originalKinds = mutate && File.Exists(kindsPath) ? await File.ReadAllBytesAsync(kindsPath, cancellationToken) : null;
        DotnetTestResult result;
        try
        {
            Directory.CreateDirectory(testDirectory);
            if (mutate)
            {
                await File.WriteAllTextAsync(kindsPath, ShippingKindStepGoldens.ContractChangedShippingKinds, cancellationToken);
            }

            foreach (var (fileName, source) in sources)
            {
                await File.WriteAllTextAsync(Path.Combine(testDirectory, fileName), NormalizeToCrlf(source), cancellationToken);
            }

            result = await DotnetTestRunner.RunAsync(testProject, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return (false, "contract-change check did not compile: " + BuildBrokenDetail(ex.Message));
        }
        finally
        {
            foreach (var (fileName, _) in sources)
            {
                File.Delete(Path.Combine(testDirectory, fileName));
            }

            if (mutate)
            {
                if (originalKinds is not null)
                {
                    await File.WriteAllBytesAsync(kindsPath, originalKinds, CancellationToken.None);
                }
                else
                {
                    File.Delete(kindsPath);
                }
            }
        }

        if (result.Total == 0 || result.Failed > 0 || result.Passed != result.Total)
        {
            var detail = $"contract-change check failed: {result.Failed} of {result.Total} failed";
            var failedLine = result.RawOutput.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("Failed ", StringComparison.Ordinal));
            if (failedLine is not null)
            {
                detail += $"; {failedLine}";
            }

            if (mutate)
            {
                detail += " (the check rewrote ShippingKinds.cs with different constant values and an extra expedited kind; code that still uses a string literal or an inline kind comparison breaks)";
            }

            return (false, detail);
        }

        return (true, string.Empty);
    }

    private static string? FindScopeViolation(int step, IReadOnlyList<string> changedPaths, IEnumerable<string> allowed)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        foreach (var raw in changedPaths)
        {
            var path = raw.Replace('\\', '/');
            if (!allowedSet.Contains(path))
            {
                return $"{path} changed but is outside the allowed paths for step {step}";
            }
        }

        return null;
    }

    private static string BuildBrokenDetail(string message)
    {
        var lines = message.Split('\n').Select(line => line.Trim()).ToArray();
        var first = lines[0];
        var errorLine = lines.FirstOrDefault(line => line.Contains("error CS", StringComparison.Ordinal));
        return errorLine is null ? first : $"{first} {errorLine}";
    }

    private static string NormalizeToCrlf(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");
}
