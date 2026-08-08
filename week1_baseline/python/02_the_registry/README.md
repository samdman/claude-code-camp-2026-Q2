# 02 · The Tool Registry (Python port)

Python port of `ruby/02_the_registry`. Adds `Registry` (tool registration +
dispatch) and `UnknownToolError` to the shared `boukensha` package. See
`ruby/02_the_registry/README.md` for the full design rationale. This file
only documents the Python-specific parts.

## How It Works

The agent NEVER calls a tool directly. It emits a structured request (name
and args) and the Registry looks up the tool and runs it.

```
Agent:  "Hey registry call move with direction='north'"
Registry: "looking up "move" in the tool table"
Registry: "Found it now calling the block with the provided args"
Registry: "Here's the result"
Agent: "Thanks buddy"
Registry: "Thats why you pay me the big tokes"
```

## `boukensha.Registry`

| Method | Description |
|---|---|
| `tool(name, *, description, parameters=None, block)` | Registers a new tool on the context |
| `dispatch(name, args=None)` | Looks up a tool by name and calls it with the provided args |

## `boukensha.UnknownToolError`

Raised when `dispatch` is called with a name that has no registered tool. A
harness needs explicit error boundaries — an unrecognised tool name should
never silently fail.

**Example:**
```
UnknownToolError: No tool registered as 'flee'
```

## Expected Output

Captured by actually running `ruby/02_the_registry/examples/example.rb` —
note this diverges from the Ruby README's own "Expected Output" section
(no `budget=8192` field, and `Context`'s repr includes `task=player`):

```
=== BOUKENSHA Step 2: Tool Registry ===

Config:  #<Boukensha::Config dir=... tasks=player>
Context: #<Context task=player turns=0 tools=2>
Tools:
  #<Tool name=move description=Move the player in a direction (north, so params=[:direction]>
  #<Tool name=shout description=Shout a message so everyone in the zone c params=[:message]>

Dispatching 'shout' with message='dragon spotted'...
Result: DRAGON SPOTTED

Dispatching 'move' with direction='north'...
Result: You move north into a torch-lit corridor.

UnknownToolError caught: No tool registered as 'flee'
```

## Considerations

We now register tools with the Registry but our code still has direct
registration and tools stored on `Context`, not on the `Registry` itself —
`Registry` is a thin façade that still calls `context.register_tool(tool)`
and `dispatch` still reads from `context.tools`. This is a known,
intentional-for-now design wart carried over from Ruby (see
`ITERATIONS.md`) and not something this step fixes.

Ruby's `dispatch` also converts string keys to symbol keys before calling
the block, since the API returns arguments as string-keyed JSON but Ruby
blocks expect symbols. The Python port skips this translation entirely —
Python's API was already all-string from step 00 onward, so there was never
a string/symbol distinction to bridge.

## Run Example

```bash
./week1_baseline/bin/python/02_the_registry
```

or directly:

```bash
cd week1_baseline/python
.venv/Scripts/python 02_the_registry/examples/example.py
```

Expected output matches `ruby/02_the_registry`'s example transcript
line-for-line.
