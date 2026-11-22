# Regression check for Get-TestRunTimings.ps1 (Runs and Projects views, run selection, source scan, JSON).
#
# Run:  pwsh -NoProfile -File scripts\Get-TestRunTimings.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as Get-ScenarioSummary.Tests.ps1. Builds canned result dirs (summary.json + TRX) and a
# canned test source tree in a THROWAWAY temp folder; it never reads the real results folder.
# Note: dot-sourcing the script defines its parameters ($Run, $Last, $View, $Json, ...) in this scope, so this file
# uses other variable names and always passes arguments to the functions explicitly.

$ErrorActionPreference = 'Stop'
$scriptUnderTest = Join-Path $PSScriptRoot 'Get-TestRunTimings.ps1'
. $scriptUnderTest

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

function Test-Near([double]$Actual, [double]$Expected) { return [math]::Abs($Actual - $Expected) -lt 0.051 }

# ---------------------------------------------------------------- canned data builders

function New-CannedRunDir([string]$Root, [string]$Name, [hashtable]$Summary, [datetime]$MtimeUtc) {
    $dir = Join-Path $Root $Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $path = Join-Path $dir 'summary.json'
    ConvertTo-Json -InputObject $Summary -Depth 6 | Set-Content -LiteralPath $path -Encoding utf8
    [System.IO.File]::SetLastWriteTimeUtc($path, $MtimeUtc)
    return $dir
}

function New-CannedProject([string]$Project, [int]$Total) {
    return @{ project = $Project; total = $Total; passed = $Total; failed = 0; skipped = 0; failures = @(); skippedTests = @(); note = ''; exitCode = 0 }
}

# Tests: hashtables with Class, Name, S and E (seconds after $Base). Times are written with $Base's own UTC offset.
function New-CannedTrx([string]$Path, [datetimeoffset]$Base, [double]$RunStartSec, [double]$RunFinishSec, [hashtable[]]$Tests) {
    $ns = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
    $results = New-Object System.Text.StringBuilder
    $defs = New-Object System.Text.StringBuilder
    $i = 0
    foreach ($t in $Tests) {
        $i++
        $id = [guid]::NewGuid().ToString()
        $dur = [timespan]::FromSeconds($t.E - $t.S).ToString('c', [cultureinfo]::InvariantCulture)
        [void]$results.AppendLine("    <UnitTestResult executionId=`"$([guid]::NewGuid())`" testId=`"$id`" testName=`"$($t.Name)`" duration=`"$dur`" startTime=`"$($Base.AddSeconds($t.S).ToString('o'))`" endTime=`"$($Base.AddSeconds($t.E).ToString('o'))`" outcome=`"Passed`" />")
        [void]$defs.AppendLine("    <UnitTest name=`"$($t.Name)`" id=`"$id`"><TestMethod codeBase=`"x.dll`" className=`"$($t.Class)`" name=`"$($t.Name)`" /></UnitTest>")
    }
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun id="$([guid]::NewGuid())" name="canned" xmlns="$ns">
  <Times creation="$($Base.ToString('o'))" start="$($Base.AddSeconds($RunStartSec).ToString('o'))" finish="$($Base.AddSeconds($RunFinishSec).ToString('o'))" />
  <Results>
$($results.ToString())  </Results>
  <TestDefinitions>
$($defs.ToString())  </TestDefinitions>
</TestRun>
"@
    Set-Content -LiteralPath $Path -Value $xml -Encoding utf8
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-testruntimings-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
    $resultsDir = Join-Path $tempRoot 'results'
    $srcDir = Join-Path $tempRoot 'src'
    New-Item -ItemType Directory -Path $resultsDir, $srcDir -Force | Out-Null

    # ---- source tree
    $one = Join-Path $srcDir 'Proj.One'
    New-Item -ItemType Directory -Path $one, (Join-Path $one 'obj'), (Join-Path $one 'sub') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $one 'Fixtures.cs') -Encoding utf8 -Value @'
using NUnit.Framework;
namespace Ns;

[TestFixture]
[Category("OutOfProcess")]
public class FooTests
{
    [Test] public void A() { }
}

public class PlainTests
{
    [Test] public void C() { }
}
'@
    Set-Content -LiteralPath (Join-Path $one 'sub\Ratchet.cs') -Encoding utf8 -Value @'
namespace Ns;

[TestFixture]
// a comment between the attributes

[Category("Ratchet")]
[Description("x")]
public class RatchetTests
{
    [Test] public void B() { }
}
'@
    # build output must be skipped: if it were scanned, PlainTests would become Ratchet
    Set-Content -LiteralPath (Join-Path $one 'obj\Gen.cs') -Encoding utf8 -Value @'
namespace Ns;
[Category("Ratchet")]
public class PlainTests { }
'@
    # a directory that only starts with the project name must not be scanned
    $decoy = Join-Path $srcDir 'Proj.One.Extra'
    New-Item -ItemType Directory -Path $decoy -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $decoy 'Decoy.cs') -Encoding utf8 -Value @'
namespace Ns;
[Category("OutOfProcess")]
public class PlainTests { }
'@
    $two = Join-Path $srcDir 'Proj.Two'
    New-Item -ItemType Directory -Path $two -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $two 'Fixtures2.cs') -Encoding utf8 -Value @'
using NUnit.Framework;
namespace Ns
{
    [TestFixture, Category("Ratchet")] public class SharedBracketTests
    {
        [Test] public void D() { var s = "{ not a brace"; }
    }

    [TestFixture]
    public class MethodOnlyTests
    {
        [Test, Category("OutOfProcess")]
        public void E() { }
    }

    public class AfterMethodTests { }

    public class Outer
    {
        [Category("OutOfProcess")]
        public class Inner { }
    }
}
'@

    # ---- runs. Windows: run1 10:00:00-10:00:30Z, run2 10:00:20-10:01:00Z (written in UTC-7), runs 3-5 from summary.json mtime.
    $base1 = [datetimeoffset]::new(2026, 1, 1, 10, 0, 0, [timespan]::Zero)
    $base2 = [datetimeoffset]::new(2026, 1, 1, 3, 0, 20, [timespan]::FromHours(-7))   # = 10:00:20Z

    $run1 = New-CannedRunDir $resultsDir 'aaaa1111-20260101-100000-1' @{
        commit = ('aaaa1111' + '0' * 32); subject = 'one'; worktree = 'w'; filter = 'TestCategory!=OutOfProcess'; seconds = 60
        projects = @(New-CannedProject 'Proj.One' 3)
    } ([datetime]::new(2026, 1, 1, 11, 0, 0, [DateTimeKind]::Utc))
    New-CannedTrx (Join-Path $run1 'Proj.One.trx') $base1 0 30 @(
        @{ Class = 'Ns.FooTests'; Name = 'A'; S = 0; E = 10 },
        @{ Class = 'Ns.RatchetTests'; Name = 'B'; S = 5; E = 15 },
        @{ Class = 'Ns.PlainTests'; Name = 'C'; S = 20; E = 25 })

    $run2 = New-CannedRunDir $resultsDir 'bbbb2222-20260101-100020-2' @{
        commit = ('bbbb2222' + '0' * 32); subject = 'two'; worktree = 'w'; filter = ''; seconds = 40
        projects = @(New-CannedProject 'Proj.Two' 4)
    } ([datetime]::new(2026, 1, 1, 11, 1, 0, [DateTimeKind]::Utc))
    # D 0-4, E 2-6, F 10-12, G 12-14: G starts exactly when F ends (an end sorts before a start at a tie)
    New-CannedTrx (Join-Path $run2 'Proj.Two.trx') $base2 0 40 @(
        @{ Class = 'Ns.SharedBracketTests'; Name = 'D'; S = 0; E = 4 },
        @{ Class = 'Ns.MethodOnlyTests'; Name = 'E'; S = 2; E = 6 },
        @{ Class = 'Ns.Outer+Inner'; Name = 'F'; S = 10; E = 12 },
        @{ Class = 'Ns.AfterMethodTests'; Name = 'G'; S = 12; E = 14 })

    # run3: full-looking (empty filter, 9 projects), summary only, no TRX
    $nineProjects = @(1..9 | ForEach-Object { New-CannedProject "Proj.Nine$_" 1 })
    New-CannedRunDir $resultsDir 'cccc3333-20260101-115820-3' @{
        commit = ('cccc3333' + '0' * 32); subject = 'three'; worktree = 'w'; filter = ''; seconds = 100; projects = $nineProjects
    } ([datetime]::new(2026, 1, 1, 12, 0, 0, [DateTimeKind]::Utc)) | Out-Null
    # run4: filtered, no TRX; run5: explicit suite field, 8 projects (below the FullOnly threshold)
    New-CannedRunDir $resultsDir 'dddd4444-20260101-125955-4' @{
        commit = ('dddd4444' + '0' * 32); subject = 'four'; worktree = 'w'; filter = 'FullyQualifiedName~Foo'; seconds = 5
        projects = @(New-CannedProject 'Proj.One' 1)
    } ([datetime]::new(2026, 1, 1, 13, 0, 0, [DateTimeKind]::Utc)) | Out-Null
    New-CannedRunDir $resultsDir 'eeee5555-20260101-135955-5' @{
        commit = ('eeee5555' + '0' * 32); subject = 'five'; worktree = 'w'; filter = ''; seconds = 5; suite = 'smoke'
        projects = @(1..8 | ForEach-Object { New-CannedProject "Proj.Eight$_" 1 })
    } ([datetime]::new(2026, 1, 1, 14, 0, 0, [DateTimeKind]::Utc)) | Out-Null
    # a dir without summary.json is ignored
    New-Item -ItemType Directory -Path (Join-Path $resultsDir 'ffff6666-empty') -Force | Out-Null

    # ---------------------------------------------------------------- loader and selection
    $allRuns = @(Get-TestRunInfo -ResultsRoot $resultsDir)
    Assert-That ($allRuns.Count -eq 5) 'loader reads the 5 dirs with a summary.json and ignores the empty dir'
    Assert-That (($allRuns | ForEach-Object Name) -join ',' -eq 'aaaa1111-20260101-100000-1,bbbb2222-20260101-100020-2,cccc3333-20260101-115820-3,dddd4444-20260101-125955-4,eeee5555-20260101-135955-5') 'runs come back oldest first by summary.json mtime'
    Assert-That (($allRuns | ForEach-Object Suite) -join ',' -eq 'fast,full,full,filtered,smoke') 'suite: fast / full / full / filtered / explicit summary suite wins'

    $byPrefix = @(Select-TestRuns -Runs $allRuns -Run 'bbbb')
    Assert-That ($byPrefix.Count -eq 1 -and $byPrefix[0].Commit8 -eq 'bbbb2222') '-Run matches a dir-name prefix'
    Assert-That (@(Select-TestRuns -Runs $allRuns -Run 'zzzz').Count -eq 0) '-Run with no match selects nothing'
    $lastTwo = @(Select-TestRuns -Runs $allRuns -Last 2)
    Assert-That (($lastTwo | ForEach-Object Commit8) -join ',' -eq 'dddd4444,eeee5555') '-Last 2 = the two newest by mtime, oldest first'
    $fullOnlyRuns = @(Select-TestRuns -Runs $allRuns -FullOnly)
    Assert-That ($fullOnlyRuns.Count -eq 1 -and $fullOnlyRuns[0].Commit8 -eq 'cccc3333') '-FullOnly keeps only empty filter AND 9+ projects'

    # ---------------------------------------------------------------- Runs view
    $runRows = @(Get-RunsView -AllRuns $allRuns -SelectedRuns $allRuns)
    Assert-That ($runRows.Count -eq 5) 'Runs view: one row per run'
    $r1 = $runRows[0]; $r2 = $runRows[1]; $r3 = $runRows[2]
    Assert-That ($r1.dir -eq 'aaaa1111-20260101-100000-1' -and $r1.commit8 -eq 'aaaa1111' -and $r1.filter -eq 'TestCategory!=OutOfProcess' -and $r1.suite -eq 'fast') 'Runs view: dir, commit8, filter, suite'
    Assert-That ($r1.seconds -eq 60 -and $r1.projects -eq 1 -and $r1.total -eq 3 -and $r1.failed -eq 0) 'Runs view: seconds, project count, total, failed'
    Assert-That ($r1.windowSource -eq 'trx' -and $r3.windowSource -eq 'mtime') 'Runs view: window from TRX Times, mtime fallback without TRX'
    Assert-That ($r1.overlappingRuns -eq 1 -and $r2.overlappingRuns -eq 1) 'Runs view: run1 and run2 windows overlap each other (UTC offsets normalised)'
    Assert-That ((@($runRows | Select-Object -Skip 2 | ForEach-Object overlappingRuns) | Where-Object { $_ -ne 0 }).Count -eq 0) 'Runs view: the mtime-window runs overlap nothing'
    $oneRow = @(Get-RunsView -AllRuns $allRuns -SelectedRuns $byPrefix)
    Assert-That ($oneRow.Count -eq 1 -and $oneRow[0].overlappingRuns -eq 1) 'Runs view: overlap counts against ALL runs under the root, not only the selected ones'

    # ---------------------------------------------------------------- sweep line
    $T = [System.TimeSpan]::TicksPerSecond
    $sweep = Measure-SerialTimeline -Starts @(0, (5 * $T), (20 * $T)) -Ends @((10 * $T), (15 * $T), (25 * $T)) -Category @(0, 1, 2)
    Assert-That ($sweep.WallTicks -eq 25 * $T -and $sweep.IdleTicks -eq 5 * $T -and $sweep.ConcurrentTicks -eq 5 * $T -and $sweep.SerialTicks -eq 20 * $T) 'sweep: A 0-10, B 5-15, C 20-25 gives wall 25, idle 5, concurrent 5, serial 20'
    Assert-That ($sweep.SingleTicks[0] -eq 5 * $T -and $sweep.SingleTicks[1] -eq 5 * $T -and $sweep.SingleTicks[2] -eq 5 * $T) 'sweep: exactly-one time is attributed to the category of the single running test'
    $tie = Measure-SerialTimeline -Starts @(0, (10 * $T), (3 * $T)) -Ends @((10 * $T), (20 * $T), (3 * $T)) -Category @(2, 2, 2)
    Assert-That ($tie.SerialTicks -eq 20 * $T -and $tie.ConcurrentTicks -eq 0) 'sweep: back-to-back tests (end == next start) are never concurrent; a zero-length test adds nothing'
    $none = Measure-SerialTimeline -Starts @() -Ends @() -Category @()
    Assert-That ($none.WallTicks -eq 0 -and $none.SerialTicks -eq 0) 'sweep: no tests gives zeros'

    # ---------------------------------------------------------------- source scan
    $scan1 = Get-ProjectClassCategories -SourceRoot $srcDir -Project 'Proj.One'
    Assert-That ($scan1.Found) 'scan: finds the project dir by exact name'
    Assert-That ((Resolve-ClassCategoryIndex -Scan $scan1 -ClassName 'Ns.FooTests') -eq 0) 'scan: [TestFixture] [Category("OutOfProcess")] above a class -> OutOfProcess'
    Assert-That ((Resolve-ClassCategoryIndex -Scan $scan1 -ClassName 'Ns.RatchetTests') -eq 1) 'scan: Category separated from the class by a comment, blank line and another attribute -> Ratchet'
    Assert-That ((Resolve-ClassCategoryIndex -Scan $scan1 -ClassName 'Ns.PlainTests') -eq 2) 'scan: the attribute binds to the class directly below, not the next class; obj/ and sibling dirs are ignored'
    Assert-That ((Resolve-ClassCategoryIndex -Scan $scan1 -ClassName 'Other.Ns.FooTests') -eq 0) 'scan: falls back to the short class name when the full name is unknown'
    Assert-That ((Resolve-ClassCategoryIndex -Scan $scan1 -ClassName 'Ns.Unknown') -eq 2) 'scan: unknown class is not attributed'
    $scan2 = Get-ProjectClassCategories -SourceRoot $srcDir -Project 'Proj.Two'
    Assert-That ((Resolve-ClassCategoryIndex -Scan $scan2 -ClassName 'Ns.SharedBracketTests') -eq 1) 'scan: [TestFixture, Category("Ratchet")] sharing one bracket list on the class line'
    Assert-That ((Resolve-ClassCategoryIndex -Scan $scan2 -ClassName 'Ns.MethodOnlyTests') -eq 2 -and $scan2.MethodLevelTags -eq 1) 'scan: a method-level Category is not attributed to the class, and is counted'
    Assert-That ((Resolve-ClassCategoryIndex -Scan $scan2 -ClassName 'Ns.AfterMethodTests') -eq 2) 'scan: a method-level Category does not leak onto the next class'
    Assert-That ((Resolve-ClassCategoryIndex -Scan $scan2 -ClassName 'Ns.Outer+Inner') -eq 0 -and (Resolve-ClassCategoryIndex -Scan $scan2 -ClassName 'Ns.Outer') -eq 2) 'scan: nested class matches by Ns.Outer+Inner and the outer class stays unattributed'
    $scanMissing = Get-ProjectClassCategories -SourceRoot $srcDir -Project 'Proj.Missing'
    Assert-That (-not $scanMissing.Found) 'scan: a project without a source dir reports Found = false'

    # ---------------------------------------------------------------- Projects view
    $proj1 = @(Get-ProjectsView -SelectedRuns @($allRuns[0]) -SourceRoot $srcDir)
    Assert-That ($proj1.Count -eq 2 -and $proj1[1].project -eq '(total)') 'Projects view: one row per project plus a (total) row'
    $p = $proj1[0]
    Assert-That ($p.project -eq 'Proj.One' -and $p.tests -eq 3) 'Projects view: project name and test count'
    Assert-That ((Test-Near $p.wallSec 25) -and (Test-Near $p.testSec 25) -and (Test-Near $p.serialSec 20) -and (Test-Near $p.idleSec 5)) 'Projects view: wall 25, test-seconds 25, serial 20, idle 5 (hand-computed)'
    Assert-That ($p.oopTests -eq 1 -and (Test-Near $p.oopTestSec 10) -and (Test-Near $p.oopSerialSec 5)) 'Projects view: OutOfProcess split (1 test, 10 s, serial 5)'
    Assert-That ($p.ratchetTests -eq 1 -and (Test-Near $p.ratchetTestSec 10) -and (Test-Near $p.ratchetSerialSec 5)) 'Projects view: Ratchet split (1 test, 10 s, serial 5)'
    Assert-That ($p.otherTests -eq 1 -and (Test-Near $p.otherTestSec 5) -and (Test-Near $p.otherSerialSec 5)) 'Projects view: not-attributed split (1 test, 5 s, serial 5)'
    Assert-That ((Test-Near ($p.idleSec + $p.oopSerialSec + $p.ratchetSerialSec + $p.otherSerialSec) $p.serialSec)) 'Projects view: category serial seconds plus idle partition the headline serial'
    $proj2 = @(Get-ProjectsView -SelectedRuns @($allRuns[1]) -SourceRoot $srcDir)
    $q = $proj2[0]
    Assert-That ($q.tests -eq 4 -and (Test-Near $q.wallSec 14) -and (Test-Near $q.testSec 12) -and (Test-Near $q.serialSec 12) -and (Test-Near $q.idleSec 4)) 'Projects view: run2 wall 14, test-seconds 12, serial 12, idle 4 (UTC-7 timestamps, tie handled)'
    Assert-That ($q.oopTests -eq 1 -and (Test-Near $q.oopSerialSec 2) -and $q.ratchetTests -eq 1 -and (Test-Near $q.ratchetSerialSec 2) -and $q.otherTests -eq 2 -and (Test-Near $q.otherTestSec 6) -and (Test-Near $q.otherSerialSec 4)) 'Projects view: run2 category split'
    Assert-That ($q.methodTagsInSource -eq 1) 'Projects view: methodTagsInSource counts the method-level Category'
    $tot = $proj1[1]
    Assert-That ($tot.tests -eq 3 -and (Test-Near $tot.wallSec 25) -and (Test-Near $tot.testSec 25) -and (Test-Near $tot.serialSec 20)) '(total) row sums the project figures'
    $projNoTrx = @(Get-ProjectsView -SelectedRuns @($allRuns[3]) -SourceRoot $srcDir)
    Assert-That ($projNoTrx[0].tests -eq 1 -and $projNoTrx[0].wallSec -eq 0 -and $projNoTrx[0].note -like 'no TRX*') 'Projects view: a project without a TRX falls back to the summary count and says so'

    # ---------------------------------------------------------------- JSON and the invoked script
    $jsonText = (Invoke-TestRunTimings -ResultsRoot $resultsDir -Run 'aaaa' -View Projects -Json -SourceRoot $srcDir) -join "`n"
    $parsed = @($jsonText | ConvertFrom-Json)
    Assert-That ($parsed.Count -eq 2 -and $parsed[0].project -eq 'Proj.One' -and $parsed[0].tests -eq 3) '-Json (Projects) emits parseable JSON rows'
    $runsJson = @(((Invoke-TestRunTimings -ResultsRoot $resultsDir -Last 1 -View Runs -Json -SourceRoot $srcDir) -join "`n") | ConvertFrom-Json)
    Assert-That ($runsJson.Count -eq 1 -and $runsJson[0].commit8 -eq 'eeee5555') '-Json (Runs) with one row is still a JSON array'

    $table = (Invoke-TestRunTimings -ResultsRoot $resultsDir -Run 'aaaa' -View Projects -SourceRoot $srcDir) -join "`n"
    Assert-That ($table -match 'Proj\.One' -and $table -match 'otherSerialSec' -and $table -match 'methodTagsInSource') 'table output lists every column (not cut at ten)'

    $childOut = & pwsh -NoProfile -File $scriptUnderTest -ResultsRoot $resultsDir -Run bbbb -View Projects -Json -SourceRoot $srcDir
    $childExit = $LASTEXITCODE
    $childRows = @(($childOut -join "`n") | ConvertFrom-Json)
    Assert-That ($childExit -eq 0 -and $childRows.Count -eq 2 -and $childRows[0].project -eq 'Proj.Two') 'invoked as a script: main body runs and prints JSON'
    $notYet = & pwsh -NoProfile -File $scriptUnderTest -ResultsRoot $resultsDir -View Fixtures -SourceRoot $srcDir 2>&1
    Assert-That ($LASTEXITCODE -eq 1 -and ($notYet -join ' ') -match 'not implemented yet \(Step 1b\)') '-View Fixtures is accepted and reports not implemented yet (Step 1b), exit 1'
    $notYet = & pwsh -NoProfile -File $scriptUnderTest -ResultsRoot $resultsDir -View Failures -SourceRoot $srcDir 2>&1
    Assert-That ($LASTEXITCODE -eq 1 -and ($notYet -join ' ') -match 'not implemented yet \(Step 1b\)') '-View Failures is accepted and reports not implemented yet (Step 1b), exit 1'
    $noMatch = & pwsh -NoProfile -File $scriptUnderTest -ResultsRoot $resultsDir -Run nomatch -SourceRoot $srcDir 2>&1
    Assert-That ($LASTEXITCODE -eq 1 -and ($noMatch -join ' ') -match 'No runs matched') 'no matching run: message and exit 1'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) FAILED"
    exit 1
}
Write-Host 'All checks passed'
exit 0
