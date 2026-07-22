---
name: play-mud
description: Play tbaMUD at localhost:4000 with Python client - tracks game state in data/player.md and data/world.md
---

# MUD Play - tbaMUD Client

Python-based tbaMUD client with automatic state tracking.

**Server:** localhost:4000  
**Player:** dummy / helloworld  
**State Files:** data/player.md, data/world.md

## Quick Start

### Find Bakery & Show Menu
```bash
python3 scripts/mud.py --bakery
```

### Interactive Mode
```bash
python3 scripts/mud.py --interactive
```

### Send Single Command
```bash
python3 scripts/mud.py --command "look"
```

### Default (Show Current Location)
```bash
python3 scripts/mud.py
```

## Python Script Usage

### Options
```
--bakery           Find bakery and show menu (saves to state files)
--interactive, -i  Interactive mode - type commands freely
--command, -c      Send single command and exit
--host             MUD host (default: localhost)
--port             MUD port (default: 4000)
--user             Username (default: dummy)
--password         Password (default: helloworld)
```

## MUD Commands

Once in interactive mode, use these commands:

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

The script automatically saves game state:

- **data/player.md** - Player activities, inventory, bakery visits
- **data/world.md** - Locations discovered, NPCs, shops

These files update after each session for persistent record-keeping.

## Examples

### Find the bakery and show menu
```bash
python3 scripts/mud.py --bakery
```
Output saved to data/player.md and data/world.md

### Enter interactive mode
```bash
python3 scripts/mud.py --interactive
```
Type commands freely, press Ctrl+C or type 'quit' to exit

### Send specific command
```bash
python3 scripts/mud.py --command "who"
```

### Connect to different server
```bash
python3 scripts/mud.py --host mud.example.com --port 5000 --user player --password pass
```

## Alternative: Telnet Direct

You can still connect directly with telnet:
```bash
telnet localhost 4000
```
Login: dummy / helloworld

---

**MUD:** tbaMUD 2025 (CircleMUD variant)  
**Type:** Python 3 telnet client  
**State Files:** data/player.md, data/world.md
