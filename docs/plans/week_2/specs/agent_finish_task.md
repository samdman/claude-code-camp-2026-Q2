# Agent finish_task — Design

Status: implemented (pending live verification) — see `docs/plans/week_2/plans/agent_finish_task_plan.md` for the task-by-task build log. Automated tests are green (118/118); the manual live-verification step against the real MUD (Task 4, Step 2) still needs to be run interactively with a configured `.boukensha/settings.yaml` and `ANTHROPIC_API_KEY`.
Owner: Sam Alhambra

## Problem

`Agent.RunAsync` (`Agent.cs:55-92`) treats any model response that isn't a tool call as "turn complete" and returns it straight to the caller, regardless of whether the user's actual goal was reached. In practice the model sometimes narrates or gives a status update in plain text — "I explored but haven't found the bakery yet" — and that ends the turn just as surely as if the goal had been achieved, handing control back to the user with the task still unfinished.

This spec makes ending a turn an explicit, inspectable act instead of an incidental one: the loop only stops when the model calls a `finish_task` tool, or when an iteration/token budget forces it to stop early (unchanged from today).

## `finish_task` tool

Registered automatically — not something a host/task has to remember to wire up. `Agent`'s constructor registers it onto the `Registry` it's given, guarded by the existing `Registry.Registered("finish_task")` check so it's a no-op if already present (keeps tests free to pre-register a fake if they want to observe the call directly).

Schema:
- `status` (string, required, one of `done` / `blocked` / `need_input`) — `done` means the goal was reached; `blocked` means the model tried and can't proceed (e.g. destination doesn't exist); `need_input` means it needs something from the user before it can continue (a decision, a missing detail).
- `summary` (string, required) — becomes the text returned to the user. For `done` this is the result; for `blocked`/`need_input` this is what to tell the user.

The tool's own handler is a trivial no-op returning an acknowledgment string (so the `tool_use` → `tool_result` contract the API requires is satisfied like any other tool call) — it does not itself end the loop. That's `Agent.RunAsync`'s job, since only the loop has the context to decide what "ending" means (return vs. continue).

## Loop change

In `RunAsync`, the `stop_reason == "tool_use"` branch (`Agent.cs:80-84`) changes to:

1. Dispatch all tool calls in the response exactly as today, via the unchanged `HandleToolCallsAsync` — every `tool_use` block still gets a matching `tool_result`, including `finish_task`'s.
2. After dispatch, if any of the tool calls in that response was named `finish_task`, stop the loop and return its `summary` argument directly — no extra model round-trip is spent just to produce "final text," since the summary already is the final text. Log `TurnEnd` with a reason that includes the status (e.g. `finish_task:done`), so session logs distinguish a real completion from a budget-forced one.
3. If no `finish_task` call was present, `continue` exactly as today (the loop calls the model again).

The plain-text branch (`Agent.cs:86-91`, when `stop_reason != "tool_use"`) changes from "return this as the final answer" to:

1. Add the assistant message to context (unchanged).
2. Raise a new narration event so the caller can show the text live (see below) — this turn's text isn't discarded, it's just no longer treated as terminal.
3. Inject a short nudge as a `user` message — "Continue working toward the goal, or call finish_task if you're done, blocked, or need my input." — mirroring the existing `WrapUpDirective` pattern (`Agent.cs:10-12`) structurally, but distinct text and purpose (this steers behavior mid-turn, `WrapUpDirective` forces a final answer under a real budget constraint).
4. `continue` the loop instead of returning.

## Narration visibility

New `AgentHooks` event, following the existing `OnBeforeToolCall`/`OnAfterToolCall` shape (`AgentHooks.cs`): `OnNarration(Action<string> handler)` / `RaiseNarration(string text, CancellationToken)`, raised once per plain-text-without-finish_task response, with that response's extracted text.

`BoukenshaSession` (`BoukenshaHost.cs`) gains a public `Hooks` property exposing the session's `AgentHooks` instance (it's already constructed there and captured in the `AgentFactory` closure, just not exposed today) so `Repl` — or any future frontend — can subscribe: `session.Hooks.OnNarration(text => Output(text))`.

## Safety net — unchanged

`max_iterations` / `max_turn_tokens` (`Agent.cs:57-66`) still force `WrapUpAsync`, which calls the model once more with `tools: []` and returns whatever plain text comes back, regardless of `finish_task`. This remains the hard backstop: a model that never calls `finish_task` is bounded exactly as it is today, it just now takes as many iterations as it used to plus however many extra plain-text-nudge round-trips happen before the budget is hit, instead of stopping on the very first one.

## System prompt

`prompts/system.md` gets a short addition explaining the contract: the agent must call `finish_task` (with the appropriate `status`) to end its turn — plain text alone won't end it. Without this the model has no way to know the tool is expected, since nothing about the tool's own schema communicates "you must call this to stop."

## Scope

Applies to every task built on `Agent.RunAsync` — currently just `PlayerTask`, since `finish_task` is registered by `Agent` itself rather than per-task configuration. No settings.yaml toggle; if a future task genuinely needs the old plain-text-ends-turn behavior, that's a small, separable follow-up (an opt-out flag on `Agent`'s constructor), not built here since nothing needs it yet.

## Testing

New `AgentTests` (none exist today) against a fake `HttpClient` (custom `HttpMessageHandler` returning scripted JSON response bodies per call, following this session's established fake-external-dependency fixture discipline) plus a real `Registry`/`Context`:

- A response containing a `finish_task` call ends the loop and returns its `summary`, without an extra model call being made (assert on the fake handler's call count).
- A plain-text response with no `finish_task` call does **not** end the loop: the fake handler is called again, and the narration hook fires with that turn's text before the nudge is injected.
- The nudge message appears in `Context.Messages` after a narrated turn, ahead of the next model call.
- `finish_task` alongside other tool calls in the same response: all get dispatched and get `tool_result`s (existing `HandleToolCallsAsync` behavior, unaffected), and the loop still ends on `summary`.
- `status: blocked` and `status: need_input` both end the loop the same way `done` does — status only affects what's logged, not whether the loop stops.
- `max_iterations` still forces `WrapUpAsync` termination even when the model never calls `finish_task` (regression check — this path must still work exactly as before).
- `Registry.Registered("finish_task")` guard: constructing `Agent` twice against the same `Registry` doesn't throw or double-register.

## Out of scope for this pass

- Per-task opt-out of requiring `finish_task` (no task needs it yet; see "Scope").
- Persisting `status`/`summary` anywhere beyond the session log (e.g. a dedicated "task outcomes" table) — the session `.jsonl` already captures it via `TurnEnd`, and nothing today reads task outcomes back out.
- Changing `WrapUpAsync`'s own behavior — it remains the budget-forced escape hatch, untouched by this spec.
