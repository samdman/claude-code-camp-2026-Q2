# MudManager

The MudManager has the following responsibilities:

- manages long-lived telnet sessions
- manages the multi-step process of logging back in
- provides generic primitives for MUD commands

## Build the Gem

From this directory:

```sh
gem build mud_manager.gemspec
gem install ./mud_manager-0.1.0.gem
```

Expected output:

```text
MudManager
```

## Uninstall

```sh
gem uninstall mud_manager
```

## Examples

Test the live session:

```sh
MUD_NAME=YourCharacterName MUD_PASSWORD=yourpassword ruby mud_manager/examples/live_session_test.rb
```

If you are already inside the `mud_manager` directory, run:

```sh
MUD_NAME=YourCharacterName MUD_PASSWORD=yourpassword ruby examples/live_session_test.rb
```

## MCP Server

`mud-manager --mcp` runs the same `MudManager::Session` + `MudManager::Primitives`
gameplay surface as an MCP server over stdio, so any MCP-capable client (in any
language) can drive the MUD without linking against this gem's Ruby code.

```sh
MUD_USERNAME=YourCharacterName MUD_PASSWORD=yourpassword ruby bin/mud-manager --mcp
```

It opens the connection and logs in once at startup, then serves `tools/list` /
`tools/call` requests for the lifetime of the process — the same "log in once,
reuse across many commands" model `Session` was built for.

Protocol-level tests (no live MUD required):

```sh
ruby examples/mcp_server_test.rb
ruby examples/mcp_tools_test.rb
```
