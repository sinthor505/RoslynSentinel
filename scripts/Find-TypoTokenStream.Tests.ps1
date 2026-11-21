# Regression check for Find-TypoTokenStream.ps1: it must read the streamed deltas of an old-format agent.log
# ("LM Studio streaming: <delta>") and of the newer ones ("<backend> streaming: <delta>", lmstudio or llama-server).
#
# Run:  pwsh -NoProfile -File scripts\Find-TypoTokenStream.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script, same style as Get-ScenarioSummary.Tests.ps1. Builds canned agent.log files in a THROWAWAY
# temp folder; no model and no host is used.

$ErrorActionPreference = 'Stop'

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

# One sample: a turn whose content streams "ShippingKinds.Oovernight" as .O|overn|ight, plus a tool-call line that must not
# count as a delta. $prefix is the logger text before the delta ("LM Studio streaming" or "<backend> streaming").
function New-CannedLog([string]$prefix) {
    $lines = @(
        '10:00:00.000 [Info] RoslynSentinel.Common.AgentLoop.ModelAgentRunner: Agent run starting in C:\fixture\sample-01. Exposing 18 tools.'
    )
    $ms = 100
    foreach ($delta in @('Use ', 'ShippingKinds', '.O', 'overn', 'ight', ';')) {
        $lines += ('10:00:01.{0:000} [Info] RoslynSentinel.Common.LmStudioAgentClient: {1}: {2}' -f $ms, $prefix, $delta)
        $ms += 10
    }
    $lines += ('10:00:02.000 [Info] RoslynSentinel.Common.LmStudioAgentClient: {0}: tool call ReadFile({{"filePath":"ShippingKinds.Oovernight"}})' -f $prefix)
    $lines += '10:00:03.000 [Info] RoslynSentinel.Common.AgentLoop.ModelAgentRunner: Turn 1: model responded in 00:00:02 - 1 tool call(s).'
    return ($lines -join "`n") + "`n"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-typotoken-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
    $script = Join-Path $PSScriptRoot 'Find-TypoTokenStream.ps1'
    $cases = @(
        @{ Name = 'old format "LM Studio streaming"'; Prefix = 'LM Studio streaming' },
        @{ Name = 'new format "lmstudio streaming"'; Prefix = 'lmstudio streaming' },
        @{ Name = 'new format "llama-server streaming"'; Prefix = 'llama-server streaming' }
    )
    foreach ($case in $cases) {
        $folder = Join-Path $tempRoot ($case.Prefix -replace '[^A-Za-z0-9]', '_')
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $folder 'agent.log'), (New-CannedLog $case.Prefix))

        $output = (& pwsh -NoProfile -File $script -Path $folder 2>&1 | Out-String)
        Assert-That ($LASTEXITCODE -eq 0) "$($case.Name): script exits 0"
        Assert-That ($output -match 'streams with deltas: 1\b') "$($case.Name): one stream with deltas found"
        Assert-That ($output -match 'deltas: 6\b') "$($case.Name): six deltas read (the tool-call line is not a delta)"
        Assert-That ($output -match 'typos: 1\b') "$($case.Name): one typo found"
        Assert-That ($output -match [regex]::Escape('ShippingKinds.Oovernight (typo of Overnight)')) "$($case.Name): reports Oovernight as a typo of Overnight"
        Assert-That ($output -match [regex]::Escape('tokenized .O|overn|ight')) "$($case.Name): reports the .O|overn|ight tokenization"
    }
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) assertion(s) failed."
    exit 1
}
Write-Host 'All Find-TypoTokenStream assertions passed.'
exit 0
