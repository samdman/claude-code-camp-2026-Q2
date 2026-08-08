---
name: porting-ruby-to-python
description: Use when porting the next ruby/<NN>_<name> Boukensha tutorial step (under week1_baseline/) to the shared python/boukensha package, or when asked to write, execute, or wrap up a python-port plan under docs/plans/python_port/.
---

# Porting Ruby to Python (Boukensha)

## Overview

`week1_baseline/ruby/<NN>_<name>/` ships a fresh, fully self-contained gem
per tutorial step — each folder is an independent snapshot, not an
accumulating diff. `week1_baseline/python/src/boukensha/` is the opposite:
one project that only ever grows. **The only thing that changes between two
consecutive Python-port steps is the delta between two consecutive Ruby
steps.** Never re-port a Ruby step's full source from scratch — find what
changed since the last step and port only that onto the existing package.

**REQUIRED BACKGROUND:** Read `docs/plans/python_port/IMPLEMENTATION.md`
before starting anything. It holds the Ruby→Python idiom mapping table,
known deliberately-unfixed warts, Ruby's own source/doc drift patterns, and
the per-step decision log. This skill is the *process*; that file is the
*accumulated knowledge* — it keeps growing, so read it fresh each time
rather than trusting memory of an earlier read.

## When to Use

- Asked to port the next Ruby tutorial step to Python.
- Asked to write a plan at `docs/plans/python_port/<NN>_<name>`.
- Asked to execute an already-written step plan.
- Asked to update `IMPLEMENTATION.md` after finishing a step.

## Workflow

1. **Find the next step, don't assume one exists.** Compare the highest
   `ruby/<NN>_<name>/` against the highest *executed* step in the Python
   port. A plan file existing at `docs/plans/python_port/<NN>_<name>` only
   proves a plan was written, not that it ran — confirm execution against
   `git log` (look for that step's "port ... to python" commits) or the
   actual state of `python/src/boukensha/`, not the plan file's mere
   existence. If Ruby has no new step beyond the last one actually ported,
   there's nothing to do yet — say so.
2. **Diff the delta, not the whole step.** `diff -u
   ruby/<prev>/lib/boukensha/x.rb ruby/<new>/lib/boukensha/x.rb` for every
   file, file by file. Only files that actually differ get a task in the
   plan — say so explicitly for the rest ("confirmed byte-identical, no
   action").
3. **Watch for Ruby's own drift, not just Python gaps.** Because each Ruby
   step is an independent snapshot, a feature can disappear in one step and
   reappear later (`IMPLEMENTATION.md` § Ruby Source Drift has a real
   example: `PROMPTS_DIR` vanished for two steps then came back). If a diff
   shows something "new" that Python already has, check whether Python
   ported it once and simply never had a reason to remove it, rather than
   assuming a missed step.
4. **Verify by running, never by reading the README.** Every step's README
   so far has had at least one stale or wrong "Expected Output" section.
   Before writing "Behavior to Preserve Exactly" in the plan, actually run
   `bash bin/ruby/<step>` (or `bundle.bat exec ruby examples/example.rb`
   from that step's dir) and capture the real transcript.
5. **Confirm before any live call with a real-world effect.** If the
   step's example makes a network call, spends money, or has any other
   real side effect (first appeared at step 04's API client), ask the user
   before running it — including during plan-writing. State plainly in the
   plan whether parity is byte-diffable or must be checked structurally
   (deterministic parts diffed exactly, non-deterministic parts checked for
   expected shape).
6. **Apply the established idiom mapping by default** — don't re-derive
   settled conventions. `IMPLEMENTATION.md`'s table covers symbols→strings,
   `attr_reader`→plain attribute, zero-arg computed method→`@property`,
   `?`/`!` suffix→dropped, implicit blocks→explicit `block=` kwarg, module
   nesting mirrored not flattened, and more. Only deviate with a documented
   reason in the new step's plan.
7. **Write the plan** at `docs/plans/python_port/<NN>_<name>` (no file
   extension — matches every step so far). **REQUIRED SUB-SKILL:**
   superpowers:writing-plans. Structure, mirroring steps 00–04: header
   (Goal/Architecture/Tech Stack + REQUIRED SUB-SKILL line), Global
   Constraints, Reference Files (the diff table from step 2), Behavior to
   Preserve Exactly (the real transcript from step 4), Confirmed Decisions
   (resolve open questions yourself with a recommendation, don't leave them
   dangling), Task Breakdown (one task per new/changed file, complete
   runnable code, no placeholders, each ending in a smoke-check and a
   commit step), Self-Review Notes.
8. **Offer an execution choice, but expect Inline.** Steps 00–04 all ended
   up executed directly in-session — the human partner shortcut the
   subagent-per-task flow partway through step 02 and that became the
   default. **REQUIRED SUB-SKILL:** superpowers:executing-plans for inline
   execution, or superpowers:subagent-driven-development only if
   explicitly requested.
9. **Don't commit per task unless asked.** Leave changes staged for the
   human partner to commit as one batch, unless told to commit as you go.
10. **Update `IMPLEMENTATION.md` when the step is done.** Append one entry
    to the Per-Step Decision Log, and add to Ruby Source Drift / README
    Drift / Known Warts / Testing & Verification if the new step surfaced
    any. Keep entries terse — the full story lives in that step's plan
    file, this doc just indexes it.

## Quick Reference

| Convention | Where it lives |
|---|---|
| Ruby→Python idiom mapping | `IMPLEMENTATION.md` § Ruby → Python Idiom Mapping |
| Known unfixed warts (port as-is, don't fix) | `IMPLEMENTATION.md` § Known, Deliberately-Unfixed Warts |
| `bin/` is gitignored repo-wide | entrypoint scripts created on disk, never committed; only that step's README is |
| One package, not per-step folders | `python/src/boukensha/` grows via git history; `python/<NN>_<name>/` holds only that step's example + README |
| No pytest suite | parity = diff `bin/ruby/<step>` vs `bin/python/<step>` output, or a structural check when output is non-deterministic |

## Common Mistakes

- Trusting a Ruby step's README "Expected Output" instead of running the
  real example — every step so far has had at least one mismatch.
- Treating a Ruby diff as ground truth for "what's new" without checking
  whether Python already has it from an earlier step.
- Making a real network/paid call without confirming with the user first.
- Re-arguing a convention `IMPLEMENTATION.md` already settled (plain
  strings over symbols, the `block=` kwarg name, etc.) instead of applying
  it.
- Committing changes the human partner hasn't asked to be committed yet.
- Writing the plan's "Behavior to Preserve Exactly" from the README instead
  of a live-captured transcript.
