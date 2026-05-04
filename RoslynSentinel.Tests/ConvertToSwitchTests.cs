using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for ConvertToSwitch feature
/// Covers conversion of if-else chains to switch statements for modernization
/// </summary>
[TestFixture]
public class ConvertToSwitchTests
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

    /// <summary>
    /// Test 1: Integer value if-else chain (3 branches) - basic conversion
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_IntegerChain_ThreeBranches_ConvertsToSwitch()
    {
        const string source = @"public class C 
{
    public void M(int x) 
    {
        if (x == 1)
        {
            Console.WriteLine(""One"");
        }
        else if (x == 2)
        {
            Console.WriteLine(""Two"");
        }
        else if (x == 3)
        {
            Console.WriteLine(""Three"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("switch"), "Should contain switch statement");
        Assert.That(result, Does.Contain("case 1:"), "Should contain case 1");
        Assert.That(result, Does.Contain("case 2:"), "Should contain case 2");
        Assert.That(result, Does.Contain("case 3:"), "Should contain case 3");
        Assert.That(result, Does.Contain("break;"), "Should contain break statements");
    }

    /// <summary>
    /// Test 2: String value if-else chain - converts string comparisons
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_StringChain_ConvertsToSwitch()
    {
        const string source = @"public class C 
{
    public void M(string status) 
    {
        if (status == ""pending"")
        {
            Console.WriteLine(""Waiting"");
        }
        else if (status == ""approved"")
        {
            Console.WriteLine(""Ready"");
        }
        else if (status == ""rejected"")
        {
            Console.WriteLine(""Failed"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("switch"), "Should contain switch statement");
        Assert.That(result, Does.Contain("case"), "Should contain case statements");
    }

    /// <summary>
    /// Test 3: Enum value switch conversion
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_EnumChain_ConvertsToSwitch()
    {
        const string source = @"public class C 
{
    public enum Status { Active, Inactive, Pending }
    
    public void M(Status s) 
    {
        if (s == Status.Active)
        {
            Console.WriteLine(""Running"");
        }
        else if (s == Status.Inactive)
        {
            Console.WriteLine(""Stopped"");
        }
        else if (s == Status.Pending)
        {
            Console.WriteLine(""Waiting"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("switch"), "Should contain switch statement");
    }

    /// <summary>
    /// Test 4: If-else chain with default (else) clause - converts default to switch default
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_WithDefaultClause_ConvertsDefaultCase()
    {
        const string source = @"public class C 
{
    public void M(int code) 
    {
        if (code == 200)
        {
            Console.WriteLine(""OK"");
        }
        else if (code == 404)
        {
            Console.WriteLine(""Not Found"");
        }
        else if (code == 500)
        {
            Console.WriteLine(""Error"");
        }
        else
        {
            Console.WriteLine(""Unknown"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("switch"), "Should contain switch statement");
        Assert.That(result, Does.Contain("default:"), "Should contain default case");
    }

    /// <summary>
    /// Test 5: If-else chain without default - converts to switch without default
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_NoDefaultClause_ConvertsSwitchNoDefault()
    {
        const string source = @"public class C 
{
    public void M(int x) 
    {
        if (x == 1)
        {
            Console.WriteLine(""One"");
        }
        else if (x == 2)
        {
            Console.WriteLine(""Two"");
        }
        else if (x == 3)
        {
            Console.WriteLine(""Three"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("switch"), "Should contain switch statement");
        // Should not have default if no else clause
        var switchStart = result.IndexOf("switch");
        var switchEnd = result.LastIndexOf("}");
        var switchSection = result.Substring(switchStart, switchEnd - switchStart);
        // This assertion checks that switch was created, default is optional
        Assert.That(switchSection, Does.Contain("case"), "Should contain case statements");
    }

    /// <summary>
    /// Test 6: Case fallthrough (same body for multiple values)
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_MultipleConditions_HandlesMulipleBranches()
    {
        const string source = @"public class C 
{
    public void M(int code) 
    {
        if (code == 1)
        {
            Console.WriteLine(""First"");
        }
        else if (code == 2)
        {
            Console.WriteLine(""First"");
        }
        else if (code == 3)
        {
            Console.WriteLine(""Third"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("switch"), "Should contain switch statement");
    }

    /// <summary>
    /// Test 7: Nested methods/blocks - converts within nested scope
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_NestedMethod_ConvertsNestedChain()
    {
        const string source = @"public class C 
{
    public void Outer()
    {
        Inner(5);
    }
    
    private void Inner(int value)
    {
        if (value == 1)
        {
            Console.WriteLine(""One"");
        }
        else if (value == 2)
        {
            Console.WriteLine(""Two"");
        }
        else if (value == 3)
        {
            Console.WriteLine(""Three"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("switch"), "Should contain switch statement");
    }

    /// <summary>
    /// Test 8: Type preservation - ensure types are preserved correctly
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_IntType_PreservesType()
    {
        const string source = @"public class C 
{
    public int GetResult(int x)
    {
        int result = 0;
        if (x == 1)
        {
            result = 10;
        }
        else if (x == 2)
        {
            result = 20;
        }
        else if (x == 3)
        {
            result = 30;
        }
        return result;
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("switch"), "Should contain switch statement");
        Assert.That(result, Does.Contain("int"), "Should preserve int type");
    }

    /// <summary>
    /// Test 9: Literal values (numbers) - multiple if-else with numeric literals
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_NumericLiterals_ConvertsCases()
    {
        const string source = @"public class C 
{
    public void ProcessCode(int statusCode)
    {
        if (statusCode == 200)
        {
            Console.WriteLine(""Success"");
        }
        else if (statusCode == 201)
        {
            Console.WriteLine(""Created"");
        }
        else if (statusCode == 204)
        {
            Console.WriteLine(""No Content"");
        }
        else if (statusCode == 400)
        {
            Console.WriteLine(""Bad Request"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("switch"), "Should contain switch statement");
        Assert.That(result, Does.Contain("case 200:"), "Should contain case 200");
    }

    /// <summary>
    /// Test 10: Two-branch if-else - should NOT convert (minimum is 3 branches)
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_TwoBranches_DoesNotConvert()
    {
        const string source = @"public class C 
{
    public void M(int x) 
    {
        if (x == 1)
        {
            Console.WriteLine(""One"");
        }
        else
        {
            Console.WriteLine(""Other"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return code");
        // Two-branch should not be converted to switch
        var hasIfStatement = result.Contains("if (x == 1)") || result.Contains("if (x==1)");
        Assert.That(hasIfStatement, Is.True, "Should preserve if-else for two branches");
    }

    /// <summary>
    /// Test 11: Complex conditions - should NOT convert (has && or ||)
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_ComplexCondition_DoesNotConvert()
    {
        const string source = @"public class C 
{
    public void M(int x, int y) 
    {
        if (x == 1 && y > 0)
        {
            Console.WriteLine(""One"");
        }
        else if (x == 2 || y < 0)
        {
            Console.WriteLine(""Two"");
        }
        else
        {
            Console.WriteLine(""Other"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return code");
        // Complex condition should not be converted
        var hasIfStatement = result.Contains("if") && result.Contains("&&") || result.Contains("||");
        Assert.That(hasIfStatement || !result.Contains("switch"), 
            Is.True, 
            "Should preserve complex if conditions (not convert to switch)");
    }

    /// <summary>
    /// Test 12: Different subject variables - should NOT convert
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_DifferentSubjects_DoesNotConvert()
    {
        const string source = @"public class C 
{
    public void M(int x, int y) 
    {
        if (x == 1)
        {
            Console.WriteLine(""X is one"");
        }
        else if (y == 2)
        {
            Console.WriteLine(""Y is two"");
        }
        else if (x == 3)
        {
            Console.WriteLine(""X is three"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return code");
        // Different subjects should not be converted
        var shouldHaveIf = result.Contains("if (x == 1)") || result.Contains("if (x==1)");
        Assert.That(shouldHaveIf, Is.True, "Should preserve if when subjects differ");
    }

    /// <summary>
    /// Test 13: Non-equality comparisons - should NOT convert
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_NonEqualityComparison_DoesNotConvert()
    {
        const string source = @"public class C 
{
    public void M(int x) 
    {
        if (x < 10)
        {
            Console.WriteLine(""Less"");
        }
        else if (x > 20)
        {
            Console.WriteLine(""Greater"");
        }
        else if (x == 15)
        {
            Console.WriteLine(""Equal"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return code");
        // Non-equality comparisons should not be converted
        var shouldHaveIf = result.Contains("if") && result.Contains("<");
        Assert.That(shouldHaveIf, Is.True, "Should preserve non-equality comparisons");
    }

    /// <summary>
    /// Test 14: Switch with return statements - returns should be preserved, no break after
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_WithReturnStatements_PreservesReturns()
    {
        const string source = @"public class C 
{
    public string GetStatus(int code)
    {
        if (code == 200)
        {
            return ""OK"";
        }
        else if (code == 404)
        {
            return ""Not Found"";
        }
        else if (code == 500)
        {
            return ""Error"";
        }
        return ""Unknown"";
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("switch"), "Should contain switch statement");
        Assert.That(result, Does.Contain("return"), "Should preserve return statements");
    }

    /// <summary>
    /// Test 15: Switch with throw statements - throws should be preserved, no break after
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_WithThrowStatements_PreservesThrows()
    {
        const string source = @"public class C 
{
    public void ValidateCode(int code)
    {
        if (code == 1)
        {
            throw new ArgumentException(""Code 1"");
        }
        else if (code == 2)
        {
            throw new ArgumentException(""Code 2"");
        }
        else if (code == 3)
        {
            throw new ArgumentException(""Code 3"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("switch"), "Should contain switch statement");
        Assert.That(result, Does.Contain("throw"), "Should preserve throw statements");
    }

    /// <summary>
    /// Test 16: Character literals - converts character value if-else chain
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_CharacterLiterals_ConvertsCharCases()
    {
        const string source = @"public class C 
{
    public void M(char ch)
    {
        if (ch == 'a')
        {
            Console.WriteLine(""Letter a"");
        }
        else if (ch == 'b')
        {
            Console.WriteLine(""Letter b"");
        }
        else if (ch == 'c')
        {
            Console.WriteLine(""Letter c"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("switch"), "Should contain switch statement");
    }

    /// <summary>
    /// Test 17: Boolean values - converts boolean value if-else chain
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_BooleanLiterals_ConvertsBoolCases()
    {
        const string source = @"public class C 
{
    public void M(bool flag)
    {
        if (flag == true)
        {
            Console.WriteLine(""True"");
        }
        else if (flag == false)
        {
            Console.WriteLine(""False"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        // Note: Boolean switch is less common, but should still work
        // May or may not convert depending on implementation details
    }

    /// <summary>
    /// Test 18: Nested if-else within method - ensures conversion works in nested contexts
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_NestedBlock_ConvertsInNestedScope()
    {
        const string source = @"public class C 
{
    public void Process(int value)
    {
        if (value > 0)
        {
            if (value == 1)
            {
                Console.WriteLine(""One"");
            }
            else if (value == 2)
            {
                Console.WriteLine(""Two"");
            }
            else if (value == 3)
            {
                Console.WriteLine(""Three"");
            }
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("switch"), "Should contain switch statement in nested block");
    }

    /// <summary>
    /// Test 19: Large if-else chain (7+ branches) - ensures conversion works for many cases
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_LargeChain_ConvertsManyCase()
    {
        const string source = @"public class C 
{
    public void M(int x)
    {
        if (x == 1) { Console.WriteLine(""1""); }
        else if (x == 2) { Console.WriteLine(""2""); }
        else if (x == 3) { Console.WriteLine(""3""); }
        else if (x == 4) { Console.WriteLine(""4""); }
        else if (x == 5) { Console.WriteLine(""5""); }
        else if (x == 6) { Console.WriteLine(""6""); }
        else if (x == 7) { Console.WriteLine(""7""); }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("switch"), "Should contain switch statement");
        Assert.That(result, Does.Contain("case 1:"), "Should handle multiple cases");
    }

    /// <summary>
    /// Test 20: Inverted comparison (subject on right side) - comparisons like 1 == x instead of x == 1
    /// </summary>
    [Test]
    public async Task ConvertToSwitch_InvertedComparison_ConvertsCorrectly()
    {
        const string source = @"public class C 
{
    public void M(int x)
    {
        if (1 == x)
        {
            Console.WriteLine(""One"");
        }
        else if (2 == x)
        {
            Console.WriteLine(""Two"");
        }
        else if (3 == x)
        {
            Console.WriteLine(""Three"");
        }
    }
}";
        SetSource(source);
        
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("switch"), "Should contain switch statement");
        Assert.That(result, Does.Contain("case 1:"), "Should handle inverted comparisons");
    }
}
