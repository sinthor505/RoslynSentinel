# Timing report over the per-run result folders written by scripts\build.ps1 / the test-tree runner
# (default C:\tmp\rs-testtrees\_results; one dir per run: <commit8>-<yyyyMMdd>-<HHmmss>-<pid>).
# Read-only: it never writes anything.
#
# Use:
#   pwsh -NoProfile -File scripts\Get-TestRunTimings.ps1 -Last 10                      # Runs view (default)
#   pwsh -NoProfile -File scripts\Get-TestRunTimings.ps1 -Run a3d98b2e -View Projects  # per-project timing of one run
#   pwsh -NoProfile -File scripts\Get-TestRunTimings.ps1 -FullOnly -Json -View Runs    # JSON instead of a table
#   . scripts\Get-TestRunTimings.ps1   # dot-source: only the functions load, nothing runs
#
# Parameters: -ResultsRoot, -Run <dir-name-prefix>, -Last <n> (most recent n runs by summary.json mtime, applied
# after -Run/-FullOnly), -FullOnly (filter empty AND 9 or more projects), -View Runs|Projects|Fixtures|Failures,
# -Json (ConvertTo-Json -Depth 6), -SourceRoot (default: the repo root; test project sources are scanned there).
#
# Data: summary.json (commit, filter, seconds, projects[]; optional "suite") and <project>.trx per project (streamed
# with XmlReader). summary.json has no timestamp, so a run's window is the min start / max finish of its TRX
# <Times>; with no TRX the window is summary.json mtime minus "seconds" .. mtime (approximate).
#
# Suite: summary.suite when present; else filter 'TestCategory!=OutOfProcess' = fast, empty filter = full, other = filtered.
#
# Definitions (Projects view), all per project, from UnitTestResult startTime/endTime/duration:
#   wallSec    first test start to last test end.
#   testSec    sum of the test durations.
#   serialSec  seconds of the wall window at concurrency <= 1 (0 running counts: gaps are included). Computed by a
#              sweep line over the [start,end] intervals (+1 at start, -1 at end, ends sort before starts at a tie;
#              zero-length tests add no interval).
#   idleSec    the part of serialSec with 0 running.
#   Category split (OutOfProcess / Ratchet / other): tests, testSec, and serialSec = seconds of the wall window with EXACTLY ONE
#              test running and that test in the category. So serialSec = idleSec + oopSerialSec + ratchetSerialSec +
#              otherSerialSec: the split partitions the headline figure and no category claims idle gaps.
#   Category of a test = its class's class-level [Category("OutOfProcess")] / [Category("Ratchet")] found by scanning
#   the test project's sources (directory named exactly like the TRX project, *.cs, bin/obj skipped): attribute(s) directly
#   above a class (other attributes, blank and comment lines may sit between; may share a bracket list). OutOfProcess wins
#   over Ratchet. Method-level tags and categories inherited from a base class are NOT attributed: such tests land in
#   "other" (the methodTagsInSource column counts method-level Category attributes seen in the project source).
#   The '(total)' row per run: sums, except wallSec = first start to last end over all projects; serialSec is the SUM of
#   the per-project figures (projects run concurrently, so it is not a run-level serial time).
#
# Fixtures and Failures views arrive in Step 1b.

param(
    [string]$ResultsRoot = 'C:\tmp\rs-testtrees\_results',
    [string]$Run,
    [int]$Last,
    [switch]$FullOnly,
    [ValidateSet('Runs', 'Projects', 'Fixtures', 'Failures')][string]$View = 'Runs',
    [switch]$Json,
    [string]$SourceRoot = (Split-Path -Parent $PSScriptRoot)
)

$script:Inv = [System.Globalization.CultureInfo]::InvariantCulture
$script:OopIndex = 0
$script:RatchetIndex = 1
$script:OtherIndex = 2

# ---------------------------------------------------------------- run directories (shared loader)

function Get-RunSuite([string]$Filter, $Summary) {
    if ($Summary.PSObject.Properties['suite'] -and -not [string]::IsNullOrWhiteSpace([string]$Summary.suite)) {
        return [string]$Summary.suite
    }
    if ($Filter -eq 'TestCategory!=OutOfProcess') { return 'fast' }
    if ($Filter -eq '') { return 'full' }
    return 'filtered'
}

# Every run dir that has a summary.json, oldest first by summary.json mtime. Cheap: reads only summary.json.
function Get-TestRunInfo {
    param([Parameter(Mandatory)][string]$ResultsRoot)
    if (-not (Test-Path -LiteralPath $ResultsRoot -PathType Container)) {
        throw "ResultsRoot not found: $ResultsRoot"
    }
    $runs = foreach ($dir in Get-ChildItem -LiteralPath $ResultsRoot -Directory) {
        $summaryPath = Join-Path $dir.FullName 'summary.json'
        if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) { continue }
        try { $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json }
        catch { Write-Warning "Skipping $($dir.Name): summary.json is not valid JSON."; continue }
        $filter = if ($summary.PSObject.Properties['filter'] -and $null -ne $summary.filter) { [string]$summary.filter } else { '' }
        $projects = if ($summary.PSObject.Properties['projects'] -and $null -ne $summary.projects) { @($summary.projects) } else { @() }
        $commit = if ($summary.PSObject.Properties['commit'] -and $summary.commit) { [string]$summary.commit } else { '' }
        [pscustomobject]@{
            Name         = $dir.Name
            Path         = $dir.FullName
            Commit       = $commit
            Commit8      = if ($commit.Length -ge 8) { $commit.Substring(0, 8) } else { $commit }
            Filter       = $filter
            Seconds      = if ($summary.PSObject.Properties['seconds'] -and $null -ne $summary.seconds) { [double]$summary.seconds } else { 0.0 }
            Suite        = Get-RunSuite $filter $summary
            Projects     = $projects
            SummaryMtime = (Get-Item -LiteralPath $summaryPath).LastWriteTimeUtc
        }
    }
    return @($runs | Sort-Object SummaryMtime, Name)
}

# -Run prefix, then -FullOnly, then -Last (most recent n by summary.json mtime). Result stays oldest first.
function Select-TestRuns {
    param(
        [Parameter(Mandatory)][object[]]$Runs,
        [string]$Run,
        [int]$Last,
        [switch]$FullOnly
    )
    $selected = @($Runs)
    if ($Run) { $selected = @($selected | Where-Object { $_.Name.StartsWith($Run, [System.StringComparison]::OrdinalIgnoreCase) }) }
    if ($FullOnly) { $selected = @($selected | Where-Object { $_.Filter -eq '' -and $_.Projects.Count -ge 9 }) }
    if ($Last -gt 0 -and $selected.Count -gt $Last) { $selected = @($selected | Select-Object -Last $Last) }
    return $selected
}

# ---------------------------------------------------------------- TRX reading (shared loader)

function ConvertTo-UtcTicks([string]$Text) {
    return [System.DateTimeOffset]::Parse($Text, $script:Inv, [System.Globalization.DateTimeStyles]::None).UtcTicks
}

# Streams one TRX. Returns Start/Finish (UTC ticks, from <Times>, $null if absent) and Results: one object per
# UnitTestResult with TestId, ClassName (joined from TestDefinitions), Name, Start, End, Duration (ticks), Outcome.
# -TimesOnly stops after <Times> (cheap; used for run windows). Returns $null for an unreadable file.
function Read-TrxFile {
    param([Parameter(Mandatory)][string]$Path, [switch]$TimesOnly)
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.IgnoreWhitespace = $true
    $reader = $null
    $start = $null; $finish = $null
    $results = [System.Collections.Generic.List[object]]::new()
    $classById = @{}
    $currentId = $null
    try {
        $reader = [System.Xml.XmlReader]::Create($Path, $settings)
        while ($reader.Read()) {
            if ($reader.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
            $depth = $reader.Depth
            $name = $reader.LocalName
            if ($depth -eq 1 -and $name -eq 'Times') {
                $s = $reader.GetAttribute('start'); $f = $reader.GetAttribute('finish')
                if ($s) { $start = ConvertTo-UtcTicks $s }
                if ($f) { $finish = ConvertTo-UtcTicks $f }
                if ($TimesOnly) { break }
            }
            elseif ($depth -eq 2 -and $name -eq 'UnitTestResult') {
                $st = $reader.GetAttribute('startTime'); $en = $reader.GetAttribute('endTime')
                if (-not $st -or -not $en) { continue }
                $dur = $reader.GetAttribute('duration')
                $results.Add([pscustomobject]@{
                        TestId    = $reader.GetAttribute('testId')
                        ClassName = ''
                        Name      = $reader.GetAttribute('testName')
                        Start     = ConvertTo-UtcTicks $st
                        End       = ConvertTo-UtcTicks $en
                        Duration  = if ($dur) { [System.TimeSpan]::Parse($dur, $script:Inv).Ticks } else { 0L }
                        Outcome   = $reader.GetAttribute('outcome')
                    })
            }
            elseif ($depth -eq 2 -and $name -eq 'UnitTest') { $currentId = $reader.GetAttribute('id') }
            elseif ($depth -eq 3 -and $name -eq 'TestMethod' -and $currentId) {
                $classById[$currentId] = $reader.GetAttribute('className')
            }
        }
    }
    catch {
        Write-Warning "Cannot read TRX '$Path': $($_.Exception.Message)"
        return $null
    }
    finally { if ($reader) { $reader.Dispose() } }
    foreach ($r in $results) {
        if ($r.TestId -and $classById.ContainsKey($r.TestId)) { $r.ClassName = [string]$classById[$r.TestId] }
    }
    return [pscustomobject]@{ Start = $start; Finish = $finish; Results = $results }
}

function Get-TrxPath([object]$RunInfo, [string]$Project) {
    return Join-Path $RunInfo.Path "$Project.trx"
}

# ---------------------------------------------------------------- run windows and overlap

# Window in UTC ticks: min start / max finish of the run's TRX <Times>; else summary.json mtime - seconds .. mtime.
function Get-RunWindow {
    param([Parameter(Mandatory)][object]$RunInfo)
    $min = $null; $max = $null
    foreach ($trx in Get-ChildItem -LiteralPath $RunInfo.Path -Filter '*.trx' -File) {
        $times = Read-TrxFile -Path $trx.FullName -TimesOnly
        if ($null -eq $times -or $null -eq $times.Start -or $null -eq $times.Finish) { continue }
        if ($null -eq $min -or $times.Start -lt $min) { $min = $times.Start }
        if ($null -eq $max -or $times.Finish -gt $max) { $max = $times.Finish }
    }
    if ($null -ne $min -and $null -ne $max) {
        return [pscustomobject]@{ Start = $min; End = $max; Source = 'trx' }
    }
    $end = ([System.DateTimeOffset]$RunInfo.SummaryMtime).UtcTicks
    return [pscustomobject]@{ Start = $end - [long]($RunInfo.Seconds * [System.TimeSpan]::TicksPerSecond); End = $end; Source = 'mtime' }
}

# ---------------------------------------------------------------- source scan (class-level categories)

function Get-SourceFilesSkippingBuildDirs([string]$Dir) {
    foreach ($f in Get-ChildItem -LiteralPath $Dir -File -Filter '*.cs') { $f.FullName }
    foreach ($sub in Get-ChildItem -LiteralPath $Dir -Directory) {
        if ($sub.Name -in 'bin', 'obj') { continue }
        Get-SourceFilesSkippingBuildDirs $sub.FullName
    }
}

function Get-TestProjectDirs([string]$SourceRoot, [string]$Project) {
    $dirs = @(Get-ChildItem -LiteralPath $SourceRoot -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $Project })
    if ($dirs.Count -eq 0) {
        $dirs = @(Get-ChildItem -LiteralPath $SourceRoot -Directory -Recurse -Depth 2 -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -eq $Project -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
    }
    return $dirs
}

$script:TypeDeclPattern = '^(?:(?:public|internal|private|protected|static|sealed|abstract|partial|unsafe|file|readonly|new)\s+)*(?:class|struct|record(?:\s+(?:class|struct))?|interface|enum)\s+(\w+)'
$script:CategoryPattern = 'Category\s*\(\s*"([^"]+)"'

# Scans one source text. Returns Classes (list of FullName, ShortName, Categories[]) and MethodLevelTags (Category
# attributes that are not directly above a type declaration). An attribute binds to the type declaration directly below
# it: blank lines, comments and further attributes may sit between; any other code line drops the pending attributes.
function Read-ClassCategoriesFromLines {
    param([Parameter(Mandatory)][AllowEmptyString()][AllowEmptyCollection()][string[]]$Lines)
    $classes = [System.Collections.Generic.List[object]]::new()
    $methodTags = 0
    $fileNs = ''
    $stack = [System.Collections.Generic.List[object]]::new()
    $brace = 0
    $pending = [System.Collections.Generic.List[string]]::new()
    $attrDepth = 0
    $attrBuffer = ''
    foreach ($line in $Lines) {
        $rest = $line.Trim()
        $code = [regex]::Replace($line, '"(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])?''', '""')
        $code = [regex]::Replace($code, '//.*$', '')

        # consume attribute groups (possibly multi-line) from the front of the line
        while ($attrDepth -gt 0 -or $rest.StartsWith('[')) {
            $i = 0
            $closed = $false
            while ($i -lt $rest.Length) {
                $ch = $rest[$i]
                if ($ch -eq '[') { $attrDepth++ }
                elseif ($ch -eq ']') { $attrDepth--; if ($attrDepth -eq 0) { $i++; $closed = $true; break } }
                $i++
            }
            $attrBuffer += $rest.Substring(0, $i)
            $rest = $rest.Substring($i).Trim()
            if (-not $closed) { $attrBuffer += ' '; break }
            if ($attrBuffer -notmatch '^\[\s*(assembly|module)\s*:') { $pending.Add($attrBuffer) }
            $attrBuffer = ''
        }

        $braceBefore = $brace
        if ($attrDepth -eq 0 -and $rest -ne '' -and -not ($rest -match '^(//|/\*|\*)')) {
            $trimmedCode = $code.Trim()
            if ($rest -match '^namespace\s+([\w.]+)\s*(;|\{|$)') {
                if ($Matches[2] -eq ';') { $fileNs = $Matches[1] }
                else { $stack.Add([pscustomobject]@{ Kind = 'ns'; Name = $Matches[1]; Depth = $braceBefore; Opened = $code.Contains('{') }) }
                $pending.Clear()
            }
            elseif ($rest -match $script:TypeDeclPattern) {
                $typeName = $Matches[1]
                $cats = [System.Collections.Generic.List[string]]::new()
                foreach ($group in $pending) {
                    foreach ($m in [regex]::Matches($group, $script:CategoryPattern)) { $cats.Add($m.Groups[1].Value) }
                }
                $pending.Clear()
                $types = @($stack | Where-Object Kind -eq 'type' | ForEach-Object Name) + $typeName
                $nsParts = @(); if ($fileNs) { $nsParts += $fileNs }
                $nsParts += @($stack | Where-Object Kind -eq 'ns' | ForEach-Object Name)
                $short = $types -join '+'
                $classes.Add([pscustomobject]@{
                        FullName   = if ($nsParts.Count) { ($nsParts -join '.') + '.' + $short } else { $short }
                        ShortName  = $short
                        Categories = $cats.ToArray()
                    })
                $hasBody = $code.Contains('{')
                if ($hasBody -or -not $trimmedCode.EndsWith(';')) {
                    $stack.Add([pscustomobject]@{ Kind = 'type'; Name = $typeName; Depth = $braceBefore; Opened = $hasBody })
                }
            }
            else {
                foreach ($group in $pending) { $methodTags += [regex]::Matches($group, $script:CategoryPattern).Count }
                $pending.Clear()
            }
        }

        foreach ($ch in $code.ToCharArray()) {
            if ($ch -eq '{') { $brace++ } elseif ($ch -eq '}') { $brace-- }
        }
        foreach ($entry in $stack) { if (-not $entry.Opened -and $brace -gt $entry.Depth) { $entry.Opened = $true } }
        while ($stack.Count -gt 0 -and $stack[$stack.Count - 1].Opened -and $brace -le $stack[$stack.Count - 1].Depth) {
            $stack.RemoveAt($stack.Count - 1)
        }
    }
    foreach ($group in $pending) { $methodTags += [regex]::Matches($group, $script:CategoryPattern).Count }
    return [pscustomobject]@{ Classes = $classes; MethodLevelTags = $methodTags }
}

$script:SourceScanCache = @{}

# Scans the project's source once per (SourceRoot, Project). Result: ByFullName and ByShortName (hashtables of
# name -> string[] categories, unioned when a short name repeats) plus MethodLevelTags and a Found flag.
function Get-ProjectClassCategories {
    param([Parameter(Mandatory)][string]$SourceRoot, [Parameter(Mandatory)][string]$Project)
    $key = "$SourceRoot|$Project"
    if ($script:SourceScanCache.ContainsKey($key)) { return $script:SourceScanCache[$key] }
    $byFull = @{}; $byShort = @{}; $methodTags = 0
    $dirs = @(Get-TestProjectDirs $SourceRoot $Project)
    foreach ($dir in $dirs) {
        foreach ($file in Get-SourceFilesSkippingBuildDirs $dir.FullName) {
            $scan = Read-ClassCategoriesFromLines -Lines ([System.IO.File]::ReadAllLines($file))
            $methodTags += $scan.MethodLevelTags
            foreach ($c in $scan.Classes) {
                $byFull[$c.FullName] = @(@($byFull[$c.FullName]) + $c.Categories | Where-Object { $_ } | Select-Object -Unique)
                $byShort[$c.ShortName] = @(@($byShort[$c.ShortName]) + $c.Categories | Where-Object { $_ } | Select-Object -Unique)
            }
        }
    }
    $result = [pscustomobject]@{ Found = ($dirs.Count -gt 0); ByFullName = $byFull; ByShortName = $byShort; MethodLevelTags = $methodTags }
    $script:SourceScanCache[$key] = $result
    return $result
}

function Get-ShortClassName([string]$ClassName) {
    $dot = $ClassName.LastIndexOf('.')
    # a nested class keeps its '+': "Ns.Outer+Inner" -> "Outer+Inner"
    return $ClassName.Substring($dot + 1)
}

# The one place that matches a TRX className to the scanned source: full name first, then the short name.
# Returns the category index: 0 OutOfProcess, 1 Ratchet, 2 other (no class-level category / class not found).
function Resolve-ClassCategoryIndex {
    param($Scan, [string]$ClassName)
    if ($null -eq $Scan -or [string]::IsNullOrEmpty($ClassName)) { return $script:OtherIndex }
    $cats = $null
    if ($Scan.ByFullName.ContainsKey($ClassName)) { $cats = $Scan.ByFullName[$ClassName] }
    else {
        $short = Get-ShortClassName $ClassName
        if ($Scan.ByShortName.ContainsKey($short)) { $cats = $Scan.ByShortName[$short] }
    }
    if ($cats -contains 'OutOfProcess') { return $script:OopIndex }
    if ($cats -contains 'Ratchet') { return $script:RatchetIndex }
    return $script:OtherIndex
}

# ---------------------------------------------------------------- sweep line

# Sweep over [Start,End] intervals (UTC ticks). Returns WallTicks (first start .. last end), IdleTicks (0 running),
# SingleTicks[category] (exactly one running, and it is in that category), ConcurrentTicks (2 or more running) and
# SerialTicks (= IdleTicks + sum SingleTicks, i.e. concurrency <= 1). Intervals with End <= Start are ignored.
function Measure-SerialTimeline {
    param([long[]]$Starts, [long[]]$Ends, [int[]]$Category, [int]$CategoryCount = 3)
    $single = New-Object 'long[]' $CategoryCount
    $keys = [System.Collections.Generic.List[long]]::new()
    $items = [System.Collections.Generic.List[int]]::new()
    for ($i = 0; $i -lt $Starts.Length; $i++) {
        if ($Ends[$i] -le $Starts[$i]) { continue }
        $keys.Add($Starts[$i] * 2 + 1); $items.Add(2 * $i)       # start sorts after an end at the same tick
        $keys.Add($Ends[$i] * 2); $items.Add(2 * $i + 1)
    }
    $idle = 0L; $concurrent = 0L; $wall = 0L
    if ($keys.Count -gt 0) {
        $keyArray = $keys.ToArray(); $itemArray = $items.ToArray()
        [System.Array]::Sort($keyArray, $itemArray)
        $running = 0
        $counts = New-Object 'int[]' $CategoryCount
        $first = $keyArray[0] -shr 1
        $prev = $first
        for ($e = 0; $e -lt $keyArray.Length; $e++) {
            $tick = $keyArray[$e] -shr 1
            $dt = $tick - $prev
            if ($dt -gt 0) {
                if ($running -eq 0) { $idle += $dt }
                elseif ($running -eq 1) {
                    for ($c = 0; $c -lt $CategoryCount; $c++) { if ($counts[$c] -eq 1) { $single[$c] += $dt; break } }
                }
                else { $concurrent += $dt }
                $prev = $tick
            }
            $item = $itemArray[$e]
            $cat = $Category[$item -shr 1]
            if (($item -band 1) -eq 0) { $running++; $counts[$cat]++ } else { $running--; $counts[$cat]-- }
        }
        $wall = $prev - $first
    }
    $singleSum = 0L; foreach ($s in $single) { $singleSum += $s }
    return [pscustomobject]@{
        WallTicks       = $wall
        IdleTicks       = $idle
        SingleTicks     = $single
        ConcurrentTicks = $concurrent
        SerialTicks     = $idle + $singleSum
    }
}

# ---------------------------------------------------------------- per-project statistics (TRX join)

function ConvertTo-Seconds([long]$Ticks) { return [math]::Round($Ticks / [double][System.TimeSpan]::TicksPerSecond, 1) }

# One project's timing from its parsed TRX. Each result also gets CategoryIndex (kept for the Fixtures view in Step 1b).
function Get-ProjectTimingStats {
    param([Parameter(Mandatory)]$Trx, $Scan)
    $results = @($Trx.Results)
    $n = $results.Count
    $starts = New-Object 'long[]' $n; $ends = New-Object 'long[]' $n; $cats = New-Object 'int[]' $n
    $testTicks = New-Object 'long[]' 3; $testCount = New-Object 'int[]' 3
    $totalTicks = 0L
    for ($i = 0; $i -lt $n; $i++) {
        $r = $results[$i]
        $ci = Resolve-ClassCategoryIndex -Scan $Scan -ClassName $r.ClassName
        $r | Add-Member -NotePropertyName CategoryIndex -NotePropertyValue $ci -Force
        $starts[$i] = $r.Start; $ends[$i] = $r.End; $cats[$i] = $ci
        $testCount[$ci]++; $testTicks[$ci] += $r.Duration; $totalTicks += $r.Duration
    }
    $sweep = Measure-SerialTimeline -Starts $starts -Ends $ends -Category $cats
    $first = $null; $last = $null
    if ($n -gt 0) { $first = ($starts | Measure-Object -Minimum).Minimum; $last = ($ends | Measure-Object -Maximum).Maximum }
    return [pscustomobject]@{
        Tests       = $n
        FirstStart  = $first
        LastEnd     = $last
        WallTicks   = if ($n -gt 0) { $last - $first } else { 0L }
        TestTicks   = $totalTicks
        SerialTicks = $sweep.SerialTicks
        IdleTicks   = $sweep.IdleTicks
        CatTests    = $testCount
        CatTestTicks = $testTicks
        CatSerialTicks = $sweep.SingleTicks
    }
}

function New-ProjectRow([string]$RunName, [string]$Project, $Stats, [int]$MethodTags, [string]$Note) {
    $o = $script:OopIndex; $r = $script:RatchetIndex; $x = $script:OtherIndex
    return [pscustomobject][ordered]@{
        run                = $RunName
        project            = $Project
        tests              = $Stats.Tests
        wallSec            = ConvertTo-Seconds $Stats.WallTicks
        testSec            = ConvertTo-Seconds $Stats.TestTicks
        serialSec          = ConvertTo-Seconds $Stats.SerialTicks
        idleSec            = ConvertTo-Seconds $Stats.IdleTicks
        oopTests           = $Stats.CatTests[$o]
        oopTestSec         = ConvertTo-Seconds $Stats.CatTestTicks[$o]
        oopSerialSec       = ConvertTo-Seconds $Stats.CatSerialTicks[$o]
        ratchetTests       = $Stats.CatTests[$r]
        ratchetTestSec     = ConvertTo-Seconds $Stats.CatTestTicks[$r]
        ratchetSerialSec   = ConvertTo-Seconds $Stats.CatSerialTicks[$r]
        otherTests         = $Stats.CatTests[$x]
        otherTestSec       = ConvertTo-Seconds $Stats.CatTestTicks[$x]
        otherSerialSec     = ConvertTo-Seconds $Stats.CatSerialTicks[$x]
        methodTagsInSource = $MethodTags
        note               = $Note
    }
}

# ---------------------------------------------------------------- views

# One row per run. overlappingRuns = other runs under ResultsRoot (not just the selected ones) whose window overlaps.
function Get-RunsView {
    param([Parameter(Mandatory)][object[]]$AllRuns, [Parameter(Mandatory)][object[]]$SelectedRuns)
    $windows = @{}
    foreach ($r in $AllRuns) { $windows[$r.Name] = Get-RunWindow -RunInfo $r }
    foreach ($run in $SelectedRuns) {
        $w = $windows[$run.Name]
        $overlap = 0
        foreach ($other in $AllRuns) {
            if ($other.Name -eq $run.Name) { continue }
            $ow = $windows[$other.Name]
            if ($w.Start -lt $ow.End -and $ow.Start -lt $w.End) { $overlap++ }
        }
        [pscustomobject][ordered]@{
            dir             = $run.Name
            commit8         = $run.Commit8
            filter          = $run.Filter
            suite           = $run.Suite
            seconds         = $run.Seconds
            projects        = $run.Projects.Count
            total           = [int](($run.Projects | Measure-Object -Property total -Sum).Sum)
            failed          = [int](($run.Projects | Measure-Object -Property failed -Sum).Sum)
            overlappingRuns = $overlap
            windowSource    = $w.Source
        }
    }
}

# Per run: one row per summary project (TRX named <project>.trx) plus a '(total)' row.
function Get-ProjectsView {
    param([Parameter(Mandatory)][object[]]$SelectedRuns, [Parameter(Mandatory)][string]$SourceRoot)
    foreach ($run in $SelectedRuns) {
        $rows = [System.Collections.Generic.List[object]]::new()
        $sum = [pscustomobject]@{ Tests = 0; WallTicks = 0L; TestTicks = 0L; SerialTicks = 0L; IdleTicks = 0L; MinStart = $null; MaxEnd = $null
            CatTests = (New-Object 'int[]' 3); CatTestTicks = (New-Object 'long[]' 3); CatSerialTicks = (New-Object 'long[]' 3); MethodTags = 0 }
        foreach ($p in $run.Projects) {
            $project = [string]$p.project
            $trxPath = Get-TrxPath $run $project
            $trx = if (Test-Path -LiteralPath $trxPath -PathType Leaf) { Read-TrxFile -Path $trxPath } else { $null }
            if ($null -eq $trx) {
                $empty = [pscustomobject]@{ Tests = [int]$p.total; WallTicks = 0L; TestTicks = 0L; SerialTicks = 0L; IdleTicks = 0L
                    CatTests = (New-Object 'int[]' 3); CatTestTicks = (New-Object 'long[]' 3); CatSerialTicks = (New-Object 'long[]' 3) }
                $rows.Add((New-ProjectRow $run.Name $project $empty 0 'no TRX: tests from summary.json, timings unavailable'))
                continue
            }
            $scan = Get-ProjectClassCategories -SourceRoot $SourceRoot -Project $project
            $stats = Get-ProjectTimingStats -Trx $trx -Scan $scan
            $note = if ($scan.Found) { '' } else { 'no test source dir found under SourceRoot: all tests counted as other' }
            $rows.Add((New-ProjectRow $run.Name $project $stats $scan.MethodLevelTags $note))
            $sum.Tests += $stats.Tests; $sum.TestTicks += $stats.TestTicks; $sum.SerialTicks += $stats.SerialTicks; $sum.IdleTicks += $stats.IdleTicks
            $sum.MethodTags += $scan.MethodLevelTags
            for ($c = 0; $c -lt 3; $c++) {
                $sum.CatTests[$c] += $stats.CatTests[$c]; $sum.CatTestTicks[$c] += $stats.CatTestTicks[$c]; $sum.CatSerialTicks[$c] += $stats.CatSerialTicks[$c]
            }
            if ($stats.Tests -gt 0) {
                if ($null -eq $sum.MinStart -or $stats.FirstStart -lt $sum.MinStart) { $sum.MinStart = $stats.FirstStart }
                if ($null -eq $sum.MaxEnd -or $stats.LastEnd -gt $sum.MaxEnd) { $sum.MaxEnd = $stats.LastEnd }
            }
        }
        if ($null -ne $sum.MinStart) { $sum.WallTicks = $sum.MaxEnd - $sum.MinStart }
        $rows.Add((New-ProjectRow $run.Name '(total)' $sum $sum.MethodTags 'wall = first start to last end over all projects; serial = sum of the per-project figures'))
        $rows
    }
}

# TODO Step 1b: Fixtures and Failures views (Get-FixturesView, Get-FailuresView); reuse Get-TestRunInfo/Select-TestRuns,
# Read-TrxFile + Get-ProjectTimingStats (each result carries ClassName and CategoryIndex) and Measure-SerialTimeline.

function Write-TimingOutput {
    param($Rows, [switch]$Json, [string]$Footer)
    $rows = @($Rows)
    if ($Json) { ConvertTo-Json -InputObject $rows -Depth 6; return }
    if ($rows.Count -eq 0) { Write-Output '(no rows)'; return }
    Write-Output ($rows | Format-Table -AutoSize -Property * | Out-String -Width 1000)
    if ($Footer) { Write-Output $Footer }
}

function Invoke-TestRunTimings {
    param(
        [string]$ResultsRoot, [string]$Run, [int]$Last, [switch]$FullOnly, [string]$View, [switch]$Json, [string]$SourceRoot
    )
    if ($View -in 'Fixtures', 'Failures') {
        [Console]::Error.WriteLine("The $View view is not implemented yet (Step 1b).")
        $script:ExitCode = 1
        return
    }
    $all = @(Get-TestRunInfo -ResultsRoot $ResultsRoot)
    $selected = @(Select-TestRuns -Runs $all -Run $Run -Last $Last -FullOnly:$FullOnly)
    if ($selected.Count -eq 0) {
        [Console]::Error.WriteLine("No runs matched under $ResultsRoot (of $($all.Count) with a summary.json).")
        $script:ExitCode = 1
        return
    }
    if ($View -eq 'Runs') {
        $rows = Get-RunsView -AllRuns $all -SelectedRuns $selected
        Write-TimingOutput -Rows $rows -Json:$Json -Footer 'overlappingRuns is approximate: windows come from TRX <Times> (windowSource=trx) or summary.json mtime minus seconds (mtime).'
    }
    else {
        $rows = Get-ProjectsView -SelectedRuns $selected -SourceRoot $SourceRoot
        Write-TimingOutput -Rows $rows -Json:$Json -Footer 'Method-level [Category] tags and inherited class categories are not attributed (those tests count as "other"); see methodTagsInSource. serial = concurrency <= 1 incl. idle gaps.'
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    $script:ExitCode = 0
    Invoke-TestRunTimings -ResultsRoot $ResultsRoot -Run $Run -Last $Last -FullOnly:$FullOnly -View $View -Json:$Json -SourceRoot $SourceRoot
    exit $script:ExitCode
}
