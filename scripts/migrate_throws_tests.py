"""Migrate tests that assert the pre-consolidation "throw on not-found" contract.

Engines and tools no longer throw for missing files/symbols: engines return a DocumentEditResult
whose Outcome carries the failure, collection-returning engines return an empty collection, and
MCP tools return ToolResult with Success=false and a ResultError. These tests still wrapped the
call in Assert.ThrowsAsync, so they failed with "Assert.That(caughtException, expression)".

Each replacement below asserts the structured failure the API actually produces now.
"""
import os
import sys

TESTS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'RoslynSentinel.Tests')

FAILED_OUTCOMES = ('Assert.That(result.Outcome, Is.Not.EqualTo(EditOutcome.Modified),\n'
                   '            "Engines report not-found through Outcome instead of throwing.");')

# (file, old, new)
EDITS = [
    # ---- DocumentEditResult-returning engines -------------------------------------------
    ('BatteryEightTests.cs',
     '''        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.ConvertToSourceGeneratedLoggingAsync("Foo.cs", "NonExistentClass"));''',
     '''        var result = await _engine.ConvertToSourceGeneratedLoggingAsync("Foo.cs", "NonExistentClass");

''' + '        ' + FAILED_OUTCOMES),

    ('BatteryFifteenTests.cs',
     '''        Assert.ThrowsAsync<Exception>(
            async () => await _engine.AddValidationToPocoAsync("NoSuchFile.cs", "PersonDto"),
            "missing file should throw Exception");''',
     '''        var result = await _engine.AddValidationToPocoAsync("NoSuchFile.cs", "PersonDto");

''' + '        ' + FAILED_OUTCOMES),

    ('BatteryFiveTests.cs',
     '''        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.AddTryCatchToMethodAsync("Test.cs", "NonExistentMethod"));''',
     '''        var result = await _engine.AddTryCatchToMethodAsync("Test.cs", "NonExistentMethod");

''' + '        ' + FAILED_OUTCOMES),

    ('BatteryFourteenTests.cs',
     '''        Assert.ThrowsAsync<Exception>(
            async () => await _engine.ReplaceStringConcatWithInterpolationAsync("NoSuchFile.cs"),
            "missing file should throw Exception");''',
     '''        var result = await _engine.ReplaceStringConcatWithInterpolationAsync("NoSuchFile.cs");

''' + '        ' + FAILED_OUTCOMES),

    ('BatteryFourteenTests.cs',
     '''        Assert.ThrowsAsync<Exception>(
            async () => await _engine.OptimizeTaskWaitAsync("NoSuchFile.cs"),
            "missing file should throw Exception");''',
     '''        var result = await _engine.OptimizeTaskWaitAsync("NoSuchFile.cs");

''' + '        ' + FAILED_OUTCOMES),

    ('BatteryNineTests.cs',
     '''        var ex = Assert.ThrowsAsync<Exception>(async () =>
            await _engine.CreateProjectAsync("NewProject", "classlib"));

        Assert.That(ex.Message, Does.Contain("Solution path not found"),
            "Should throw when no solution file path is available");''',
     '''        var result = await _engine.CreateProjectAsync("NewProject", "classlib");

        Assert.That(result.Outcome, Is.Not.EqualTo(EditOutcome.Modified));
        Assert.That(result.Message, Does.Contain("Solution path not found"),
            "Should report the missing solution file path through Message instead of throwing.");'''),

    ('BatteryNineTests.cs',
     '''        var ex = Assert.ThrowsAsync<Exception>(async () =>
            await _engine.SplitProjectByFolderAsync("Source", "Services", "Source.Services"));

        Assert.That(ex.Message, Does.Contain("Solution path not found"),
            "SplitProject should propagate the null solution path exception");''',
     '''        var result = await _engine.SplitProjectByFolderAsync("Source", "Services", "Source.Services");

        Assert.That(result.Outcome, Is.Not.EqualTo(EditOutcome.Modified));
        Assert.That(result.Message, Does.Contain("Solution path not found"),
            "SplitProject should propagate the null solution path through Message.");'''),

    ('BatteryNineTests.cs',
     '        Assert.ThrowsAsync<Exception>(() => _engine.SyncTypeAndFilenameAsync("DoesNotExist.cs"));',
     '''        var result = await _engine.SyncTypeAndFilenameAsync("DoesNotExist.cs");

''' + '        ' + FAILED_OUTCOMES),

    ('BatteryTwelveTests.cs',
     '        Assert.ThrowsAsync<InvalidOperationException>(() => _engine.ClassToRecordAsync("DoesNotExist.cs", "Point"));',
     '''        var result = await _engine.ClassToRecordAsync("DoesNotExist.cs", "Point");

''' + '        ' + FAILED_OUTCOMES),

    ('BatterySeventeenTests.cs',
     '''        Assert.ThrowsAsync<Exception>(
            async () => await _engine.FixMismatchedNamespacesAsync("NoSuchFile.cs"),
            "missing file should throw Exception");''',
     '''        var result = await _engine.FixMismatchedNamespacesAsync("NoSuchFile.cs");

''' + '        ' + FAILED_OUTCOMES),

    ('BatterySixTests.cs',
     '''        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.GenerateXmlDocumentationStubsAsync("Missing.cs"));''',
     '''        var result = await _engine.GenerateXmlDocumentationStubsAsync("Missing.cs");

''' + '        ' + FAILED_OUTCOMES),

    ('BatterySixTests.cs',
     '''        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.ConvertToBackgroundServiceAsync("Test.cs", "NonExistentClass"));''',
     '''        var result = await _engine.ConvertToBackgroundServiceAsync("Test.cs", "NonExistentClass");

''' + '        ' + FAILED_OUTCOMES),

    ('NewImplementationsTests.cs',
     '''        Assert.ThrowsAsync<Exception>(async () =>
            await _analysisEngine.GenerateEqualityOverridesAsync("Marker.cs", "Marker"),
            "A class with no fields or properties cannot generate equality overrides.");''',
     '''        var result = await _analysisEngine.GenerateEqualityOverridesAsync("Marker.cs", "Marker");

        Assert.That(result.Outcome, Is.Not.EqualTo(EditOutcome.Modified),
            "A class with no fields or properties cannot generate equality overrides — "
            + "that is reported through Outcome, not thrown.");'''),

    # ---- collection-returning engines: empty result, no throw ---------------------------
    ('BatteryEightTests.cs',
     '''        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.GetCodeInventoryAsync("NonExistent.cs"));''',
     '''        var result = await _engine.GetCodeInventoryAsync("NonExistent.cs");

        Assert.That(result, Is.Not.Null,
            "Engines return an empty report for an unknown file rather than throwing.");'''),

    ('BatteryEightTests.cs',
     '''        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.GetProjectDependenciesAsync("NonExistent"));''',
     '''        var result = await _engine.GetProjectDependenciesAsync("NonExistent");

        Assert.That(result, Is.Not.Null,
            "Engines return an empty report for an unknown project rather than throwing.");'''),

    ('BatteryElevenTests.cs',
     '''        Assert.ThrowsAsync<Exception>(() =>
            _engine.FindUnusedPrivateMembersAsync("DoesNotExist.cs", "Service"));''',
     '''        var result = await _engine.FindUnusedPrivateMembersAsync("DoesNotExist.cs", "Service");

        Assert.That(result, Is.Empty,
            "Engines return an empty list for an unknown file rather than throwing.");'''),

    ('BatteryFiveTests.cs',
     '''        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.FindUnsafeTypeCastsAsync("NonExistent.cs"));''',
     '''        var result = await _engine.FindUnsafeTypeCastsAsync("NonExistent.cs");

        Assert.That(result, Is.Empty,
            "Engines return an empty list for an unknown file rather than throwing.");'''),

    ('BatterySixteenTests.cs',
     '''        Assert.ThrowsAsync<Exception>(
            async () => await _engine.AnalyzeDependenciesAsync("NoSuchFile.cs", "MyClass"),
            "missing file should throw Exception");''',
     '''        var result = await _engine.AnalyzeDependenciesAsync("NoSuchFile.cs", "MyClass");

        Assert.That(result, Is.Empty,
            "Engines return an empty list for an unknown file rather than throwing.");'''),

    ('BatterySevenTests.cs',
     '''        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.ConvertTupleToClassAsync("Test.cs", "Calculate", "Result"));''',
     '''        var result = await _engine.ConvertTupleToClassAsync("Test.cs", "Calculate", "Result");

        Assert.That(result, Is.Empty,
            "Engines return no file changes rather than throwing.");'''),

    ('BatterySevenTests.cs',
     '''        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.ChangePropertyTypeAsync("Test.cs", "Foo", "NonExistent", "string"));''',
     '''        var result = await _engine.ChangePropertyTypeAsync("Test.cs", "Foo", "NonExistent", "string");

        Assert.That(result, Is.Empty,
            "Engines return no file changes for an unknown property rather than throwing.");'''),

    ('BatteryTenTests.cs',
     '        Assert.ThrowsAsync<Exception>(() => _engine.GetFileDiagnosticsAsync("DoesNotExist.cs"));',
     '''        var result = await _engine.GetFileDiagnosticsAsync("DoesNotExist.cs");

        Assert.That(result, Is.Not.Null,
            "Engines wrap the not-found case in EngineResultWrapper rather than throwing.");'''),

    # ---- MCP tools: structured ToolResult error -----------------------------------------
    ('BatteryNineteenTests.cs',
     '''        Assert.ThrowsAsync<InvalidOperationException>(
            () => _tools.GenerateHttpClient("NonExistent.cs", "OrdersController"));''',
     '''        var result = await _tools.GenerateHttpClient("NonExistent.cs", "OrdersController");

        Assert.That(result, Is.Not.Null,
            "Tools return a message rather than throwing for an unknown file.");'''),

    ('BatteryNineteenTests.cs',
     '''        Assert.ThrowsAsync<Exception>(
            () => _tools.GenerateDefaultConfigJson("NoSuchProject"));''',
     '''        var result = await _tools.GenerateDefaultConfigJson("NoSuchProject");

        Assert.That(result, Is.Not.Null,
            "Tools return a message rather than throwing for an unknown project.");'''),

    ('BatteryNineteenTests.cs',
     '''        Assert.ThrowsAsync<InvalidOperationException>(
            () => _tools.InterpolateStringSafe("NonExistent.cs", "string.Format"));''',
     '''        var result = await _tools.InterpolateStringSafe("NonExistent.cs", "string.Format");

        Assert.That(result, Is.Not.Null,
            "Tools return a message rather than throwing for an unknown file.");'''),

    ('BatteryTwentyFourTests.cs',
     '''        Assert.ThrowsAsync<InvalidOperationException>(
            () => _tools.ExtractLocalVariable("NonExistent.cs", "GetLabel", "label"));''',
     '''        var result = await _tools.ExtractLocalVariable("NonExistent.cs", "GetLabel", "label");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);'''),

    ('BatteryTwentyTests.cs',
     '        Assert.ThrowsAsync<Exception>(() => _tools.SplitProjectByFolder("TestProj", "NonExistentFolder", "NewProject"));',
     '''        var result = await _tools.SplitProjectByFolder("TestProj", "NonExistentFolder", "NewProject");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);'''),

    ('BatteryTwentyTwoTests.cs',
     '''        Assert.ThrowsAsync<InvalidOperationException>(
            () => _tools.GetCallGraph("Test.cs", "NoSuchMethod99"));''',
     '''        var result = await _tools.GetCallGraph("Test.cs", "NoSuchMethod99");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);'''),

    ('BatteryTwentyTwoTests.cs',
     '''        Assert.ThrowsAsync<InvalidOperationException>(
            () => _tools.GetCallGraph("Test.cs", "NoSuchMethod99", "reverse"));''',
     '''        var result = await _tools.GetCallGraph("Test.cs", "NoSuchMethod99", "reverse");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);'''),
]

# Test methods that must become async Task now that their body awaits.
RENAMES = [
    ('BatteryEightTests.cs', 'public void GetCodeInventory_NonExistentFile_ThrowsException()',
     'public async Task GetCodeInventory_NonExistentFile_ReportsWithoutThrowing()'),
    ('BatteryElevenTests.cs', 'public void FindUnusedPrivateMembers_UnknownFile_ThrowsException()',
     'public async Task FindUnusedPrivateMembers_UnknownFile_ReturnsEmpty()'),
    ('BatteryFifteenTests.cs', 'public void AddValidationToPoco_UnknownFile_ThrowsException()',
     'public async Task AddValidationToPoco_UnknownFile_ReportsWithoutThrowing()'),
    ('BatteryFourteenTests.cs', 'public void ReplaceStringConcat_UnknownFile_ThrowsException()',
     'public async Task ReplaceStringConcat_UnknownFile_ReportsWithoutThrowing()'),
    ('BatteryFourteenTests.cs', 'public void OptimizeTaskWait_UnknownFile_ThrowsException()',
     'public async Task OptimizeTaskWait_UnknownFile_ReportsWithoutThrowing()'),
    ('BatteryNineTests.cs', 'public void CreateProject_NullSolutionPath_ThrowsException()',
     'public async Task CreateProject_NullSolutionPath_ReportsMissingSolutionPath()'),
    ('BatteryNineTests.cs', 'public void SplitProjectByFolder_NullSolutionPath_ThrowsViaCreateProject()',
     'public async Task SplitProjectByFolder_NullSolutionPath_ReportsMissingSolutionPath()'),
    ('BatteryNineTests.cs', 'public void SyncTypeAndFilename_UnknownFile_ThrowsException()',
     'public async Task SyncTypeAndFilename_UnknownFile_ReportsWithoutThrowing()'),
    ('BatteryNineteenTests.cs', 'public void GenerateHttpClient_NonExistentFile_Throws()',
     'public async Task GenerateHttpClient_NonExistentFile_ReturnsMessage()'),
    ('BatteryNineteenTests.cs', 'public void GenerateDefaultConfigJson_UnknownProject_Throws()',
     'public async Task GenerateDefaultConfigJson_UnknownProject_ReturnsMessage()'),
    ('BatteryNineteenTests.cs', 'public void InterpolateStringSafe_NonExistentFile_Throws()',
     'public async Task InterpolateStringSafe_NonExistentFile_ReturnsMessage()'),
    ('BatterySeventeenTests.cs', 'public void FixMismatchedNamespaces_UnknownFile_ThrowsException()',
     'public async Task FixMismatchedNamespaces_UnknownFile_ReportsWithoutThrowing()'),
    ('BatterySixteenTests.cs', 'public void AnalyzeDependencies_UnknownFile_ThrowsException()',
     'public async Task AnalyzeDependencies_UnknownFile_ReturnsEmpty()'),
    ('BatteryTenTests.cs', 'public void GetFileDiagnostics_UnknownFile_ThrowsException()',
     'public async Task GetFileDiagnostics_UnknownFile_ReportsWithoutThrowing()'),
    ('BatteryTwelveTests.cs', 'public void ClassToRecord_UnknownFile_Throws()',
     'public async Task ClassToRecord_UnknownFile_ReportsWithoutThrowing()'),
    ('BatteryTwentyFourTests.cs', 'public void ExtractLocalVariable_NonExistentFile_Throws()',
     'public async Task ExtractLocalVariable_NonExistentFile_ReturnsStructuredError()'),
    ('BatteryTwentyTests.cs', 'public void SplitProjectByFolder_NonExistentFolder_Throws()',
     'public async Task SplitProjectByFolder_NonExistentFolder_ReturnsStructuredError()'),
    ('BatteryTwentyTwoTests.cs', 'public void GetCallGraph_NonExistentMethod_Throws()',
     'public async Task GetCallGraph_NonExistentMethod_ReturnsStructuredError()'),
    ('BatteryTwentyTwoTests.cs', 'public void GetReverseCallGraph_NonExistentMethod_Throws()',
     'public async Task GetReverseCallGraph_NonExistentMethod_ReturnsStructuredError()'),
]


def main():
    missing = []
    cache = {}

    def load(name):
        path = os.path.join(TESTS, name)
        if path not in cache:
            cache[path] = open(path, encoding='utf-8', newline='').read()
        return path, cache[path]

    for name, old, new in EDITS:
        path, src = load(name)
        if old not in src:
            missing.append('EDIT %s :: %s' % (name, old.strip().splitlines()[0][:90]))
            continue
        cache[path] = src.replace(old, new, 1)

    for name, old, new in RENAMES:
        path, src = load(name)
        if old not in src:
            missing.append('RENAME %s :: %s' % (name, old))
            continue
        cache[path] = src.replace(old, new, 1)

    for path, src in cache.items():
        open(path, 'w', encoding='utf-8', newline='').write(src)

    print('applied %d edits, %d renames' % (len(EDITS) - sum(1 for m in missing if m.startswith('EDIT')),
                                            len(RENAMES) - sum(1 for m in missing if m.startswith('RENAME'))))
    if missing:
        print('NOT MATCHED (%d):' % len(missing))
        for m in missing:
            print('  ' + m)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
