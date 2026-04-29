using Microsoft.Build.Locator;
using System;

var instances = MSBuildLocator.QueryVisualStudioInstances();
Console.WriteLine($"Found {instances.Count()} instances:");
foreach (var instance in instances)
{
    Console.WriteLine($"- {instance.Name} ({instance.Version}) at {instance.MSBuildPath}");
}

if (!MSBuildLocator.IsRegistered)
{
    var def = MSBuildLocator.RegisterDefaults();
    Console.WriteLine($"Registered default: {def.Name} at {def.MSBuildPath}");
}
