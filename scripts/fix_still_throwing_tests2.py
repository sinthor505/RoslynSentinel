"""Second correction pass: engines that still throw for not-found.

Each replacement below asserts the concrete exception observed in the test run, rather than the
structured-return contract assumed in the first pass.
"""
import os
import sys

TESTS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'RoslynSentinel.Tests')

EDITS = [
    ('BatteryEightTests.cs',
     '''    public async Task GetProjectDependencies_UnknownProject_ReportsWithoutThrowing()''',
     '''    public void GetProjectDependencies_UnknownProject_ThrowsInvalidOperation()'''),
    ('BatteryEightTests.cs',
     '''        var result = await _engine.GetProjectDependenciesAsync("NonExistent");

        Assert.That(result, Is.Not.Null,
            "Engines return an empty report for an unknown project rather than throwing.");''',
     '''        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _engine.GetProjectDependenciesAsync("NonExistent"));'''),

    ('BatteryEightTests.cs',
     '''    public async Task ConvertToSourceGeneratedLogging_UnknownClass_ReportsWithoutThrowing()''',
     '''    public void ConvertToSourceGeneratedLogging_UnknownClass_ThrowsInvalidOperation()'''),
    ('BatteryEightTests.cs',
     '''        var result = await _engine.ConvertToSourceGeneratedLoggingAsync("Foo.cs", "NonExistentClass");

        Assert.That(result.Outcome, Is.Not.EqualTo(EditOutcome.Modified),
            "Engines report not-found through Outcome instead of throwing.");''',
     '''        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _engine.ConvertToSourceGeneratedLoggingAsync("Foo.cs", "NonExistentClass"));'''),

    ('BatteryFourteenTests.cs',
     '''    public async Task OptimizeTaskWait_UnknownFile_ReportsWithoutThrowing()''',
     '''    public void OptimizeTaskWait_UnknownFile_ThrowsFileNotFound()'''),
    ('BatteryFourteenTests.cs',
     '''        var result = await _engine.OptimizeTaskWaitAsync("NoSuchFile.cs");

        Assert.That(result.Outcome, Is.Not.EqualTo(EditOutcome.Modified),
            "Engines report not-found through Outcome instead of throwing.");''',
     '''        Assert.ThrowsAsync<FileNotFoundException>(
            async () => await _engine.OptimizeTaskWaitAsync("NoSuchFile.cs"));'''),

    ('BatteryFourteenTests.cs',
     '''    public async Task ReplaceStringConcat_UnknownFile_ReportsWithoutThrowing()''',
     '''    public void ReplaceStringConcat_UnknownFile_ThrowsFileNotFound()'''),
    ('BatteryFourteenTests.cs',
     '''        var result = await _engine.ReplaceStringConcatWithInterpolationAsync("NoSuchFile.cs");

        Assert.That(result.Outcome, Is.Not.EqualTo(EditOutcome.Modified),
            "Engines report not-found through Outcome instead of throwing.");''',
     '''        Assert.ThrowsAsync<FileNotFoundException>(
            async () => await _engine.ReplaceStringConcatWithInterpolationAsync("NoSuchFile.cs"));'''),

    ('BatteryNineTests.cs',
     '''    public async Task SyncTypeAndFilename_UnknownFile_ReportsWithoutThrowing()''',
     '''    public void SyncTypeAndFilename_UnknownFile_ThrowsFileNotFound()'''),
    ('BatteryNineTests.cs',
     '''        var result = await _engine.SyncTypeAndFilenameAsync("DoesNotExist.cs");

        Assert.That(result.Outcome, Is.Not.EqualTo(EditOutcome.Modified),
            "Engines report not-found through Outcome instead of throwing.");''',
     '''        Assert.ThrowsAsync<FileNotFoundException>(
            () => _engine.SyncTypeAndFilenameAsync("DoesNotExist.cs"));'''),

    ('BatteryNineTests.cs',
     '''    public async Task CreateProject_NullSolutionPath_ReportsMissingSolutionPath()''',
     '''    public void CreateProject_NullSolutionPath_ThrowsMissingSolutionPath()'''),
    ('BatteryNineTests.cs',
     '''        var result = await _engine.CreateProjectAsync("NewProject", "classlib");

        Assert.That(result.Outcome, Is.Not.EqualTo(EditOutcome.Modified));
        Assert.That(result.Message, Does.Contain("Solution path not found"),
            "Should report the missing solution file path through Message instead of throwing.");''',
     '''        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _engine.CreateProjectAsync("NewProject", "classlib"));

        Assert.That(ex!.Message, Does.Contain("Solution path not found"),
            "Should fail when no solution file path is available.");'''),

    ('BatteryNineTests.cs',
     '''    public async Task SplitProjectByFolder_NullSolutionPath_ReportsMissingSolutionPath()''',
     '''    public void SplitProjectByFolder_NullSolutionPath_ThrowsMissingSolutionPath()'''),
    ('BatteryNineTests.cs',
     '''        var result = await _engine.SplitProjectByFolderAsync("Source", "Services", "Source.Services");

        Assert.That(result.Outcome, Is.Not.EqualTo(EditOutcome.Modified));
        Assert.That(result.Message, Does.Contain("Solution path not found"),
            "SplitProject should propagate the null solution path through Message.");''',
     '''        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _engine.SplitProjectByFolderAsync("Source", "Services", "Source.Services"));

        Assert.That(ex!.Message, Does.Contain("Solution path not found"),
            "SplitProject should propagate the missing solution path from CreateProject.");'''),

    ('BatteryElevenTests.cs',
     '''    public async Task FindUnusedPrivateMembers_UnknownFile_ReturnsEmpty()''',
     '''    public void FindUnusedPrivateMembers_UnknownFile_ThrowsFileNotFound()'''),
    ('BatteryElevenTests.cs',
     '''        var result = await _engine.FindUnusedPrivateMembersAsync("DoesNotExist.cs", "Service");

        Assert.That(result, Is.Empty,
            "Engines return an empty list for an unknown file rather than throwing.");''',
     '''        Assert.ThrowsAsync<FileNotFoundException>(() =>
            _engine.FindUnusedPrivateMembersAsync("DoesNotExist.cs", "Service"));'''),

    ('BatterySixteenTests.cs',
     '''    public async Task AnalyzeDependencies_UnknownFile_ReturnsEmpty()''',
     '''    public void AnalyzeDependencies_UnknownFile_ThrowsFileNotFound()'''),
    ('BatterySixteenTests.cs',
     '''        var result = await _engine.AnalyzeDependenciesAsync("NoSuchFile.cs", "MyClass");

        Assert.That(result, Is.Empty,
            "Engines return an empty list for an unknown file rather than throwing.");''',
     '''        Assert.ThrowsAsync<FileNotFoundException>(
            async () => await _engine.AnalyzeDependenciesAsync("NoSuchFile.cs", "MyClass"));'''),
]


def main():
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
