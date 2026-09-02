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
                return (string)method.Invoke(instance, [fileText, className])!;
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
