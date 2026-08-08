# Python Port — Implementation Reference

Cross-cutting decisions, conventions, and context for the `ruby/` → `python/` port of Boukensha, gathered across steps 00–03. Read this before writing a new step's plan or reviewing an implementer's work — it exists so decisions made once don't need re-litigating (or silently drifting) every step. Per-step specifics live in `docs/plans/python_port/<NN>_<name>`; this file only holds things that apply *across* steps.

## Project Structure

- **One evolving package, not per-step duplication.** Ruby ships a fresh, fully self-contained `ruby/<NN>_<name>/` gem per step. Python is the opposite: a single project at `week1_baseline/python/` (`pyproject.toml`, one `src/boukensha/` package, one `.venv`), growing across steps via git history. The numbered folders (`python/00_config/`, `python/01_struct_skeleton/`, ...) hold **only** that step's runnable example + step-specific `README.md` — never a copy of the package itself.
- **`bin/` is gitignored repo-wide** (`.gitignore:13: bin/`), consistent for both `bin/ruby/*` and `bin/python/*`. Every step's entrypoint script (`bin/python/<NN>_<name>`) is created on disk and used for parity checks, but is **never committed** — only the step's `README.md` gets committed in that task. Commit messages for that task note this explicitly (see step 01/02's "add bin/python/NN entrypoint and step README" commits) so a future reader doesn't wonder where the script went.
- **Python version:** 3.13.3 via `pyenv` (pinned with `pyenv local` inside `python/`), chosen because pyenv-win's index had no stable 3.14 at decision time — revisit if that changes.
- **Dependencies stay minimal and stdlib-first**, matching Ruby's `ITERATIONS.md` philosophy: `PyYAML` + `python-dotenv` only, added in step 00. No new third-party deps introduced in steps 01–03 (step 03's backends are pure dict/list builders — no HTTP library yet; that's step 04).
- **No pytest suite.** Every step ships no test suite in Ruby either — parity is verified by running `examples/example.py` and diffing its stdout against `examples/example.rb`'s, byte for byte. Smoke checks inside each task are throwaway `python -c "..."` snippets, not persisted tests. Don't add a test framework unless a future step's Ruby source actually adds one.

## Ruby → Python Idiom Mapping

These are the standing translation rules. Apply them by default; only deviate with a documented reason in that step's plan.

| Ruby | Python | Notes |
|---|---|---|
| Symbols as string-ish keys/values (`:player`, `:tokens`) | Plain strings (`"player"`, `"tokens"`) | Decided step 00: "plain-string API." No Ruby symbol/string duality to bridge — e.g. `Registry#dispatch`'s `args.transform_keys(&:to_sym)` has no Python equivalent and is simply dropped, not translated. |
| `attr_reader :x` (plain field) | Plain public instance attribute (`self.x = ...`) | E.g. `Config.dir`, `Config.settings`, `Context.task`, `Backend.model`. No property wrapper needed — Ruby's `attr_reader` is just exposing state, not computing it. |
| Zero-arg method with actual logic (`def mud_host; ...; end`) | `@property` | E.g. `Config.mud_host`, `Context.tool_count`, `Backend.context_window`, `Backend.headers`/`url`. Rule of thumb: if the Ruby method *computes* something from state (dict lookup, string formatting, default fallback), it's a property; if it just returns an ivar, it's a plain attribute. |
| Zero-arg method that transforms/converts (`to_s`, `to_messages`, `to_tools`) | Regular callable method, **not** a property | `to_*`-prefixed methods stay callable even at zero args, matching Ruby's own `to_s`/`to_i` convention of never being "just a field." `PromptBuilder.to_messages()`/`.to_tools()`/`.to_api_payload()` are plain methods for this reason, while `.headers`/`.url` (pure passthrough, no verb prefix) are properties. |
| `method_name?` (query method) | `method_name` (drop the `?`) | E.g. `Tasks::Base.prompt_override?` → `prompt_override`. Python identifiers can't contain `?`. |
| `method_name!` (bang/dangerous method) | `method_name` (drop the `!`) | E.g. `Backends::Base.validate_model!` → `validate_model`. No Python convention distinguishes "mutates/raises" via naming the way Ruby's bang does; don't invent one — just drop it. |
| `to_s` / `inspect` | `__repr__`, with `__str__ = __repr__` | Established step 00 (`Config`), reused every step since (`Context`, `Message`, `Tool`). |
| Implicit trailing block (`registry.tool(...) do |x| ... end`) | Explicit keyword argument | No Python syntax equivalent. Named `block=` (not `handler=`/`fn=`) to keep 1:1 traceability back to the Ruby source — decided in step 02. |
| A Ruby class method and instance method sharing one name (separate namespaces in Ruby, illegal collision in Python) | Rename one of them | Ruby's `Backends::Base` has both `self.model_info(model)` (class-level lookup) and `model_info` (instance-level cached reader) — legal in Ruby (singleton-class vs instance-class method tables), impossible in Python (same class `__dict__` slot). Resolved in step 03 by renaming the class-level lookup to `model_info_for(cls, model)` and keeping the instance-level value as a plain attribute (`self.model_info`, per the `attr_reader` rule above). **Watch for this pattern in future steps** — anywhere Ruby has both `self.foo` and `foo` methods on the same class. |
| `StandardError` subclass | `Exception` subclass, `pass` body | `UnknownToolError`, `UnsupportedModelError`. No custom `__init__` unless Ruby's constructor does more than accept a message. |
| Ruby `nil` in a data table (e.g. `cost_per_million: { input: nil }`) | Python `None` | Straightforward; `json.dumps` renders `None` as `null`, matching `JSON.generate`'s `nil` → `null`. |
| `ArgumentError` (Ruby's generic bad-argument error) | `ValueError` | E.g. `Tasks::Base.provider`/`.model` (step 00), the provider-dispatch `else` branch in each step's `example.py` (step 03). No new Boukensha-specific exception type unless Ruby introduces one (`errors.rb`) for that exact case. |
| Ruby module nesting (`Boukensha::Backends::X` vs `Boukensha::X`) | Mirror the nesting, don't flatten everything | `Tool`/`Message`/`Context`/`Registry`/`Config`/`Player`/`PromptBuilder` are all directly under `Boukensha::` in Ruby and are flattened into the top-level `boukensha` package's `__init__.py`. `Backends::Anthropic` etc. live under a nested Ruby module and get their own `boukensha.backends` subpackage with its own `__init__.py`, imported as `from boukensha.backends import Anthropic`, not `from boukensha import Anthropic`. |
| `JSON.pretty_generate(hash)` | `json.dumps(dict, indent=2)` | Both preserve insertion order and use 2-space indents; verified byte-identical output empirically in step 03 rather than assumed. If a future step's payload ever mismatches, adjust `separators=`, don't hand-roll a serializer. |

## Known, Deliberately-Unfixed Warts

The Ruby tutorial source has a few acknowledged rough edges that later steps promise to fix "later" (per `ITERATIONS.md`) but haven't yet as of step 03. **Port these warts as-is — do not silently fix them in the Python port.** If a reviewer flags one as a defect, the answer is "tracked here as an intentional carry-over," not a bug in the port.

1. **`Context` still owns `tools`, not `Registry`.** Introduced in step 02: `Registry#tool` still calls `context.register_tool(tool)`, and `Registry#dispatch` still reads from `context.tools`. `Registry` has no `tools` collection of its own. `ITERATIONS.md`: *"Context is still responsible for managing tools which is not correct... We'll correct this manually in a future step."* Not yet fixed as of step 03.
2. **`PromptBuilder.to_messages()` has an arity bug for stateful backends.** Introduced in step 03: it calls `backend.to_messages(context.messages)` with one argument. This matches Anthropic's and Gemini's `to_messages(messages)` (system sent as a separate top-level field) but **raises `TypeError`/Ruby's `ArgumentError`** for Ollama/OllamaCloud/OpenAI, whose `to_messages(system, messages)` requires two (system prompt gets folded into the messages array for those APIs). Not exercised by any `example.rb`/`example.py` — both only ever call `to_api_payload()`, which calls `backend.to_payload(context, ...)` directly and never routes through the broken `PromptBuilder.to_messages()` path.

## Ruby Source Drift (not Python gaps)

Because each `ruby/<NN>_<name>/` folder is an independent snapshot (not an accumulating diff), the tutorial author has occasionally **removed and later re-added** a feature across steps — usually to keep an early step focused, not on purpose as a design change. When you see the Python port "already having" something a Ruby step's diff claims to introduce, check whether this is what happened before assuming an error:

- **`Config::PROMPTS_DIR`** (and the `default_prompts_dir` plumbing in `Tasks::Base`): present in `ruby/00_config`, **absent** in `ruby/01_struct_skeleton` and `ruby/02_the_registry`, restored in `ruby/03_prompt_builder`. The Python port ported it once in step 00 and never removed it (there's no reason to, since Python doesn't duplicate per-step folders) — so `config.py`/`tasks/base.py` already matched step 03's target state two steps early. Confirmed by diffing `ruby/00_config/lib/boukensha/config.rb` against `ruby/01_struct_skeleton`'s copy.
- **General pattern:** before writing a "Reference Files" diff table for a new step, diff the *previous* Ruby step's file against the *new* one directly (`diff -u ruby/<prev>/lib/boukensha/x.rb ruby/<new>/lib/boukensha/x.rb`) rather than trusting that step's README — the README has repeatedly (steps 00–02, confirmed again in 03) shown either stale/illustrative example output or copy-paste artifacts (see next section).

## README / Documentation Drift

Every step so far has had at least one place where `ruby/<NN>_<name>/README.md` doesn't match what the actual Ruby code does. Always verify the real transcript by *running* `examples/example.rb`, never by reading its README's "Expected Output" section as ground truth.

- **Step 00:** none of note beyond the general rule.
- **Step 01:** README's illustrative output didn't match the real code's fields.
- **Step 02:** README's "Expected Output" was missing the real `task=player` field and included a `budget=8192` field that doesn't exist in the actual `Context` implementation. README also has two separate "## Considerations" sections (a copy-paste artifact, not something to replicate).
- **Step 03:** no live-vs-README mismatch found in this step (the README has no verbatim "Expected Output" block to check against), but the general rule still applies — the transcript in that step's plan was captured by actually running `ruby/03_prompt_builder/examples/example.rb`.

## Testing & Verification Approach

- No automated test suite in either language for steps 00–03. Parity is: run `bin/ruby/<step>` and `bin/python/<step>`, diff stdout, expect empty diff.
- Each plan's tasks include inline smoke-check snippets (`python -c "..."`) that are run once during implementation and then discarded — not committed as test files.
- Every plan's final task is a **cross-language parity check** that also re-runs *all prior steps'* Python entrypoints as a regression check, since the package is shared and cumulative — a change in an early step's file could silently break a later step.
- When a plan's "Behavior to Preserve Exactly" section includes a transcript, that transcript must have been captured by actually executing the Ruby example in this environment during plan-writing, not copied from a README.

## Execution Workflow Notes

- Every plan's header requires `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` as a **REQUIRED SUB-SKILL** for task-by-task execution — this is the default, spawn-a-subagent-per-task-plus-review-gate flow.
- **In practice, on step 02, the user asked to stop the per-task subagent dispatch + review-subagent round trips partway through** ("lets not do this extra trips and just do the same procedure with 00 and 01") and switch to direct in-session execution — i.e., the controlling session edits files itself, runs the verification commands itself, and commits itself, without spawning a fresh implementer/reviewer subagent pair per task. Steps 00 and 01 were done this way from the start.
- **Takeaway for future steps:** default to asking, or defaulting to direct in-session execution matching 00/01/02's actual precedent, rather than assuming the subagent-driven flow is wanted just because the plan header mentions it as recommended. If explicitly asked to use full subagent-driven-development (as was tried at the start of step 02), that's fine too — just don't be surprised if the user shortcuts it once they see the overhead of per-task subagent review round-trips on a small, mechanical plan.

## Per-Step Decision Log

Quick index of step-specific decisions that were non-obvious enough to need a call — full detail lives in each step's plan file.

- **Step 00 (`00_config`):** one-project packaging (not per-step); plain-string `Config.tasks("player")` API; `ValueError`/`NotImplementedError` for missing settings / abstract `task_name()`; src-layout (`python/src/boukensha/...`).
- **Step 01 (`01_struct_skeleton`):** `Tool.__repr__` manually renders `params=[:direction]` (Ruby-symbol style) instead of Python's default `dict.keys()` rendering, to stay diff-compatible with Ruby's output — a deliberate cosmetic exception to "plain strings everywhere," scoped only to that one `__repr__`.
- **Step 02 (`02_the_registry`):** block callables passed as an explicit `block=` keyword (see mapping table above); `Registry` intentionally has no `tools` dict of its own (see Known Warts above).
- **Step 03 (`03_prompt_builder`):** `backends` kept as a nested subpackage, not flattened (see mapping table); `Backends::Base`'s `model_info` naming collision resolved via `model_info_for` classmethod + plain `model_info` instance attribute; `PromptBuilder.to_messages()` arity wart ported as-is (see Known Warts above); no new custom exception for provider-dispatch fallback (`ValueError`, matching step 00's precedent).
