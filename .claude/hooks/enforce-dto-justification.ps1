# PreToolUse hook enforcing the DTO justification policy (skill dto-conventions, sections 1-2).
#
# A new data type (record, or a class/struct that carries data) added through the MCP write
# tools must be justified in the call's reason field: either a NEW-DTO line per type plus an
# Evaluated line, or a DTO-EXEMPT line. The hook makes an unjustified call fail at the moment it
# is attempted, naming the types and the two allowed forms.
#
# Contract: stdin receives PreToolUse JSON; exit 2 blocks the call and feeds stderr back to
# the model as the reason. Any other exit code lets the call proceed.
#
# FAIL-OPEN BY DESIGN: an empty or non-JSON payload, or any internal error, exits 0 (allow).
# A broken guardrail that halts all work is worse than no guardrail.
#
# Covered tools (bare name, or mcp__<server>__ prefixed as the harness sends it):
#   Member      operation addTopLevelType or addMember (case-insensitive). Types are parsed from
#               newMemberSource. Other operations (addTypedMember, remove, replace, view) never trigger.
#   CreateFile  typeKind class|record|struct triggers; the type name is typeName.
#               interface, enum and staticClass never trigger.
#   WriteFile   operation CreateFile|Create|ReplaceFile; types are parsed from content. For ReplaceFile,
#               or Create when the file exists, types already declared in the on-disk file are subtracted
#               (they are not new). A non-existent file means every parsed type is new.
#
# Scope: production .cs paths inside the repo only. Skipped: non-.cs paths; paths outside the repo
# (Test-PathOutsideRepo); any path with a \Worktree\ segment (harness clone); a bin or obj segment;
# a first segment under the repo root matching ^RoslynSentinel\.Tests; any Samples, TempCheck or
# DummyConsole segment (matched anywhere in the repo-relative path).
#
# Type detection is a regex, not Roslyn (a heuristic, like the other hooks). It matches record, class
# and struct declarations at line start after optional modifiers (modifiers are kept so static and
# abstract can be detected). interface, enum and delegate never match.
#
# Triggers: every record triggers. A class or struct triggers unless it is static or abstract, or its
# name ends in one of: Engine Tools Impl Helper Helpers Extensions Service Manager Factory Exception
# Attribute Builder Visitor Rewriter Walker Provider Handler Converter Comparer. Options types DO trigger.
#
# Allow routes, read from tool_input.reason (case-insensitive):
#   (a) DTO-EXEMPT: <why>   - DTO-EXEMPT: followed by at least 3 characters on that line
#                             (pattern: DTO-EXEMPT:[ \t]*\S[^\r\n]{2,}).
#   (b) NEW-DTO: <Name> ... - one NEW-DTO line naming EVERY triggering type, plus a non-empty
#                             Evaluated: line. The Wire impact: line is in the template but NOT enforced.
# Anything else is denied. No other bypass route is offered.
#
# Logging: one JSON line per call that has triggering types (allowed or denied), appended to
# <repo>\.claude\journal\dto-hook.jsonl. Logging failures are silent and never block.
#
# Tests (separate slice): pwsh -NoProfile -File .claude/hooks/enforce-dto-justification.Tests.ps1

$ErrorActionPreference = 'Stop'

# This script lives in <repo>\.claude\hooks\.
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\', '/')

$exemptSuffixes = @('Engine', 'Tools', 'Impl', 'Helper', 'Helpers', 'Extensions', 'Service', 'Manager', 'Factory',
                    'Exception', 'Attribute', 'Builder', 'Visitor', 'Rewriter', 'Walker', 'Provider', 'Handler',
                    'Converter', 'Comparer')

# Optional modifiers, then record/class/struct at line start, then the type name.
$declPattern = '(?m)^\s*(?<mods>(?:(?:public|internal|private|protected|file|sealed|abstract|static|partial|readonly|unsafe|new)\s+)*)(?<kind>record\s+struct|record\s+class|record|class|struct)\s+(?<name>\w+)'

function Deny([string]$message) {
    [Console]::Error.WriteLine($message)
    exit 2
}

function Test-ToolName([string]$name) {
    return ($toolName -ceq $name) -or $toolName.EndsWith("__$name", [StringComparison]::Ordinal)
}

function Test-PathOutsideRepo([string]$p) {
    if (-not $p) { return $false }
    $p = $p.Trim().Trim('"', "'")
    # Git-Bash style /c/foo -> C:\foo
    if ($p -match '^/([A-Za-z])/') { $p = ($Matches[1] + ':\' + $p.Substring(3)) }
    try {
        # Relative or unresolvable means "can't tell": treat as in-repo, i.e. keep enforcing.
        if (-not [System.IO.Path]::IsPathRooted($p)) { return $false }
        $full = [System.IO.Path]::GetFullPath($p).TrimEnd('\', '/')
    }
    catch { return $false }
    return -not ($full -eq $repoRoot -or $full.StartsWith("$repoRoot\", [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith("$repoRoot/", [StringComparison]::OrdinalIgnoreCase))
}

# Absolute path for a file argument (relative paths resolve against the repo root), or $null.
function Get-FullPathOrNull([string]$p) {
    if (-not $p) { return $null }
    $p = $p.Trim().Trim('"', "'")
    if ($p -match '^/([A-Za-z])/') { $p = ($Matches[1] + ':\' + $p.Substring(3)) }
    try {
        if (-not [System.IO.Path]::IsPathRooted($p)) { $p = Join-Path $repoRoot $p }
        return [System.IO.Path]::GetFullPath($p).TrimEnd('\', '/')
    }
    catch { return $null }
}

function Test-InScope([string]$p) {
    if (-not $p) { return $false }
    $trim = $p.Trim()
    if (-not $trim.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase)) { return $false }
    if ($trim -match '[\\/]Worktree[\\/]') { return $false }
    if (Test-PathOutsideRepo $trim) { return $false }
    $full = Get-FullPathOrNull $trim
    if (-not $full) { return $false }
    $rel = ''
    if ($full.Length -gt $repoRoot.Length) { $rel = $full.Substring($repoRoot.Length).TrimStart('\', '/') }
    if ($rel -match '^RoslynSentinel\.Tests') { return $false }
    if ($rel -match '(^|[\\/])(bin|obj)[\\/]') { return $false }
    if ($rel -match '(^|[\\/])(Samples|TempCheck|DummyConsole)[\\/]') { return $false }
    return $true
}

function Get-TypeDeclarations([string]$src) {
    $out = New-Object System.Collections.Generic.List[object]
    if ($src) {
        foreach ($m in [regex]::Matches($src, $declPattern)) {
            $out.Add([pscustomobject]@{
                Kind = ($m.Groups['kind'].Value -replace '\s+', ' ').ToLowerInvariant()
                Name = $m.Groups['name'].Value
                Mods = $m.Groups['mods'].Value
            })
        }
    }
    return $out.ToArray()
}

function Test-DtoTrigger([string]$kind, [string]$name, [string]$mods) {
    if ($kind.StartsWith('record', [StringComparison]::Ordinal)) { return $true }
    if ($mods -match '\b(static|abstract)\b') { return $false }
    foreach ($suffix in $exemptSuffixes) {
        if ($name.EndsWith($suffix, [StringComparison]::Ordinal)) { return $false }
    }
    return $true
}

function Write-DtoLog([string]$outcome, [string]$filePath, [string[]]$types, [string]$reason) {
    try {
        $dir = Join-Path $repoRoot '.claude\journal'
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $entry = [ordered]@{
            ts = (Get-Date).ToUniversalTime().ToString('o'); session = [string]$payload.session_id
            tool = $toolName; filePath = $filePath; types = @($types); outcome = $outcome; reason = $reason
        } | ConvertTo-Json -Compress
        [System.IO.File]::AppendAllText((Join-Path $dir 'dto-hook.jsonl'), $entry + "`n", (New-Object System.Text.UTF8Encoding($false)))
    }
    catch { }
}

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

    try {
        $payload = $raw | ConvertFrom-Json
    }
    catch {
        [Console]::Error.WriteLine("enforce-dto-justification.ps1: could not parse hook payload; DTO check SKIPPED for this call.")
        exit 0
    }

    $toolName  = [string]$payload.tool_name
    $toolInput = $payload.tool_input
    if (-not $toolInput) { exit 0 }

    # Declarations this call would add, each as Kind/Name/Mods, before the trigger filter.
    $candidates = New-Object System.Collections.Generic.List[object]
    $filePath = ''

    if (Test-ToolName 'Member') {
        if ([string]$toolInput.operation -in @('addTopLevelType', 'addMember')) {
            $filePath = [string]$toolInput.filePath
            if (-not (Test-InScope $filePath)) { exit 0 }
            foreach ($d in @(Get-TypeDeclarations ([string]$toolInput.newMemberSource))) { $candidates.Add($d) }
        }
    }
    elseif (Test-ToolName 'CreateFile') {
        $filePath = [string]$toolInput.filePath
        if (-not (Test-InScope $filePath)) { exit 0 }
        $typeKind = [string]$toolInput.typeKind
        if ($typeKind -in @('class', 'record', 'struct')) {
            $candidates.Add([pscustomobject]@{ Kind = $typeKind; Name = [string]$toolInput.typeName; Mods = '' })
        }
    }
    elseif (Test-ToolName 'WriteFile') {
        $op = [string]$toolInput.operation
        if ($op -in @('CreateFile', 'Create', 'ReplaceFile')) {
            $filePath = [string]$toolInput.filePath
            if (-not (Test-InScope $filePath)) { exit 0 }
            $declared = @(Get-TypeDeclarations ([string]$toolInput.content))

            # Types the file already declares on disk are not new. Only ReplaceFile, or Create over an
            # existing file, can have such types; a read failure subtracts nothing (more denials, not fewer).
            $onDisk = @()
            if ($op -in @('ReplaceFile', 'Create')) {
                $full = Get-FullPathOrNull $filePath
                if ($full -and (Test-Path -LiteralPath $full -PathType Leaf)) {
                    try { $onDisk = @(Get-TypeDeclarations (Get-Content -LiteralPath $full -Raw)) }
                    catch { $onDisk = @() }
                }
            }
            $existingNames = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($s in $onDisk) { [void]$existingNames.Add($s.Name) }
            foreach ($d in $declared) {
                if (-not $existingNames.Contains($d.Name)) { $candidates.Add($d) }
            }
        }
    }

    if ($candidates.Count -eq 0) { exit 0 }

    $triggering = New-Object System.Collections.Generic.List[object]
    foreach ($c in $candidates) {
        if (Test-DtoTrigger ([string]$c.Kind) ([string]$c.Name) ([string]$c.Mods)) { $triggering.Add($c) }
    }
    if ($triggering.Count -eq 0) { exit 0 }

    $names = @($triggering | ForEach-Object { $_.Name } | Select-Object -Unique)

    # Allow routes (see header).
    $reason = [string]$toolInput.reason
    $outcome = 'deny'
    if ($reason -match '(?i)DTO-EXEMPT:[ \t]*\S[^\r\n]{2,}') {
        $outcome = 'allow-exempt'
    }
    else {
        $declaredNames = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($m in [regex]::Matches($reason, '(?im)^\W*NEW-DTO:\s*(\w+)')) { [void]$declaredNames.Add($m.Groups[1].Value) }
        $allNamed = $true
        foreach ($n in $names) { if (-not $declaredNames.Contains($n)) { $allNamed = $false } }
        $evaluated = $false
        foreach ($m in [regex]::Matches($reason, '(?im)^\W*Evaluated:(?<t>[^\r\n]*)')) {
            if ($m.Groups['t'].Value.Trim().Length -gt 0) { $evaluated = $true }
        }
        if ($allNamed -and $evaluated) { $outcome = 'allow-newdto' }
    }

    Write-DtoLog $outcome $filePath $names $reason
    if ($outcome -ne 'deny') { exit 0 }

    $nameList = $names -join ', '
    $firstName = $names[0]
    Deny @"
BLOCKED by DTO justification policy: $toolName would add data type(s) with no justification.

  file:  $filePath
  types: $nameList

Each new data type (a record, or a class/struct that carries data) must be justified in the
reason field of this call, in one of these two forms (see the dto-conventions skill, sections 1-2):

  Form 1 - a new data type. One NEW-DTO line for EACH type named above, plus an Evaluated line:

    NEW-DTO: $firstName (<role>, <layer>.<domain>)
    Evaluated: <Existing1> - <why not>; <Existing2> - <why not>
    Wire impact: none | new tool output field(s)

  Form 2 - not a data type that needs a justification:

    DTO-EXEMPT: <why>

If the hook is wrong and this is not a data type, use:

    DTO-EXEMPT: hook false positive - <why>

and also append a journal line for the false positive (.claude/journal, see CLAUDE.md) and a line
starting with "HOOK-FALSE-POSITIVE:" in your report. Nothing was written.
"@
}
catch {
    # Fail open, but leave a trace so a silently-broken hook is discoverable.
    [Console]::Error.WriteLine("enforce-dto-justification.ps1 error (allowing call): $($_.Exception.Message)")
    exit 0
}

exit 0
