using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for ConvertToNullCoalescing feature
/// Covers conversion of null checks to null coalescing operators (?? and ??=)
/// </summary>
[TestFixture]
public class ConvertToNullCoalescingTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private LogicOptimizationEngine _logicOptimizationEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _logicOptimizationEngine = new LogicOptimizationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { (fileName, source) });
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task ConvertToNullCoalescing_TernaryPattern_NullLeftToCoalesce()
    {
        const string source = @"public class C 
{
    public void M(string x, string defaultValue) 
    {
        string result = x == null ? defaultValue : x;
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToNullCoalescingAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("??"), "Should contain null coalescing operator");
    }

    [Test]
    public async Task ConvertToNullCoalescing_TernaryPattern_NotNullToCoalesce()
    {
        const string source = @"public class C 
{
    public void M(string x, string defaultValue) 
    {
        string result = x != null ? x : defaultValue;
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToNullCoalescingAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("??"), "Should contain null coalescing operator");
    }

    [Test]
    public async Task ConvertToNullCoalescing_IfStatementWithAssignment_ToCoalesceAssign()
    {
        const string source = @"public class C 
{
    public void M(string x) 
    {
        if (x == null)
        {
            x = ""default"";
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToNullCoalescingAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("??="), "Should contain coalesce assignment operator");
    }

    [Test]
    public async Task ConvertToNullCoalescing_IfStatementNoBlock_ToCoalesceAssign()
    {
        const string source = @"public class C 
{
    public void M(string x) 
    {
        if (x == null) x = ""default"";
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToNullCoalescingAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("??="), "Should contain coalesce assignment operator");
    }

    [Test]
    public async Task ConvertToNullCoalescing_MultiplePatterns_ConvertAll()
    {
        const string source = @"public class C 
{
    public void M(string x, string y, int count) 
    {
        string result1 = x == null ? ""default"" : x;
        string result2 = y != null ? y : """";
        if (count == null) count = 0;
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToNullCoalescingAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("??"), "Should contain null coalescing operators");
    }

    [Test]
    public async Task ConvertToNullCoalescing_ComplexExpression_ToCoalesce()
    {
        const string source = @"public class C 
{
    public void M(string x) 
    {
        string result = x == null ? string.Empty : x;
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToNullCoalescingAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("??"), "Should contain null coalescing operator");
    }

    [Test]
    public async Task ConvertToNullCoalescing_MethodCall_DefaultValue()
    {
        const string source = @"public class C 
{
    public void M(string x) 
    {
        string result = x != null ? x : GetDefault();
    }
    
    private string GetDefault() => """";
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToNullCoalescingAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("??"), "Should contain null coalescing operator");
    }

    [Test]
    public async Task ConvertToNullCoalescing_IntType_ToCoalesceAssign()
    {
        const string source = @"public class C 
{
    public void M(int? count) 
    {
        if (count == null) count = 0;
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToNullCoalescingAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("??="), "Should contain coalesce assignment operator");
    }

    [Test]
    public async Task ConvertToNullCoalescing_NoConversion_LeavesUnmodified()
    {
        const string source = @"public class C 
{
    public void M(int x) 
    {
        if (x < 0) x = 0;
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToNullCoalescingAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return code");
        Assert.That(result, Does.Not.Contain("??"), "Should not modify non-null check patterns");
    }
}
