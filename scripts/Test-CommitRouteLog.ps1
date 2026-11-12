<#
.SYNOPSIS
  Audits the commit-route log (<common git dir>/roslynsentinel/commit-route.jsonl) for taken commits.

.DESCRIPTION
  Read-only. Runs outside the server and calls only the git CLI. Prints one line per outcome=taken entry
  with a class: ok, unchecked:<why>, warn:<kind> or fail:<kind>. Declined lines are not checked here.
  Exits 0 when no FAIL was printed, 1 otherwise.

  Checks per taken entry:
    fail:missing                  the commit is not in the object store (may be pruned about 2 weeks after a reset)
    fail:parent-mismatch          logged parent, or the reflog old value, differs from git's parent of the commit
                                  (or the reflog message is not 'commit: <subject>' / 'commit (initial): <subject>')
    warn:reflog-expired           no reflog entry of the line's ref produced the commit
    warn:branch-gone              the reflog file of the line's ref is gone
    fail:tree-outside-listed-paths  the commit changed files outside the logged paths
    unchecked:whole-index         plain or whole-index commit: no listed paths to compare
    unchecked:paths-truncated     the logged path list was truncated
    fail:duplicate                the same commit id logged again (the later line is reported)
    unchecked:pre-step-1 line     the line lacks kind, ref or parent; it is never a FAIL
  Once per log: fail:fsck when `git fsck --no-dangling` exits non-zero (skipped by -SkipFsck).

  Limits: the reflog is read from -RepoPath's worktree (HEAD) and common git dir (refs/...), so a
  detached-HEAD line from another linked worktree is checked against the wrong HEAD reflog.

.PARAMETER RepoPath
  Any worktree of the repository. Defaults to the current directory.

.PARAMETER LogPath
  Overrides the log location (default: <common git dir>/roslynsentinel/commit-route.jsonl).

.PARAMETER Since
  Only check taken entries whose utc is at or after this time.

.PARAMETER SkipFsck
  Skips the once-per-log git fsck (slow on large repositories).
#>
[CmdletBinding()]
param(
    [string]$RepoPath = (Get-Location).Path,
    [string]$LogPath,
    [datetime]$Since,
    [switch]$SkipFsck
)

$ErrorActionPreference = 'Continue'
$EmptyTree = '4b825dc642cb6eb9a060e54bf8d69288fbee4904'
$ZeroId = '0' * 40
$Rank = @{ 'fail' = 3; 'warn' = 2; 'unchecked' = 1; 'ok' = 0 }

if (-not (Test-Path -LiteralPath $RepoPath)) { Write-Host "no such path: $RepoPath"; exit 1 }
$script:RepoPath = (Resolve-Path -LiteralPath $RepoPath).Path
$script:ReflogCache = @{}

function Invoke-Git([string[]]$GitArgs) {
    $out = & git -C $script:RepoPath @GitArgs 2>$null
    $script:GitExit = $LASTEXITCODE
    return $out
}

function Has($Object, [string]$Name) {
    return ($null -ne $Object -and $null -ne $Object.PSObject.Properties[$Name])
}

function Get-ChangedNames([string[]]$GitArgs) {
    # Names from a diff-tree -z run, or $null when git fails.
    $out = Invoke-Git $GitArgs
    if ($script:GitExit -ne 0) { return $null }
    $text = [string]($out -join "`n")
    $names = @($text.Split([char]0) | Where-Object { $_ -ne '' })
    return , $names
}

function Get-ReflogRows([string]$Ref) {
    # Returns [pscustomobject]@{ Exists; Rows } for a ref; Exists is $false when the reflog file is gone.
    if ($script:ReflogCache.ContainsKey($Ref)) { return $script:ReflogCache[$Ref] }
    $file = $null
    if ($Ref -eq 'HEAD') {
        $file = [IO.Path]::Combine($script:GitDirAbs, 'logs', 'HEAD')
    }
    elseif ($Ref -match '^refs/' -and $Ref -notmatch '\.\.') {
        $file = [IO.Path]::Combine($script:CommonAbs, 'logs', $Ref.Replace('/', [IO.Path]::DirectorySeparatorChar))
    }
    $result = [pscustomobject]@{ Exists = $false; Rows = @() }
    if ($file -and (Test-Path -LiteralPath $file -PathType Leaf)) {
        $rows = @(foreach ($l in (Get-Content -LiteralPath $file)) {
                $tab = $l.IndexOf([char]9)
                if ($tab -lt 0) { continue }
                $f = $l.Substring(0, $tab).Split(' ')
                if ($f.Count -lt 2) { continue }
                [pscustomobject]@{ Old = $f[0]; New = $f[1]; Message = $l.Substring($tab + 1) }
            })
        $result = [pscustomobject]@{ Exists = $true; Rows = $rows }
    }
    $script:ReflogCache[$Ref] = $result
    return $result
}

function Get-EntryNotes($Entry, [string]$Commit) {
    # Returns a list of [pscustomobject]@{ Class; Detail } for one shaped taken entry.
    $notes = New-Object System.Collections.Generic.List[object]
    function Add-Note([string]$Class, [string]$Detail) {
        $notes.Add([pscustomobject]@{ Class = $Class; Detail = $Detail })
    }

    # 1. exists
    Invoke-Git @('cat-file', '-e', "$Commit^{commit}") | Out-Null
    if ($script:GitExit -ne 0) {
        Add-Note 'fail:missing' 'commit is not in the object store; it may have been pruned, so check reflog/gc'
        return $notes.ToArray()
    }

    # 2. parent vs git, logged parent and reflog
    $parentOut = Invoke-Git @('rev-parse', '--verify', '-q', "$Commit^")
    $isRoot = ($script:GitExit -ne 0)
    $expected = if ($isRoot) { $ZeroId } else { ([string]($parentOut | Select-Object -First 1)).Trim() }
    $logged = if ($null -eq $Entry.parent) { $ZeroId } else { [string]$Entry.parent }
    if ($logged -ne $expected) { Add-Note 'fail:parent-mismatch' "logged parent $logged, git parent $expected" }

    $log = Get-ReflogRows ([string]$Entry.ref)
    if (-not $log.Exists) {
        Add-Note 'warn:branch-gone' "reflog for $($Entry.ref) is gone"
    }
    else {
        $hits = @($log.Rows | Where-Object { $_.New -eq $Commit })
        if ($hits.Count -eq 0) {
            Add-Note 'warn:reflog-expired' "no reflog entry of $($Entry.ref) produced this commit"
        }
        else {
            $subjectOut = Invoke-Git @('log', '-1', '--format=%s', $Commit)
            $subject = [string]($subjectOut | Select-Object -First 1)
            $good = @($hits | Where-Object {
                    $_.Old -eq $expected -and ($_.Message -eq "commit: $subject" -or $_.Message -eq "commit (initial): $subject")
                })
            if ($good.Count -eq 0) {
                Add-Note 'fail:parent-mismatch' "reflog of $($Entry.ref) has no entry with old $expected and message 'commit: $subject'"
            }
        }
    }

    # 3. tree vs listed paths
    $kind = [string]$Entry.kind
    $hasPaths = (Has $Entry 'paths') -and $null -ne $Entry.paths -and @($Entry.paths).Count -gt 0
    if ($kind -eq 'plain' -or -not $hasPaths) {
        Add-Note 'unchecked:whole-index' 'no listed paths to compare'
    }
    elseif ($Entry.pathsTruncated -eq $true) {
        Add-Note 'unchecked:paths-truncated' 'the logged path list was truncated'
    }
    else {
        $base = if ($isRoot) { $EmptyTree } else { $expected }
        $all = Get-ChangedNames @('diff-tree', '-r', '--no-renames', '--name-only', '-z', $base, $Commit)
        $listedArgs = @('--literal-pathspecs', 'diff-tree', '-r', '--no-renames', '--name-only', '-z', $base, $Commit, '--') +
            @($Entry.paths | ForEach-Object { [string]$_ })
        $listed = Get-ChangedNames $listedArgs
        if ($null -eq $all -or $null -eq $listed) {
            Add-Note 'unchecked:diff-tree-failed' 'git diff-tree failed; tree not compared'
        }
        else {
            $outside = @($all | Where-Object { $listed -cnotcontains $_ })
            if ($outside.Count -gt 0) {
                $sample = (@($outside | Select-Object -First 3) -join ', ')
                Add-Note 'fail:tree-outside-listed-paths' "changed outside the listed paths: $sample"
            }
        }
    }

    return $notes.ToArray()
}

# ---- locate the repository and the log
$common = Invoke-Git @('rev-parse', '--git-common-dir')
if ($script:GitExit -ne 0 -or -not $common) { Write-Host "not a git repository: $script:RepoPath"; exit 1 }
$script:CommonAbs = (Resolve-Path -LiteralPath ([IO.Path]::Combine($script:RepoPath, ([string]($common | Select-Object -First 1)).Trim()))).Path
$gitDir = Invoke-Git @('rev-parse', '--git-dir')
$script:GitDirAbs = (Resolve-Path -LiteralPath ([IO.Path]::Combine($script:RepoPath, ([string]($gitDir | Select-Object -First 1)).Trim()))).Path

if (-not $LogPath) { $LogPath = [IO.Path]::Combine($script:CommonAbs, 'roslynsentinel', 'commit-route.jsonl') }
if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
    Write-Host "no commit-route log at $LogPath; nothing to check"
    exit 0
}

# ---- per-entry checks
$seen = @{}
$fails = 0
$lineNo = 0
foreach ($raw in (Get-Content -LiteralPath $LogPath)) {
    $lineNo++
    $text = $raw.Trim()
    if ($text -eq '') { continue }
    try { $entry = $text | ConvertFrom-Json -ErrorAction Stop }
    catch {
        Write-Host ('{0,-30} line {1}' -f 'unchecked:unparseable-line', $lineNo)
        continue
    }
    if ([string]$entry.outcome -ne 'taken') { continue }
    if ($PSBoundParameters.ContainsKey('Since')) {
        try {
            $when = [datetimeoffset]::Parse([string]$entry.utc, [Globalization.CultureInfo]::InvariantCulture)
            if ($when.UtcDateTime -lt $Since.ToUniversalTime()) { continue }
        }
        catch { }
    }

    $commit = [string]$entry.commit
    $short = if ($commit.Length -ge 8) { $commit.Substring(0, 8) } else { $commit }
    $shaped = (Has $entry 'ref') -and (Has $entry 'kind') -and (Has $entry 'parent') -and [bool]$entry.ref

    $dupLine = $null
    if ($seen.ContainsKey($commit)) { $dupLine = $seen[$commit] } else { $seen[$commit] = $lineNo }

    if (-not $shaped) {
        $notes = @([pscustomobject]@{ Class = 'unchecked:pre-step-1 line'; Detail = 'lacks kind, ref or parent' })
    }
    else {
        $notes = @(Get-EntryNotes $entry $commit)
        if ($null -ne $dupLine) {
            $notes += [pscustomobject]@{ Class = 'fail:duplicate'; Detail = "commit already logged at line $dupLine" }
        }
    }

    $worst = 'ok'
    foreach ($n in $notes) {
        if ($Rank[$n.Class.Split(':')[0]] -gt $Rank[$worst.Split(':')[0]]) { $worst = $n.Class }
    }
    if ($worst -like 'fail:*') { $fails++ }
    $detail = (@($notes | ForEach-Object { "$($_.Class) ($($_.Detail))" }) -join '; ')
    Write-Host ('{0,-30} line {1,-5} {2} {3}' -f $worst, $lineNo, $short, $detail).TrimEnd()
}

# ---- once per log
if ($SkipFsck) {
    Write-Host ('{0,-30} {1}' -f 'fsck', 'skipped (-SkipFsck)')
}
else {
    $fsckOut = & git -C $script:RepoPath fsck --no-dangling 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host ('{0,-30} {1}' -f 'ok', 'fsck --no-dangling')
    }
    else {
        $fails++
        $sample = (@($fsckOut | Select-Object -First 3 | ForEach-Object { $_.ToString() }) -join ' | ')
        Write-Host ('{0,-30} {1}' -f 'fail:fsck', "git fsck --no-dangling exit ${LASTEXITCODE}: $sample")
    }
}

# TODO Step 6: summary, -AsJson, setup header
if ($fails -gt 0) { exit 1 }
exit 0
