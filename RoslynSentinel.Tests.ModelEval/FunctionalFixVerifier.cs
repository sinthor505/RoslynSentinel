using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Builds the fixture's ContosoOrders.Core project after a model's edit and reflection-invokes
/// <c>BlockConverter.ConvertAbstractClassToInterface</c> directly, so a test can assert on the
/// actual returned string instead of only text-scanning the edited source file. Exists because a
/// text scan can pass on code that compiles but is functionally broken — see
/// project_planimplementverify_5run_result_postfix_verify.md's run 3, where a model replaced the
/// entire file with every line commented out; that "fix" compiled with 0 errors and satisfied a
/// substring check for the wrong reason, and was only caught by the model's own separate
/// LLM-driven verify phase, not by any mechanical assertion.
/// </summary>
internal static class FunctionalFixVerifier
{
    /// <summary>
    /// Runs `dotnet build` against the given ContosoOrders.Core.csproj, loads the resulting
    /// assembly into a collectible <see cref="AssemblyLoadContext"/>, and invokes
    /// <c>ContosoOrders.Core.FixtureHelpers.BlockConverter.ConvertAbstractClassToInterface</c>
    /// with <paramref name="fileText"/>/<paramref name="className"/>. Throws with a diagnostic
    /// message (including captured dotnet build output) on any failure — build failure, missing
    /// type/method, or an invocation exception — rather than returning a sentinel, since every
    /// failure path here already means the fix is broken and the caller wants a hard assertion
    /// failure either way.
    /// </summary>
    public static async Task<string> InvokeConvertAbstractClassToInterfaceAsync(
        string coreProjectDirectory, string fileText, string className, CancellationToken cancellationToken)
    {
        var csprojPath = Path.Combine(coreProjectDirectory, "ContosoOrders.Core.csproj");
        if (!File.Exists(csprojPath))
        {
            throw new FileNotFoundException($"FunctionalFixVerifier: no project file at '{csprojPath}'.", csprojPath);
        }

        var buildOutput = await RunDotnetBuildAsync(csprojPath, cancellationToken);

        var assemblyPath = Path.Combine(coreProjectDirectory, "bin", "Debug", "net10.0", "ContosoOrders.Core.dll");
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                $"FunctionalFixVerifier: dotnet build reported success but no assembly was produced at " +
                $"'{assemblyPath}'. Build output:\n{buildOutput}", assemblyPath);
        }

        string result;
        var loadContextRef = InvokeInCollectibleContext(assemblyPath, fileText, className, out result);

        // AssemblyLoadContext.Unload() only requests collection — it does not synchronously
        // release the mmap'd file handle on the .dll. The caller's TearDown deletes this same
        // fixture directory moments later, and without waiting here that delete can lose the
        // race and throw UnauthorizedAccessException on the still-locked file (observed
        // masking the real pass/fail result in 4/5 runs of a batch, all AFTER a successful
        // invoke). Polling GC + WaitForPendingFinalizers until the weak ref dies makes the
        // unload actually complete before this method returns.
        for (var i = 0; i < 10 && loadContextRef.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return result;
    }

    private static WeakReference InvokeInCollectibleContext(
        string assemblyPath, string fileText, string className, out string result)
    {
        var loadContext = new AssemblyLoadContext("FunctionalFixVerifier", isCollectible: true);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var converterType = assembly.GetType("ContosoOrders.Core.FixtureHelpers.BlockConverter")
                ?? throw new InvalidOperationException(
                    "FunctionalFixVerifier: type 'ContosoOrders.Core.FixtureHelpers.BlockConverter' " +
                    "not found in the built assembly — the model's edit may have renamed or removed it.");
            var method = converterType.GetMethod("ConvertAbstractClassToInterface", [typeof(string), typeof(string)])
                ?? throw new InvalidOperationException(
                    "FunctionalFixVerifier: method 'ConvertAbstractClassToInterface(string, string)' not found " +
                    "on BlockConverter — the model's edit may have changed its signature.");
            var instance = Activator.CreateInstance(converterType)
                ?? throw new InvalidOperationException("FunctionalFixVerifier: could not construct BlockConverter.");

            try
            {
                result = (string)method.Invoke(instance, [fileText, className])!;
            }
            catch (TargetInvocationException ex)
            {
                throw new InvalidOperationException(
                    $"FunctionalFixVerifier: ConvertAbstractClassToInterface threw at runtime: " +
                    $"{ex.InnerException?.Message ?? ex.Message}", ex.InnerException ?? ex);
            }
        }
        finally
        {
            loadContext.Unload();
        }

        return new WeakReference(loadContext);
    }

    /// <summary>
    /// Builds the fixture's ContosoOrders.Core project after a model's refactor and
    /// reflection-invokes <c>OrderPricingCalculator.CalculateDiscountedTotal</c> (the renamed form
    /// of <c>CalcDisc</c> — see <c>RoslynSentinel.Tests.ModelEval.Fixtures.OrderPricingRefactorReproducer</c>) once per branch
    /// (preferred/standard customer), so a test can assert both real returned values instead of
    /// only text-scanning the edited source. Looked up by name directly (rather than by declared
    /// parameter types, which the model isn't asked to change) since the method's accessibility is
    /// expected to have changed to internal as part of the task.
    /// </summary>
    public static async Task<(decimal Preferred, decimal Standard)> InvokeCalculateDiscountedTotalAsync(
        string coreProjectDirectory, decimal amount, decimal rate, CancellationToken cancellationToken)
    {
        var csprojPath = Path.Combine(coreProjectDirectory, "ContosoOrders.Core.csproj");
        if (!File.Exists(csprojPath))
        {
            throw new FileNotFoundException($"FunctionalFixVerifier: no project file at '{csprojPath}'.", csprojPath);
        }

        var buildOutput = await RunDotnetBuildAsync(csprojPath, cancellationToken);

        var assemblyPath = Path.Combine(coreProjectDirectory, "bin", "Debug", "net10.0", "ContosoOrders.Core.dll");
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                $"FunctionalFixVerifier: dotnet build reported success but no assembly was produced at " +
                $"'{assemblyPath}'. Build output:\n{buildOutput}", assemblyPath);
        }

        (decimal Preferred, decimal Standard) result;
        var loadContextRef = InvokeCalculateDiscountedTotalInCollectibleContext(assemblyPath, amount, rate, out result);

        for (var i = 0; i < 10 && loadContextRef.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return result;
    }

    private static WeakReference InvokeCalculateDiscountedTotalInCollectibleContext(
        string assemblyPath, decimal amount, decimal rate, out (decimal Preferred, decimal Standard) result)
    {
        var loadContext = new AssemblyLoadContext("FunctionalFixVerifier", isCollectible: true);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var calculatorType = assembly.GetType("ContosoOrders.Core.FixtureHelpers.OrderPricingCalculator")
                ?? throw new InvalidOperationException(
                    "FunctionalFixVerifier: type 'ContosoOrders.Core.FixtureHelpers.OrderPricingCalculator' " +
                    "not found in the built assembly — the model's edit may have renamed or removed it.");
            var method = calculatorType.GetMethod(
                    "CalculateDiscountedTotal",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    [typeof(decimal), typeof(decimal), typeof(bool)])
                ?? throw new InvalidOperationException(
                    "FunctionalFixVerifier: method 'CalculateDiscountedTotal(decimal, decimal, bool)' not " +
                    "found on OrderPricingCalculator — the rename or signature may be wrong.");
            var instance = Activator.CreateInstance(calculatorType)
                ?? throw new InvalidOperationException("FunctionalFixVerifier: could not construct OrderPricingCalculator.");

            try
            {
                var preferred = (decimal)method.Invoke(instance, [amount, rate, true])!;
                var standard = (decimal)method.Invoke(instance, [amount, rate, false])!;
                result = (preferred, standard);
            }
            catch (TargetInvocationException ex)
            {
                throw new InvalidOperationException(
                    $"FunctionalFixVerifier: CalculateDiscountedTotal threw at runtime: " +
                    $"{ex.InnerException?.Message ?? ex.Message}", ex.InnerException ?? ex);
            }
        }
        finally
        {
            loadContext.Unload();
        }

        return new WeakReference(loadContext);
    }

    private static async Task<string> RunDotnetBuildAsync(string csprojPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("dotnet", $"build \"{csprojPath}\" -c Debug --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("FunctionalFixVerifier: failed to start dotnet build.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await stdOutTask + await stdErrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FunctionalFixVerifier: dotnet build exited {process.ExitCode} for '{csprojPath}':\n{output}");
        }

        return output;
    }
}
