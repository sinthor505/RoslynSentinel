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
      -Channel reasoning  the reasoning prefix is sent as the assistant message "<think>\n" + prefix. The stock Qwen3.5 template
                          wraps every assistant message after the user turn in "<think>\n{reasoning}\n</think>\n\n", so it renders
                          "<think>\n\n</think>\n\n<think>\n" + prefix: a CLOSED empty think block, i.e. content mode, an approximation.
                          Loading .scratch/chat_template_probe.txt (one added branch: a final assistant message that starts with
                          "<think>" and has no "</think>" is rendered raw) makes it continue inside an open think block, which is how
                          the real reasoning is generated. -ReasoningAsText drops the "<think>" (plain text).
    Sampler: 200 sampled replies on the 9b step 5 decision request (-SampleCount 200) were all ".Over", matching the logprobs
    (0.5% ".O", below the min-p cut-off), so a mismatch with real slip rates is in the rendered context, not the sampling path.
      -Channel auto       content if the live turn's content has the decision, otherwise reasoning (default).

    Per variant it makes three one-token requests: at the decision, after "." and after ".O". Variants (all by default):
      Baseline            the conversation as recorded.
      Capitalized         quoted "standard"/"express"/"overnight"/"pickup" become "Standard"... everywhere (history and prefix).
      ExampleOvernight    the user prompt's example "ShippingKinds.Express" becomes "ShippingKinds.Overnight" (a prompt change).
      QualifiedResult     the ReadFile result of ShippingKinds.cs gets a "qualifiedMembers" field listing ShippingKinds.Standard,
                          ...Overnight... (stands in for a tool result that shows the qualified names: a change we could ship).

    "Sampled" is the probability of drawing ".O" under the sampler settings (-Temperature, -TopK, -TopP, -MinP; llama.cpp order
    top-k, top-p, min-p, temperature), computed from the returned top candidates, so it is an estimate.

    Endpoints (-Endpoint): "responses" (default) sends everything to /v1/responses. LM Studio's /v1/responses IGNORES logit_bias
    and repeat_penalty. "chat" sends the same conversation to /v1/chat/completions (system, user, then per kept turn an assistant
    message with tool_calls and role "tool" messages, the live turn's reasoning, and a trailing assistant message holding the
    prefix, which LM Studio continues). /v1/chat/completions HONOURS logit_bias; LM Studio documents these fields there: model,
    top_p, top_k, messages, temperature, max_tokens, stream, stop, presence_penalty, frequency_penalty, logit_bias, repeat_penalty,
    seed. -LogitBias and the -Sample* / -RepeatPenalty / -PresencePenalty fields are therefore accepted only with -Endpoint chat.
    They apply to the sampled replies (-SampleCount) only: the one-token logprob requests report the raw, unbiased distribution.
    Token ids on the Qwen3.5 models: "." = 13, ".O" = 8123.

.PARAMETER Transcript
    transcript.json (or its folder) holding the kept turns and the live turn.

.PARAMETER KeepTurns
    Turns 1..K are history; the decision prefix comes from turn K+1. Default: the turn after the first ReadFile of ShippingKinds.cs.

.PARAMETER HostAddress
    An alias from scripts/eval-hosts.json (for example 85u or 85lms), a machine key (its default
    endpoint) or a base URL. A bare number not in the table (for example 114) is read as
    http://192.168.1.<n>:1234. Or pass -BaseUrl.

.PARAMETER Model
    Model key. Default: the model loaded on the host (fails if none or several).

.PARAMETER Endpoint
    responses (default, behaves as before) or chat (/v1/chat/completions, needed for -LogitBias and the sampler fields).

.PARAMETER LogitBias
    Entries id=bias (e.g. '8123=-100','13=-3'; a comma list in one entry also works), sent as logit_bias {"8123": -100, "13": -3} on
    the SAMPLE requests only; the one-token logprob requests stay unbiased. Needs -Endpoint chat.

.PARAMETER SampleTemperature
    Request field temperature for the sample requests, sent only when passed. Not the same as -Temperature, which only feeds the
    computed "Sampled" estimate. Needs -Endpoint chat. Likewise -SampleTopK (top_k), -SampleTopP (top_p), -SampleMinP (min_p),
    -RepeatPenalty (repeat_penalty) and -PresencePenalty (presence_penalty).

.PARAMETER SampleTokens
    max_tokens (max_output_tokens on responses) of each sampled reply. Default 3.

.PARAMETER ChatReasoningAs
    How -Endpoint chat carries an assistant turn's reasoning: field (reasoning_content on the assistant message) or think
    (content = "<think>\n" + reasoning + "\n</think>\n\n" + text).

.EXAMPLE
    pwsh scripts/Probe-OvernightLogprobs.ps1 -Transcript c:\tmp\replay-tiel-original\sample-02 -HostAddress 113

.EXAMPLE
    pwsh scripts/Probe-OvernightLogprobs.ps1 -Transcript c:\tmp\replay-tiel-original\sample-02 -HostAddress 85 -Variant Baseline -Endpoint chat -SampleCount 40 -LogitBias '13=-3','8123=-100' -RepeatPenalty 1.1
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
    [switch]$ShowPrefix,
    # Sampled-frequency check: send the first variant's decision request this many times with the server's own sampler settings
    # (no temperature, no logprobs, 3 tokens) and count how often the reply starts with ".O". Compares the logprob-implied rate with
    # what the sampling path actually draws on the same rendered prompt.
    [int]$SampleCount = 0,
    [ValidateSet('responses', 'chat')][string]$Endpoint = 'responses',
    # id=bias entries for the sample requests (chat endpoint only); see .PARAMETER LogitBias.
    [string[]]$LogitBias,
    # Request-side sampler fields of the sample loop, each sent only when passed (chat endpoint only). Not -Temperature/-TopK/-TopP/-MinP.
    [double]$SampleTemperature,
    [int]$SampleTopK,
    [double]$SampleTopP,
    [double]$SampleMinP,
    [double]$RepeatPenalty,
    [double]$PresencePenalty,
    [int]$SampleTokens = 3,
    # Reasoning carrier on the chat endpoint. Default 'think': measured on host 85 (qwen3.5-4b-mtp, 2026-11-17), Baseline vs the
    # /v1/responses path: step5-qwen9b-slip (reasoning channel) P(.) 97.58 / P(.O) 2.13 for responses, 'field' and 'think' alike;
    # tiel sample-02 (content channel) responses P(.) 2.85 / P(.O) 97.02, think 2.66 / 97.22 (diff 0.2 points), field 2.38 / 97.51
    # (diff 0.5 points). 'think' is the closer one.
    [ValidateSet('field', 'think')][string]$ChatReasoningAs = 'think'
)

$ErrorActionPreference = 'Stop'

$samplerFields = [ordered]@{
    temperature = 'SampleTemperature'; top_k = 'SampleTopK'; top_p = 'SampleTopP'; min_p = 'SampleMinP'
    repeat_penalty = 'RepeatPenalty'; presence_penalty = 'PresencePenalty'
}
$passedChatOnly = @(@('LogitBias') + @($samplerFields.Values) | Where-Object { $PSBoundParameters.ContainsKey($_) })
if ($Endpoint -eq 'responses' -and $passedChatOnly) {
    throw "LM Studio's /v1/responses ignores logit_bias and the request-side sampler fields, so -$($passedChatOnly -join ', -') would have no effect there. Use -Endpoint chat (/v1/chat/completions honours logit_bias)."
}

. (Join-Path $PSScriptRoot 'roslynsentinel-evalhosts.ps1')

$hostEntry = $null
if (-not $BaseUrl) {
    if (-not $HostAddress) { throw 'Pass -HostAddress (an alias or machine key from scripts/eval-hosts.json) or -BaseUrl.' }
    $hostEntry = Resolve-EvalHostEntry -Address $HostAddress -Table (Read-EvalHostTable)
    if ($hostEntry.Known) {
        $BaseUrl = [string]$hostEntry.BaseUrl
    }
    else {
        $BaseUrl = switch ($HostAddress) { '85' { 'http://192.168.10.85:1234' } default { "http://192.168.1.$($HostAddress):1234" } }
    }
}
$BaseUrl = $BaseUrl.TrimEnd('/') -replace '/v1$', ''

$path = if (Test-Path -LiteralPath $Transcript -PathType Container) { Join-Path $Transcript 'transcript.json' } else { $Transcript }
$t = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 100
$turns = @($t.Turns)

if (-not $Model) {
    $modelEntry = if ($hostEntry -and $hostEntry.Known) { $hostEntry } else { [pscustomobject]@{ BaseUrl = "$BaseUrl/v1"; Backend = 'lmstudio' } }
    $loaded = @((Get-EvalHostModels -Entry $modelEntry -TimeoutSec 15).Models | Where-Object { $_.Loaded -eq $true } | ForEach-Object Id)
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
    $system = (Convert-Text $t.SystemPrompt $variant 'system').Trim()
    if ($toolBlock) { $system = $toolBlock + "`n`n" + $system }
    $items.Add(@{ type = 'message'; role = 'system'; content = $system })
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

# LM Studio refuses logprobs together with a tools list ("logprobs is not supported with tools + stream"). Inline mode puts the
# tool block into the system message text exactly as the loaded Qwen3.5 chat template writes it (.scratch/chat_template.txt: the
# "# Tools" block first, then "\n\n" + the system prompt, all in one system message), so with no tools list the template renders the
# same system turn. Unverified detail: how LM Studio's tojson spaces the tool JSON (Python-style ", " / ": " assumed) and the key
# order of its tool objects; check against __lmstudio.model_input in a trace log. None drops the tools; Native sends them.
function ConvertTo-PyJson($value) {
    if ($null -eq $value) { return 'null' }
    if ($value -is [bool]) { return $(if ($value) { 'true' } else { 'false' }) }
    if ($value -is [string]) { return ($value | ConvertTo-Json -Compress) }
    if ($value -is [int] -or $value -is [long] -or $value -is [double] -or $value -is [decimal]) { return ([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0}', $value)) }
    if ($value -is [System.Collections.IDictionary]) {
        return '{' + (($value.Keys | ForEach-Object { (ConvertTo-PyJson ([string]$_)) + ': ' + (ConvertTo-PyJson $value[$_]) }) -join ', ') + '}'
    }
    if ($value -is [pscustomobject]) {
        return '{' + (($value.PSObject.Properties | ForEach-Object { (ConvertTo-PyJson $_.Name) + ': ' + (ConvertTo-PyJson $_.Value) }) -join ', ') + '}'
    }
    if ($value -is [System.Collections.IEnumerable]) { return '[' + ((@($value) | ForEach-Object { ConvertTo-PyJson $_ }) -join ', ') + ']' }
    return ($value | ConvertTo-Json -Compress)
}

$toolBlock = ''
if ($ToolsMode -eq 'Inline' -and $tools) {
    $lines = foreach ($tool in $tools) {
        $o = [ordered]@{ type = 'function'; function = [ordered]@{ name = $tool.name; description = $tool.description; parameters = $tool.parameters } }
        ConvertTo-PyJson $o
    }
    $toolBlock = "# Tools`n`nYou have access to the following functions:`n`n<tools>`n" + ($lines -join "`n") + "`n</tools>" +
        "`n`nIf you choose to call a function ONLY reply in the following format with NO suffix:`n`n<tool_call>`n<function=example_function_name>`n<parameter=example_parameter_1>`nvalue_1`n</parameter>`n<parameter=example_parameter_2>`nThis is the value for the second parameter`nthat can span`nmultiple lines`n</parameter>`n</function>`n</tool_call>`n`n<IMPORTANT>`nReminder:`n- Function calls MUST follow the specified format: an inner <function=...></function> block must be nested within <tool_call></tool_call> XML tags`n- Required parameters MUST be specified`n- You may provide optional reasoning for your function call in natural language BEFORE the function call, but NOT after`n- If there is no function call available, answer the question like normal with your current knowledge and do not tell the user about function calls`n</IMPORTANT>"
}

# /v1/chat/completions twin of New-Input, item for item. Reasoning carrier ($ChatReasoningAs): 'field' puts it in the assistant
# message's reasoning_content (the loaded template reads message.reasoning_content, chat_template.txt line 94), 'think' wraps it into
# the content as "<think>\n...\n</think>\n\n" (the template splits that back out, lines 97-99). See the comment at the default of
# -ChatReasoningAs for what was measured.
function New-ChatMessages([string]$variant, [string]$assistantText) {
    $messages = [System.Collections.Generic.List[object]]::new()
    $system = (Convert-Text $t.SystemPrompt $variant 'system').Trim()
    if ($toolBlock) { $system = $toolBlock + "`n`n" + $system }
    $messages.Add(@{ role = 'system'; content = $system })
    $messages.Add(@{ role = 'user'; content = (Convert-Text $t.UserPrompt $variant 'user') })
    foreach ($turn in $turns[0..($KeepTurns - 1)]) {
        $msg = $turn.ModelMessage
        $a = @{ role = 'assistant'; content = [string](Convert-Text $msg.Content $variant 'content') }
        if (-not $NoReasoningReplay -and $msg.ReasoningContent) {
            $reasoning = Convert-Text $msg.ReasoningContent $variant 'reasoning'
            if ($ChatReasoningAs -eq 'field') { $a.reasoning_content = $reasoning }
            else { $a.content = "<think>`n" + $reasoning + "`n</think>`n`n" + $a.content }
        }
        $calls = foreach ($call in @($msg.ToolCalls)) {
            @{ id = $call.Id; type = 'function'; function = @{ name = $call.Name; arguments = (Convert-Text $call.ArgumentsJson $variant 'arguments') } }
        }
        if ($calls) { $a.tool_calls = @($calls) }
        $messages.Add($a)
        foreach ($rec in @($turn.ToolCalls)) {
            $messages.Add(@{ role = 'tool'; tool_call_id = $rec.ToolCallId; content = (Convert-Text $rec.ResultJson $variant 'result') })
        }
    }
    if ($useChannel -eq 'content' -and -not $NoReasoningReplay -and $reasoningText) {
        # The live turn's reasoning, then the content prefix: one assistant message when the reasoning rides in the content.
        $liveReasoning = Convert-Text $reasoningText $variant 'reasoning'
        if ($ChatReasoningAs -eq 'field') { $messages.Add(@{ role = 'assistant'; content = $assistantText; reasoning_content = $liveReasoning }); return , $messages.ToArray() }
        $assistantText = "<think>`n" + $liveReasoning + "`n</think>`n`n" + $assistantText
    }
    $messages.Add(@{ role = 'assistant'; content = $assistantText })
    return , $messages.ToArray()
}

# Body of a chat request; $extra holds the per-request fields (max_tokens, sampling fields, logit_bias).
function New-ChatBody([string]$variant, [string]$assistantText, [hashtable]$extra) {
    $body = @{ model = $Model; messages = (New-ChatMessages $variant $assistantText); stream = $false }
    foreach ($k in $extra.Keys) { $body[$k] = $extra[$k] }
    if ($ToolsMode -eq 'Native') {
        $body.tools = @($tools | ForEach-Object { @{ type = 'function'; function = @{ name = $_.name; description = $_.description; parameters = $_.parameters } } })
        $body.tool_choice = 'auto'
    }
    return $body
}

function Get-NextToken([string]$variant, [string]$assistantText) {
    if ($Endpoint -eq 'chat') {
        $body = New-ChatBody $variant $assistantText @{ max_tokens = 1; temperature = 0; logprobs = $true; top_logprobs = $TopLogprobs }
        $r = Invoke-RestMethod -Uri "$BaseUrl/v1/chat/completions" -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 60 -Compress) -TimeoutSec 900
        $tok = @($r.choices[0].logprobs.content) | Select-Object -First 1
        if (-not $tok -or -not $tok.top_logprobs) { throw "The server returned no logprobs on /v1/chat/completions (finish_reason: $($r.choices[0].finish_reason))." }
        return @($tok.top_logprobs | ForEach-Object { [pscustomobject]@{ Token = $_.token; P = [Math]::Exp($_.logprob) } })
    }
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

if ($SampleCount -gt 0) {
    $v = $Variant[0]
    $p = Convert-Text $prefix $v $useChannel
    if ($useChannel -eq 'reasoning' -and -not $ReasoningAsText) { $p = "<think>`n" + $p }
    $counts = @{}
    $examples = @{}
    $members = @{}
    $sentFields = [ordered]@{}
    $tokenLevel = $Endpoint -eq 'chat'
    $chatExtra = @{ max_tokens = $SampleTokens }
    if ($Endpoint -eq 'chat') {
        foreach ($field in $samplerFields.Keys) {
            if ($PSBoundParameters.ContainsKey($samplerFields[$field])) { $chatExtra[$field] = Get-Variable -Name $samplerFields[$field] -ValueOnly; $sentFields[$field] = $chatExtra[$field] }
        }
        if ($LogitBias) {
            $bias = [ordered]@{}
            # Split on commas and drop stray quotes: `pwsh -File ... -LogitBias '13=-3','8123=-100'` arrives as one string with the quotes.
            foreach ($entry in @($LogitBias | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().Trim('"', "'").Trim() } | Where-Object { $_ })) {
                $kv = $entry -split '=', 2
                $biasValue = 0.0
                if ($kv.Count -ne 2 -or $kv[0].Trim() -notmatch '^\d+$' -or -not [double]::TryParse($kv[1].Trim(), [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$biasValue)) {
                    throw "-LogitBias entry '$entry' is not id=bias (for example 8123=-100)."
                }
                $bias[$kv[0].Trim()] = $biasValue
            }
            $chatExtra.logit_bias = $bias
        }
        $chatExtra.logprobs = $true   # first sampled token; dropped below when the server refuses it
    }
    for ($i = 1; $i -le $SampleCount; $i++) {
        $firstToken = $null
        if ($Endpoint -eq 'chat') {
            $body = New-ChatBody $v $p $chatExtra
            $json = $body | ConvertTo-Json -Depth 60 -Compress
            if ($i -eq 1) { Write-Verbose ('Sample request body: ' + $json) }
            try {
                $r = Invoke-RestMethod -Uri "$BaseUrl/v1/chat/completions" -Method Post -ContentType 'application/json' -Body $json -TimeoutSec 900
            }
            catch {
                if (-not $chatExtra.logprobs) { throw }
                # The server refused logprobs on a sample request: retry once without and classify by text.
                $chatExtra.Remove('logprobs'); $tokenLevel = $false
                $body = New-ChatBody $v $p $chatExtra
                $r = Invoke-RestMethod -Uri "$BaseUrl/v1/chat/completions" -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 60 -Compress) -TimeoutSec 900
            }
            $text = [string]$r.choices[0].message.content
            if ($tokenLevel) {
                $firstToken = @($r.choices[0].logprobs.content) | Select-Object -First 1 | ForEach-Object token
                if ($null -eq $firstToken) { $tokenLevel = $false }
            }
        }
        else {
            $body = @{ model = $Model; input = (New-Input $v $p); max_output_tokens = $SampleTokens }
            if ($ToolsMode -eq 'Native') { $body.tools = @($tools); $body.tool_choice = 'auto' }
            $r = Invoke-RestMethod -Uri "$BaseUrl/v1/responses" -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 60 -Compress) -TimeoutSec 900
            $text = (($r.output | Where-Object { $_.type -eq 'message' } | Select-Object -First 1).content | Select-Object -First 1).text
        }
        if ($null -eq $text) { $text = '' }
        $class = if ($tokenLevel -and $null -ne $firstToken) {
                if ($firstToken -ceq '.') { "first token '.' (bare dot)" } elseif ($firstToken -ceq '.O') { "first token '.O'" } else { "first token other [$firstToken]" }
            }
            elseif ($text.StartsWith('.Over', [StringComparison]::Ordinal)) { '.Over (correct path)' }
            elseif ($text.StartsWith('.O', [StringComparison]::Ordinal)) { '.O (typo path)' }
            elseif ($text.StartsWith('.', [StringComparison]::Ordinal)) { '. then other' }
            else { 'other' }
        $counts[$class] = 1 + [int]$counts[$class]
        if (-not $examples.ContainsKey($class)) { $examples[$class] = $text }
        $member = [regex]::Match($text, '^\.?[A-Za-z]+')
        $memberKey = if ($member.Success) { $member.Value } else { $text.Substring(0, [Math]::Min(16, $text.Length)) -replace "`n", '\n' }
        $members[$memberKey] = 1 + [int]$members[$memberKey]
        if ($i % 25 -eq 0) { Write-Host "  $i/$SampleCount sampled" }
    }
    if ($Endpoint -eq 'chat') {
        "Sampled $SampleCount replies via /v1/chat/completions to the '$v' decision request, max_tokens $SampleTokens$(if ($tokenLevel) { ', classified by first token (logprobs returned)' } else { '; the server returned no token logprobs, so the counts are text-level' }):"
        $sent = @($sentFields.Keys | ForEach-Object { "$_=$($sentFields[$_])" })
        if ($chatExtra.logit_bias) { $sent += 'logit_bias={' + (($chatExtra.logit_bias.Keys | ForEach-Object { "$_`: $($chatExtra.logit_bias[$_])" }) -join ', ') + '}' }
        "Sent with the sample requests: $(if ($sent) { $sent -join ', ' } else { '(no sampler fields, no logit_bias: server defaults)' })"
    }
    else {
        "Sampled $SampleCount replies to the '$v' decision request with the server's sampler settings (temperature reported: $($r.temperature), top_p: $($r.top_p)):"
    }
    foreach ($k in $counts.Keys | Sort-Object) { '  {0,-30} {1,4}  ({2:P1})  e.g. [{3}]' -f $k, $counts[$k], ($counts[$k] / $SampleCount), ($examples[$k] -replace "`n", '\n') }
    if ($Endpoint -eq 'chat') {
        'Top sampled members (regex ^\.?[A-Za-z]+, else first 16 characters):'
        foreach ($m in $members.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 10) { '  {0,-18} {1,4}' -f $m.Key, $m.Value }
    }
}
