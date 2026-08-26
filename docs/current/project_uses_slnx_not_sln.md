---
name: project-uses-slnx-not-sln
description: "RoslynSentinel's top-level solution file is RoslynSentinel.slnx (new XML solution format), not a .sln — there is no top-level .sln"
metadata:
  type: project
  originSessionId: ed38ed5b-d0aa-4f27-828c-fcbbb5d0b086
  modified: 2026-08-26T02:32:18.262Z
---

The repo root has no `RoslynSentinel.sln` — only `RoslynSentinel.slnx` (the newer XML-based solution format `dotnet build`/`dotnet test` both accept directly). `git status --short` on `docs/current` confirmed this after `dotnet build RoslynSentinel.sln` failed with `MSB1009: Project file does not exist`.

The only `.sln` files in the repo are unrelated, nested ones: `DummyConsole\DummyClassic.sln` and `Samples\ContosoOrders\ContosoOrders.sln`.

**How to apply:** always target `RoslynSentinel.slnx` for solution-wide `dotnet build`/`dotnet test` in this repo, e.g. `dotnet build RoslynSentinel.slnx -c Debug`. Don't assume a `.sln` exists at the root — check with Glob first if unsure. See also [[project_server_flavors_and_build_configs]] for the build.ps1 wrapper that also bootstraps bin-vscode.
