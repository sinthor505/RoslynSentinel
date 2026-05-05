# RoslynSentinel — Backlog & Known Limitations

This document tracks planned enhancements, known edge cases, and deferred features.
For implementation history and session notes, see the git log.

---

## 🛠️ Planned: Advanced Refactoring Suite

### Easy Difficulty
- **`ConvertIndexerToMethod`** — Replace an indexer with `GetX(int index)` / `SetX(int index, T value)` methods
- **`ConvertPropertyToAutoProperty`** — Remove manual backing fields when the getter/setter logic is trivial
- **`CopyType`** — Create a deep structural clone of a type in a new file
- **`Add/Remove params modifier`** — Toggle the `params` keyword on the final array parameter of a method
- **`IDE0011 (AddBraces)`** — Surgically add braces to all single-line `if`, `foreach`, and `while` blocks across a project

### Medium Difficulty
- **`ConvertInterfaceToAbstractClass`** — Convert an interface to an abstract base class, adding method stubs
- **`ReplaceConstructorWithFactoryMethod`** — Move instantiation logic to a static `Create(...)` factory; update call sites
- **`SpecificExceptionCatching`** — Trace method call chains to narrow `catch (Exception)` to the exact exception types thrown
- **`LocalFunctionMigration`** — Convert anonymous lambdas to named local functions
- **`EPC26 (TasksInUsing)`** — Detect unawaited tasks inside `using` blocks that may outlive resource disposal

### Hard Difficulty
- **`MoveInstanceMethod`** — Move a method to another type, resolve the correct object reference at call sites, and update all usages

---

## 🐛 Known Limitations

### `inline_class` — Cross-File Symbol Discovery Required
`AdvancedStructuralEngine.InlineClassAsync` throws `InvalidOperationException` with an explanatory message. Inlining a class requires finding all references across the solution and updating them atomically — this requires the Phase 2.5 workspace enhancement (`SymbolFinder`-based cross-file rewriting). The tool exists and communicates its limitation clearly.

### `inline_method` — Single-File Call Sites Only
`RefactoringEngine.InlineMethodAsync` only updates call sites within the same file. Cross-file references are not updated. **Workaround:** use `find_callers` to locate other files, then apply `inline_method` to each file individually.

### `move_type_to_file` — File-Scoped Types (C# 11+)
May generate incorrect output when the source type uses the `file` keyword (C# 11 file-scoped types). Manually verify the `file` modifier is preserved in the generated output.

### `extract_class` — Generic Constraints and Circular References
`AdvancedRefactoringEngine.ExtractClassAsync` does not yet handle:
- Extracting from generic classes with type-parameter constraints
- Members that reference type parameters from the enclosing class
- Circular reference detection between extracted and source class

**Priority:** Medium-low (affects < 5% of uses). Planned for Phase 3.

### `IntroduceField` — Nested Class / Interface Scoping
`RefactoringEngine.IntroduceFieldAsync` may introduce the field at the wrong scope when the target member is inside a nested class or an interface. **Workaround:** validate the generated output and move the field manually if needed. Planned fix: Phase 2.5.

---

## 🔮 Phase 2.5 Vision: Intent-Based AST Commands

The long-term direction is a "Refactor Recipe" model where AI agents issue high-level intents such as:
- `InjectDependency(className, interfaceName)`
- `AddGuard(methodName, parameterName)`
- `WrapInTryCatch(methodName, exceptionType)`

Roslyn handles all structural manipulation, formatting, and trivia preservation. The AI agent never touches raw text.
