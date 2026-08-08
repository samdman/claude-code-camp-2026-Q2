# 00 · Configuration (Python port)

Python port of `ruby/00_config`. Same behavior, same `.boukensha/` directory, same
`settings.yaml` schema — see `ruby/00_config/README.md` for the full design
rationale. This file only documents the Python-specific parts.

Unlike `ruby/`, the Python port does not duplicate the package per step. The
`boukensha` package lives once at `python/src/boukensha/` and grows across
iterations; this folder holds only this step's runnable example.

## Design Considerations

Same as the Ruby step: use the standard library as much as possible. The only
third-party dependencies are `PyYAML` (YAML parsing) and `python-dotenv`
(`.env` loading) — the direct equivalents of Ruby's stdlib `YAML` and the
`dotenv` gem.

## Code Changes

| File | Purpose |
|------|---------|
| `python/src/boukensha/config.py` | `Config` class |
| `python/src/boukensha/tasks/base.py` | abstract `Base` (provider/model + prompt resolution) |
| `python/src/boukensha/tasks/player.py` | concrete `Player` (the main loop) |
| `python/src/boukensha/__init__.py` | package exports (`Config`, `Player`) |
| `python/prompts/system.md` | default system prompt shipped with the package |
| `python/00_config/examples/example.py` | runnable smoke-test |

---

## Config directory resolution

Same order as Ruby:

1. **`BOUKENSHA_DIR` env var** — set this to point at any directory you like.
2. **`~/.boukensha`** — the default location for a real install.

## Config directory structure

```
.boukensha/
  .env                 # stores credentials eg. LLMs APIs (never committed to repo)
  settings.yaml        # all non-secret settings
  prompts/
    <task>/
      system.md        # per-task override for the default system prompt (optional)
```

---

## Tasks

`Base` is an abstract class, never instantiated — all behaviour is exposed as
classmethods that accept a `settings` dict. Concrete subclasses define
`task_name()`. For now only `Player` exists.

`Config.tasks()` returns the raw dict from `settings.yaml` under `tasks:`.
Pass a name to look up a specific task's settings dict, then pass it to the
task class:

```python
from boukensha import Config, Player

config = Config()
Player.provider(config.tasks("player"))
Player.system_prompt(
    config.tasks("player"),
    user_prompts_dir=config.user_prompts_dir,
    default_prompts_dir=Config.PROMPTS_DIR,
)
```

## System prompt resolution

Per task, `Player.system_prompt` is resolved in this order:

1. **`.boukensha/prompts/<task>/system.md`** — used when the task's
   `prompt_override.system` is `True` and the file exists.
2. **`prompts/system.md`** — the default system prompt shipped with the package.

## Configuration Schema

Identical to `ruby/00_config`:

```yaml
tasks:
  player:
    provider: anthropic        # provider name (string)
    model: claude-haiku-4-5
    prompt_override:
      system: true
mud:
  host: localhost
  port: 4000
  username: dummy
  password: helloworld
```

## Run Example

```bash
./week1_baseline/bin/python/00_config
```

or directly:

```bash
cd week1_baseline/python
.venv/Scripts/python 00_config/examples/example.py
```

Expected output (values from your `.boukensha/`) matches `ruby/00_config`'s
"Run Example" transcript line-for-line, aside from `prompt_override?` and
`API key set?`, which are printed lowercase (`true`/`false`) to match Ruby's
output exactly rather than Python's default `True`/`False`.

## Considerations

Inherited from `ruby/00_config`'s own "Considerations" — not fixed here since
future steps will address them:

- the default prompt (`prompts/system.md`) is not yet scoped per-task
  (`prompts/<task>/system.md`)
- `settings.yaml` is the only extension accepted; `.yml` is not
