<#
.SYNOPSIS
  Self-test (acceptance fixture) for Test-CommitRouteLog.ps1.

.DESCRIPTION
  Builds a throwaway git repository under c:\tmp with three real commits, then runs the audit against:
    (a) a hand-built clean log: expect exit 0 and no fail:* line;
    (b) a hand-built broken log: expect exit 1 and each of fail:missing, fail:parent-mismatch and
        fail:tree-outside-listed-paths;
    (c) the broken log with -AsJson: expect exit 1, parseable JSON, exitCode 1 and the same three fail classes.
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

    function Run-Audit([string]$LogFile, [string[]]$Extra = @()) {
        $out = & pwsh -NoProfile -File $Audit -RepoPath $repo -LogPath $LogFile -SkipFsck @Extra 2>&1
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
