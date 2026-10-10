# Dot-sourced by usage-nudge.ps1: estimates how full the model context is and where auto-compaction
# will fire, so the nudge can warn a session to park in-flight work first.
#
# Context size = input + cache_read + cache_creation tokens of the last non-sidechain assistant record
# in the transcript (hook payload `transcript_path`). The transcript lines can be hundreds of KB, so only
# the file tail is read. Compaction point = autoCompactWindow (settings) minus the fixed autocompact
# buffer (~33k, as shown by /context). Observed auto compact_boundary preTokens: 217345 for a 250000 window.
#
# FAIL-OPEN: every function returns $null on any problem; callers treat that as "unknown, stay silent".

function Get-ContextUsage([string]$transcriptPath) {
    try {
        if ([string]::IsNullOrWhiteSpace($transcriptPath) -or -not (Test-Path -LiteralPath $transcriptPath)) { return $null }
        $fs = [IO.File]::Open($transcriptPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            foreach ($tailBytes in @(524288, 4194304)) {
                $start = [math]::Max(0, $fs.Length - $tailBytes)
                [void]$fs.Seek($start, [IO.SeekOrigin]::Begin)
                $buf = New-Object byte[] ($fs.Length - $start)
                $read = 0
                while ($read -lt $buf.Length) {
                    $n = $fs.Read($buf, $read, $buf.Length - $read)
                    if ($n -le 0) { break }
                    $read += $n
                }
                $lines = [Text.Encoding]::UTF8.GetString($buf, 0, $read) -split "`n"
                # The first line is a partial record when we started mid-file.
                $first = if ($start -gt 0) { 1 } else { 0 }
                for ($i = $lines.Length - 1; $i -ge $first; $i--) {
                    $l = $lines[$i]
                    if (-not $l.Contains('"type":"assistant"') -or -not $l.Contains('"usage"') -or $l.Contains('"isSidechain":true')) { continue }
                    try { $o = $l | ConvertFrom-Json } catch { continue }
                    $u = $o.message.usage
                    if ($o.type -ne 'assistant' -or $null -eq $u -or $o.isSidechain) { continue }
                    $tokens = [long]$u.input_tokens + [long]$u.cache_read_input_tokens + [long]$u.cache_creation_input_tokens
                    if ($tokens -le 0) { continue }
                    return @{ Tokens = $tokens; Model = [string]$o.message.model }
                }
                if ($start -eq 0) { break }
            }
        }
        finally { $fs.Dispose() }
    }
    catch { }
    return $null
}

function Get-CompactionPoint([string]$model) {
    try {
        $buffer = 33000
        if ($env:ROSLYNSENTINEL_CONTEXT_BUFFER -match '^\d+$') { $buffer = [int]$env:ROSLYNSENTINEL_CONTEXT_BUFFER }
        $window = 0
        if ($env:ROSLYNSENTINEL_CONTEXT_WINDOW -match '^\d+$') { $window = [int]$env:ROSLYNSENTINEL_CONTEXT_WINDOW }
        else {
            $files = if ($env:ROSLYNSENTINEL_CLAUDE_SETTINGS) { @($env:ROSLYNSENTINEL_CLAUDE_SETTINGS) } else {
                $proj = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
                @((Join-Path $proj '.claude/settings.local.json'), (Join-Path $proj '.claude/settings.json'), (Join-Path $env:USERPROFILE '.claude/settings.json'))
            }
            foreach ($f in $files) {
                if (-not (Test-Path -LiteralPath $f)) { continue }
                $s = Get-Content -LiteralPath $f -Raw | ConvertFrom-Json
                $m = if ($model -and $s.modelSettings) { $s.modelSettings.$model } else { $null }
                if ($m -and $m.autoCompactWindow) { $window = [int]$m.autoCompactWindow; break }
                if ($s.autoCompactWindow) { $window = [int]$s.autoCompactWindow; break }
            }
        }
        if ($window -le 0) { $window = 200000 }
        if ($window -le $buffer) { return $null }
        return $window - $buffer
    }
    catch { return $null }
}
