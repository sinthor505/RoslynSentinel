"""Restore throws-assertions for engines that genuinely still throw on a missing file.

NUnit's Assert.ThrowsAsync<T> requires an *exact* type match, so `Assert.ThrowsAsync<Exception>`
fails when the engine throws FileNotFoundException or InvalidOperationException. That is why these
tests looked like "the engine stopped throwing" — it never stopped, the assertion was just wrong
about the type. Assert the concrete exception the engine actually raises.
"""
import os
import sys

TESTS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'RoslynSentinel.Tests')

EDITS = [
    ('BatteryEightTests.cs',
     '''    public async Task GetCodeInventory_UnknownFile_ReportsWithoutThrowing()
    {
        SetSource("public class Foo { }");

        var result = await _engine.GetCodeInventoryAsync("NonExistent.cs");

        Assert.That(result, Is.Not.Null,
            "Engines return an empty report for an unknown file rather than throwing.");
    }''',
     '''    public void GetCodeInventory_UnknownFile_ThrowsFileNotFound()
    {
        SetSource("public class Foo { }");

        Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await _engine.GetCodeInventoryAsync("NonExistent.cs"));
    }'''),

    ('BatteryTenTests.cs',
     '''    public async Task GetFileDiagnostics_UnknownFile_ReportsWithoutThrowing()
    {
        var result = await _engine.GetFileDiagnosticsAsync("DoesNotExist.cs");

        Assert.That(result, Is.Not.Null,
            "Engines wrap the not-found case in EngineResultWrapper rather than throwing.");
    }''',
     '''    public void GetFileDiagnostics_UnknownFile_ThrowsFileNotFound()
    {
        Assert.ThrowsAsync<FileNotFoundException>(
            () => _engine.GetFileDiagnosticsAsync("DoesNotExist.cs"));
    }'''),

    ('BatterySeventeenTests.cs',
     '''    public async Task FixMismatchedNamespaces_UnknownFile_ReportsWithoutThrowing()
    {
        var result = await _engine.FixMismatchedNamespacesAsync("NoSuchFile.cs");

        Assert.That(result.Outcome, Is.Not.EqualTo(EditOutcome.Modified),
            "Engines report not-found through Outcome instead of throwing.");
    }''',
     '''    public void FixMismatchedNamespaces_UnknownFile_ThrowsFileNotFound()
    {
        Assert.ThrowsAsync<FileNotFoundException>(
            async () => await _engine.FixMismatchedNamespacesAsync("NoSuchFile.cs"));
    }'''),

    ('BatteryFiveTests.cs',
     '''    public async Task FindUnsafeTypeCasts_FileNotFound_ThrowsException()
    {
        SetSource("public class C { }", "Test.cs");

        var result = await _engine.FindUnsafeTypeCastsAsync("NonExistent.cs");

        Assert.That(result, Is.Empty,
            "Engines return an empty list for an unknown file rather than throwing.");
    }''',
     '''    public void FindUnsafeTypeCasts_FileNotFound_ThrowsFileNotFound()
    {
        SetSource("public class C { }", "Test.cs");

        Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await _engine.FindUnsafeTypeCastsAsync("NonExistent.cs"));
    }'''),

    ('BatterySixTests.cs',
     '''    public async Task GenerateXmlDocStubs_FileNotFound_ThrowsException()
    {
        SetSource("public class C { }", "Test.cs");

        var result = await _engine.GenerateXmlDocumentationStubsAsync("Missing.cs");

        Assert.That(result.Outcome, Is.Not.EqualTo(EditOutcome.Modified),
            "Engines report not-found through Outcome instead of throwing.");
    }''',
     '''    public void GenerateXmlDocStubs_FileNotFound_ThrowsFileNotFound()
    {
        SetSource("public class C { }", "Test.cs");

        Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await _engine.GenerateXmlDocumentationStubsAsync("Missing.cs"));
    }'''),

    # These three assert a return value but the engine throws InvalidOperationException.
    ('BatteryEighteenTests.cs',
     '''    public async Task SortAndDeduplicateUsings_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.SortAndDeduplicateUsingsAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }''',
     '''    public void SortAndDeduplicateUsings_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _msEngine.SortAndDeduplicateUsingsAsync("NonExistent.cs"));
    }'''),

    ('BatteryTwentyThreeTests.cs',
     '''    public async Task RemoveConfigureAwaitFalse_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _asyncOptimizationEngine.RemoveConfigureAwaitFalseAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }''',
     '''    public void RemoveConfigureAwaitFalse_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _asyncOptimizationEngine.RemoveConfigureAwaitFalseAsync("NonExistent.cs"));
    }'''),

    ('BatteryNineteenTests.cs',
     '''    public async Task GenerateAsyncOverload_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _asyncOptimizationEngine.GenerateAsyncOverloadAsync("NonExistent.cs", "GetData");
        Assert.That(result, Is.Null.Or.Empty);
    }''',
     '''    public void GenerateAsyncOverload_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _asyncOptimizationEngine.GenerateAsyncOverloadAsync("NonExistent.cs", "GetData"));
    }'''),
]


def main():
    """Match against an LF-normalized copy, then write back the file's dominant ending.

    Several test files are CRLF; comparing them against LF-only literals silently matched
    nothing, which is what made an earlier run look like the code had changed.
    """
    missing = []
    cache = {}
    crlf = {}
    for name, old, new in EDITS:
        path = os.path.join(TESTS, name)
        if path not in cache:
            raw = open(path, 'rb').read()
            crlf[path] = raw.count(b'\r\n') * 2 >= raw.count(b'\n')
            cache[path] = raw.decode('utf-8').replace('\r\n', '\n')
        if old not in cache[path]:
            missing.append('%s :: %s' % (name, old.strip().splitlines()[0][:90]))
            continue
        cache[path] = cache[path].replace(old, new, 1)
    for path, src in cache.items():
        if crlf[path]:
            src = src.replace('\n', '\r\n')
        open(path, 'wb').write(src.encode('utf-8'))
    print('applied %d/%d' % (len(EDITS) - len(missing), len(EDITS)))
    for m in missing:
        print('  NOT MATCHED: ' + m)
    return 1 if missing else 0


if __name__ == '__main__':
    sys.exit(main())
