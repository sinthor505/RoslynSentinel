<#
.SYNOPSIS
    Runs one comparison task in a pristine, history-free copy of the repo with a headless Claude Code
    session, then scores it. Arms: plain (built-in tools only), mcp (RoslynSentinel MCP + dotnet only),
    both.

.DESCRIPTION
    See docs/current/references/reference_tool_comparison_harness.md. Per run it:
      1. exports -Commit with `git archive` (no history, no uncommitted changes), strips docs/, .claude/,
         scripts/, ClaudeFiles/, CLAUDE.md so no plan, resolution note or hook can leak into the run;
      2. writes a neutral CLAUDE.md (plus one tool-guidance line for the mcp/both arms);
      3. commits that as a throwaway baseline, records gold answers, pre-builds (-SkipPrebuild to skip);
      4. runs `claude -p` with the task prompt, capturing stream-json to run.jsonl;
      5. calls Score-ToolComparison.ps1.
    Tasks run sequentially; never launch two runs at once against the same MCP server build.

.EXAMPLE
    .\scripts\Invoke-ToolComparison.ps1 -TaskId R2 -Arm plain -Model sonnet
    .\scripts\Invoke-ToolComparison.ps1 -TaskId N2 -Arm mcp -SetupOnly
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$TaskId,
    [Parameter(Mandatory)][ValidateSet('plain', 'mcp', 'both')][string]$Arm,
    [string]$Model = 'sonnet',
    # Pin this. Defaults to the current HEAD of the repo, printed and recorded in run.json.
    [string]$Commit = 'HEAD',
    [string]$Root = 'C:\tmp\toolcmp',
    [string]$ServerMode = 'claude-lean',
    # claude 2.1.296 has no --max-turns; spend and wall-clock are the two caps.
    [double]$MaxBudgetUsd = 5,
    [int]$TimeoutMinutes = 30,
    [switch]$SkipPrebuild,
    [switch]$SetupOnly,
    [switch]$RunTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
# `powershell -File` passes "D1,R2" as one string; accept both forms.
$TaskId = @($TaskId | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$manifest = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'tool-comparison\tasks.psd1')

function Invoke-Git([string]$Dir, [string[]]$GitArgs) {
    # Continue: git writes warnings (CRLF etc.) to stderr, which Stop would turn into a terminating error.
    $ErrorActionPreference = 'Continue'
    $out = & git -C $Dir @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed in ${Dir}: $out" }
    $out
}

function Get-TreeFiles([string]$Work) {
    # Tracked + untracked, honouring .gitignore, minus files deleted from disk.
    Invoke-Git $Work @('-c', 'core.quotepath=false', 'ls-files', '-co', '--exclude-standard') |
        Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $Work $_)) }
}

function Get-GoldFromRegex([string]$Work, $task) {
    $gold = New-Object System.Collections.Generic.List[string]
    $files = Get-TreeFiles $Work | Where-Object {
        $_ -match '\.cs$' -and $_ -notmatch '(^|/)(bin|obj)/' -and
        (-not $task.GoldExcludePath -or $_ -notmatch $task.GoldExcludePath)
    }
    foreach ($f in $files) {
        $lines = [System.IO.File]::ReadAllLines((Join-Path $Work $f))
        for ($i = 0; $i -lt $lines.Length; $i++) {
            if ($lines[$i] -match $task.GoldRegex -and $lines[$i].TrimStart() -notmatch '^//') {
                $gold.Add("$f`:$($i + 1)")
            }
        }
    }
    , $gold.ToArray()
}

function Test-BaselineChecks([string]$Work, $task) {
    foreach ($c in @($task.Checks)) {
        if (-not $c) { continue }
        $n = 0
        if ($c.Target -eq 'Path') {
            $n = @(Get-TreeFiles $Work | Where-Object { $_ -match $c.Pattern }).Count
        }
        else {
            foreach ($f in (Get-TreeFiles $Work | Where-Object { $_ -match $c.PathRegex -and $_ -notmatch '(^|/)(bin|obj)/' })) {
                $n += [regex]::Matches([System.IO.File]::ReadAllText((Join-Path $Work $f)), $c.Pattern).Count
            }
        }
        $note = ''
        if ($null -ne $c.Baseline -and $c.Baseline -ne $n) { $note = "  <-- MISMATCH, manifest says $($c.Baseline)" }
        Write-Host ("  baseline check '{0}': {1}{2}" -f $c.Name, $n, $note) -ForegroundColor $(if ($note) { 'Yellow' } else { 'Gray' })
    }
}

function ConvertTo-ArgString([string[]]$Items) {
    ($Items | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
}

# Preflight: every claude flag used below must exist in this CLI version.
$claude = (Get-Command claude -ErrorAction Stop).Source
$help = (& $claude --help 2>&1) -join "`n"
foreach ($flag in '--output-format', '--max-budget-usd', '--permission-mode', '--setting-sources', '--strict-mcp-config', '--mcp-config', '--allowedTools', '--disallowedTools', '--model') {
    if ($help -notmatch [regex]::Escape($flag)) { Write-Warning "claude --help does not mention $flag; adjust Invoke-ToolComparison.ps1 before trusting results." }
}

$resolved = (Invoke-Git $repo @('rev-parse', $Commit)).Trim()
$serverDirty = @(Invoke-Git $repo @('status', '--porcelain')).Count
Write-Host "Base commit $resolved (main tree has $serverDirty uncommitted path(s); mcp arm runs the server built from the main tree)."

foreach ($id in $TaskId) {
    $task = $manifest.Tasks | Where-Object { $_.Id -eq $id }
    if (-not $task) { throw "Unknown task id '$id'. Known: $(($manifest.Tasks | ForEach-Object Id) -join ', ')" }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $runDir = Join-Path $Root "$stamp-$id-$Arm"
    $work = Join-Path $runDir 'work'
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    Write-Host "`n== $id ($Arm) -> $runDir" -ForegroundColor Cyan

    # 1. Pristine export.
    $tar = Join-Path $runDir 'base.tar'
    Invoke-Git $repo @('archive', '--format=tar', '-o', $tar, $resolved) | Out-Null
    # Windows bsdtar explicitly: a Git-bash GNU tar earlier on PATH reads "C:" as a remote host.
    $tarExe = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path -LiteralPath $tarExe)) { $tarExe = 'tar' }
    & $tarExe -xf $tar -C $work
    if ($LASTEXITCODE -ne 0) { throw 'tar extract failed' }
    Remove-Item $tar
    foreach ($strip in 'docs', '.claude', 'scripts', 'ClaudeFiles', '.vscode', 'CLAUDE.md', '.mcp.json') {
        $p = Join-Path $work $strip
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
    }

    # 2. Neutral CLAUDE.md, identical across arms except the tool-guidance section.
    $claudeMd = @"
# Repository notes
A .NET solution: RoslynSentinel.slnx. Build with ``dotnet build RoslynSentinel.slnx``. Tests use NUnit.
Work only inside this directory. Do not run ``git commit``; leave changes in the working tree.
"@
    if ($Arm -ne 'plain') {
        $claudeMd += @"

## Tools
Use the roslyn_sentinel MCP tools to read, search and edit C#. Call LoadSolution with
$work\RoslynSentinel.slnx first if no solution is loaded. The shell is available for ``dotnet`` only.
"@
    }
    Set-Content -LiteralPath (Join-Path $work 'CLAUDE.md') -Value $claudeMd -Encoding UTF8

    # 3. Throwaway baseline repo (-f so the blanket *.json ignore does not drop global.json etc.).
    Invoke-Git $work @('init', '-q') | Out-Null
    # No line-ending conversion: it would corrupt the diff against the exported files.
    Invoke-Git $work @('config', 'core.autocrlf', 'false') | Out-Null
    Invoke-Git $work @('config', 'user.email', 'toolcmp@localhost') | Out-Null
    Invoke-Git $work @('config', 'user.name', 'toolcmp') | Out-Null
    Invoke-Git $work @('add', '-A', '-f') | Out-Null
    Invoke-Git $work @('commit', '-q', '-m', 'baseline') | Out-Null

    $gold = @()
    if ($task.Kind -eq 'answer') {
        if ($task.GoldRegex) { $gold = Get-GoldFromRegex $work $task } else { $gold = @($task.GoldFiles) }
        $goldLeaves = $gold | ForEach-Object { ($_ -replace ':\d+$', '') -split '/' | Select-Object -Last 1 } | Sort-Object -Unique
        Write-Host "  gold: $(@($gold).Count) hit(s) in $(@($goldLeaves).Count) file(s)" -ForegroundColor Gray
        if ($task.GoldHint) {
            $diff = Compare-Object ($task.GoldHint | Sort-Object) $goldLeaves
            if ($diff) { Write-Host "  gold differs from GoldHint: $(($diff | ForEach-Object { "$($_.SideIndicator)$($_.InputObject)" }) -join ', ')" -ForegroundColor Yellow }
        }
    }
    else { Test-BaselineChecks $work $task }

    if (-not $SkipPrebuild) {
        Write-Host '  pre-build (stripped tree must build)...' -ForegroundColor Gray
        $b = & dotnet build (Join-Path $work 'RoslynSentinel.slnx') -c Debug --nologo -v q -nr:false 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Baseline build failed in $work; stripped tree is not buildable.`n$($b | Select-Object -Last 15 | Out-String)" }
    }

    # 4. Prompt, MCP config, tool flags.
    $head = 'You are working in a copy of a .NET solution (RoslynSentinel.slnx) in the current directory.'
    $tail = if ($task.Kind -eq 'answer') {
        'Write your answer to ANSWER.md in the repository root as one `relative/path.cs:line` per line, nothing else. Do not modify any other file.'
    } else {
        'Leave your changes in the working tree and do not commit. The solution must still build. Finish with a one-paragraph summary.'
    }
    $prompt = "$head`n`n$($task.Prompt.Trim())`n`n$tail`n"
    $promptPath = Join-Path $runDir 'prompt.txt'
    Set-Content -LiteralPath $promptPath -Value $prompt -Encoding UTF8

    $mcpPath = Join-Path $runDir 'mcp.json'
    if ($Arm -eq 'plain') {
        Set-Content -LiteralPath $mcpPath -Value '{"mcpServers":{}}' -Encoding UTF8
    }
    else {
        $mcp = @{ mcpServers = @{ roslyn_sentinel = @{
                    command = 'powershell.exe'
                    args    = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repo 'scripts\roslynsentinel-mcp-launch.ps1'),
                        "--mode=$ServerMode", "--solution=$(Join-Path $work 'RoslynSentinel.slnx')",
                        '--replace-snippet-max-old-lines=300', '--replace-snippet-max-new-lines=300',
                        '--replace-snippet-max-old-chars=6000', '--replace-snippet-max-new-chars=6000')
                } } }
        Set-Content -LiteralPath $mcpPath -Value ($mcp | ConvertTo-Json -Depth 6) -Encoding UTF8
    }

    switch ($Arm) {
        'plain' { $allow = 'Bash,Read,Edit,Write,Glob,Grep'; $deny = '' }
        'mcp'   { $allow = 'mcp__roslyn_sentinel,Bash(dotnet:*)'; $deny = 'Read,Edit,Write,Glob,Grep,NotebookEdit' }
        'both'  { $allow = 'mcp__roslyn_sentinel,Bash,Read,Edit,Write,Glob,Grep'; $deny = '' }
    }
    $cliArgs = @('-p', '--output-format', 'stream-json', '--verbose', '--model', $Model, '--max-budget-usd', "$MaxBudgetUsd",
        '--permission-mode', 'acceptEdits', '--setting-sources', 'project', '--strict-mcp-config', '--mcp-config', $mcpPath,
        '--allowedTools', $allow)
    if ($deny) { $cliArgs += @('--disallowedTools', $deny) }

    $meta = [ordered]@{
        TaskId = $id; Arm = $Arm; Model = $Model; Commit = $resolved; ServerMode = $ServerMode
        MainTreeDirtyPaths = $serverDirty; WorkDir = $work; RunDir = $runDir; Started = (Get-Date).ToString('o')
        MaxBudgetUsd = $MaxBudgetUsd; TimeoutMinutes = $TimeoutMinutes; TimedOut = $false; Gold = @($gold)
        ClaudeArgs = $cliArgs
    }
    $meta | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDir 'run.json') -Encoding UTF8

    if ($SetupOnly) { Write-Host "  setup only; workspace ready at $work"; continue }

    # 5. Run.
    Write-Host '  running claude...' -ForegroundColor Gray
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $claude -ArgumentList (ConvertTo-ArgString $cliArgs) -WorkingDirectory $work -NoNewWindow -PassThru `
        -RedirectStandardInput $promptPath -RedirectStandardOutput (Join-Path $runDir 'run.jsonl') -RedirectStandardError (Join-Path $runDir 'stderr.txt')
    if (-not $proc.WaitForExit($TimeoutMinutes * 60000)) {
        $meta.TimedOut = $true
        & taskkill /PID $proc.Id /T /F | Out-Null
    }
    $sw.Stop()
    $meta.WallSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    $meta | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDir 'run.json') -Encoding UTF8
    Write-Host "  finished in $($meta.WallSeconds)s (timed out: $($meta.TimedOut))" -ForegroundColor Gray

    # 6. Score.
    & (Join-Path $PSScriptRoot 'Score-ToolComparison.ps1') -RunDir $runDir -RunTests:$RunTests
}
