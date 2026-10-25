using System.Reflection;
using System.Text.Json;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Common;

public static class ToolSignatureCatalogReflection
{
    private static IEnumerable<Type> TryGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
        catch (Exception)
        {
            return Array.Empty<Type>();
        }
    }

    /// <summary>
    /// Builds a catalog from every <see cref="McpServerToolAttribute"/> method on a
    /// <see cref="McpServerToolTypeAttribute"/> class in the assemblies loaded in this AppDomain.
    /// Fallback for when no server is running. Each schema comes from the same path the server uses
    /// at startup (<see cref="McpToolSchemaPatcher.CreateSentinelTool"/>), so parameters, required
    /// flags and enum values match what the server emits. Never throws: a tool whose schema cannot
    /// be generated is skipped. A duplicate tool name keeps the first declaration.
    /// </summary>
    public static ToolSignatureCatalog FromLoadedAssemblies()
    {
        var pairs = new List<KeyValuePair<string, JsonElement>>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            foreach (var type in TryGetTypes(assembly))
            {
                if (type.GetCustomAttribute<McpServerToolTypeAttribute>(inherit: false) is null)
                {
                    continue;
                }

                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.GetCustomAttribute<McpServerToolAttribute>(inherit: false) is null)
                    {
                        continue;
                    }

                    try
                    {
                        var tool = McpToolSchemaPatcher.CreateSentinelTool(method, type, services: null!);
                        pairs.Add(new KeyValuePair<string, JsonElement>(tool.ProtocolTool.Name, tool.ProtocolTool.InputSchema));
                    }
                    catch (Exception)
                    {
                        // A tool whose schema cannot be generated is skipped, never thrown.
                    }
                }
            }
        }

        return ToolSignatureCatalog.FromSchemas(pairs);
    }
}
