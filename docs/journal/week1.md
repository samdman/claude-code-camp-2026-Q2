# Week 1 Technical Documentation

## Technical Goal

Understand the individual components that make up a baseline LLM agent (agentic loop, tool registry, prompt/response normalization, context management, logging) well enough to produce a reusable, golden-template implementation of a base agent.

## Technical Uncertainty

Neither Ruby nor Python is my primary language, so idiomatic usage in both was an open question going in. Running the tutorial on Windows was an additional unknown, since the setup was authored against Mac/Linux and some tooling (native gems, cert paths, shell scripts) was expected to need workarounds.

## Technical Observations

Building the agent loop from raw REST calls (no SDK) exposed how much a normal agent framework hides: request/response shape differences across 5 providers, tool-call dispatch, context-window/token tracking, and conversation compaction all had to be handled by hand, one HTTP call at a time. Windows was the single biggest source of friction, not the languages themselves:

- Ruby's TUI step depends on native gems (bubbletea/lipgloss) that simply don't ship a Windows build — had to run that step in a Linux Docker container to verify it at all.
- OpenSSL cert paths, console encoding (UTF-8 vs the Windows codepage), and even gem/bundle platform resolution needed manual intervention more than once.
- The tutorial's own Ruby source drifted from its documentation (a step's README or ITERATIONS.md description didn't match what the checked-in code actually did) more than once, which reinforced "trust the code you can run, not the doc describing it."
- I have a few skills installed which picks up the creation of plan etc. sometimes it over engineers the approach.

## Technical Conclusions

Coming from a decade of C#/.NET, Ruby and Python's differences from each other (symbols vs. strings, implicit blocks vs. explicit callbacks, snake_case query methods) were smaller than the shared adjustment away from .NET: duck typing and dynamic dispatch instead of interfaces/generics, and no compiler catching a mismatched shape until runtime. Porting the Ruby baseline to Python line-by-line turned out to be a good way to learn both languages at once, since "does this match known Ruby behavior" is a much sharper correctness bar than "does this look idiomatic." Python's single, cumulatively-growing package (versus Ruby's independent per-step snapshots) also forced more upfront thinking about backward compatibility than Ruby's tutorial structure ever required.

## Key Takeaway

Hand-building the loop, tool registry, and multi-provider normalization made it clear that agent frameworks aren't doing anything conceptually exotic — it's a small, learnable set of concerns (loop, context, tool dispatch, provider translation) worth understanding directly at least once before reaching for a framework that hides them.

