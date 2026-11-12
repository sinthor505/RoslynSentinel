using System.Collections.ObjectModel;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Fixture file layout shared by the targeted tool scenarios: the starting files every ten-chain run
/// stages (minus RefactorAcceptanceTests.cs), and path helpers for reading them back after a run.
/// </summary>
public static class ScenarioFixtureFiles
{
    public const string CalculatorRelativePath = "ContosoOrders.Core/FixtureHelpers/OrderPricingCalculator.cs";
    public const string CheckoutRelativePath = "ContosoOrders.Core/FixtureHelpers/OrderCheckout.cs";
    public const string CheckoutTestsRelativePath = "ContosoOrders.Tests/ModelEvalGenerated/OrderCheckoutTests.cs";

    public static IReadOnlyDictionary<string, string> ChainStartingFiles { get; } = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>
        {
            [CalculatorRelativePath] = OrderPricingRefactorReproducer.StartingCalculatorFileContent,
            [CheckoutRelativePath] = OrderPricingRefactorReproducer.CheckoutCallerFileContent,
            [CheckoutTestsRelativePath] = OrderPricingRefactorReproducer.CheckoutFrontDoorTestsFileContent,
        });

    public static string CoreProjectDirectory(string solutionDirectory) => Path.Combine(solutionDirectory, "ContosoOrders.Core");

    public static string Absolute(string solutionDirectory, string relativePath) =>
        Path.Combine(solutionDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Reads a fixture file as text, or null when it does not exist.</summary>
    public static async Task<string?> ReadFileOrNullAsync(string solutionDirectory, string relativePath, CancellationToken cancellationToken)
    {
        var path = Absolute(solutionDirectory, relativePath);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
    }

    /// <summary>
    /// Reads the calculator after a run. A class rename may also rename the file, so the Standard-named
    /// file is tried first, then the original name. Null when neither exists.
    /// </summary>
    public static async Task<string?> ReadCalculatorOrNullAsync(string solutionDirectory, CancellationToken cancellationToken)
    {
        const string standardRelativePath = "ContosoOrders.Core/FixtureHelpers/StandardOrderPricingCalculator.cs";
        var standardText = await ReadFileOrNullAsync(solutionDirectory, standardRelativePath, cancellationToken);
        return standardText ?? await ReadFileOrNullAsync(solutionDirectory, CalculatorRelativePath, cancellationToken);
    }
}
