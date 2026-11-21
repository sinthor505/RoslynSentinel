<#
.SYNOPSIS
  Self-test (acceptance fixture) for Test-CommitRouteLog.ps1.

.DESCRIPTION
  Builds a throwaway git repository under c:\tmp with three real commits, then runs the audit against:
    (a) a hand-built clean log: expect exit 0 and no fail:* line;
    (b) a hand-built broken log: expect exit 1 and each of fail:missing, fail:parent-mismatch and
        fail:tree-outside-listed-paths;
    (c) the broken log with -AsJson: expect exit 1, parseable JSON, exitCode 1 and the same three fail classes;
    (d) a taken line without kind/ref/parent: unchecked:no-commit-fields, exit 0;
    (e) fsck on the healthy repository: no fsck warn or fail, exit 0;
    (f) a damaged commit-graph file: warn:fsck-commit-graph, exit 0;
    (g) a corrupt tree object: fail:fsck-corrupt, exit 1.
  Prints PASS/FAIL per case and exits 1 if any case fails. The temp repository is removed at the end.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
$Audit = Join-Path $PSScriptRoot 'Test-CommitRouteLog.ps1'
$Zero = '0' * 40
$Failures = 0

function Report-Case([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { Write-Host "PASS $Name" }
    else { $script:Failures++; Write-Host "FAIL $Name $Detail" }
}

$base = Join-Path 'c:\tmp' ('rs-commitlog-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$repo = Join-Path $base 'repo'
New-Item -ItemType Directory -Path $repo -Force | Out-Null

try {
    function Git-Run {
        & git -C $repo -c user.name=fixture -c user.email=fixture@example.invalid -c commit.gpgsign=false @args 2>$null
    }

    function Commit-File([string]$File, [string]$Subject) {
        Set-Content -LiteralPath (Join-Path $repo $File) -Value $Subject -Encoding ascii
        Git-Run add -- $File | Out-Null
        Git-Run commit -q -m $Subject | Out-Null
        return ([string](Git-Run rev-parse HEAD | Select-Object -First 1)).Trim()
    }

    function Taken([string]$Commit, [string]$Parent, [string]$Kind, [string[]]$Paths) {
        $o = [ordered]@{
            utc = (Get-Date).ToUniversalTime().ToString('o')
            outcome = 'taken'
            commit = $Commit
            pid = 4242
            kind = $Kind
            ref = $script:Ref
            parent = $Parent
        }
        if ($Kind -ne 'plain') {
            $o['pathCount'] = @($Paths).Count
            $o['paths'] = @($Paths)
        }
        return ($o | ConvertTo-Json -Compress -Depth 4)
    }

    function Run-Audit([string]$LogFile, [string[]]$Extra = @(), [bool]$WithFsck = $false) {
        $skip = @(if ($WithFsck) { } else { '-SkipFsck' })
        $out = & pwsh -NoProfile -File $Audit -RepoPath $repo -LogPath $LogFile @skip @Extra 2>&1
        return [pscustomobject]@{ Exit = $LASTEXITCODE; Text = (@($out) -join "`n") }
    }

    # ---- fixture repository: three real commits on the current branch
    Git-Run init -q | Out-Null
    $script:Ref = ([string](Git-Run symbolic-ref HEAD | Select-Object -First 1)).Trim()
    $c1 = Commit-File 'a.txt' 'first'
    $c2 = Commit-File 'b.txt' 'second'
    $c3 = Commit-File 'c.txt' 'third'
    $logDir = Join-Path $base 'logs'
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null

    # ---- (a) clean log
    $clean = Join-Path $logDir 'clean.jsonl'
    Set-Content -LiteralPath $clean -Encoding utf8 -Value @(
        (Taken $c1 $Zero 'plain' $null),
        (Taken $c2 $c1 'only-paths' @('b.txt'))
    )
    $a = Run-Audit $clean
    Report-Case 'clean log exits 0' ($a.Exit -eq 0) "exit $($a.Exit)`n$($a.Text)"
    Report-Case 'clean log prints no fail:' ($a.Text -notmatch 'fail:') $a.Text

    # ---- (b) broken log: missing commit, wrong parent, path outside the listed paths
    $broken = Join-Path $logDir 'broken.jsonl'
    Set-Content -LiteralPath $broken -Encoding utf8 -Value @(
        (Taken '0123456789abcdef0123456789abcdef01234567' $Zero 'plain' $null),
        (Taken $c2 ('1' * 40) 'only-paths' @('b.txt')),
        (Taken $c3 $c2 'only-paths' @('a.txt'))
    )
    $b = Run-Audit $broken
    Report-Case 'broken log exits 1' ($b.Exit -eq 1) "exit $($b.Exit)`n$($b.Text)"
    Report-Case 'broken log names fail:missing' ($b.Text -match 'fail:missing') $b.Text
    Report-Case 'broken log names fail:parent-mismatch' ($b.Text -match 'fail:parent-mismatch') $b.Text
    Report-Case 'broken log names fail:tree-outside-listed-paths' ($b.Text -match 'fail:tree-outside-listed-paths') $b.Text

    # ---- (c) same broken log as JSON
    $c = Run-Audit $broken @('-AsJson')
    $json = $null
    try { $json = $c.Text | ConvertFrom-Json -ErrorAction Stop } catch { }
    Report-Case 'json output parses' ($null -ne $json) $c.Text
    Report-Case 'json exit code matches' ($c.Exit -eq 1 -and $null -ne $json -and [int]$json.exitCode -eq 1) "exit $($c.Exit)"
    $classes = if ($null -ne $json) { @($json.entries | ForEach-Object { [string]$_.class }) -join ' ' } else { '' }
    Report-Case 'json entries name the three fail classes' (
        $classes -match 'fail:missing' -and $classes -match 'fail:parent-mismatch' -and $classes -match 'fail:tree-outside-listed-paths') $classes

    # ---- (d) a taken line with no kind/ref/parent is unchecked:no-commit-fields, never a FAIL
    $noFields = Join-Path $logDir 'nofields.jsonl'
    $bare = [ordered]@{ utc = (Get-Date).ToUniversalTime().ToString('o'); outcome = 'taken'; commit = $c1; pid = 4242 }
    Set-Content -LiteralPath $noFields -Encoding utf8 -Value @(($bare | ConvertTo-Json -Compress))
    $d = Run-Audit $noFields
    Report-Case 'commit-less line is unchecked:no-commit-fields and exits 0' ($d.Exit -eq 0 -and $d.Text -match 'unchecked:no-commit-fields' -and $d.Text -notmatch 'pre-step-1') "exit $($d.Exit)`n$($d.Text)"

    # ---- (e) healthy repository with fsck: ok, exit 0
    $fsckOk = Run-Audit $clean @() $true
    Report-Case 'fsck on a healthy repository exits 0 with no fsck warn or fail' ($fsckOk.Exit -eq 0 -and $fsckOk.Text -notmatch 'fsck-(commit-graph|corrupt)') "exit $($fsckOk.Exit)`n$($fsckOk.Text)"

    # ---- (f) a damaged commit-graph file: fsck fails, passes with core.commitGraph=false -> warn:fsck-commit-graph, exit 0
    Git-Run commit-graph write --reachable | Out-Null
    $graphFile = Join-Path $repo '.git\objects\info\commit-graph'
    if (Test-Path -LiteralPath $graphFile) {
        Set-ItemProperty -LiteralPath $graphFile -Name IsReadOnly -Value $false
        $graphBytes = [IO.File]::ReadAllBytes($graphFile)
        $graphBytes[$graphBytes.Length - 1] = $graphBytes[$graphBytes.Length - 1] -bxor 0xFF
        [IO.File]::WriteAllBytes($graphFile, $graphBytes)
        $graphProbe = & git -C $repo fsck --no-dangling 2>&1
        if ($LASTEXITCODE -eq 0) {
            Report-Case 'stale commit-graph fixture makes fsck fail (fixture check)' $false "fsck still passed: $graphProbe"
        }
        else {
            $f = Run-Audit $clean @() $true
            Report-Case 'stale commit-graph prints warn:fsck-commit-graph and exits 0' ($f.Exit -eq 0 -and $f.Text -match 'warn:fsck-commit-graph' -and $f.Text -notmatch 'fail:fsck') "exit $($f.Exit)`n$($f.Text)"
        }
        Remove-Item -LiteralPath $graphFile -Force
    }
    else {
        Report-Case 'commit-graph file was written (fixture check)' $false "missing $graphFile"
    }

    # ---- (g) a corrupt object: fsck fails even without the commit-graph -> fail:fsck-corrupt, exit 1
    $treeId = ([string](Git-Run rev-parse "$c1^{tree}" | Select-Object -First 1)).Trim()
    $objectFile = Join-Path $repo (".git\objects\{0}\{1}" -f $treeId.Substring(0, 2), $treeId.Substring(2))
    if (Test-Path -LiteralPath $objectFile) {
        Set-ItemProperty -LiteralPath $objectFile -Name IsReadOnly -Value $false
        [IO.File]::WriteAllBytes($objectFile, [byte[]](1..40))
        $g = Run-Audit $clean @() $true
        Report-Case 'corrupt object prints fail:fsck-corrupt and exits 1' ($g.Exit -eq 1 -and $g.Text -match 'fail:fsck-corrupt' -and $g.Text -notmatch 'warn:fsck-commit-graph') "exit $($g.Exit)`n$($g.Text)"
    }
    else {
        Report-Case 'loose tree object exists (fixture check)' $false "missing $objectFile"
    }
}
finally {
    if ((Split-Path -Parent $base) -eq 'c:\tmp' -and (Test-Path -LiteralPath $base)) {
        Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($Failures -gt 0) {
    Write-Host "$Failures case(s) failed"
    exit 1
}
Write-Host 'all cases passed'
exit 0
