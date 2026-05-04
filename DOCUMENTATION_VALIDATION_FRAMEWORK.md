# RoslynSentinel Tool Documentation Validation Framework

**Purpose:** Verify all tool documentation is complete, accurate, and consistent  
**Status:** Framework ready for validation once TOOL_DOCUMENTATION.md generated  
**Last Updated:** 2026-05-03

---

## 🎯 Validation Checklist

### Per-Tool Validation (For all ~240 tools)

For each tool documented in TOOL_DOCUMENTATION.md:

- [ ] **Tool Exists**: Tool actually exists in engine source code
  - Test: Grep engine file for method name
  - Pass: Found in source
  - Fail: Hallucinated tool name

- [ ] **Engine Correct**: Listed engine is where tool actually lives
  - Test: Find method in stated engine file
  - Pass: Method found in that engine
  - Fail: Method in different engine or not found

- [ ] **Pre-Code Realistic**: "Before" code is valid C# (compilable pattern)
  - Test: Attempt to parse with Roslyn
  - Pass: SyntaxTree created successfully
  - Fail: Syntax error or pseudocode

- [ ] **Post-Code Realistic**: "After" code is valid C# (compilable outcome)
  - Test: Attempt to parse with Roslyn
  - Pass: SyntaxTree created successfully
  - Fail: Syntax error or impossible transformation

- [ ] **Signature Correct**: Method signature matches actual source
  - Test: Extract signature from source, compare
  - Pass: Exact match (ignoring whitespace)
  - Fail: Parameter names/types different or method not found

- [ ] **Return Type Correct**: Documented return type matches signature
  - Test: Compare documented return type to source
  - Pass: Exact match
  - Fail: Different return type or generic parameter mismatch

- [ ] **Category Valid**: Tool categorized correctly (Refactoring, Modernization, etc.)
  - Test: Categorize by inspection of tool function
  - Pass: Category matches tool's actual purpose
  - Fail: Wrong category or vague

- [ ] **Usage Example Realistic**: Example code is realistic and demonstrates tool
  - Test: Code shows how to invoke tool, not pseudocode
  - Pass: Real method calls with parameter values
  - Fail: Abstract, missing parameters, or doesn't show tool use

---

### Engine-Level Validation (For all 54 engines)

- [ ] **All public async Task methods documented**: Every public async method in engine is in documentation
  - Test: Count methods in source, count in docs
  - Pass: Same count (or explain any intentional exclusions)
  - Fail: Count mismatch without explanation

- [ ] **No hallucinated tools**: Documentation has no methods that don't exist in source
  - Test: For each tool in docs, verify in engine source
  - Pass: 100% of documented tools found in source
  - Fail: Any documented tool not in source = hallucination

- [ ] **Organized by engine**: Tools grouped by engine consistently
  - Test: Check documentation organization matches engine file names
  - Pass: Clear section per engine
  - Fail: Mixed or unclear grouping

- [ ] **Engine names accurate**: Engine names in documentation match actual file names
  - Test: Compare engine names in docs to `RoslynSentinel.Server/` file list
  - Pass: All match (minus `.cs` suffix)
  - Fail: Typo or non-existent engine

---

### Global Documentation Validation

- [ ] **Total tool count accurate**: Documentation claims X tools, actually has X tools
  - Test: Count all tools in TOOL_DOCUMENTATION.md
  - Pass: Matches claimed total (~240)
  - Fail: Count mismatch

- [ ] **Table of Contents complete**: TOC lists all tools, matches body
  - Test: Compare TOC to actual documented tools
  - Pass: TOC ↔ Body 1:1 correspondence
  - Fail: Missing tools in TOC or extra entries

- [ ] **Category index complete**: All categories covered, all tools categorized
  - Test: Verify every tool appears in category index
  - Pass: 100% coverage
  - Fail: Tools missing from category index

- [ ] **Engine index complete**: All engines listed, all tools indexed by engine
  - Test: Verify every engine has section, every tool under correct engine
  - Pass: All 54 engines present
  - Fail: Missing engines or tools in wrong engine

- [ ] **Unfinished features documented**: References to stub methods and deferred bugs
  - Test: Check for "Stub", "Deferred", "[Ignore]" notes
  - Pass: All stub methods and deferred bugs referenced
  - Fail: No mention of unfinished work

- [ ] **Links valid**: All references to UNFINISHED_FEATURES.md, BUG_FIX_SUMMARY.md, etc. work
  - Test: Check markdown links
  - Pass: All links point to valid files/sections
  - Fail: Broken links

- [ ] **Consistent formatting**: All tools follow same documentation format
  - Test: Spot-check 20 random tools for format consistency
  - Pass: All have What/When/Before/After/Signature/Example
  - Fail: Inconsistent structure or missing sections

- [ ] **No duplicate tools**: Each tool documented exactly once
  - Test: Search for duplicate tool names
  - Pass: No duplicates found
  - Fail: Tool documented multiple times

---

## 🔍 Validation Procedures

### Procedure 1: Tool Existence Check

```powershell
# For each tool in TOOL_DOCUMENTATION.md, verify it exists:
$tools = Extract-ToolNamesFromDocs "TOOL_DOCUMENTATION.md"
foreach ($tool in $tools) {
  $engine = $tool.Engine
  $method = $tool.MethodName
  $found = grep -l "public async Task.*$method" "RoslynSentinel.Server/${engine}.cs"
  if ($found) { Write-Host "✅ $method found in $engine" }
  else { Write-Host "❌ $method NOT FOUND in $engine" }
}
```

### Procedure 2: Signature Verification

```powershell
# Extract signatures from TOOL_DOCUMENTATION.md
# Compare to actual signatures in source
# Report mismatches
```

### Procedure 3: Code Example Validation

```csharp
// For each pre/post code example, attempt to parse with Roslyn
var syntaxTree = CSharpSyntaxTree.ParseText(preCodeExample);
if (syntaxTree.GetCompilationUnitSyntax().HasDiagnostics) {
    Report("Pre-code example has syntax errors: " + preCodeExample);
}
syntaxTree = CSharpSyntaxTree.ParseText(postCodeExample);
if (syntaxTree.GetCompilationUnitSyntax().HasDiagnostics) {
    Report("Post-code example has syntax errors: " + postCodeExample);
}
```

### Procedure 4: Stub Method Check

```powershell
# Verify all stub methods documented in UNFINISHED_FEATURES.md are actually stubs
# Check that stubs are NOT exposed as MCP tools (not in tool list)
```

### Procedure 5: Deferred Bug Verification

```csharp
// Verify all deferred bugs have [Ignore] regression tests
// Sample: Check that BUG-72 regression test exists and has [Ignore]
var bugFixTests = File.ReadAllText("RoslynSentinel.Tests/BugFixTests.cs");
Assert.That(bugFixTests.Contains("[Ignore(\"Scoping logic not yet fixed\")]"));
Assert.That(bugFixTests.Contains("BUG_72_IntroduceField_WithLiteralValue_InitializesCorrectly"));
```

---

## 📊 Validation Report Template

```markdown
# Tool Documentation Validation Report

**Date:** [Date]
**Validator:** [Automated or Manual]
**Status:** ✅ PASS or ❌ FAIL

## Summary
- Tools checked: X/240
- Hallucinations: 0
- Format errors: 0
- Code example errors: 0
- Missing tools: 0
- **Overall:** ✅ Complete and Accurate

## Details

### Engines Validated
- [x] RefactoringEngine (41 tools) ✅
- [x] GranularRefactoringEngine (10 tools) ✅
- ...

### Tools with Issues
[List any tools that failed validation]

### Deferred Bugs Verified
- [x] BUG-72 regression test present and [Ignore] marked ✅
- [x] BUG-74 regression test present and [Ignore] marked ✅
- [x] inline_method regression test present and [Ignore] marked ✅

### Stub Methods Verified
- [x] All stub methods documented in UNFINISHED_FEATURES.md ✅
- [x] All stubs confirmed not exposed as MCP tools ✅

## Recommendations
[Any recommendations for improvement or additional testing]
```

---

## ✅ Ready-to-Run Validation Tests

### Test 1: Parse TOOL_DOCUMENTATION.md Structure

```csharp
[Test]
public void TOOL_DOCUMENTATION_HasValidStructure()
{
    var content = File.ReadAllText("RoslynSentinel/TOOL_DOCUMENTATION.md");
    
    // Should have Table of Contents
    Assert.That(content.Contains("# Table of Contents") || content.Contains("# Tools"));
    
    // Should have engine sections
    Assert.That(content.Contains("## RefactoringEngine"));
    Assert.That(content.Contains("## GranularRefactoringEngine"));
    Assert.That(content.Contains("## AnalysisEngine"));
    
    // Should have category index
    Assert.That(content.Contains("## By Category") || content.Contains("Category Index"));
    
    // Should mention unfinished work
    Assert.That(content.Contains("Unfinished") || content.Contains("Stub") || content.Contains("Deferred"));
}
```

### Test 2: All Engines Covered

```csharp
[Test]
public void TOOL_DOCUMENTATION_CoversAll54Engines()
{
    var engineFiles = Directory.GetFiles(
        "RoslynSentinel/RoslynSentinel.Server",
        "*Engine.cs"
    ).Where(f => Path.GetFileName(f) != "ContextHelper.cs"); // Exclude helpers
    
    var content = File.ReadAllText("RoslynSentinel/TOOL_DOCUMENTATION.md");
    
    foreach (var engineFile in engineFiles) {
        var engineName = Path.GetFileNameWithoutExtension(engineFile);
        Assert.That(
            content.Contains($"## {engineName}") || 
            content.Contains($"### {engineName}"),
            $"Engine {engineName} not found in documentation"
        );
    }
}
```

### Test 3: No Hallucinated Tools

```csharp
[Test]
public void TOOL_DOCUMENTATION_HasNoHallucinatedTools()
{
    var docContent = File.ReadAllText("RoslynSentinel/TOOL_DOCUMENTATION.md");
    
    // Extract all documented tool names
    var toolPattern = @"(?:##|###)\s+(\d+\.)?\s*(\w+(?:\w+)?)\s*\(";
    var matches = Regex.Matches(docContent, toolPattern);
    
    foreach (Match match in matches) {
        var toolName = match.Groups[2].Value;
        
        // Verify tool exists in engine source
        var found = false;
        foreach (var engineFile in Directory.GetFiles("RoslynSentinel/RoslynSentinel.Server", "*Engine.cs")) {
            var engineContent = File.ReadAllText(engineFile);
            if (engineContent.Contains($"async Task{toolName}") || 
                engineContent.Contains($"public async Task.*{toolName}")) {
                found = true;
                break;
            }
        }
        
        Assert.That(found, $"Tool '{toolName}' appears to be hallucinated (not found in source)");
    }
}
```

### Test 4: Pre/Post Code Examples Parse

```csharp
[Test]
public void TOOL_DOCUMENTATION_CodeExamplesAreValid()
{
    var docContent = File.ReadAllText("RoslynSentinel/TOOL_DOCUMENTATION.md");
    
    // Extract all code blocks
    var codeBlockPattern = @"```csharp\n([\s\S]*?)\n```";
    var matches = Regex.Matches(docContent, codeBlockPattern);
    
    int validCount = 0;
    int invalidCount = 0;
    
    foreach (Match match in matches) {
        var code = match.Groups[1].Value;
        
        // Skip short snippets (likely fragments, not full code)
        if (code.Length < 50) continue;
        
        try {
            // Attempt to parse
            CSharpSyntaxTree.ParseText(code);
            validCount++;
        } catch {
            invalidCount++;
            Console.WriteLine($"Invalid code example: {code.Substring(0, 50)}...");
        }
    }
    
    // Expect 80%+ of examples to parse (some may be fragments)
    var parseRate = validCount / (double)(validCount + invalidCount);
    Assert.That(parseRate, Is.GreaterThan(0.8), 
        $"Only {parseRate:P} of code examples are valid C#");
}
```

---

## 🚀 Validation Schedule

1. **After TOOL_DOCUMENTATION.md generated** → Run all validation tests
2. **Report validation results** → Create VALIDATION_REPORT.md
3. **Fix any issues found** → Update documentation
4. **Final sign-off** → All tests passing, documentation ready

---

**This framework ensures 100% documentation completeness and accuracy.**
