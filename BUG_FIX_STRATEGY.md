# RoslynSentinel Remaining Bugs - Bug Fix Strategy

**Status:** Establishing regression test framework and fixing bugs systematically
**Baseline:** 533 tests passing, 0 failures (verified)
**Target:** Fix 22 remaining bugs with regression tests

## Bugs by Priority

### Priority 1: Crashes (6 bugs) - MUST NOT THROW
- **BUG-52**: Null dictionary causes NullReferenceException
- **BUG-53**: Empty project causes IndexOutOfRangeException  
- **BUG-58**: Malformed syntax causes parsing crash
- **BUG-69**: Unicode characters cause encoding error
- **BUG-76**: Recursive method causes stack overflow
- **BUG-77**: Generic constraints cause type resolution error

### Priority 2: Uncompilable Output (8 bugs) - GENERATED CODE WON'T COMPILE
- **BUG-55**: Generated equals method has syntax errors
- **BUG-56**: Generated constructor is incomplete
- **BUG-57**: Generated fluent builder has syntax errors
- **BUG-60**: Generated mapping missing imports
- **BUG-62**: Generated repository class is incomplete
- **BUG-64**: Generated decorator signatures are invalid
- **BUG-75**: Generated record equality is uncompilable
- **BUG-78**: Generated test has wrong namespace

### Priority 3: Silent Failures (8 bugs) - WRONG RESULT WITHOUT ERROR
- **BUG-45**: Refactoring produces wrong result
- **BUG-47**: Interface extraction drops members
- **BUG-48**: Wrong exception type used
- **BUG-49**: Accessibility modifier changed silently
- **BUG-50**: Refactoring breaks semantics
- **BUG-51**: Branch condition mishandled
- **BUG-54**: Type mapping is incorrect
- **BUG-59**: Async/await handling is wrong

## Implementation Strategy

### Phase 1: Test Framework (IN PROGRESS)
- [x] Establish baseline (533 tests)
- [ ] Create regression tests for each bug
- [ ] Tests initially marked [Ignore] to document requirements
- [ ] Remove [Ignore] as bugs are fixed

### Phase 2: Fix Priority 1 Bugs (NEXT)
1. Create simple regression test for BUG-52
2. Add null-check to dictionary handling
3. Run full test suite (should still be 533+ passing)
4. Repeat for BUG-53, BUG-58, etc.

### Phase 3: Fix Priority 2 Bugs
- Focus on code generation bugs
- Validate generated output compiles
- Check imports and namespace handling

### Phase 4: Fix Priority 3 Bugs  
- Semantic correctness checks
- Member preservation in extraction
- Type safety in mapping operations

## Testing Pattern for Each Bug

```csharp
[Test]
public async Task BUG_XX_DescriptionOfFix()
{
    // Arrange: Set up minimal reproduction case
    SetSource("code that triggers bug");
    
    // Act: Call engine method
    var result = await engine.MethodAsync(...);
    
    // Assert: Verify bug is fixed
    Assert.That(result, Matches.Expected("behavior"));
}
```

## Tracking Progress

- [ ] BUG-52: Null dictionary
- [ ] BUG-53: Empty project
- [ ] BUG-58: Malformed syntax
- [ ] BUG-69: Unicode characters
- [ ] BUG-76: Recursive methods
- [ ] BUG-77: Generic constraints
- [ ] BUG-55: Equals generation
- [ ] BUG-56: Constructor generation
- [ ] BUG-57: Builder generation
- [ ] BUG-60: Mapping imports
- [ ] BUG-62: Repository generation
- [ ] BUG-64: Decorator signatures
- [ ] BUG-75: Record equality
- [ ] BUG-78: Test namespace
- [ ] BUG-45: Refactoring result
- [ ] BUG-47: Member extraction
- [ ] BUG-48: Exception type
- [ ] BUG-49: Accessibility
- [ ] BUG-50: Semantic preservation
- [ ] BUG-51: Branch conditions
- [ ] BUG-54: Type mapping
- [ ] BUG-59: Async/await

## Files Modified

- `RoslynSentinel.Server/RefactoringEngine.cs` - Primary refactoring engine fixes
- `RoslynSentinel.Server/CodeGenerationEngine.cs` - Code generation bug fixes
- `RoslynSentinel.Tests/BugFixTests.cs` - Add regression tests as bugs are fixed

## Git Commit Pattern

Each bug fix will be committed separately:
```
git commit -m "fix(BUG-XX): description of fix

- Specific change made
- Validation logic added
- Regression test added"
```
