<#
.SYNOPSIS
    Reads the model's next-token probabilities at the exact point where it writes ShippingKinds.Overnight: P(".") vs P(".O").

.DESCRIPTION
    Rebuilds a saved ShippingKind conversation from transcript.json the way LmStudioAgentClient sends it to /v1/responses (system and
    user message, then per kept turn: reasoning item, message text, function calls, function call outputs; the tool list from the
    transcript header), then appends a partial assistant reply that ends right before the decision token and asks LM Studio for one
    token with top_logprobs. LM Studio continues a trailing assistant message, so the returned distribution is the model's choice at
    that point: "." (then "Over" "night", the only correct path) or ".O" (every observed misspelling starts with it).

    The decision prefix comes from turn KeepTurns+1 of the same transcript: the text up to the first "ShippingKinds.O..." in the
    chosen channel. Use a ShippingKindReplay sample (sample-NN\transcript.json, seeded turns + one live turn) or a full run.
      -Channel content    the live turn's reasoning goes in as a reasoning item, the content prefix as the assistant message. This is
                          exact: logprobs are reported for message content.
      -Channel reasoning  the reasoning prefix is sent as the assistant message "<think>\n" + prefix, so the model continues inside its
                          think block; LM Studio still reports logprobs for that continuation. -ReasoningAsText drops the "<think>"
                          (plain-text approximation, which measured far lower P(".O")).
      -Channel auto       content if the live turn's content has the decision, otherwise reasoning (default).

    Per variant it makes three one-token requests: at the decision, after "." and after ".O". Variants (all by default):
      Baseline            the conversation as recorded.
      Capitalized         quoted "standard"/"express"/"overnight"/"pickup" become "Standard"... everywhere (history and prefix).
      ExampleOvernight    the user prompt's example "ShippingKinds.Express" becomes "ShippingKinds.Overnight" (a prompt change).
      QualifiedResult     the ReadFile result of ShippingKinds.cs gets a "qualifiedMembers" field listing ShippingKinds.Standard,
                          ...Overnight... (stands in for a tool result that shows the qualified names: a change we could ship).

    "Sampled" is the probability of drawing ".O" under the sampler settings (-Temperature, -TopK, -TopP, -MinP; llama.cpp order
    top-k, top-p, min-p, temperature), computed from the returned top candidates, so it is an estimate.

.PARAMETER Transcript
    transcript.json (or its folder) holding the kept turns and the live turn.

.PARAMETER KeepTurns
    Turns 1..K are history; the decision prefix comes from turn K+1. Default: the turn after the first ReadFile of ShippingKinds.cs.

.PARAMETER HostAddress
    112, 113 or 85 (LM Studio on port 1234). Or pass -BaseUrl.

.PARAMETER Model
    Model key. Default: the model loaded on the host (fails if none or several).

.EXAMPLE
    pwsh scripts/Probe-OvernightLogprobs.ps1 -Transcript c:\tmp\replay-tiel-original\sample-02 -HostAddress 113
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Transcript,
    [int]$KeepTurns,
    [ValidateSet('auto', 'content', 'reasoning')][string]$Channel = 'auto',
    [ValidateSet('Baseline', 'Capitalized', 'ExampleOvernight', 'QualifiedResult')][string[]]$Variant = @('Baseline', 'Capitalized', 'ExampleOvernight', 'QualifiedResult'),
    [string]$HostAddress,
    [string]$BaseUrl,
    [string]$Model,
    [switch]$NoReasoningReplay,
    [switch]$ReasoningAsText,
    [ValidateSet('Inline', 'None', 'Native')][string]$ToolsMode = 'Inline',
    [int]$TopLogprobs = 10,
    [double]$Temperature = 0.6,
    [int]$TopK = 20,
    [double]$TopP = 0.95,
    [double]$MinP = 0.05,
    [switch]$ShowPrefix
)

$ErrorActionPreference = 'Stop'

if (-not $BaseUrl) {
    if (-not $HostAddress) { throw 'Pass -HostAddress (112, 113, 85) or -BaseUrl.' }
    $BaseUrl = switch ($HostAddress) { '85' { 'http://192.168.10.85:1234' } default { "http://192.168.1.$($HostAddress):1234" } }
}
$BaseUrl = $BaseUrl.TrimEnd('/') -replace '/v1$', ''

$path = if (Test-Path -LiteralPath $Transcript -PathType Container) { Join-Path $Transcript 'transcript.json' } else { $Transcript }
$t = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 100
$turns = @($t.Turns)

if (-not $Model) {
    $loaded = @((Invoke-RestMethod "$BaseUrl/api/v1/models" -TimeoutSec 15).models | Where-Object { $_.loaded_instances.Count -gt 0 } | ForEach-Object key)
    if ($loaded.Count -ne 1) { throw "Pass -Model: $($loaded.Count) model(s) loaded on $BaseUrl ($($loaded -join ', '))." }
    $Model = $loaded[0]
}

if (-not $KeepTurns) {
    $readTurn = $turns | Where-Object { $_.ToolCalls | Where-Object { $_.ToolName -eq 'ReadFile' -and [string]$_.ResultJson -match 'class ShippingKinds' } } | Select-Object -First 1
    if (-not $readTurn) { throw 'No ReadFile of ShippingKinds.cs in the transcript: pass -KeepTurns.' }
    $KeepTurns = [int]$readTurn.TurnNumber
}
if ($turns.Count -le $KeepTurns) { throw "The transcript has $($turns.Count) turn(s); turn $($KeepTurns + 1) is needed for the decision prefix." }
$live = $turns[$KeepTurns]

# --- the decision prefix -------------------------------------------------------------------------------------------------------
$decisionRegex = [regex]'ShippingKinds(?=\.O)'
$contentText = [string]$live.ModelMessage.Content
$reasoningText = [string]$live.ModelMessage.ReasoningContent
$inContent = $decisionRegex.Match($contentText)
$inReasoning = $decisionRegex.Match($reasoningText)
$useChannel = if ($Channel -ne 'auto') { $Channel } elseif ($inContent.Success) { 'content' } else { 'reasoning' }
$cut = if ($useChannel -eq 'content') { $inContent } else { $inReasoning }
if (-not $cut.Success) {
    throw "Turn $($KeepTurns + 1) has no 'ShippingKinds.O...' in its $useChannel (content: $($inContent.Success), reasoning: $($inReasoning.Success)). Pick another -Channel or transcript."
}
$prefixSource = if ($useChannel -eq 'content') { $contentText } else { $reasoningText }
$prefix = $prefixSource.Substring(0, $cut.Index + $cut.Length)
$recorded = [regex]::Match($prefixSource.Substring($cut.Index + $cut.Length), '^\.[A-Za-z]+').Value

# --- variants --------------------------------------------------------------------------------------------------------------------
$literalRegex = [regex]'(\\?")(standard|express|overnight|pickup)(\\?")'
function Convert-Text([string]$text, [string]$variant, [string]$kind) {
    if ([string]::IsNullOrEmpty($text)) { return $text }
    switch ($variant) {
        'Capitalized' { return $literalRegex.Replace($text, { param($m) $m.Groups[1].Value + $m.Groups[2].Value.Substring(0, 1).ToUpperInvariant() + $m.Groups[2].Value.Substring(1) + $m.Groups[3].Value }) }
        'ExampleOvernight' { if ($kind -eq 'user') { return $text.Replace('(for example ShippingKinds.Express)', '(for example ShippingKinds.Overnight)') } }
        'QualifiedResult' {
            if ($kind -eq 'result' -and $text -match 'class ShippingKinds' -and $text.StartsWith('{')) {
                $names = @('Standard', 'Express', 'Overnight', 'Pickup') | ForEach-Object { '"ShippingKinds.' + $_ + '"' }
                return '{"qualifiedMembers":[' + ($names -join ',') + '],' + $text.Substring(1)
            }
        }
    }
    return $text
}

function New-Input([string]$variant, [string]$assistantText) {
    $items = [System.Collections.Generic.List[object]]::new()
    $items.Add(@{ type = 'message'; role = 'system'; content = (Convert-Text $t.SystemPrompt $variant 'system') + $inlineTools })
    $items.Add(@{ type = 'message'; role = 'user'; content = (Convert-Text $t.UserPrompt $variant 'user') })
    $n = 0
    foreach ($turn in $turns[0..($KeepTurns - 1)]) {
        $msg = $turn.ModelMessage
        if (-not $NoReasoningReplay -and $msg.ReasoningContent) {
            $items.Add(@{ type = 'reasoning'; id = "rs_probe$((++$n))"; summary = @(); content = @(@{ type = 'reasoning_text'; text = (Convert-Text $msg.ReasoningContent $variant 'reasoning') }) })
        }
        if ($msg.Content) { $items.Add(@{ type = 'message'; role = 'assistant'; content = (Convert-Text $msg.Content $variant 'content') }) }
        foreach ($call in @($msg.ToolCalls)) {
            $items.Add(@{ type = 'function_call'; call_id = $call.Id; name = $call.Name; arguments = (Convert-Text $call.ArgumentsJson $variant 'arguments') })
        }
        foreach ($rec in @($turn.ToolCalls)) {
            $items.Add(@{ type = 'function_call_output'; call_id = $rec.ToolCallId; output = (Convert-Text $rec.ResultJson $variant 'result') })
        }
    }
    if ($useChannel -eq 'content' -and -not $NoReasoningReplay -and $reasoningText) {
        $items.Add(@{ type = 'reasoning'; id = 'rs_probe_live'; summary = @(); content = @(@{ type = 'reasoning_text'; text = (Convert-Text $reasoningText $variant 'reasoning') }) })
    }
    $items.Add(@{ type = 'message'; role = 'assistant'; content = $assistantText })
    return , $items.ToArray()
}

$tools = foreach ($name in @($t.Tools)) {
    @{ type = 'function'; name = $name; description = $t.ToolDescriptions.$name; parameters = $t.ToolSchemas.$name }
}

# LM Studio refuses logprobs together with a tools list ("logprobs is not supported with tools + stream"). Inline mode writes the
# tool list into the system message in the common Qwen layout instead; it approximates what the chat template renders (check it
# against the rendered prompt in an LM Studio trace log). None drops the tools; Native sends them (fails until LM Studio allows it).
$inlineTools = ''
if ($ToolsMode -eq 'Inline' -and $tools) {
    $lines = foreach ($tool in $tools) {
        @{ type = 'function'; function = @{ name = $tool.name; description = $tool.description; parameters = $tool.parameters } } | ConvertTo-Json -Depth 60 -Compress
    }
    $inlineTools = "`n`n# Tools`n`nYou may call one or more functions to assist with the user query.`n`nYou are provided with function signatures within <tools></tools> XML tags:`n<tools>`n" +
        ($lines -join "`n") + "`n</tools>`n`nFor each function call, return a json object with function name and arguments within <tool_call></tool_call> XML tags:`n<tool_call>`n{""name"": <function-name>, ""arguments"": <args-json-object>}`n</tool_call>"
}

function Get-NextToken([string]$variant, [string]$assistantText) {
    $body = @{
        model = $Model; input = (New-Input $variant $assistantText)
        max_output_tokens = 1; temperature = 0; top_logprobs = $TopLogprobs; include = @('message.output_text.logprobs')
    }
    if ($ToolsMode -eq 'Native') { $body.tools = @($tools); $body.tool_choice = 'auto' }
    $r = Invoke-RestMethod -Uri "$BaseUrl/v1/responses" -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 60 -Compress) -TimeoutSec 900
    $message = $r.output | Where-Object { $_.type -eq 'message' } | Select-Object -First 1
    $first = $message.content | Select-Object -First 1
    $tok = $first.logprobs | Select-Object -First 1
    if (-not $tok) { throw "No logprobs in the response (output types: $(($r.output | ForEach-Object type) -join ', '))." }
    return @($tok.top_logprobs | ForEach-Object { [pscustomobject]@{ Token = $_.token; P = [Math]::Exp($_.logprob) } })
}

function Get-SampledProbability($candidates, [string]$token) {
    $kept = @($candidates | Sort-Object P -Descending | Select-Object -First $TopK)
    $sum = 0.0; $topPKept = foreach ($c in $kept) { if ($sum -lt $TopP) { $c; $sum += $c.P } }
    $max = ($topPKept | Measure-Object P -Maximum).Maximum
    $minPKept = @($topPKept | Where-Object { $_.P -ge $MinP * $max })
    $weights = $minPKept | ForEach-Object { [pscustomobject]@{ Token = $_.Token; W = [Math]::Pow($_.P, 1.0 / $Temperature) } }
    $total = ($weights | Measure-Object W -Sum).Sum
    $hit = $weights | Where-Object { $_.Token -ceq $token } | Select-Object -First 1
    if ($hit) { return $hit.W / $total } else { return 0.0 }
}

function Format-Top($candidates, [int]$count = 5) {
    ($candidates | Select-Object -First $count | ForEach-Object { '[{0}] {1:P1}' -f ($_.Token -replace "`n", '\n'), $_.P }) -join '  '
}

"Model $Model at $BaseUrl; transcript $path"
"History: turns 1..$KeepTurns; decision prefix from turn $($KeepTurns + 1) $useChannel$(if ($useChannel -eq 'reasoning') { if ($ReasoningAsText) { ' (as plain text: approximation)' } else { ' (inside <think>)' } }); recorded next: '$recorded'; tools $ToolsMode"
if ($ShowPrefix) { 'Prefix tail: ...' + $prefix.Substring([Math]::Max(0, $prefix.Length - 300)) }
''

$rows = foreach ($v in $Variant) {
    $p = Convert-Text $prefix $v $useChannel
    # Opening the think block puts the prefix back inside the reasoning: LM Studio still reports logprobs for the continuation
    # (verified 2026-11-17: the reply stays a message item), and P(".O") there differs a lot from the same text as plain content.
    if ($useChannel -eq 'reasoning' -and -not $ReasoningAsText) { $p = "<think>`n" + $p }
    $atDecision = Get-NextToken $v $p
    $afterDot = Get-NextToken $v ($p + '.')
    $afterDotO = Get-NextToken $v ($p + '.O')
    $pDot = ($atDecision | Where-Object { $_.Token -ceq '.' } | Select-Object -First 1).P
    $pDotO = ($atDecision | Where-Object { $_.Token -ceq '.O' } | Select-Object -First 1).P
    Write-Host "== $v"
    Write-Host "   at decision: $(Format-Top $atDecision)"
    Write-Host "   after '.':   $(Format-Top $afterDot 4)"
    Write-Host "   after '.O':  $(Format-Top $afterDotO 5)"
    [pscustomobject]@{
        Variant      = $v
        'P(.)'       = '{0:P2}' -f [double]$pDot
        'P(.O)'      = '{0:P2}' -f [double]$pDotO
        'P(.O) sampled' = '{0:P2}' -f (Get-SampledProbability $atDecision '.O')
        'P(Over|.)'  = '{0:P1}' -f [double](($afterDot | Where-Object { $_.Token -ceq 'Over' } | Select-Object -First 1).P)
        'top after .O' = ($afterDotO | Select-Object -First 3 | ForEach-Object { '{0} {1:P0}' -f $_.Token, $_.P }) -join ', '
    }
}
''
"Sampled = chance of drawing '.O' with temperature $Temperature, top-k $TopK, top-p $TopP, min-p $MinP (from the top $TopLogprobs candidates)."
$rows | Format-Table -AutoSize | Out-String -Width 200
