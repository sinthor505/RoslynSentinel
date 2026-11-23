using System;
using System.Collections.Generic;
using System.Linq;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Pins the shipped scripts/eval-hosts.json (copied to the test output dir and read via <see cref="EvalHostTable.DefaultPath"/>) to the table's rules. No live model.
/// </summary>
[TestFixture]
public class ShippedEvalHostTableTests
{
    private static IReadOnlyList<EvalHost> Hosts() => EvalHostTable.Load();

    [Test]
    public void Shipped_Table_HasSixEntries()
    {
        Assert.That(Hosts().Count, Is.EqualTo(6));
    }

    [Test]
    public void Shipped_Table_HasOneDefaultPerMachine_AndItIsLlamaServer()
    {
        var hosts = Hosts();
        foreach (var machine in hosts.Select(h => h.Machine).Distinct())
        {
            var defaults = hosts.Where(h => h.Machine == machine && h.IsDefault).ToList();
            Assert.That(defaults.Count, Is.EqualTo(1), $"machine {machine} default count");
            Assert.That(defaults[0].Backend, Is.EqualTo(EvalHostTable.BackendLlamaServer), $"machine {machine} default backend");
        }
    }

    [Test]
    public void Shipped_Table_Port8888Hosts_AreLlamaServer()
    {
        var hosts = Hosts().Where(h => h.BaseUrl.EndsWith(":8888/v1", StringComparison.Ordinal)).ToList();
        Assert.That(hosts.Count, Is.EqualTo(3));
        Assert.That(hosts.Select(h => h.Backend).Distinct(), Is.EqualTo(new[] { EvalHostTable.BackendLlamaServer }));
    }

    [Test]
    public void Shipped_Table_Port1234Hosts_AreLmStudio()
    {
        var hosts = Hosts().Where(h => h.BaseUrl.EndsWith(":1234/v1", StringComparison.Ordinal)).ToList();
        Assert.That(hosts.Count, Is.EqualTo(3));
        Assert.That(hosts.Select(h => h.Backend).Distinct(), Is.EqualTo(new[] { EvalHostTable.BackendLmStudio }));
    }

    [Test]
    public void Shipped_Table_LmStudioSuffixes_PinTodaysResultsFolders()
    {
        var suffixes = Hosts()
            .Where(h => h.Backend == EvalHostTable.BackendLmStudio)
            .Select(h => h.Suffix)
            .ToList();
        Assert.That(suffixes, Is.EquivalentTo(new[] { "112", "113", "85" }));
    }
}
