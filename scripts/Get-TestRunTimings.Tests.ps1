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
    Assert-That ($none.WallTicks -eq 0 -and $none.SerialTicks -eq 0 -and $null -eq $none.TailStartTick) 'sweep: no tests gives zeros and no tail start'
    Assert-That ($sweep.TailStartTick -eq 10 * $T) 'sweep: tail starts at the last 2+ -> 1 transition (B and A overlap until A ends at 10)'
    Assert-That ($tie.TailStartTick -eq 0) 'sweep: concurrency never above 1 -> the tail starts at the first tick'

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
    $childFail = & pwsh -NoProfile -File $scriptUnderTest -ResultsRoot $resultsDir -View Failures -Json -SourceRoot $srcDir | ConvertFrom-Json
    Assert-That ($LASTEXITCODE -eq 0 -and $childFail.summary.summaryLine -eq 'red full runs: 0 of 1') 'invoked as a script: -View Failures runs (no stub) and prints a JSON summary'
    $noMatch = & pwsh -NoProfile -File $scriptUnderTest -ResultsRoot $resultsDir -Run nomatch -SourceRoot $srcDir 2>&1
    Assert-That ($LASTEXITCODE -eq 1 -and ($noMatch -join ' ') -match 'No runs matched') 'no matching run: message and exit 1'

    # ---------------------------------------------------------------- Failures view (own results root; summary.json only)
    function New-CannedFailureRun([string]$Root, [string]$Name, [datetime]$MtimeUtc, [string]$FilterText, [int]$ProjectCount, [hashtable]$FailuresByProject) {
        $projects = @(1..$ProjectCount | ForEach-Object {
                $pn = "Proj.$([char](64 + $_))"
                $list = @()
                if ($FailuresByProject.ContainsKey($pn)) { $list = @($FailuresByProject[$pn] | ForEach-Object { @{ test = $_[0]; message = $_[1] } }) }
                $entry = New-CannedProject $pn 5
                $entry.failures = $list; $entry.failed = $list.Count; $entry.passed = 5 - $list.Count
                $entry
            })
        New-CannedRunDir $Root $Name @{ commit = ($Name.Substring(0, 8) + '0' * 32); subject = 's'; worktree = 'w'; filter = $FilterText; seconds = 10; projects = $projects } $MtimeUtc | Out-Null
    }
    $failRoot = Join-Path $tempRoot 'failresults'
    New-Item -ItemType Directory -Path $failRoot -Force | Out-Null
    $day = [datetime]::new(2026, 2, 1, 0, 0, 0, [DateTimeKind]::Utc)
    $assertMsg = 'Assert.That(a, Is.True)'
    $ioMsg = "System.IO.IOException: busy`nsecond line"
    $timeoutMsg = 'System.TimeoutException: timed out'
    # five full runs by mtime F1..F5 plus one filtered red run N1 between F3 and F4
    New-CannedFailureRun $failRoot 'f1f1f1f1-1' $day.AddHours(10) '' 9 @{ 'Proj.A' = @(, @('T1', $assertMsg)); 'Proj.B' = @(, @('T4', $timeoutMsg)) }
    New-CannedFailureRun $failRoot 'f2f2f2f2-2' $day.AddHours(11) '' 9 @{ 'Proj.A' = @(, @('T1', $assertMsg)); 'Proj.B' = @(, @('T2', $ioMsg)) }
    New-CannedFailureRun $failRoot 'f3f3f3f3-3' $day.AddHours(12) '' 9 @{ 'Proj.C' = @(, @('T3', $assertMsg)) }
    New-CannedFailureRun $failRoot 'n1n1n1n1-n' $day.AddHours(12.5) 'Name~T1' 1 @{ 'Proj.A' = @(, @('T1', $assertMsg)) }
    New-CannedFailureRun $failRoot 'f4f4f4f4-4' $day.AddHours(13) '' 9 @{}
    New-CannedFailureRun $failRoot 'f5f5f5f5-5' $day.AddHours(14) '' 9 @{ 'Proj.B' = @(, @('T4', $timeoutMsg)) }
    $failAll = @(Get-TestRunInfo -ResultsRoot $failRoot)

    Assert-That ((Get-FailurePrefix 'Assert.That(a, Is.True)') -eq 'Assert.That' -and (Get-FailurePrefix 'System.IO.IOException: busy') -eq 'System.IO.IOException') 'prefix: text before the first ( or :'
    Assert-That ((Get-FailurePrefix 'plain words only') -eq 'plain words only' -and (Get-FailurePrefix '') -eq '(no message)' -and (Get-FailurePrefix '   ') -eq '(no message)') 'prefix: whole line when no separator, placeholder when empty'
    Assert-That ((Get-FailureFirstLine $ioMsg) -eq 'System.IO.IOException: busy') 'first message line only'

    $fv = Get-FailuresView -AllRuns $failAll -SelectedRuns @(Select-TestRuns -Runs $failAll -FullOnly)
    Assert-That ($fv.summary.summaryLine -eq 'red full runs: 4 of 5' -and $fv.summary.redFullRuns -eq 4 -and $fv.summary.selectedFullRuns -eq 5) 'failures: -FullOnly red full runs = 4 of 5'
    Assert-That ($fv.failures.Count -eq 6) 'failures: one row per failing test of each red run (2 + 2 + 1 + 1)'
    $pc = @{}; foreach ($g in $fv.prefixCounts) { $pc[$g.prefix] = $g.count }
    Assert-That ($pc['Assert.That'] -eq 3 -and $pc['System.TimeoutException'] -eq 2 -and $pc['System.IO.IOException'] -eq 1 -and $fv.prefixCounts[0].prefix -eq 'Assert.That') 'failures: counts grouped by exception-type prefix, largest first'
    function Find-FailureRow($Rows, [string]$RunPrefix, [string]$TestName) { return @($Rows | Where-Object { $_.run.StartsWith($RunPrefix) -and $_.test -eq $TestName })[0] }
    $rowF2T2 = Find-FailureRow $fv.failures 'f2f2' 'T2'
    Assert-That ($rowF2T2.flakeCandidate -and $rowF2T2.runCount -eq 1 -and $rowF2T2.project -eq 'Proj.B' -and $rowF2T2.message -eq 'System.IO.IOException: busy') 'failures: T2 failed only in F2 -> flake candidate, runCount 1, first message line'
    Assert-That ((Find-FailureRow $fv.failures 'f3f3' 'T3').flakeCandidate) 'failures: T3 failed only in F3 (neighbours F2/F4 pass) -> flake candidate'
    $rowF2T1 = Find-FailureRow $fv.failures 'f2f2' 'T1'
    Assert-That ((-not $rowF2T1.flakeCandidate) -and $rowF2T1.runCount -eq 2) 'failures: T1 failed in F1 and F2 -> not a flake candidate for F2, runCount 2'
    Assert-That ((-not (Find-FailureRow $fv.failures 'f1f1' 'T1').flakeCandidate) -and (-not (Find-FailureRow $fv.failures 'f1f1' 'T4').flakeCandidate)) 'failures: the first full run has no previous run -> never a flake candidate'
    $rowF5 = Find-FailureRow $fv.failures 'f5f5' 'T4'
    Assert-That ((-not $rowF5.flakeCandidate) -and $rowF5.runCount -eq 2) 'failures: the last full run has no next run -> not a flake candidate; runCount 2 (F1 and F5)'
    Assert-That ((Find-FailureRow $fv.failures 'f1f1' 'T4').runCount -eq 2) 'failures: runCount is per test across all full runs'

    $fvAll = Get-FailuresView -AllRuns $failAll -SelectedRuns $failAll
    Assert-That ($fvAll.summary.selectedRuns -eq 6 -and $fvAll.summary.redRuns -eq 5 -and $fvAll.summary.redFullRuns -eq 4 -and $fvAll.summary.summaryLine -eq 'red full runs: 4 of 5' -and $fvAll.summary.fullRunsUnderRoot -eq 5) 'failures: without -FullOnly the filtered red run counts as red but not as a full run'
    $rowN1 = Find-FailureRow $fvAll.failures 'n1n1' 'T1'
    Assert-That ((-not $rowN1.fullRun) -and (-not $rowN1.flakeCandidate) -and $rowN1.runCount -eq 2) 'failures: a non-full red run is listed, never a flake candidate, and does not add to runCount'
    Assert-That ((Find-FailureRow $fvAll.failures 'f3f3' 'T3').flakeCandidate) 'failures: a filtered run between two full runs does not become their neighbour'
    $fvLast = Get-FailuresView -AllRuns $failAll -SelectedRuns @(Select-TestRuns -Runs $failAll -Last 2)
    Assert-That ($fvLast.summary.summaryLine -eq 'red full runs: 1 of 2' -and $fvLast.failures.Count -eq 1 -and $fvLast.failures[0].runCount -eq 2) 'failures: -Last 2 selects F4 and F5; flags and counts still use all runs under the root'
    $fvGreen = Get-FailuresView -AllRuns $failAll -SelectedRuns @(Select-TestRuns -Runs $failAll -Run 'f4f4')
    Assert-That ($fvGreen.summary.summaryLine -eq 'red full runs: 0 of 1' -and $fvGreen.failures.Count -eq 0) 'failures: a green run yields no rows'
    $failJson = ((Invoke-TestRunTimings -ResultsRoot $failRoot -FullOnly -View Failures -Json -SourceRoot $srcDir) -join "`n") | ConvertFrom-Json
    Assert-That ($failJson.summary.summaryLine -eq 'red full runs: 4 of 5' -and @($failJson.failures).Count -eq 6 -and @($failJson.prefixCounts).Count -eq 3) '-Json (Failures) emits one parseable object with summary, prefixCounts and failures'
    $failTable = (Invoke-TestRunTimings -ResultsRoot $failRoot -FullOnly -View Failures -SourceRoot $srcDir) -join "`n"
    Assert-That ($failTable -match 'red full runs: 4 of 5' -and $failTable -match 'flakeCandidate' -and $failTable -match 'System\.TimeoutException') 'failures: table output prints the summary line, the prefix groups and the rows'
    # Failures must not need the TRX: there is none in this root and nothing complained above.

    # ---------------------------------------------------------------- Fixtures view (own root, one TRX)
    $fixSrc = Join-Path $srcDir 'Proj.Fix'
    New-Item -ItemType Directory -Path $fixSrc -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $fixSrc 'Fix.cs') -Encoding utf8 -Value @'
namespace Ns;
[Category("OutOfProcess")]
public class EarlyTests { }
public class TailTests { }
'@
    $fixRoot = Join-Path $tempRoot 'fixresults'
    $fixRun = New-CannedRunDir $fixRoot 'fix00001-20260301-100000-1' @{
        commit = ('fix00001' + '0' * 32); subject = 'fix'; worktree = 'w'; filter = ''; seconds = 30
        projects = @(New-CannedProject 'Proj.Fix' 6)
    } ([datetime]::new(2026, 3, 1, 10, 1, 0, [DateTimeKind]::Utc))
    # concurrency: [0,2) 1, [2,4) 2, [4,8) 3, [8,10) 2, [10,12) 1 -> the last 2 -> 1 transition is at 10 s = tail start.
    # EarlyTests: E1 0-10, E2 2-8, E3 4-12 (before the tail) and E4 20-21 (in the tail). TailTests: T1 12-16, T2 16-20 (both in the tail).
    New-CannedTrx (Join-Path $fixRun 'Proj.Fix.trx') $base1 0 30 @(
        @{ Class = 'Ns.EarlyTests'; Name = 'E1'; S = 0; E = 10 },
        @{ Class = 'Ns.EarlyTests'; Name = 'E2'; S = 2; E = 8 },
        @{ Class = 'Ns.EarlyTests'; Name = 'E3'; S = 4; E = 12 },
        @{ Class = 'Ns.EarlyTests'; Name = 'E4'; S = 20; E = 21 },
        @{ Class = 'Ns.TailTests'; Name = 'T1'; S = 12; E = 16 },
        @{ Class = 'Ns.TailTests'; Name = 'T2'; S = 16; E = 20 })
    $fixRuns = @(Get-TestRunInfo -ResultsRoot $fixRoot)
    $fx = @(Get-FixturesView -SelectedRuns $fixRuns -SourceRoot $srcDir)
    Assert-That ($fx.Count -eq 2 -and $fx[0].class -eq 'EarlyTests' -and $fx[1].class -eq 'TailTests') 'fixtures: one row per class, sorted by test-seconds descending'
    $early = $fx[0]; $tailRow = $fx[1]
    Assert-That ($early.project -eq 'Proj.Fix' -and $early.category -eq 'OutOfProcess' -and $early.tests -eq 4 -and (Test-Near $early.testSec 25)) 'fixtures: EarlyTests is OutOfProcess, 4 tests, 25 s'
    Assert-That ((Test-Near $early.firstOffsetSec 0) -and (Test-Near $early.lastOffsetSec 21)) 'fixtures: EarlyTests offsets 0 and 21 s from the project start'
    Assert-That ($early.tailTests -eq 1 -and (-not $early.tailAll) -and (Test-Near $early.tailStartSec 10)) 'fixtures: EarlyTests has 1 test in the serial tail (E4), tail starts at 10 s, not all in the tail'
    Assert-That ($tailRow.category -eq 'none' -and $tailRow.tests -eq 2 -and (Test-Near $tailRow.testSec 8) -and (Test-Near $tailRow.firstOffsetSec 12) -and (Test-Near $tailRow.lastOffsetSec 20)) 'fixtures: TailTests category none, 2 tests, 8 s, offsets 12 and 20'
    Assert-That ($tailRow.tailTests -eq 2 -and $tailRow.tailAll) 'fixtures: TailTests lies entirely in the serial tail'
    $fx1 = @(Get-FixturesView -SelectedRuns $fixRuns -SourceRoot $srcDir -Top 1)
    Assert-That ($fx1.Count -eq 1 -and $fx1[0].class -eq 'EarlyTests') 'fixtures: -Top 1 keeps the heaviest class'
    Assert-That ((Get-ShortClassName 'Ns.Sub.Outer+Inner') -eq 'Outer+Inner') 'short class name keeps the nested +'
    $fxJson = @(((Invoke-TestRunTimings -ResultsRoot $fixRoot -View Fixtures -Top 1 -Json -SourceRoot $srcDir) -join "`n") | ConvertFrom-Json)
    Assert-That ($fxJson.Count -eq 1 -and $fxJson[0].class -eq 'EarlyTests' -and $fxJson[0].tailAll -eq $false) '-Json (Fixtures) emits parseable rows and honours -Top'
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
