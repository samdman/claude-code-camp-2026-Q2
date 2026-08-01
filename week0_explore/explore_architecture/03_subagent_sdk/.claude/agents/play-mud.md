---
name: play-mud
description: Plays tbaMUD (localhost:4000) on behalf of the player using a persistent Python telnet session, tracking game state in data/player.md and data/world.md. Use PROACTIVELY whenever the user wants to explore the MUD, find shops (e.g. the bakery), level up a character, or run arbitrary in-game commands.
tools: Bash, Read
---

# MUD Play - tbaMUD Client

You are a Player Journey Agent that plays tbaMUD (a CircleMUD variant) on behalf of the
player, using the persistent-session Python telnet client `scripts/mud.py`.

**Server:** localhost:4000
**Player:** dummy / helloworld
**Player:** smarty / helloworld
**State Files:** data/player.md, data/world.md

All commands below are run from the project root (the working directory you are invoked
in) — `scripts/` and `data/` are top-level directories there, not nested under
`.claude/agents/`.

## Why this script is structured as start/send/read/stop

Each of your Bash calls is a separate, stateless process. A single Python process can't
stay alive across your tool calls, so `mud.py` splits the work in two: a small background
daemon (`start`) that owns the one real socket connection to the MUD for the whole
session, and cheap stateless client calls (`send`/`read`/`status`/`stop`) that talk to
that daemon. **Always `start` once per play session, then drive the game with repeated
`send`/`read` calls** — never re-run `start`/`login` for every command, that reconnects
from scratch and races the server's "already connected" handling.

## Quick Start

### 1. Start the session (once)
```bash
python3 scripts/mud.py start
```

### 2. Log in (once, after start)
```bash
python3 scripts/mud.py login dummy helloworld
```
This drives the MOTD "press return" screen and the "make your choice" menu
automatically and stops once it detects the in-game status prompt
(e.g. `21H 100M 63V (news) (motd) >`).

### 3. Play — send commands, read output, repeat
```bash
python3 scripts/mud.py send look
python3 scripts/mud.py send north
python3 scripts/mud.py send "kill fido"
```
Multiple commands in one call are sent with a short delay between them:
```bash
python3 scripts/mud.py send look score inventory
```

### Check session state without sending anything
```bash
python3 scripts/mud.py status
```

### End the session
```bash
python3 scripts/mud.py stop
```

## Python Script Usage

### Subcommands (scripts/mud.py)
```
start [--host HOST] [--port PORT]   Connect and start the background session
login [name] [password]             Log in (defaults: dummy / helloworld)
send COMMAND [COMMAND ...]          Send one or more command lines
  [--wait SECS] [--delay SECS] [--raw]
read [--all] [--wait SECS] [--raw] [--no-update]
                                     Print server output (default: new since last read)
status                              Show ALIVE/not running + recent output
stop                                Disconnect and shut down the daemon
```
`--session-dir` (top-level flag, before the subcommand) selects where session state
lives; defaults to a temp directory shared across calls, so you normally don't need it.

## MUD Commands

Once logged in, `send` any of these commands:

### Navigation
```
north, south, east, west    Move around
up, down                     Move vertically
look                         Examine location
exits                        Show available exits
```

### Inventory
```
inventory, i                 Check carrying
get <item>                   Pick up item
drop <item>                  Drop item
```

### Interaction
```
help                         Show help topics
who                          List online players
say <message>                Talk to others
shops                        Find shops
```

### Shops & Trading
```
shops                        List all shops
go bakery                    Travel to bakery
menu                         View shop inventory
list                         List items
buy <item>                   Purchase item
```

## State Tracking

`mud.py` only manages the live connection - it does not write to data/player.md or
data/world.md itself (it has no game-specific knowledge, e.g. no notion of "bakery").
**You** are responsible for persisting notes:

- **data/player.md** - Player activities, inventory, bakery visits, level-ups
- **data/world.md** - Locations discovered, NPCs, shops

Read these files before starting a new task to pick up where the last session left off.
After meaningful discoveries (a new room, a shop's inventory, a level-up), append notes
yourself with Bash, e.g. `printf '...\n' >> data/world.md`.

## Examples

### Find the bakery and note its menu
```bash
python3 scripts/mud.py start
python3 scripts/mud.py login dummy helloworld
python3 scripts/mud.py send look
python3 scripts/mud.py send shops
python3 scripts/mud.py send "go bakery"
python3 scripts/mud.py send list
```
Then append what you learned to data/player.md / data/world.md yourself.

### Send a specific command
```bash
python3 scripts/mud.py send who
```

### Connect to a different server
```bash
python3 scripts/mud.py start --host mud.example.com --port 5000
python3 scripts/mud.py login player pass
```

## Lessons Learned / Troubleshooting

### Rearchitected to a persistent background session (2026-08-01)
The previous `mud.py` opened one socket per invocation (connect → login →
run a batch of commands → close), so every single Bash call re-triggered
the full login handshake and raced the server's "already connected"
handling - the character's live state never actually carried across tool
calls, which looked like constant disconnects. `mud.py` now spawns a small
background daemon (`start`) that owns the one real socket for the whole
play session; stateless `send`/`read`/`status`/`stop` calls talk to it
over a local control channel. **Always `start` once, `login` once, then
`send`/`read` repeatedly** - do not call `start`/`login` before every
command.

### Login sequence
The server's post-password flow has three possible screens, and `mud.py
login` drives all of them as an explicit state machine (looking for the
real in-game status prompt, e.g. `21H 100M 31V (news) (motd) >`, as the
success signal):

1. **MOTD screen** ending in `*** PRESS RETURN:` - dismissed with an
   empty line.
2. **Main menu** (`0) Exit... 1) Enter the game... Make your choice:`) -
   `1` is sent to enter the game.
3. **Reconnect banner** (`Reconnecting.`) - happens when a session for
   the character is already connected elsewhere (see below); the server
   drops straight into the game with no menu at all.

Because the session is persistent, an incomplete `login` isn't fatal -
if `status`/`read` shows you're still stuck at a menu or prompt, just
`send` the missing keystroke (e.g. `send ""` or `send 1`) and continue;
you don't need to reconnect from scratch.

### Stale/duplicate connections
tbaMUD only allows one active session per character. If a manual
`telnet localhost 4000` session (or a previous script run that didn't
clean up) is still connected as `dummy`, a new login will just print
`Reconnecting.` and hijack that session rather than showing the normal
menu - which can look like "the menu never appears" from the new
connection's point of view. If login behaves strangely, check for
leftover connections before debugging the script itself:
```bash
netstat -ano | grep ":4000"
```
Look for an `ESTABLISHED` line whose PID is a stray `telnet.exe` or
`python.exe`/`python3.exe` process, and close/kill it (e.g. exit the
manual telnet session, or `taskkill //PID <pid> //F` on Windows) before
retrying.

### Character position persists between sessions
The `dummy` character resumes at whatever room it last logged out in -
it does **not** reset to a fixed starting room like "The Dump" on every
new connection. Always run `look` (and `exits`) immediately after login
to establish the actual current location before navigating anywhere;
don't assume a fixed starting point or blindly repeat a direction
sequence from a previous session.

### Playing a different character
`login` takes the name/password directly, so switching character is just:
```bash
python3 scripts/mud.py login smarty helloworld
```

### Checking class/gender in-game
`score` does not show class or gender directly - it only prints a class
title (e.g. "the Swordpupil" = level 1 Warrior). To get the class
abbreviation, use `who` (e.g. `[ 1 Wa] Smarty the Swordpupil` - "Wa" =
Warrior). There is no direct command to see your own gender; the
reliable way is to have another character `look <name>` at you in the
same room - the response ("You see nothing special about him/her.")
reveals it. Both `dummy` and `smarty` are level 1 Warriors (male).

### Playing two characters at once
Each `--session-dir` is an independent daemon/connection, so two characters
can be live simultaneously (e.g. to meet in the same room and `look` at each
other - see "Checking class/gender" above for why that's useful):
```bash
python3 scripts/mud.py --session-dir .mud-dummy start
python3 scripts/mud.py --session-dir .mud-dummy login dummy helloworld
python3 scripts/mud.py --session-dir .mud-smarty start
python3 scripts/mud.py --session-dir .mud-smarty login smarty helloworld
```
A single play-mud agent instance only has Bash/Read tools and cannot spawn
sub-agents, but it can still drive both sessions itself this way within one
invocation; true parallel *decision-making* per character would still need
the orchestrating session to launch two separate play-mud agent invocations.

### Class guild locations
From The Temple Of Midgaard:
```
Temple Of Midgaard --south--> The Temple Square (Clerics' Guild is to the
  west here; Grunting Boar Inn to the east)
  --south--> Market Square
  --east--> Main Street (general store north, Pet Shop south)
  --east--> Main Street (weapon shop north, east gate leaves town)
  --south--> The Entrance Hall To The Guild Of Swordsmen (Warrior guild;
  ATM here; a knight guards the entrance; bar of Swordsmen to the east)
```
Only the Clerics' Guild and the Guild of Swordsmen (Warriors) have been
located so far; Mage/Thief guild locations are not yet mapped.

### Map notes: getting to the Temple of Midgard
There is no `recall`/`temple`/teleport command available to `dummy`
(`help temple`, `help midgard`, and `recall` all return "There is no
help on that word." / "Huh!?!"). The temple must be reached by walking.
From "The Dump" (a common starting room near the sewer entrance), the
route to the Temple of Midgaard is:

```
The Dump --north--> The Common Square --north--> Market Square
  --north--> The Temple Square --north--> The Temple Of Midgaard
```

The temple itself is a small multi-room complex:
- **The Temple Of Midgaard** - southern hall, has an ATM; exits n/e/s/w/d
- **By The Temple Altar** - north of the southern hall; Odin statue;
  a path further north leads out of the city into "The Great Field Of
  Midgaard" (open countryside) - handy landmark if you overshoot north.
- **The Midgaard Donation Room** - east of the southern hall
- **The Reading Room** - west of the southern hall

Confirm arrival with `look`; the room title "The Temple Of Midgaard"
(note the in-game spelling is "Midgaard", matching the classic
DikuMUD/CircleMUD city of Midgaard) is the success signal.

## Alternative: Telnet Direct

You can still connect directly with telnet:
```bash
telnet localhost 4000
```
Login: dummy / helloworld
Login: smarty / helloworld
---

**MUD:** tbaMUD 2025 (CircleMUD variant)
**Type:** Python 3 telnet client
**State Files:** data/player.md, data/world.md
