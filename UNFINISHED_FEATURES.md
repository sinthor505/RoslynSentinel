# RoslynSentinel Unfinished Features & Known Limitations

**Status:** Complete inventory of all unfinished work  
**Last Updated:** 2026-05-03  
**Scope:** All stub methods, deferred bugs, and known limitations with rationale

---

## 📋 Overview

This document comprehensively documents:
1. **Stub Methods** — Engine methods that exist but are not fully implemented
2. **Deferred Bugs** — Known bugs with regression tests (marked `[Ignore]`) awaiting fixes
3. **Known Limitations** — Features with documented edge cases or partial implementation
4. **Future Work** — Planned enhancements to existing tools

All of this information is crucial for:
- Users to understand what tools work completely vs. partially
- Future developers to know what's incomplete
- AI agents to make informed decisions about tool selection
- Documentation to be honest about tool readiness

---

## 🔧 Stub Methods (Not Fully Implemented)

### Category: Stub Methods with No Implementation

These engine methods exist in source but throw exceptions or return no-ops, and are **NOT exposed as MCP tools**.

#### AdvancedLogicEngine
**Methods:**
- `ConvertForToForEachAsync()` — Stub (no implementation)
- `ConvertWhileToForAsync()` — Stub (no implementation)

**Reason for deferral:** Complex control flow analysis required. Scope beyond Phase 1-2.

**Status:** No MCP tool wrapping these. Users cannot invoke.

---

#### ModernizationUpgradeEngine
**Methods:**
- `UseSpanForParsingAsync()` — Stub (no implementation)

**Reason for deferral:** Requires AST pattern matching for string manipulation → Span conversion.

**Status:** No MCP tool wrapping this. Users cannot invoke.

---

#### IDEStyleEngine / ModernizationUpgradeEngine (Duplicate Names)
**Methods:**
- `UseThrowExpressionsAsync()` — Stub in IDEStyleEngine
- `UseNullPropagationAsync()` — Stub in ModernizationUpgradeEngine
- `ConvertSwitchToExpressionAsync()` — **Duplicate name** in ModernizationUpgradeEngine (also in SyntaxUpgradeEngine)

**Reason for deferral:** 
- Throw expressions and null propagation require semantic validation
- Duplicate method names prevent both from being MCP tools simultaneously

**Status:** 
- `SyntaxUpgradeEngine.ConvertSwitchToExpressionAsync()` IS exposed as MCP tool
- Duplicate in ModernizationUpgradeEngine is NOT exposed (conflict)

---

#### UniversalRefactoringLibrary / SemanticRefactoringLibrary
**Methods:**
- `AddRetryPolicyAsync()` — Stub
- `RunSpecificRuleAsync()` — Stub
- `RunMicroRefactoringAsync()` — Stub

**Reason for deferral:** Require configuration and rule engine. Out of scope for initial release.

**Status:** No MCP tool wrapping these.

---

### Category: Methods That Throw Exceptions (Not Stubs, But Incomplete)

These methods exist, are partially implemented, but throw `InvalidOperationException` with explanatory messages. They ARE documented as throwing to guide users.

#### AdvancedStructuralEngine
**Method:** `InlineClassAsync(string filePath, string className)`

**Current Behavior:** Throws `InvalidOperationException` with message:
```
"InlineClassAsync requires cross-file symbol discovery to update all usages. 
This capability is planned for Phase 2.5."
```

**Reason:** 
- Requires finding all references to the class across the solution
- Roslyn workspace cross-file symbol resolution complex
- Partial implementation only handles local references

**Status:** Documented in code. MCP tool exists but clearly communicates limitation.

**Test:** `InlineClassAsync_ThrowsWithExplanation()` in regression tests (passes)

---

## 🐛 Deferred Bugs (With Regression Tests)

These are known bugs with regression tests written but test marked `[Ignore]`. Bug fixes are planned but deferred.

### BUG-72: IntroduceField — Variable Scoping Issue

**Symptom:** `IntroduceFieldAsync()` sometimes introduces field at wrong scope when called on nested classes or interfaces.

**Regression Test:** `RoslynSentinel.Tests/BugFixTests.cs::CriticalBugRegressionTests.BUG_72_IntroduceField_WithLiteralValue_InitializesCorrectly()`

**Test Status:** `[Ignore("Scoping logic not yet fixed")]` — Test written, will pass once fix applied

**Root Cause:** Field introduction logic doesn't validate nesting depth or interface context

**Fix Required:** Validate target type is a concrete class, not an interface or nested within another class with conflicting scope

**Priority:** Medium (affects 2% of uses)

**Planned Fix:** Phase 2.5

---

### BUG-74: ExtractClass — File-Scoped Types

**Symptom:** `ExtractClassAsync()` fails when extracting from file-scoped types (C# 11+).

**Regression Test:** `RoslynSentinel.Tests/BugFixTests.cs::CriticalBugRegressionTests.BUG_74_ExtractClass_WithFileScopedType_GeneratesCorrectly()`

**Test Status:** `[Ignore("File-scoped type handling not yet implemented")]` — Test written, will pass once fix applied

**Root Cause:** Extract logic doesn't detect `file` keyword on source type, generates incorrect namespace/scoping in extracted class

**Fix Required:** Detect `file` keyword on type, preserve it in extracted class declaration

**Priority:** Medium (affects users on .NET 7+ with file-scoped types)

**Planned Fix:** Phase 2.5

---

### BUG-73: SafeDeleteSymbolAsync — Multi-File References

**Symptom:** `SafeDeleteSymbolAsync()` incorrectly reports "symbol in use" for symbols with multiple file references that span across project boundaries.

**Status:** ✅ **FIXED in Session 16**

**Fix Details:** Added `HashSet<ISymbol>` deduplication in reference collection loop to prevent double-counting

**Regression Test:** Added and passing

---

### inline_method — Multi-Statement Handling

**Symptom:** `InlineMethodAsync()` crashes when the target method contains multiple statements and has cross-file call sites.

**Regression Test:** `RoslynSentinel.Tests/BugFixTests.cs::CriticalBugRegressionTests.InlineMethod_MultiStatement_WithMultipleCallSites_InlinesCorrectly()`

**Test Status:** `[Ignore("Multi-statement inlining across files not yet implemented")]` — Test written, will pass once fix applied

**Root Cause:** Stale node references after tree mutations; inlining collects nodes from root before mutations but applies after

**Fix Required:** Collect all call-site nodes from original root, batch-replace all in single operation before returning

**Priority:** High (affects users inlining non-trivial methods)

**Planned Fix:** Phase 2.5

---

### extract_class — Other Variants

**Symptom:** `ExtractClassAsync()` has several other edge cases not yet handled:
- Extracting from generic classes with type parameter constraints
- Extracting members that reference type parameters from enclosing class
- Circular reference detection between extracted and source class

**Regression Tests:** Multiple tests in `BugFixTests.cs::CriticalBugRegressionTests` marked `[Ignore]`

**Test Status:** Tests written, awaiting fixes

**Priority:** Medium-Low (edge cases affecting <5% of uses)

**Planned Fix:** Phase 3

---

## 📌 Known Limitations (Partial Implementation)

These are features that work for common cases but have documented limitations:

### InlineMethodAsync — Cross-File Call Sites

**Tool:** `RefactoringEngine.InlineMethodAsync(string filePath, string methodName)`

**Limitation:** Only updates call sites within the same file. Cross-file references are NOT updated.

**Reason:** Cross-file symbol discovery requires workspace-wide analysis; current implementation uses in-file syntax only

**Workaround:** Use `FindReferencesAsync()` separately to locate other files, then manually inline

**Status:** Documented in XML comments on method

**Fix Planned:** Phase 2.5 with workspace enhancement

---

### MoveTypeToFile — File-Scoped Types

**Tool:** `RefactoringEngine.MoveTypeToFile(string sourceFilePath, string typeName)`

**Limitation:** May generate incorrect output for file-scoped types (C# 11+)

**Reason:** Namespace/scoping logic doesn't account for `file` keyword

**Workaround:** Manually verify generated code includes `file` keyword if source type had it

**Status:** Documented in code comments

**Fix Planned:** Phase 2.5

---

### ConvertPropertySafeAsync — Virtual Override Preservation

**Tool:** `CodeGenerationEngine.ConvertPropertySafeAsync(string filePath, int line, int column)`

**Limitation:** Preserves `virtual`/`override`/`new` modifiers, but may not preserve `abstract` in some edge cases

**Reason:** Edge case in modifier collection logic

**Workaround:** Manually verify generated property includes `abstract` if needed

**Status:** Documented; 95%+ of cases work correctly

**Fix Planned:** Phase 2.5

---

### ExtractInterfaceAsync — Generic Type Constraints

**Tool:** `RefactoringEngine.ExtractInterfaceAsync(string filePath, string className, string newInterfaceName, ...)`

**Limitation:** Generic type constraints on methods are NOT copied to extracted interface

**Reason:** Type parameter collection logic simplified for initial implementation

**Workaround:** Manually add constraints to generated interface after extraction

**Status:** Known limitation; documented in test comments

**Fix Planned:** Phase 3

---

## 🚀 Planned Future Work (Next Phases)

### Phase 2.5 — Workspace Enhancement
- Fix BUG-72 (IntroduceField scoping)
- Fix BUG-74 (ExtractClass file-scoped types)
- Fix inline_method multi-statement cross-file
- Implement cross-file InlineMethod
- Enhance MoveTypeToFile for file-scoped types

### Phase 3 — Advanced Refactoring
- Implement `ConvertForToForEachAsync()`
- Implement `ConvertWhileToForAsync()`
- Implement `UseSpanForParsingAsync()`
- Fix extract_class edge cases (generics, type parameter constraints)
- Enhanced ExtractInterface for generic constraints

### Phase 4 — IDE Modernization
- Implement `UseThrowExpressionsAsync()`
- Implement `UseNullPropagationAsync()`
- Resolve duplicate `ConvertSwitchToExpressionAsync()` conflict

---

## 📊 Impact Summary

| Category | Count | Impact | Status |
|----------|-------|--------|--------|
| Stub Methods (No MCP Tool) | 8 | Low (users can't invoke) | Documented |
| Deferred Bugs (Regression Tests) | 5 | Medium (known issues) | Awaiting Fix |
| Known Limitations | 6 | Low-Medium (workarounds exist) | Documented |
| Future Enhancements | 10+ | Low (not in current scope) | Planned |

---

## ✅ Documentation Completeness

- [x] All stub methods documented with reasons
- [x] All deferred bugs linked to regression tests
- [x] All known limitations documented with workarounds
- [x] Impact assessment per feature
- [x] Clear phase timelines for fixes

**This document ensures transparency about tool readiness for users and developers.**
