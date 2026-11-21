# Regression harness for scripts\roslynsentinel-scratch.ps1 (scratch root, results root, test-runs root,
# eval-checkout detection and build dir).
#
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\roslynsentinel-scratch.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as scripts\roslynsentinel-instances-common.Tests.ps1. Every path lives under a
# throwaway folder in the temp folder; ROSLYNSENTINEL_SCRATCH_ROOT and ROSLYNSENTINEL_RESULTS_ROOT are set and
# restored around each case in try/finally.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'roslynsentinel-scratch.ps1')

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

# Runs $Body with the two root variables set to $Scratch / $Results ($null clears them), then restores them.
function Invoke-WithRootEnv {
    [CmdletBinding()]
    param([string]$Scratch, [string]$Results, [Parameter(Mandatory)][scriptblock]$Body)

    $names = @('ROSLYNSENTINEL_SCRATCH_ROOT', 'ROSLYNSENTINEL_RESULTS_ROOT')
    $saved = @{}
    foreach ($n in $names) { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
    try {
        [Environment]::SetEnvironmentVariable('ROSLYNSENTINEL_SCRATCH_ROOT', $Scratch)
        [Environment]::SetEnvironmentVariable('ROSLYNSENTINEL_RESULTS_ROOT', $Results)
        & $Body
    }
    finally {
        foreach ($n in $names) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
    }
}

# Returns the message of the exception $Block throws, or $null when it does not throw.
function Get-ThrownMessage([scriptblock]$Block) {
    try { $null = & $Block; return $null }
    catch { return $_.Exception.Message }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-scratch-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    $repo = Join-Path $tempRoot 'parent\repo'
    $beside = Join-Path $tempRoot 'parent\RoslynSentinel-ScratchDir'
    $explicit = Join-Path $tempRoot 'explicit'

    # --- Scratch root ---
    Invoke-WithRootEnv -Scratch "$tempRoot\envscratch\" -Body {
        $got = Get-RoslynSentinelScratchRoot -RepoRoot $repo
        Assert-That ($got -eq "$tempRoot\envscratch") 'Scratch: absolute env wins and trailing backslash is trimmed'
    }

    Invoke-WithRootEnv -Scratch 'relative\scratch' -Body {
        $msg = Get-ThrownMessage { Get-RoslynSentinelScratchRoot -RepoRoot $repo }
        Assert-That ($msg -and $msg -match 'ROSLYNSENTINEL_SCRATCH_ROOT' -and $msg -match 'relative\\scratch') 'Scratch: relative env throws and the message names the variable and value'
    }

    Invoke-WithRootEnv -Scratch '%RS_SCRATCH_TEST_UNSET_VAR%\scratch' -Body {
        $msg = Get-ThrownMessage { Get-RoslynSentinelScratchRoot -RepoRoot $repo }
        Assert-That ($msg -and $msg -match 'ROSLYNSENTINEL_SCRATCH_ROOT') 'Scratch: unexpanded %VAR% in env throws'
    }

    Invoke-WithRootEnv -Body {
        $got = Get-RoslynSentinelScratchRoot -RepoRoot $repo
        Assert-That ($got -eq $beside) 'Scratch: default sits beside the repo'
    }

    Invoke-WithRootEnv -Body {
        $got = Get-RoslynSentinelScratchRoot -RepoRoot 'C:\'
        Assert-That ($got -eq (Join-Path $env:TEMP 'RoslynSentinel-ScratchDir')) 'Scratch: drive-root repo falls back to TEMP'
    }

    # --- Results root ---
    Invoke-WithRootEnv -Body {
        $got = Get-RoslynSentinelResultsRoot -RepoRoot $repo
        Assert-That ($got -eq "$beside\ModelTestingResults") 'Results: default is ModelTestingResults under the scratch default'
    }

    Invoke-WithRootEnv -Results "$tempRoot\results\" -Body {
        $got = Get-RoslynSentinelResultsRoot -RepoRoot $repo
        Assert-That ($got -eq "$tempRoot\results") 'Results: RESULTS_ROOT override beats the scratch default (trailing backslash trimmed)'
    }

    Invoke-WithRootEnv -Results 'relative\results' -Body {
        $msg = Get-ThrownMessage { Get-RoslynSentinelResultsRoot -RepoRoot $repo }
        Assert-That ($msg -and $msg -match 'ROSLYNSENTINEL_RESULTS_ROOT') 'Results: relative RESULTS_ROOT throws'
    }

    # --- -ScratchRoot beats env and default ---
    Invoke-WithRootEnv -Scratch "$tempRoot\envscratch" -Results "$tempRoot\envresults" -Body {
        $res = Get-RoslynSentinelResultsRoot -RepoRoot $repo -ScratchRoot $explicit
        Assert-That ($res -eq "$explicit\ModelTestingResults") 'ScratchRoot: beats env scratch and ignores RESULTS_ROOT for the results root'

        $runs = Get-RoslynSentinelTestRunsRoot -RepoRoot $repo -ScratchRoot $explicit
        Assert-That ($runs -eq "$explicit\TestRuns") 'ScratchRoot: beats env scratch for the test-runs root'

        $build = Get-RoslynSentinelBuildDir -RepoRoot $repo -Suffix 'Advanced' -ScratchRoot $explicit
        Assert-That ($build -eq "$explicit\build\Advanced") 'ScratchRoot: beats env scratch for the build dir'
    }

    # --- Prefix ---
    Invoke-WithRootEnv -Body {
        $build = Get-RoslynSentinelBuildDir -RepoRoot $repo -Suffix 'Advanced' -Prefix 'ev_' -ScratchRoot $explicit
        Assert-That ($build -eq "$explicit\build\ev_Advanced") 'Build dir: -Prefix ev_ is prepended to the suffix under build\'
    }

    # --- Marker files: eval checkout ---
    Invoke-WithRootEnv -Body {
        $evalRepo = Join-Path $tempRoot 'evalcheckout'
        New-Item -ItemType Directory -Path $evalRepo -Force | Out-Null

        Set-Content -LiteralPath (Join-Path $evalRepo 'complete') -Value 'marker'
        $oneMarkerBuild = Get-RoslynSentinelBuildDir -RepoRoot $evalRepo -Suffix 'Advanced' -Prefix 'ev_' -ScratchRoot $explicit
        Assert-That ((-not (Test-RoslynSentinelEvalCheckout -RepoRoot $evalRepo)) -and $oneMarkerBuild -eq "$explicit\build\ev_Advanced") 'Eval checkout: a single marker is not enough, build dir stays in scratch'

        Set-Content -LiteralPath (Join-Path $evalRepo 'lastused') -Value 'marker'
        Assert-That (Test-RoslynSentinelEvalCheckout -RepoRoot $evalRepo) 'Eval checkout: complete and lastused together are detected'
        $build = Get-RoslynSentinelBuildDir -RepoRoot $evalRepo -Suffix 'Advanced' -Prefix 'ev_' -ScratchRoot $explicit
        Assert-That ($build -eq "$evalRepo\_scratchbuild_ev_Advanced") 'Eval checkout: markers flip the build dir to the checkout-local name'
    }
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) FAILED"; exit 1 }
Write-Host 'All passed.'
exit 0
