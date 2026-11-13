Added `roslynsentinel-interrogate.ps1` (`scripts/`, 2026-09-07) to automate the transcript-
replay-and-interrogate technique that found the root cause in
eval_qwen36_35b_ladder_preferred_branch_extract_bug.md — previously done via one-off
hand-written Python per investigation.

Mirrors `roslynsentinel-modeleval.ps1`'s front-door conventions: same `-HostAddress` known-alias
table (112/113, or any URL used verbatim), same doc-comment style. Takes `-TranscriptPath`
(a run's `transcript.json` or its containing dir — matches `ModelTestingResults\<host>\<TestName>\<timestamp>\`
layout), `-Question` (the follow-up asked after replaying the conversation), `-Model`,
`-Temperature`/`-TopP` (default 0.1/0.7 per project_lmstudio_sampling_params_for_code),
`-TimeoutSec` (default 1800 — CPU-only inference needs a long timeout), and `-DryRun` (skips
the API call, still writes a human-readable `.txt` reconstruction plus the request JSON, for
sanity-checking or just reading a transcript without spending inference time).
`-StopAfterTurn N` (2026-11-12; default 0 = replay all turns) keeps turn N's assistant message and
tool results and drops later turns, to ask the question at the moment of a failure (it throws when no
turn has that number). Each tool message now also carries the call's `TrailingText` (the extra result
blocks, e.g. an error-policy pointer) after a blank line, as `ModelAgentRunner` builds it for the model;
older transcripts have none, and `-OmitTrailingText` restores the ResultJson-only replay. The script is
`scripts/roslynsentinel-interrogate.ps1`. Pass a per-sample `-OutDir`: outputs are named after the run
folder (`sample-NN-...`), so samples of different runs collide in one directory.

Reconstructs `SystemPrompt`/`UserPrompt`/`Turns` into an OpenAI chat/completions message array:
system, user, then per-turn assistant messages (folding `ReasoningContent` into `<think>` tags
ahead of `Content`, since plain chat/completions replay has no separate reasoning channel) with
`tool_calls` built from each `ToolCalls[].ArgumentsJson`, followed by matching `tool`-role
messages from `ToolCalls[].ResultJson`. Appends the `-Question` as a final user message, POSTs
to `<host>/v1/chat/completions`, and writes request/response JSON plus prints the answer
(reasoning + content) to console — always both, not just one.

**How to apply**: whenever a ModelEval batch produces a failure worth understanding, run this
against the failing run's archive dir instead of hand-reconstructing the message history —
verified live 2026-09-07 against `ModelTestingResults\113\Model_AppliesSevenChainedRefactors\20260907-114903-737`
via `-DryRun`, producing a byte-comparable reconstruction to the equivalent hand-written Python
script (49,743 vs 39,039 chars — difference is JSON formatting only, not missing content).
