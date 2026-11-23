# Stop retrying spent truncated turns and abort phrase loops while streaming

**Status:** APPROVED 2026-11-23. Decided in todo review 2026-11-23; not built.

## Motivation

Four overnight runs on 2026-11-23 (16-35 min each) ended on one turn that streamed ~16k output tokens twice without
`response.completed`; the tails were phrase loops ('the tests will fail' x239): 112 PlanStalenessReview 20261123-111053-189,
113 Sweep 20261123-104014-303, 85 Migrate 20261123-083829-620 and -095142-038.

- **Retry doubles the waste.** `StreamTruncatedException` derives from `InvalidOperationException` (`LmStudioAgentClient.cs:~1144`) and
  the retry filter in `CompleteAsync` (`:~205`) catches that type, so a truncated turn is retried once; the `status=incomplete` path
  throws a plain `InvalidOperationException` (`:~377`) and is retried too. The runner then stops (`ModelAgentRunner.cs:394-403`,
  TODO at `:397`).
- **No detector reads text.** The runaway guard in `CompleteOnceAsync` (`:~316-345`) inspects only `function_call` items; the text and
  reasoning delta cases (`:~290-308`) only append. The idle timeout cannot fire while tokens keep arriving.
- Callers pass `maxTokensPerTurn: 16384` (`ImplementationScenarioAgentTests.cs:123`, `ShippingKindSteppedAgentTests.cs:114`), matching
  the ~16k figure; that the host closed the stream at the cap is a hypothesis (agent.log of the four runs would confirm).

## Proposal

Decided (owner, 2026-11-23):

1. **Skip the retry when the first attempt is spent.** No retry when the truncated attempt streamed close to the cap (>= 90% of
   `maxTokensPerTurn`, from the output token count or a char estimate); a short truncation (early connection close) is still retried.
2. **Loop detector, K = 20, both channels.** In the content and reasoning delta handlers, cut the stream into sentences at `. `,
   `! `, `? ` and newline; normalize (trim, lowercase, collapse whitespace); ignore sentences under 15 characters and text inside
   code fences; count occurrences over the last ~200 sentences. When one sentence reaches 20 occurrences, abort the stream with a
   non-retried exception mapped to a new `AgentStopReason.ReasoningLoopDetected` (appended last; stop reasons are persisted as numbers,
   `AgentTranscript.cs:~568`). The name is chosen to be obvious in transcripts.

## Alternatives considered, not pursued

- Never retry truncation: loses the retry for early connection closes, which motivated it.
- K = 10: earlier abort, higher false-positive risk on long per-file checklists in reasoning.
- Ratio trigger (>= 50% repeats in the last 60 sentences): catches alternating 2-3 phrase loops but is harder to tune; revisit if
  alternating loops show up.

## Open questions

- Whether the output token count is available mid-stream on every backend (LM Studio /v1/responses, llama-server); fall back to a char estimate.
- Consumers that switch on `AgentStopReason` (scorers, `ScenarioScorer.ClassifyFailure`) need a mapping for the new value; not checked.

## Cost / risk

Small-Medium, in `LmStudioAgentClient` and `ModelAgentRunner`. False positives on legitimately repetitive text are bounded by the
15-char floor, the code-fence exclusion and K = 20. Acceptance: (1) a scripted stream that ends without `response.completed` after about
the cap sends exactly one request and stops with `StreamTruncated`; (2) a scripted 'the tests will fail. ' loop aborts after the 20th
repeat with one request and `StopReason == ReasoningLoopDetected` on result and transcript; (3) a long normal output (including a code
block with repeated `}` lines) does not trip it. Tests beside `StreamRunawayRunnerTests` (Tests.Harness).

## Related

- TODO.md entry "Truncated phrase-loop turns are retried".
