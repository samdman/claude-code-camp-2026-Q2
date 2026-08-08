# 04 · The API Client (Python port)

Python port of `ruby/04_api_client`. Adds `Client` — a small, retrying HTTP
client built entirely on the standard library — and `ApiError` to the
shared `boukensha` package. See `ruby/04_api_client/README.md` for the full
design rationale. This file only documents the Python-specific parts.

The API Client takes the payload assembled by `PromptBuilder` and sends it
to the API. One HTTP POST, one response. No tool loop yet — just proving
the round trip works.

## How It Works

```
PromptBuilder
      ↓
Client
      ↓
POST to API endpoint
      ↓
Raw JSON response
```

## `boukensha.Client`

| Method | Description |
|---|---|
| `call(*, max_output_tokens=1024)` | POSTs the payload and returns the parsed JSON response |

`Client` retries on transient network failures and retryable HTTP status
codes (`408, 409, 429, 500, 502, 503, 504`), up to 3 retries (4 attempts
total), with exponential backoff (`0.5s, 1.0s, 2.0s`). Any failure past
that budget — or an immediately non-retryable failure like a `401` — raises
`ApiError`.

## Task Configuration

This step uses the task-based configuration introduced in earlier steps:

```yaml
tasks:
  player:
    provider: anthropic
    model: claude-haiku-4-5
    prompt_override:
      system: true
```

When `prompt_override.system` is true, Boukensha reads
`.boukensha/prompts/player/system.md`. Otherwise it falls back to this
project's shipped `python/prompts/system.md`.

## No Third-Party Dependencies

`Client` uses Python's standard `urllib.request` — no `requests`, no
`httpx`. This mirrors Ruby's own stance (`net/http`, no gems): the HTTP
call itself is trivial and should stay visible, not hidden behind a
library.

**SSL is handled automatically, with no manual configuration.** Ruby's
`client.rb` has to explicitly set `verify_mode = OpenSSL::SSL::VERIFY_PEER`
and comments out a broken cross-platform certificate-path workaround
(`OpenSSL::X509::DEFAULT_CERT_FILE` doesn't exist on Linux/WSL2, even
though it does on macOS). Python's `urllib.request.urlopen` doesn't need
any of this — it uses `http.client.HTTPSConnection` automatically for
`https://` URLs and, since Python 3.4.3, defaults to an `SSLContext` that
verifies certificates against the OS's own trust store on every platform.
There's no Ruby-style workaround to port because there's no equivalent
problem in Python's stdlib.

**Retry exception mapping.** Ruby's `TRANSIENT_ERRORS` list doesn't map 1:1
onto Python's networking exceptions. The chosen mapping:

| Ruby | Python |
|---|---|
| `Errno::ECONNRESET` | `ConnectionResetError` |
| `Errno::ECONNREFUSED` | `ConnectionRefusedError` |
| `Net::OpenTimeout` / `Net::ReadTimeout` / `Timeout::Error` | `TimeoutError` |
| `OpenSSL::SSL::SSLError` | `ssl.SSLError` |
| `SocketError` (DNS failure) | `socket.gaierror` |
| `EOFError` (truncated response) | `http.client.IncompleteRead` |
| — | `http.client.RemoteDisconnected` (added for robustness) |
| — (catch-all `urlopen` wrapper) | `urllib.error.URLError` |

`urllib.error.HTTPError` (any non-2xx response) is caught separately,
*before* the transient-errors tuple, since it's technically a subclass of
`URLError` — this lets its status code feed the same retryable-status-code
logic that a successful-but-retryable response uses. Ruby's `Net::HTTP`
never raises for non-2xx at all, so this split is a Python-specific
necessity to reach the same behavior.

This retry logic isn't exercised by a live run (the network is healthy),
so it's covered by a mocked smoke check instead — see that task's
verification step for four scenarios: success, transient-error-then-
success, persistent-retryable-failure past the retry budget, and an
immediate non-retryable failure.

## Response Is Non-Deterministic

Unlike every prior step, this one makes a real call to a live LLM. The
response — the model's actual reply — differs run to run: sometimes plain
text, sometimes a `tool_use` request, different token counts and IDs every
time. Parity with the Ruby port is checked structurally (the deterministic
preamble matches exactly; the JSON response is checked for expected
top-level keys), not by a blind byte-for-byte `diff`.

No tool actually gets *dispatched* in this step — the model may request
`read_file` or `list_directory`, but nothing executes it yet. That's step
05, the Agent Loop.

## Run Example

```bash
./week1_baseline/bin/python/04_api_client
```

or directly:

```bash
cd week1_baseline/python
.venv/Scripts/python 04_api_client/examples/example.py
```

**This makes a real, billed API call** to whichever provider `settings.yaml`
configures (Anthropic in this environment).
