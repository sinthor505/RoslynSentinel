# RoslynSentinel Complete Tool Documentation

## Overview

This directory contains comprehensive documentation for all **320+ refactoring and analysis tools** available in RoslynSentinel across 51 specialized sources.

## Documentation Files

### TOOL_DOCUMENTATION.md
**Location:** `RoslynSentinel/TOOL_DOCUMENTATION.md`

The complete reference for all RoslynSentinel tools, organized into three main sections:

#### 1. Index by Category
Tools organized by functional area:
- **Refactoring** (112 tools) - Core refactoring operations
- **Code Generation** (23 tools) - Code synthesis and generation
- **Modernization** (41 tools) - C# version upgrades and syntax modernization
- **Analysis & Diagnostics** (25 tools) - Code analysis and diagnostic tools
- **Performance & Optimization** (16 tools) - Performance improvements
- **Quality & Style** (34 tools) - Code quality and style tools
- **Type & Semantic Analysis** (38 tools) - Type and semantic operations
- **Testing** (4 tools) - Testing-related tools
- **Security & Safety** (2 tools) - Security and thread-safety analysis
- **Workspace & Utilities** (25 tools) - Workspace and utility operations

#### 2. Complete Tool List
Quick reference listing all 320 tools organized by source:
- **44 Engine Classes** - Focused refactoring and analysis engines
- **7 Tool Classes** - Sentinel augmentation, generation, quality, and workspace tools

#### 3. Detailed Tool Documentation
Full documentation for each tool including:
- **Purpose** - Clear explanation of what the tool does
- **Signature** - Complete method signature with parameters

## Tool Sources

### Engine Classes (44 engines, 157 tools)
Specialized engines for specific refactoring and analysis domains:

| Engine | Tools | Purpose |
|--------|-------|---------|
| **RefactoringEngine** | 34 | Core refactoring operations (extract, rename, signature change, etc.) |
| **CodeGenerationEngine** | 10 | Code synthesis and generation |
| **SyntaxUpgradeEngine** | 10 | C# version upgrades and modern syntax |
| **GranularRefactoringEngine** | 9 | Fine-grained refactoring operations |
| **AdvancedLogicEngine** | 7 | Logic transformation and optimization |
| **AsyncOptimizationEngine** | 7 | Async/await optimization |
| **CodeStyleEngine** | 7 | Code style and formatting |
| **MsToolAugmentEngine** | 10 | Microsoft tools augmentation |
| **TestingEngine** | 4 | Testing utilities and generation |
| **AnalysisEngine** | 3 | Code analysis |
| **ControlFlowEngine** | 3 | Control flow analysis |
| **DiagnosticEngine** | 3 | Diagnostic reporting |
| **IDEStyleEngine** | 3 | IDE style conventions |
| **InstrumentationEngine** | 3 | Code instrumentation |
| **ModernizationEngine** | 3 | Modernization operations |
| *And 29 more engines...* | | |

### Tool Classes (7 classes, 163 tools)
Sentinel-specific tool collections:

| Class | Tools | Purpose |
|-------|-------|---------|
| **SentinelRefactoringTools** | 61 | Advanced refactoring utilities |
| **SentinelModernizationTools** | 24 | Modernization operations |
| **SentinelWorkspaceTools** | 19 | Workspace management tools |
| **SentinelIntelligenceTools** | 16 | AI-assisted analysis and transformation |
| **SentinelQualityTools** | 14 | Code quality analysis |
| **SentinelGenerationTools** | 13 | Code generation utilities |
| **SentinelAugmentTools** | 10 | Tool augmentation |

## Document Statistics

- **Total Sources:** 51 (44 engines + 7 tool classes)
- **Total Tools:** 320
- **Total Lines:** 3,708
- **File Size:** 118 KB
- **Generated:** [See TOOL_DOCUMENTATION.md header]

## How to Use

### Finding a Specific Tool

1. **Quick Lookup:** Check the "Complete Tool List" section for the tool name
2. **By Category:** Use the "Index by Category" to browse related tools
3. **Detailed Info:** Go to the "Detailed Tool Documentation" section for full signatures

### Understanding Tool Signatures

Each tool includes:
- **Tool Name:** Clear, descriptive identifier (e.g., `ExtractMethodAsync`)
- **Parameters:** Full method signature showing inputs
- **Return Type:** What the tool returns (usually modified source code or analysis results)
- **Purpose:** Brief description of what the tool does

### Example Tool Format

```
### ConvertForEachToForAsync

**Purpose:**
Converts a foreach loop to an equivalent for loop for performance analysis.

**Signature:**
```csharp
public async Task<string> ConvertForEachToForAsync(string filePath, int line, CancellationToken ct = default)
```
```

## Tool Categories Explained

### Refactoring
Core refactoring operations for code transformation:
- Extract method, constant, class
- Rename symbols
- Change accessibility
- Reorder parameters
- Convert syntax forms

### Code Generation
Generate new code structures:
- Class generation from JSON
- Constructor/property generation
- Interface implementation
- Builder pattern generation
- Test scaffold generation

### Modernization
Upgrade C# code to modern syntax:
- Switch to switch expressions
- Primary constructors
- Collection expressions
- Pattern matching improvements
- Modern guard patterns

### Analysis & Diagnostics
Analyze code for issues and patterns:
- Control flow analysis
- Data flow analysis
- Call graph generation
- Complexity calculation
- Diagnostic reporting

### Performance & Optimization
Performance-focused transformations:
- Async optimization
- Value task conversion
- Cancellation token addition
- Await optimization
- Loop optimization

### Quality & Style
Code quality and style improvements:
- Style conformance
- Code smell detection
- Pattern usage
- Best practice enforcement
- Style guide adherence

### Type & Semantic Analysis
Type and semantic operations:
- Type conversion
- Symbol navigation
- Interface sync
- Dependency analysis
- API validation

## Integration with RoslynSentinel

RoslynSentinel uses Microsoft Roslyn for all code analysis and transformation:

- **Roslyn Workspace API** - Document and project manipulation
- **Syntax Trees** - Code structure analysis
- **Semantic Models** - Type and symbol information
- **Code Fixes & Analyzers** - Integration with Visual Studio

## Notes

1. **Tool Availability:** Not all tools may be enabled by default. Check configuration for feature flags.
2. **CancellationToken:** Most async tools accept an optional CancellationToken for operation cancellation.
3. **Error Handling:** Tools return typed results indicating success/failure status.
4. **Immutability:** Refactoring operations don't modify original code; they return modified versions.

## Related Documentation

- **README.md** - RoslynSentinel overview and getting started
- **GEMINI.md** - AI assistant guide for RoslynSentinel
- **BUG_FIX_STRATEGY.md** - Bug fix and issue resolution patterns

---

**Last Updated:** 2026-05-03  
**Documentation Version:** 1.0  
**Tools Documented:** 320+
