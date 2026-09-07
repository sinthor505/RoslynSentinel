<#
.SYNOPSIS
    Replay a ModelEval agent run's transcript back to the model, plus a follow-up question,
    to get the model's own explanation for why a run failed - the "resume the failing
    transcript and ask the model why" technique documented in memory
    project_qwen36_35b_ladder_preferred_branch_extract_bug (that investigation found a real,
    fixable prompt-wording bug this way).

.DESCRIPTION
    Reads a run's transcript.json (SystemPrompt/UserPrompt/Turns, each turn carrying
    ModelMessage.Content/ReasoningContent and ToolCalls[].ArgumentsJson/ResultJson),
    reconstructs it into an OpenAI chat/completions message array (system, user, then
    alternating assistant messages - with tool_calls when the turn made any - and matching
    tool-role messages), appends one new user message asking the model to explain itself,
    and POSTs the whole thing to /v1/chat/completions on the chosen host.

    This is read-only against the repo (no source files touched) and makes exactly one
    outbound HTTP call per invocation (skipped entirely with -DryRun) - safe to run whenever,
    but avoid running it against a host that's also mid-batch on a live ModelEval run, since
    most LM Studio setups serve one request at a time and this will queue behind (or contend
    with) that batch's own requests.

.PARAMETER HostAddress
    LM Studio host to send the follow-up to. Known aliases: 112 (http://192.168.1.112:1234,
    GTX 1080) and 113 (http://192.168.1.113:1234, RTX 4060). Any other value is used verbatim
    as a base URL (e.g. http://192.168.10.85:1234) - matches roslynsentinel-modeleval.ps1's
    -HostAddress convention so the two scripts stay familiar side by side.

.PARAMETER TranscriptPath
    Path to a run's transcript.json, OR to the run's containing directory (in which case
    transcript.json is looked up inside it). Required unless -DryRun is used with an already-
    known path - still required either way, this just documents that both forms work.

.PARAMETER Question
    The follow-up user message appended after the replayed conversation. Required. Be specific
    and reference concrete details from the run (a checklist item that was claimed true but
    wasn't, a branch that was skipped, etc.) - a vague "why did you fail?" gets a vague answer.

.PARAMETER Model
    Model name sent in the request body. Default: qwen/qwen3.6-35b-a3b. Must match a model
    actually loaded on the target host - check with `curl http://<host>:1234/v1/models` first
    if unsure (see reference_lmstudio_loaded_models_endpoint memory).

.PARAMETER Temperature
    Sampling temperature. Default 0.1, matching project_lmstudio_sampling_params_for_code.

.PARAMETER TopP
    Nucleus sampling top-p. Default 0.7, matching project_lmstudio_sampling_params_for_code.

.PARAMETER TimeoutSec
    HTTP request timeout in seconds. Default 1800 (30 min) - CPU-only inference on larger
    contexts can take a long time; raise this rather than the call failing partway through.

.PARAMETER DryRun
    Skip the HTTP call entirely. Still reconstructs the full message array and writes it to
    <OutDir>\<run-name>-interrogation-request.json (and prints a human-readable transcript to
    the console) so you can sanity-check the reconstruction, or just read what happened,
    without spending any GPU/CPU time.

.PARAMETER OutDir
    Directory to write the request/response JSON into. Default: alongside the source
    transcript.json (same directory as -TranscriptPath resolves to).

.EXAMPLE
    .\roslynsentinel-interrogate.ps1 -HostAddress 113 `
        -TranscriptPath "ModelTestingResults\113\Model_AppliesSevenChainedRefactors\20260907-114903-737" `
        -Question "Your OrderCheckout.cs WriteFile used 'new()' against an IOrderPricingCalculator-typed field instead of 'new OrderPricingCalculator()' - that doesn't compile. Why did you write that, and why did your own verification checklist claim otherwise?"

    Replays a known-failing Seven-chain run back to qwen3.6-35b-a3b on .113 and asks it to
    explain the compile-breaking mistake its own checklist missed.

.EXAMPLE
    .\roslynsentinel-interrogate.ps1 -HostAddress 192.168.10.85:1234 -Model "qwen/qwen3-4b-thinking-2507" `
        -TranscriptPath "ModelTestingResults\113\Model_AppliesSevenChainedRefactors\20260907-114903-737\transcript.json" `
        -Question "Why did this fail?"

    Same run, but interrogated by a different model on a third box - useful for comparing
    how differently-sized/tuned models diagnose the same failure.

.EXAMPLE
    .\roslynsentinel-interrogate.ps1 -TranscriptPath "ModelTestingResults\112\Model_FixesWholeFileRewriteBug_MinimalGuidance\20260907-130015-395" -Question "why" -DryRun

    Just reconstructs and prints the conversation - no HostAddress/API call needed for a dry run,
    handy for eyeballing a transcript without spending any inference time.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$HostAddress,

    [Parameter(Mandatory)]
    [string]$TranscriptPath,

    [Parameter(Mandatory)]
    [string]$Question,

    [string]$Model = 'qwen/qwen3.6-35b-a3b',

    [double]$Temperature = 0.1,

    [double]$TopP = 0.7,

    [int]$TimeoutSec = 1800,

    [switch]$DryRun,

    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

$knownHosts = @{
    '112' = 'http://192.168.1.112:1234'
    '113' = 'http://192.168.1.113:1234'
}

if (-not $DryRun) {
    if (-not $HostAddress) {
        throw "-HostAddress is required unless -DryRun is passed."
    }
    if ($knownHosts.ContainsKey($HostAddress)) {
        $baseUrl = $knownHosts[$HostAddress]
    }
    elseif ($HostAddress -match '^https?://') {
        $baseUrl = $HostAddress.TrimEnd('/')
    }
    else {
        $baseUrl = "http://$HostAddress".TrimEnd('/')
        Write-Warning "'$HostAddress' is not a known host alias (112, 113) - using it verbatim as http://$HostAddress."
    }
}

if (Test-Path $TranscriptPath -PathType Container) {
    $resolvedTranscriptPath = Join-Path $TranscriptPath 'transcript.json'
}
else {
    $resolvedTranscriptPath = $TranscriptPath
}

if (-not (Test-Path $resolvedTranscriptPath)) {
    throw "Could not find transcript.json at '$resolvedTranscriptPath'."
}

$runDir = Split-Path $resolvedTranscriptPath -Parent
$runName = Split-Path $runDir -Leaf
if (-not $OutDir) {
    $OutDir = $runDir
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Write-Host "Reconstructing conversation from $resolvedTranscriptPath ..." -ForegroundColor Cyan
$transcript = Get-Content $resolvedTranscriptPath -Raw | ConvertFrom-Json

$messages = [System.Collections.Generic.List[object]]::new()
$messages.Add([ordered]@{ role = 'system'; content = $transcript.SystemPrompt })
$messages.Add([ordered]@{ role = 'user'; content = $transcript.UserPrompt })

$humanReadable = [System.Text.StringBuilder]::new()
[void]$humanReadable.AppendLine("=== SYSTEM ===")
[void]$humanReadable.AppendLine($transcript.SystemPrompt)
[void]$humanReadable.AppendLine()
[void]$humanReadable.AppendLine("=== USER (initial task) ===")
[void]$humanReadable.AppendLine($transcript.UserPrompt)
[void]$humanReadable.AppendLine()

$callCounter = 0
foreach ($turn in $transcript.Turns) {
    $mm = $turn.ModelMessage
    $content = $mm.Content
    $reasoning = $mm.ReasoningContent

    [void]$humanReadable.AppendLine("--- Turn $($turn.TurnNumber) ---")
    if ($reasoning) {
        [void]$humanReadable.AppendLine("[reasoning] $reasoning")
    }
    if ($content) {
        [void]$humanReadable.AppendLine("[content] $content")
    }

    $assistantContent = $null
    if ($reasoning) {
        $assistantContent = "<think>`n$reasoning`n</think>`n$content"
    }
    else {
        $assistantContent = $content
    }

    $assistantMsg = [ordered]@{ role = 'assistant' }

    $toolCalls = @($turn.ToolCalls)
    $replayIds = @()
    if ($toolCalls.Count -gt 0) {
        $toolCallEntries = @()
        foreach ($tc in $toolCalls) {
            $callCounter++
            $cid = "call_$callCounter"
            $replayIds += $cid
            [void]$humanReadable.AppendLine("[tool_call] $($tc.ToolName) $($tc.ArgumentsJson)")
            $toolCallEntries += [ordered]@{
                id       = $cid
                type     = 'function'
                function = [ordered]@{
                    name      = $tc.ToolName
                    arguments = $(if ($tc.ArgumentsJson) { $tc.ArgumentsJson } else { '{}' })
                }
            }
        }
        $assistantMsg.tool_calls = $toolCallEntries
        $assistantMsg.content = $(if ($assistantContent) { $assistantContent } else { $null })
    }
    else {
        $assistantMsg.content = $assistantContent
    }

    $messages.Add($assistantMsg)

    for ($i = 0; $i -lt $toolCalls.Count; $i++) {
        $resultJson = $toolCalls[$i].ResultJson
        [void]$humanReadable.AppendLine("[tool_result] $resultJson")
        $messages.Add([ordered]@{
            role         = 'tool'
            tool_call_id = $replayIds[$i]
            content      = $(if ($resultJson) { $resultJson } else { '' })
        })
    }
    [void]$humanReadable.AppendLine()
}

$messages.Add([ordered]@{ role = 'user'; content = $Question })
[void]$humanReadable.AppendLine("=== USER (interrogation follow-up) ===")
[void]$humanReadable.AppendLine($Question)

$humanReadablePath = Join-Path $OutDir "$runName-interrogation-transcript.txt"
$humanReadable.ToString() | Set-Content -Path $humanReadablePath -Encoding utf8
Write-Host "Wrote human-readable reconstruction to $humanReadablePath" -ForegroundColor DarkGray

$requestBody = [ordered]@{
    model       = $Model
    messages    = $messages
    temperature = $Temperature
    top_p       = $TopP
    stream      = $false
}
$requestJson = $requestBody | ConvertTo-Json -Depth 20
$requestPath = Join-Path $OutDir "$runName-interrogation-request.json"
$requestJson | Set-Content -Path $requestPath -Encoding utf8
Write-Host "Wrote request payload to $requestPath ($('{0:N0}' -f $requestJson.Length) chars)" -ForegroundColor DarkGray

if ($DryRun) {
    Write-Host ""
    Write-Host "-DryRun: skipping the API call. Reconstruction above is ready for review." -ForegroundColor Yellow
    return
}

Write-Host ""
Write-Host "POSTing to $baseUrl/v1/chat/completions (model=$Model, temp=$Temperature, top_p=$TopP) ..." -ForegroundColor Cyan
Write-Host "This can take a long time on CPU-only inference or large contexts - timeout is ${TimeoutSec}s." -ForegroundColor DarkGray

$response = Invoke-RestMethod -Uri "$baseUrl/v1/chat/completions" -Method Post -Body $requestJson -ContentType 'application/json' -TimeoutSec $TimeoutSec

$responsePath = Join-Path $OutDir "$runName-interrogation-response.json"
$response | ConvertTo-Json -Depth 20 | Set-Content -Path $responsePath -Encoding utf8
Write-Host "Wrote full response to $responsePath" -ForegroundColor DarkGray

$answer = $response.choices[0].message.content
$answerReasoning = $response.choices[0].message.reasoning_content

Write-Host ""
Write-Host "=== MODEL'S ANSWER ===" -ForegroundColor Green
if ($answerReasoning) {
    Write-Host "[reasoning]" -ForegroundColor DarkGray
    Write-Host $answerReasoning
    Write-Host ""
}
Write-Host $answer
