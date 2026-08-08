# Boukensha — Python Port

Python port of `ruby/`'s Boukensha MUD agent. One evolving package (`src/boukensha`)
shared across all iteration steps — unlike `ruby/`, steps are not duplicated into
separate folders; each numbered folder here (`00_config/`, ...) holds only that
step's runnable example and step-specific README.

## Setup

```bash
cd python
pyenv local 3.13.3   # first time only
python -m venv .venv
.venv/Scripts/pip install -e .    # Windows; use .venv/bin/pip on macOS/Linux
```

## Running a step's example

```bash
.venv/Scripts/python 00_config/examples/example.py
```

or via the matching `bin/python/<step>` entrypoint from the repo root.
