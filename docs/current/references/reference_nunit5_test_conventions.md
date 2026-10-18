---
name: nunit5-test-conventions
description: "Solution uses NUnit 5.0 (not 4.6) - async assertions must be awaited, removed delegate types, how to write new tests correctly first time"
metadata:
  node_type: memory
  type: reference
  originSessionId: 5c6305b5-d257-4cd4-8144-5fecc14519c1
  modified: 2026-10-08T22:57:41.276Z
---

All test projects moved NUnit 4.6.1 -> 5.0.0 in commit c0d1837 (44 files; ~150 call sites were
changed to `await`). Sources: docs.nunit.org V5BreakingChanges / V5NewFeatures.

## Rules for new tests

- `Assert.ThrowsAsync`, `Assert.CatchAsync`, `Assert.DoesNotThrowAsync` now return `Task` and **must
  be awaited**; the test must be `public async Task`. Un-awaited, they compile (maybe with an analyzer
  warning) but assert nothing - the test silently passes.

  ```csharp
  var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Sut.RunAsync());
  await Assert.DoesNotThrowAsync(async () => result = await engine.AnalyzeAsync(path));
  ```
- Sync `Assert.Throws<T>(() => ...)` / `Assert.DoesNotThrow` are unchanged (still sync, not awaited).
- `Assert.That(x, constraint)` is unchanged. Prefer it for value checks.
- `TestDelegate`, `AsyncTestDelegate`, `ActualValueDelegate<T>` are removed: use `Action`, `Func<Task>`,
  `Func<T>`.
- `Is.SameAs` is reference types only (`where T : class?`); use `Is.EqualTo` for value types.
- `Has.Attribute<T>()` requires `T : Attribute`.
- `StringAssert`, `CollectionAssert`, `FileAssert`, `DirectoryAssert` live in `NUnit.Framework` again;
  do not add `using NUnit.Framework.Legacy;` (the repo has no remaining uses of it or `ClassicAssert`).
- Targets are `net462`, `net8.0`, `net10.0` (no net6.0). Repo tests are `net10.0`.

## Useful new features (optional)

- `[DependsOnTest(nameof(Other))]` / `[DependsOnFixture]`: ordering plus skip-if-dependency-failed.
- `Throws.ArgumentNullException.ParamName.EqualTo("x")`.
- `TestCaseData.Create(1, 2).Returns(3)` is compile-time typed.
- `[MaxTime(2000, WarningTime = 1000)]`, `[Repeat(20, RequiredPassPercentage = 90)]`.

## Gotcha when reviewing agent-written tests

Haiku-tier agents trained on NUnit 4 will write `Assert.ThrowsAsync(...)` without `await`. After any
agent adds tests, check with `Search(mode: text, query: "(?<!await )Assert\\.(ThrowsAsync|CatchAsync|DoesNotThrowAsync)")`
- zero matches is the healthy state as of 2026-10-08. Hand this rule to `test-writer` / `implementer`
briefs for test work.
