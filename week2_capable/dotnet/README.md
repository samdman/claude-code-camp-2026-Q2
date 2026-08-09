# Boukensha (.NET)

An agentic loop that plays a CircleMUD/tbaMUD-derived MUD, with a companion observability
viewer. Three projects:

- **`Boukensha.Console`** — the agent itself: an interactive REPL (or optional TUI) that
  drives an Anthropic model against the MUD over MCP.
- **`Boukensha.Observability`** — a Razor Pages web app for inspecting sessions, the
  room/exit knowledge graph, a map visualization, the change journal, and a live cockpit view.
- **`Boukensha.Core`** — shared library: MCP client, knowledge store, hooks, logging.

Both apps read the same config directory (`BOUKENSHA_DIR`, default `~/.boukensha`),
containing `settings.yaml` (provider/model, MCP server launch config) and an optional
`.env` (for `ANTHROPIC_API_KEY`).

## Running natively

**Prerequisites:** .NET 10 SDK, Ruby 3+ (for `mud_manager`, the MCP server that talks to
the MUD — see `../../week0_explore/mud_manager`), and a reachable CircleMUD/tbaMUD server.

1. Set up a config directory (defaults to `~/.boukensha`, or point `BOUKENSHA_DIR` at
   anything else) containing `settings.yaml`:

   ```yaml
   tasks:
     player:
       provider: anthropic
       model: claude-haiku-4-5
       prompt_override:
         system: true
   mcp_servers:
     mud:
       command: ruby
       args:
         - /path/to/week0_explore/mud_manager/bin/mud-manager
         - --mcp
       env:
         MUD_HOST: localhost
         MUD_PORT: "4000"
         MUD_USERNAME: dummy
         MUD_PASSWORD: helloworld
   ```

2. Put your key in `<BOUKENSHA_DIR>/.env`:

   ```
   ANTHROPIC_API_KEY=sk-ant-...
   ```

3. Run the agent:

   ```sh
   cd week2_capable/dotnet
   dotnet run --project src/Boukensha.Console
   ```

   By default this launches a TUI. To use the plain REPL instead (recommended over SSH,
   CI, or Docker — see below), pass `--no-tui` or set `BOUKENSHA_TUI=0`.

4. Run the observability viewer (separately, same `BOUKENSHA_DIR`):

   ```sh
   dotnet run --project src/Boukensha.Observability
   ```

   Open `http://localhost:5059` — nav has Sessions, Knowledge, Map, Changes, and Live
   (a 3s-polling cockpit).

5. Build / test:

   ```sh
   dotnet build Boukensha.slnx
   dotnet test Boukensha.slnx
   ```

## Running with Docker Compose

`docker-compose.yml` (repo root) brings up all three components with one command: the
MUD server itself (`circlemud`, tbaMUD, built from `week0_explore/infrastructure/`),
the agent (`boukensha-agent`), and the viewer (`boukensha-observability`).

**Why the agent bundles Ruby too:** the agent spawns `mud-manager --mcp` as a local
child process communicating over stdio (not a network socket) — that's how the whole
MCP wiring in `Boukensha.Core` works today. So `boukensha-agent`'s image contains both
the .NET runtime and Ruby; splitting them into separate containers isn't possible
without rewriting the MCP transport, which this setup doesn't do.

**Shared state:** both `boukensha-agent` and `boukensha-observability` mount the same
named volume at `/data` (`BOUKENSHA_DIR=/data` in both), so the viewer sees the agent's
sessions, knowledge graph, and logs live.

**Secrets:** `ANTHROPIC_API_KEY` is supplied via a repo-root `.env` file
(`env_file:` in compose), never baked into the image.

### First run

```sh
# from the repo root
cp .env.example .env   # then fill in ANTHROPIC_API_KEY
docker compose up -d circlemud boukensha-observability
```

Open the viewer at `http://localhost:8080`.

`Boukensha.Console` is an interactive REPL, so it doesn't fit the "start in the
background" model the other two services use — run it in the foreground instead,
whenever you're ready to actually play:

```sh
docker compose run --rm boukensha-agent
```

The container's `settings.yaml` is bootstrapped automatically on first start (from
`docker/settings.template.yaml`) into the shared `/data` volume, pointed at the
`circlemud` service by its Compose network name and using the same `dummy`/`helloworld`
throwaway character as native runs.

### One-time character creation

A fresh `circlemud` volume has no player accounts yet. `mud_manager`'s login flow only
handles *returning* players (name → straight to a `Password:` prompt) — a brand-new
name instead gets CircleMUD's character-creation dialog (spelling confirmation, set
password, choose sex/class), which the agent doesn't drive. So the very first time you
bring up a fresh stack, create the `dummy` character once by hand before running the
agent:

```sh
docker compose up -d circlemud
docker network ls   # find the Compose network, normally "<project-dir-name>_default"
docker run --rm -it --network <that-network-name> subfuzion/netcat circlemud 4000
```

Walk the prompts: name `dummy` → confirm `Y` → password `helloworld` (twice) → sex
`M` or `F` → any class letter → `[Enter]` through the MOTD → `1` to enter the game →
`quit`. After that, `docker compose run --rm boukensha-agent` logs in automatically on
every subsequent run, since the account now persists in the `circlemud-lib` volume.

### Tearing down

```sh
docker compose down            # stop and remove containers
docker compose down -v         # also delete the circlemud-lib and boukensha-data volumes
```

### Known issue (not Docker-specific)

Long agent turns can hit `400 messages.N.content.0: Input should be an object` from the
Anthropic API after enough tool-call round trips in a single turn. Confirmed to
reproduce identically against a native (non-Docker) run too, so it's a pre-existing bug
in the agent's message construction, not something introduced by containerizing —
tracked separately, not addressed by this Docker setup.
