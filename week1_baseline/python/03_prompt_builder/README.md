# 03 · The Prompt Builder (Python port)

Python port of `ruby/03_prompt_builder`. Adds `PromptBuilder` and five
`boukensha.backends` classes (`Base`, `Anthropic`, `Gemini`, `Ollama`,
`OllamaCloud`, `OpenAI`) to the shared `boukensha` package. See
`ruby/03_prompt_builder/README.md` for the full design rationale. This file
only documents the Python-specific parts.

Because LLM access, cost, and quality are constantly changing, we want to be
able to switch between multiple LLMs that drive the agent loop. The Prompt
Builder serializes `Context` into the exact format each API expects,
delegating to whichever backend you pass in. **`PromptBuilder` does not call
the API** — it only prepares the payload. Making the actual HTTP request is
step 04's job.

## How It Works

```
Context (Python objects)
        ↓
PromptBuilder
        ↓
Backend (Anthropic, Gemini, Ollama, OllamaCloud, or OpenAI)
        ↓
API Payload (plain dicts and lists)
        ↓
POST to API
```

## `boukensha.PromptBuilder`

| Method | Description |
|---|---|
| `to_messages()` | Delegates message serialization to the backend |
| `to_tools()` | Delegates tool serialization to the backend |
| `to_api_payload(*, max_output_tokens=1024)` | Assembles the complete payload ready to POST |
| `headers` (property) | Returns the correct headers for the backend |
| `url` (property) | Returns the correct endpoint URL for the backend |

## Backends

Each API has its own conventions for how data is expected. Anthropic and
Gemini are the most alike (system prompt as a top-level field), while
OpenAI and Ollama/OllamaCloud share the same `function`-wrapped tool schema.
Backends live in `boukensha.backends` — a nested subpackage, not flattened
into the top-level `boukensha` import, mirroring Ruby's `Boukensha::Backends`
module nesting: `from boukensha.backends import Anthropic`.

Backends also own their supported model table. A backend refuses to
initialize with an unknown model (`UnsupportedModelError`), so
`settings.yaml` cannot silently select an unsupported or misspelled model.
Each model entry carries:

| Key | Meaning |
|---|---|
| `context_window` | The model's known token context window |
| `cost_per_million.input` | USD input token price per million tokens, when known |
| `cost_per_million.output` | USD output token price per million tokens, when known |
| `usage_unit` | `"tokens"`, `"local_compute"`, or `"ollama_cloud_usage"` |
| `usage_level` | Ollama Cloud usage tier, when applicable |

Backend instances expose `context_window`, `input_token_cost_per_million`,
`output_token_cost_per_million`, `usage_unit`, `usage_level`, and
`estimate_cost(*, input_tokens, output_tokens)`. For local Ollama models,
token cost is `0.0`. For Ollama Cloud, public pricing is plan/usage based
rather than token based, so `estimate_cost` returns `None`.

The prices in this step are static tutorial data, current as of June 16,
2026, and should be reviewed whenever the selected model set changes.

### `boukensha.backends.Anthropic`

Talks to `https://api.anthropic.com/v1/messages`. Requires an
`ANTHROPIC_API_KEY`. Supported models are listed in
`boukensha.backends.Anthropic.MODELS`.

### `boukensha.backends.Ollama`

Talks to `http://localhost:11434/api/chat`. Requires `ollama serve` running
locally. No API key needed. Supported models are listed in
`boukensha.backends.Ollama.MODELS`.

### `boukensha.backends.OllamaCloud`

Talks to `https://ollama.com/api/chat`. Requires an `OLLAMA_API_KEY`.
Supported models are listed in `boukensha.backends.OllamaCloud.MODELS`.

### `boukensha.backends.OpenAI`

Talks to `https://api.openai.com/v1/chat/completions`. Requires an
`OPENAI_API_KEY`. Supported models are listed in
`boukensha.backends.OpenAI.MODELS`.

### `boukensha.backends.Gemini`

Talks to `https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent`.
Requires a `GEMINI_API_KEY`. Supported models are listed in
`boukensha.backends.Gemini.MODELS`.

### System Prompt

Anthropic and Gemini send the system prompt as a top-level field, separate
from the messages array. Ollama, OllamaCloud, and OpenAI put it inside the
messages array as a `role: system` message.

```json
// Anthropic
{ "system": "You are a MUD player assistant.", "messages": [ ... ] }

// Gemini
{ "systemInstruction": { "parts": [{ "text": "You are a MUD player assistant." }] }, "contents": [ ... ] }

// Ollama / OllamaCloud / OpenAI
{ "messages": [ { "role": "system", "content": "You are a MUD player assistant." }, ... ] }
```

### Tool Results

Anthropic wraps tool results in a user message. Ollama/OllamaCloud and
OpenAI use their own `role: tool` message type (with slightly different
identifier fields). Gemini wraps results in a `functionResponse` part on a
`user` message.

```json
// Anthropic
{ "role": "user", "content": [{ "type": "tool_result", "tool_use_id": "toolu_01X", "content": "A damp stone corridor stretches north. Torches flicker on the walls." }] }

// Ollama / OllamaCloud
{ "role": "tool", "tool_name": "look", "content": "A damp stone corridor stretches north. Torches flicker on the walls." }

// OpenAI
{ "role": "tool", "tool_call_id": "toolu_01X", "content": "A damp stone corridor stretches north. Torches flicker on the walls." }

// Gemini
{ "role": "user", "parts": [{ "functionResponse": { "name": "toolu_01X", "response": { "content": "A damp stone corridor stretches north. Torches flicker on the walls." } } }] }
```

### Tool Definitions

Anthropic uses `input_schema`. Ollama/OllamaCloud and OpenAI wrap everything
in a `function` envelope with `parameters`. Gemini wraps tools in a
`functionDeclarations` array. `required` always lists every parameter key —
these schemas have no concept of optional tool parameters.

### Message Roles

Anthropic, Ollama/OllamaCloud, and OpenAI all use `assistant` for the
model's turn. Gemini calls it `model`.

## Considerations

**The conversation is stateless.** The model has no memory between turns.
Every API call includes the entire history from the beginning. BOUKENSHA is
responsible for carrying that state.

**Tool results are user messages on Anthropic.** This feels counterintuitive
— the result came from BOUKENSHA, not the human — but it reflects how the
Anthropic API models the conversation. Ollama, OllamaCloud, OpenAI, and
Gemini all handle this with dedicated message/part types instead.

**The agent only sees schemas.** The `description` field on each tool is the
only thing the agent uses to decide which tool to call. The actual block
never leaves BOUKENSHA.

**`model_info` naming collision (Python-specific).** Ruby's
`Backends::Base` has both a class method `self.model_info(model)` (lookup by
name) and an instance method `model_info` (cached reader) — legal in Ruby
because class methods and instance methods live in separate namespaces.
Python can't do that: a `@classmethod` and a same-named instance attribute
collide. The class-level lookup is renamed `model_info_for(cls, model)`;
the instance-level value stays a plain `self.model_info` attribute.

**`PromptBuilder.to_messages()` has a known arity bug for stateful
backends.** It calls `backend.to_messages(context.messages)` with one
argument, which matches Anthropic's and Gemini's `to_messages(messages)`
but raises `TypeError` for Ollama/OllamaCloud/OpenAI, whose
`to_messages(system, messages)` needs two (their APIs fold the system
prompt into the messages array). This is a real bug carried over from the
Ruby source (same `ArgumentError` there) — it is **not** exercised by
`example.rb`/`example.py`, which only ever call `to_api_payload()`. Ported
as-is, not fixed.

## Run Example

```bash
./week1_baseline/bin/python/03_prompt_builder
```

or directly:

```bash
cd week1_baseline/python
.venv/Scripts/python 03_prompt_builder/examples/example.py
```

Expected output matches `ruby/03_prompt_builder`'s example transcript
line-for-line. In this environment, only `provider: anthropic` is
configured with an API key, so the Anthropic branch is the one actually
exercised — the other four backends are fully implemented but unexercised
by this parity check.
