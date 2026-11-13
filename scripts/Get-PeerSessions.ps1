# Lists the live Claude sessions on this box as: peer name | tab title | session id (8) | status.
#
# Peer name  = ~/.claude/sessions/<pid>.json "name" (what ListAgents / SendMessage use).
# Tab title  = last "ai-title" entry in ~/.claude/projects/<project>/<sessionId>.jsonl (what the VS Code
#              tab shows, shortened). The transcript file name IS the session id. The ListAgents
#              [ref] is NOT a prefix of the session id, so it cannot be used for this join.
# Titles are rewritten as a session goes on, so the latest one is taken on each run.
#
# Usage: pwsh -NoProfile -File scripts/Get-PeerSessions.ps1 [-SelfSessionId <id>] [-AsText]
#   -SelfSessionId  marks that row "(you)".
#   -AsText         one line per session instead of objects (used by .claude/hooks/peer-names.ps1).
# Read-only. Fail-soft: an unreadable session file is skipped.
param(
    [string]$SelfSessionId = '',
    [switch]$AsText
)

$ErrorActionPreference = 'Stop'
$claude = Join-Path $env:USERPROFILE '.claude'
$sessionsDir = Join-Path $claude 'sessions'
$projectsDir = Join-Path $claude 'projects'
if (-not (Test-Path $sessionsDir)) { return }

# The ai-title entry repeats throughout the transcript, so the last 256 KB is enough and avoids reading
# multi-megabyte files.
function Get-LastAiTitle([string]$path) {
    $fs = [System.IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try {
        $len = $fs.Length
        $take = [Math]::Min($len, 262144)
        $null = $fs.Seek($len - $take, 'Begin')
        $buf = New-Object byte[] $take
        $read = $fs.Read($buf, 0, $take)
        $text = [System.Text.Encoding]::UTF8.GetString($buf, 0, $read)
    }
    finally { $fs.Dispose() }
    $m = [regex]::Matches($text, '"aiTitle":"((?:[^"\\]|\\.)*)"')
    if ($m.Count -eq 0) { return '' }
    # The title is JSON-escaped text; ConvertFrom-Json on a quoted string unescapes it.
    try { return ('"' + $m[$m.Count - 1].Groups[1].Value + '"') | ConvertFrom-Json } catch { return $m[$m.Count - 1].Groups[1].Value }
}

$rows = foreach ($f in Get-ChildItem $sessionsDir -Filter '*.json' -File) {
    try {
        $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
        if (-not $j.sessionId -or -not (Get-Process -Id $j.pid -ErrorAction SilentlyContinue)) { continue }
        $title = ''
        foreach ($pd in Get-ChildItem $projectsDir -Directory) {
            $tp = Join-Path $pd.FullName "$($j.sessionId).jsonl"
            if (Test-Path $tp) { $title = Get-LastAiTitle $tp; break }
        }
        [pscustomobject]@{
            Name   = [string]$j.name
            Tab    = $title
            Id8    = $j.sessionId.Substring(0, 8)
            Status = [string]$j.status
            Self   = ($SelfSessionId -ne '' -and $j.sessionId -eq $SelfSessionId)
        }
    }
    catch { continue }
}

$rows = @($rows | Sort-Object Name)
if ($AsText) {
    foreach ($r in $rows) {
        $tab = if ($r.Tab) { $r.Tab } else { '(no tab title yet)' }
        "$($r.Name) | tab: $tab | $($r.Id8) | $($r.Status)$(if ($r.Self) { ' | (you)' })"
    }
}
else { $rows }
