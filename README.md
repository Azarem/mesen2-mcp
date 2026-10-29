# Mesen

Mesen is a multi-system emulator (NES, SNES, Game Boy, Game Boy Advance, PC Engine, SMS/Game Gear, WonderSwan) for Windows, Linux and macOS.  

## Releases

The latest stable version of the Mesen emulator is available from the [releases on GitHub](https://github.com/SourMesen/Mesen2/releases).  

**MCP server (this fork):** download the latest zip from the [mesen2-mcp releases](https://github.com/Azarem/mesen2-mcp/releases/latest) and follow the [installation instructions](#installation).  

## Development Builds

[![Mesen](https://github.com/SourMesen/Mesen2/actions/workflows/build.yml/badge.svg)](https://github.com/SourMesen/Mesen2/actions/workflows/build.yml)

#### <ins>Native builds</ins> (recommended) ####

These builds don't require .NET to be installed.  

* [Windows 10 / 11](https://nightly.link/SourMesen/Mesen2/workflows/build/master/Mesen%20%28Windows%20-%20net8.0%20-%20AoT%29.zip)  
* [Linux x64](https://nightly.link/SourMesen/Mesen2/workflows/build/master/Mesen%20%28Linux%20-%20ubuntu-22.04%20-%20clang_aot%29.zip)  (requires **SDL2**)
* [macOS - Intel](https://nightly.link/SourMesen/Mesen2/workflows/build/master/Mesen%20%28macOS%20-%20macos-13%20-%20clang_aot%29.zip)  (requires **SDL2**)
* [macOS - Apple Silicon](https://nightly.link/SourMesen/Mesen2/workflows/build/master/Mesen%20%28macOS%20-%20macos-14%20-%20clang_aot%29.zip)  (requires **SDL2**)

#### <ins>.NET builds</ins> ####

These builds require **.NET 8** to be installed (except the Windows 7 build which requires .NET 6).  
For Linux and macOS, **SDL2** must also be installed.

* [Windows 7 / 8 (.NET 6)](https://nightly.link/SourMesen/Mesen2/workflows/build/master/Mesen%20%28Windows%20-%20net6.0%29.zip)  
* [Linux x64 - AppImage](https://nightly.link/SourMesen/Mesen2/workflows/build/master/Mesen%20(Linux%20x64%20-%20AppImage).zip)  
* [Linux ARM64](https://nightly.link/SourMesen/Mesen2/workflows/build/master/Mesen%20%28Linux%20-%20ubuntu-22.04-arm%20-%20clang%29.zip)  
* [Linux ARM64 - AppImage](https://nightly.link/SourMesen/Mesen2/workflows/build/master/Mesen%20(Linux%20ARM64%20-%20AppImage).zip)


#### <ins>Notes</ins> ####

Other builds are also available in the [Actions](https://github.com/SourMesen/Mesen2/actions) tab.

**SteamOS**: See [SteamOS.md](SteamOS.md)

## Compiling

See [COMPILING.md](COMPILING.md)

## MCP Server Mode

This fork includes an in-process C# [Model Context Protocol](https://modelcontextprotocol.io) server that drives the emulator and its debugger over stdio JSON-RPC. It exposes 80 tools: ROM and state control, stepping, memory, labels, breakpoints (halting, counting and logging), tracing, code/data log, screenshots, PPU/DMA/OAM state, input recording and replay, Lua scripting with a helper kit, two-ROM comparison and a self test.

### Installation

#### 1. Requirements

- The [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (the "Runtime", not the SDK, is enough). Check with `dotnet --list-runtimes`; you need a `Microsoft.NETCore.App 8.x` line.
- A 64-bit OS: Windows x64, Linux x64 or arm64, macOS x64 or Apple Silicon. Pick the release zip that matches.
- An MCP client that can start a stdio server (Cursor, Claude Desktop, Claude Code, VS Code, Codex CLI, ...).
- Your own ROM files. No ROMs are included.

#### 2. Download

1. Open the [latest release](https://github.com/Azarem/mesen2-mcp/releases/latest) (all versions: [Releases](https://github.com/Azarem/mesen2-mcp/releases)).
2. Download the zip for your platform, for example `mesen2-mcp-win-x64.zip`, `mesen2-mcp-linux-x64.zip`, `mesen2-mcp-osx-arm64.zip`.
3. Extract it to a **permanent** folder (your MCP client will point at it), not to Downloads or a temp folder:

| OS | Suggested folder |
|---|---|
| Windows | `C:\Tools\mesen2-mcp` |
| Linux | `~/.local/share/mesen2-mcp` |
| macOS | `~/Library/Application Support/mesen2-mcp` or `~/mesen2-mcp` |

The folder is the .NET `Release` output: `MesenMcpHost.dll` (the server), `Mesen.dll` and the native core, the Avalonia/Skia libraries, and `lua/lib/` (the Lua kit and recipes). You start the server with `dotnet MesenMcpHost.dll`; there is nothing to install and no installer to run.

Per-OS steps after extracting:

- **Windows:** if SmartScreen or "blocked file" warnings appear, unblock the zip before extracting (right click, Properties, Unblock) or run `Get-ChildItem C:\Tools\mesen2-mcp -Recurse | Unblock-File` in PowerShell.
- **macOS:** remove the download quarantine flag: `xattr -dr com.apple.quarantine ~/mesen2-mcp`.
- **Linux:** nothing extra. If the native libraries fail to load, install `libfontconfig1` (needed by SkiaSharp).

#### 3. Check that it starts

The server speaks JSON-RPC on stdin/stdout, so it simply waits for a client. This sends one `initialize` request and should print a JSON line containing `"serverInfo"`:

```
# Linux / macOS (bash, zsh)
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"check","version":"1"}}}' | dotnet ~/mesen2-mcp/MesenMcpHost.dll

# Windows (PowerShell)
'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"check","version":"1"}}}' | dotnet C:\Tools\mesen2-mcp\MesenMcpHost.dll
```

On first start the server unpacks the remaining native files into the Mesen home folder (`Documents\Mesen2` on Windows, `~/.config/Mesen2` on Linux, `~/Library/Application Support/Mesen2` on macOS). A ROM is not needed to start. Load one with the `load_rom` tool, or pass `--mcp-rom=<file>` in the client configuration.

#### 4. Register the server with your MCP client

Every client needs the same three things: the command `dotnet`, the **absolute** path to `MesenMcpHost.dll` as the first argument, and optional [startup flags](#startup-flags). Use forward slashes in JSON on Windows. Restart the client (or reload its MCP servers) after editing a config file.

**Cursor**: edit `~/.cursor/mcp.json` (all projects) or `<project>/.cursor/mcp.json` (one project), or use Settings, Tools & MCP, Add. Toggle the server on and look for the tool list.

```json
{
  "mcpServers": {
    "mesen2-mcp": {
      "command": "dotnet",
      "args": ["C:/Tools/mesen2-mcp/MesenMcpHost.dll"]
    }
  }
}
```

**Claude Desktop**: Settings, Developer, Edit Config opens `claude_desktop_config.json` (Windows `%APPDATA%\Claude\`, macOS `~/Library/Application Support/Claude/`, Linux `~/.config/Claude/` where Claude Desktop is available). Same `mcpServers` block as above. Fully quit and restart Claude Desktop.

**Claude Code** (CLI):

```
claude mcp add mesen2-mcp -- dotnet /home/me/.local/share/mesen2-mcp/MesenMcpHost.dll
claude mcp add --scope project mesen2-mcp -- dotnet ./tools/mesen2-mcp/MesenMcpHost.dll
```

The second form stores the entry in the project's `.mcp.json` so the whole team shares it. Check it with `claude mcp list`.

**VS Code** (GitHub Copilot agent mode): `.vscode/mcp.json` in the workspace, or the command palette entry "MCP: Add Server". VS Code uses `servers`, not `mcpServers`:

```json
{
  "servers": {
    "mesen2-mcp": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["C:/Tools/mesen2-mcp/MesenMcpHost.dll"]
    }
  }
}
```

**Codex CLI**: add to `~/.codex/config.toml`:

```toml
[mcp_servers.mesen2-mcp]
command = "dotnet"
args = ["/home/me/.local/share/mesen2-mcp/MesenMcpHost.dll"]
```

**Any other stdio MCP client**: run `dotnet /path/to/MesenMcpHost.dll [flags]` as the server command, with no extra environment.

Optional settings for any of them: `"env": {"MESEN_MCP_DEBUG": "1"}` writes the host's debug log to stderr (clients show it in their MCP log), and flags such as `--mcp-rom=` and `--mcp-results-dir=` go after the DLL path in `args`. A second entry with its own `--mcp-rom=` gives you two ROMs in two servers (the `slot` argument does the same inside one server):

```json
{
  "mcpServers": {
    "mesen2-mcp": {
      "command": "dotnet",
      "args": ["C:/Tools/mesen2-mcp/MesenMcpHost.dll"],
      "env": { "MESEN_MCP_DEBUG": "1" }
    },
    "mesen2-mcp-rebuilt": {
      "command": "dotnet",
      "args": [
        "C:/Tools/mesen2-mcp/MesenMcpHost.dll",
        "--mcp-rom=C:/Work/gaia-iog-baserom/rebuilt/Illusion of Gaia - Rebuilt.smc"
      ]
    }
  }
}
```

If the client cannot find `dotnet` (common for macOS and Linux desktop apps, which do not inherit your shell `PATH`), put the full path in `command`: the output of `which dotnet`, for example `/usr/local/share/dotnet/dotnet`, `/opt/homebrew/bin/dotnet` or `/usr/bin/dotnet`.

#### 5. First session

Ask the client to call `load_rom` with the absolute path of a ROM, then `get_rom_info`, `advance_frame` and `take_screenshot`. When those work, the rest of the tools (see [Tool Reference](#tool-reference)) are available. The built-in `self_test` tool checks the scripting and emulation features on your machine.

#### Updating and uninstalling

Stop the server in your client (or quit the client) so the DLLs are not locked, extract the new zip over the folder (delete the old folder first to drop removed files), and restart the client. To uninstall, delete the folder, remove the entry from the client configuration, and optionally delete the Mesen home folder named above.

#### Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Client shows the server as failed or "spawn dotnet ENOENT" | `dotnet` is not on the client's `PATH`. Use the full path to `dotnet` in `command`. |
| "You must install or update .NET" | The .NET 8 runtime is missing. A newer runtime alone is not enough unless roll-forward is enabled: install the 8.x runtime or set `DOTNET_ROLL_FORWARD=Major` in `env`. |
| `DllNotFoundException` for `MesenCore` | The native core is missing or from another platform. Re-extract the zip for your OS and CPU; do not mix files from different releases. |
| Server starts but tools time out | The ROM is large or the machine slow: pass `timeoutMs` to the stepping tool, or use `turbo`. |
| Build or update fails with "file is being used by another process" | The client still runs the old host. Stop the server first. |
| Garbage or JSON errors on the client | Something wrote to stdout. Do not wrap the host in a script that prints; run `dotnet MesenMcpHost.dll` directly. |
| Need more detail | Set `MESEN_MCP_DEBUG=1` and read the client's MCP log, or call `get_capabilities` and `self_test`. |

#### Releasing (maintainers)

The version lives in one place, the `VERSION` file at the repo root (it becomes the host's assembly version and the `serverVersion` the tools report). The GitHub workflow `.github/workflows/build.yml` builds and smoke-tests the host on Windows, Linux (x64, arm64) and macOS (x64, arm64) for every push to `master` and every pull request. When `VERSION` holds a version that has no release yet, it also creates the tag `v<VERSION>` and a GitHub release with one zip per platform (`mesen2-mcp-<rid>.zip`). To publish: edit `VERSION`, commit, push to `master`.

#### Building from source

```
git clone https://github.com/Azarem/mesen2-mcp.git
cd mesen2-mcp
dotnet build McpHost/McpHost.csproj -c Release
```

The output is `McpHost/bin/Release/net8.0/`, the same folder the release zip contains. The alternate entry point is `Mesen --mcp-server` (the UI executable). Rebuilding fails while a client has the host running (the DLLs are locked); stop the server first. For the full emulator build (native core, UI) see [COMPILING.md](COMPILING.md).

### Startup flags

| Flag | Meaning |
|---|---|
| `--mcp-rom=<file>` | Load this ROM at startup. |
| `--mcp-savestatepath=<file>` | After the ROM, load this state file. It must exist; a sidecar (`<file>.json`) naming another ROM is refused and reported as `startupWarning` in `get_rom_info`. |
| `--mcp-savestateslot=<1-10>` | Load the ROM's numbered save-state slot instead (exclusive with `--mcp-savestatepath`). |
| `--mcp-results-dir=<dir>` | Sandbox for result files, screenshots, traces and every relative output path. Default `<Mesen home>/McpResults`. |
| `--mcp-lib-dir=<dir>` | Folder with the Lua kit modules and recipes. Default `lua/lib` next to the host, else `tools/lua/lib`. |
| `--mcp-video`, `--mcp-audio`, `--mcp-input` | Enable video output, audio output, live input devices. All are off by default (headless, fastest). Screenshots do not need `--mcp-video`. |

### Conventions

- **Transport.** stdout carries only JSON-RPC; native logging goes to stderr. Tool calls run one at a time. An argument that is not in a tool's schema is rejected with an error, not ignored.
- **Results.** A tool returns one JSON text block (`take_screenshot` also returns a PNG image block). A few legacy tools (`read_memory` with `format: "hex"`, for example) return plain text.
- **Errors.** Expected failures return `isError: true` with `{"error": {"code": "...", "message": "...", "details": {...}}}` (for example `rom_mismatch`, `invalid_condition`, `no_trace`). Unexpected failures return `Error: <message>` text.
- **Stepping tools** (`advance_frame`, `step_*`, `run_script`, `run_until`, `play_input`, `trace_between`, ...) block until done, a breakpoint hits or the timeout expires and return `{stopReason, framesAdvanced, instructionsExecuted, frame, elapsedMs, framesPerSecond, cpu, break?}`. `stopReason` is `done`, `breakpoint`, `break` (BRK/STP exception), `script_done`, `condition` or `timeout`. `cpu` on SNES is `{k, pc, pc24, a, x, y, sp, d, dbr, ps, psFlags, emulationMode}`.
- **Turbo.** `turbo: true` removes the frame limiter for the call. It defaults to on when the server runs without video and audio (about 200-370 frames per second on a desktop).
- **Addresses.** Tools that take a CPU address accept an integer or a `"BB:AAAA"` string. Memory-type names (`snesMemory`, `snesPrgRom`, `snesWorkRam`, `snesCgRam`, `snesVideoRam`, `snesSpriteRam`, `spcRam`, ...) are listed by `list_memory_types`; the short names `cgram`, `vram`, `spcram`, `dsp`, `palette` also work.
- **Files.** Relative output paths (`dump_memory`, `take_screenshot`, `start_trace`, `save_cdl`, `record_input`, ...) go to the results folder. Scripts write there with `emu.mcp.writeFile(name, bytes, append?)`; names with `..`, absolute paths or drive letters are refused.
- **States.** `save_state_file` writes a sidecar `<path>.json` with the ROM SHA-1; `load_state_file` and `load_rom` refuse a state saved for another ROM (`rom_mismatch`) unless `requireRomMatch=false`.
- **Slots (two ROMs).** Every tool accepts an optional `slot` string. `load_rom {path, slot}` registers the ROM under that name; any other tool called with `slot` first switches to that ROM (the server saves the state and labels of the slot it leaves, and restores the other). `list_slots`, `switch_slot` and `compare_runs` build on this.
- **Events.** The server sends `notifications/mesen/event` notifications. By default: `breakpoint_hit`, `game_loaded`, `game_paused`, `game_resumed`, `emulation_stopped`. `subscribe_events` selects the set and adds `state_loaded`, `frame_end`, `script_log` and `error`.

### Breakpoints and conditions

`set_breakpoints` takes a list of `{callbackType (read|write|exec, comma list), startAddress, endAddress, memType, condition, action, maxLogged, name}`.

- `action: "break"` (default) is a native breakpoint that halts emulation. Its `condition` is checked with the native expression evaluator (a bad one fails with `invalid_condition`).
- `action: "count"` and `"log"` never halt. They run as a hidden Lua script, so their conditions use this subset of the Mesen syntax: `value`, `oldvalue`, `address`, `pc`, `a`, `x`, `y`, `sp`, `d`, `k`, `dbr`, `ps`, `frame`, `[addr]`, `{addr}` and `&& || ! != ^ /`. Read the counts and logged rows with `get_breakpoint_hits`.

### Lua scripting

`run_script {code, frames, includes?}` pauses, loads the script, advances, collects the log and the result, and unloads it. `load_script` keeps a script running across calls. The server prepends a one-line prelude (user line numbers do not shift) that adds:

- `emu.mcp.result(table)`: store a JSON result (about 90 KB limit; read it with `get_script_result`).
- `emu.mcp.done()`: stop the run early (`stopReason: "script_done"`).
- `emu.mcp.writeFile(name, bytes, append?)`: write into the results folder; returns `nil, err` if refused.

`io` and `os` are not available to scripts. The native log keeps 500 rows; the server harvests it between frame chunks into a 5000-row ring (`get_script_log`), but a script that logs more than 500 rows in one frame loses rows. `emu.createSavestate` works only inside an exec memory callback.

`includes: ["mesen_kit", "iog_kit"]` loads modules from the lib folder:

- `mesen_kit`: `kit.r8/r16/w8/w16`, `kit.setBit/getBit`, `kit.frame`, `kit.onFrame`, `kit.machine`, `kit.pulse/stopPulse`, `kit.topCounts`, `kit.finish(result)`.
- `iog_kit` (Illusion of Gaia): `iog.warp{scene, x, y, facing, scroll, flags, ...}`, `iog.setEventFlag/getEventFlag`, `iog.state()`.

`list_recipes` and `run_recipe {name, args?, frames?}` run ready-made scripts: `warp`, `census_pc`, `exec_coverage`. In a recipe the Lua table `args` holds the arguments.

### Typical workflows

```
load_rom {path}                          -> fileSha1, header
run_recipe {name: "warp", args: {scene: 10, flags: [63]}}
take_screenshot {label: "edward_castle", crop: {x: 0, y: 0, w: 256, h: 224}}
set_breakpoints {breakpoints: [{callbackType: "write", startAddress: 0x7E0642, endAddress: 0x7E0643, action: "log", name: "sceneNext"}]}
advance_frame {frames: 600, turbo: true}
get_breakpoint_hits {}                   -> counts and rows (frame, value, pc, registers)
snapshot_memory {name: "before"} ... diff_snapshots {a: "before", b: "current"}
record_input {action: "start", path: "run1.json"} ... record_input {action: "stop"}
load_rom {path: rebuilt, slot: "rebuilt"} ; compare_runs {inputFile: "run1.json", watch: [{name: "scene", address: 0x7E0642}]}
```

### Tests and tooling

All tests run against a real ROM and take `--host <MesenMcpHost.dll>` (use the Debug build while a client holds the Release DLLs):

| Command | Covers |
|---|---|
| `python tools/mcp_p0_test.py` | Original tool set, ROM identity, bound states, stepping, CDL, script I/O |
| `python tools/mcp_week2_test.py` | Labels, turbo and `run_until`, result files, Lua kit and recipes |
| `python tools/mcp_week3_test.py` | Breakpoints, memory, trace, screenshots, PPU state, input, events, slots, `self_test` |
| `python tools/mcp_iog_e2e_test.py` | End to end: Illusion of Gaia warped into Edward's Castle |
| `python tools/gen_api_docs.py` | Regenerates the tool reference below from the live `tools/list` |

The `self_test` tool checks the Lua and emulator features scripts rely on (memory callbacks, `emu.setInput`, Lua save states, log harvest, stepping, state files, breakpoints, CDL, sandbox, screen buffer) and restores the emulator state afterwards.

More detail: [MCP_TOOLS_CONTRACT.md](MCP_TOOLS_CONTRACT.md) (behaviour of each extension, by feature id), [MCP_MIGRATION.md](MCP_MIGRATION.md) (legacy `mesen-mcp` compatibility).

## Tool Reference

<!-- MCP-API:BEGIN -->
_80 tools, generated from the server's `tools/list` by `tools/gen_api_docs.py`. Every tool also accepts the optional `slot` argument (see Conventions)._

| Category | Tools |
|---|---|
| [Session, ROM and slots](#session-rom-and-slots) | [`load_rom`](#load_rom), [`get_rom_info`](#get_rom_info), [`stop`](#stop), [`reset`](#reset), [`pause`](#pause), [`resume`](#resume), [`get_timing_info`](#get_timing_info), [`revert_rom_changes`](#revert_rom_changes), [`list_slots`](#list_slots), [`switch_slot`](#switch_slot), [`compare_runs`](#compare_runs) |
| [Execution and stepping](#execution-and-stepping) | [`advance_frame`](#advance_frame), [`step_instruction`](#step_instruction), [`step_scanline`](#step_scanline), [`step_cpu_cycles`](#step_cpu_cycles), [`rewind`](#rewind), [`run_until`](#run_until) |
| [Memory](#memory) | [`read_memory`](#read_memory), [`write_memory`](#write_memory), [`read_word`](#read_word), [`write_word`](#write_word), [`list_memory_types`](#list_memory_types), [`dump_memory`](#dump_memory), [`snapshot_memory`](#snapshot_memory), [`diff_snapshots`](#diff_snapshots), [`list_snapshots`](#list_snapshots), [`delete_snapshot`](#delete_snapshot), [`get_prg_rom_offset`](#get_prg_rom_offset), [`get_chr_rom_offset`](#get_chr_rom_offset), [`get_access_summary`](#get_access_summary), [`reset_access_counters`](#reset_access_counters) |
| [Labels and addresses](#labels-and-addresses) | [`load_labels`](#load_labels), [`list_labels`](#list_labels), [`resolve_address`](#resolve_address), [`resolve_addresses`](#resolve_addresses), [`get_label_address`](#get_label_address) |
| [Debugger](#debugger) | [`get_emulator_state`](#get_emulator_state), [`set_cpu_state`](#set_cpu_state), [`set_breakpoints`](#set_breakpoints), [`clear_breakpoints`](#clear_breakpoints), [`get_breakpoint_hits`](#get_breakpoint_hits), [`get_callstack`](#get_callstack), [`get_disassembly`](#get_disassembly), [`get_execution_trace`](#get_execution_trace), [`start_trace`](#start_trace), [`stop_trace`](#stop_trace), [`trace_between`](#trace_between), [`get_profiler_data`](#get_profiler_data) |
| [Code/data log (CDL)](#codedata-log-cdl) | [`get_cdl_data`](#get_cdl_data), [`get_cdl_statistics`](#get_cdl_statistics), [`reset_cdl`](#reset_cdl), [`save_cdl`](#save_cdl), [`load_cdl`](#load_cdl) |
| [Video and PPU](#video-and-ppu) | [`take_screenshot`](#take_screenshot), [`get_ppu_state`](#get_ppu_state), [`get_dma_state`](#get_dma_state), [`get_oam`](#get_oam), [`get_tilemap_info`](#get_tilemap_info), [`get_tilemap_size`](#get_tilemap_size), [`get_palette_info`](#get_palette_info) |
| [Input](#input) | [`get_input`](#get_input), [`set_input`](#set_input), [`clear_input`](#clear_input), [`play_input`](#play_input), [`record_input`](#record_input), [`play_input_file`](#play_input_file) |
| [Save states](#save-states) | [`save_state_file`](#save_state_file), [`load_state_file`](#load_state_file), [`list_states`](#list_states) |
| [Lua scripting and recipes](#lua-scripting-and-recipes) | [`load_script`](#load_script), [`run_script`](#run_script), [`get_script_log`](#get_script_log), [`get_script_result`](#get_script_result), [`list_scripts`](#list_scripts), [`unload_script`](#unload_script), [`list_recipes`](#list_recipes), [`run_recipe`](#run_recipe) |
| [Server and events](#server-and-events) | [`get_capabilities`](#get_capabilities), [`self_test`](#self_test), [`subscribe_events`](#subscribe_events) |

### Session, ROM and slots

#### `load_rom`

Load a ROM file and optionally restore a save state path or slot. Returns the ROM identity (size, SHA-1, CRC32, header). A state that has a sidecar from another ROM is rejected (code rom_mismatch) unless requireRomMatch is false.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Absolute ROM path |
| `saveStatePath` | string |  | Optional save-state file path |
| `saveStateSlot` | integer |  | Optional save-state slot 1-10 |
| `requireRomMatch` | boolean |  | Reject a save state whose sidecar names a different ROM (default true). A state without a sidecar is always loaded here. |

#### `get_rom_info`

Get info about the loaded ROM: path, file size, SHA-1, CRC32, SNES header (map mode, speed, title, checksum), PRG size and console timing.

_No parameters._

#### `stop`

Stop emulation and exit MCP server.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `exitCode` | integer |  | Exit code |

#### `reset`

Soft reset emulation.

_No parameters._

#### `pause`

Pause emulation.

_No parameters._

#### `resume`

Resume emulation.

_No parameters._

#### `get_timing_info`

Get frame, scanline and cycle timing info.

_No parameters._

#### `revert_rom_changes`

Undo memory edits when undo history is available.

_No parameters._

#### `list_slots`

List the ROM slots of this session (see load_rom slot, switch_slot).

_No parameters._

#### `switch_slot`

Make a slot active: the active slot's state and labels are saved, the slot's ROM is loaded and its saved state restored (paused). Every tool also accepts a slot argument that does this first. Lua scripts and Lua-based breakpoints do not survive a switch.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Slot name |

#### `compare_runs`

Play the same recorded input on two slots and report the first divergence of the watched values. watch: [{type?: memory type (default workRam), address, size?: 1|2|4, name?}]. startStates optionally maps slot name to a state file to load first. Values are sampled after every frame.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `inputFile` | string | yes | Recording from record_input |
| `watch` | array | yes | Values to compare |
| `slots` | array<string> |  | Two slot names (default: the first two slots) |
| `startStates` | object |  | Slot name to state file |
| `frames` | integer |  | Limit the compared frames (default: whole recording) |
| `maxDivergences` | integer |  | Maximum divergences listed (default 20) |

### Execution and stepping

#### `advance_frame`

Advance by one or more frames (default 1, max 200000). Blocks until done, a breakpoint hits, or timeoutMs elapses. Returns stopReason (done|breakpoint|break|script_done|timeout), framesAdvanced, frame, elapsedMs, framesPerSecond and the CPU registers. turbo removes the frame limiter (default: on when the server runs without video and audio).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `frames` | integer |  | Number of frames to advance (1-200000, default 1) |
| `timeoutMs` | integer |  | Give up after this many milliseconds (default: 30000 + frames*20 with turbo, max(10000, frames*500) without) |
| `turbo` | boolean |  | Run without the frame limiter |

#### `step_instruction`

Step CPU instructions synchronously. Returns stopReason, instructionsExecuted, frame and the CPU registers. For count <= 32 each executed instruction's address is listed in pcs.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `count` | integer |  | Instruction count (default 1) |
| `over` | boolean |  | Step over calls (default false) |
| `timeoutMs` | integer |  | Give up after this many milliseconds (default 10000) |

#### `step_scanline`

Step PPU scanlines synchronously. Returns stopReason, frame and the CPU registers.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `count` | integer |  | Scanline count (default 1) |
| `timeoutMs` | integer |  | Give up after this many milliseconds (default 10000) |

#### `step_cpu_cycles`

Step CPU cycles synchronously. Returns stopReason, frame and the CPU registers.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `count` | integer |  | CPU cycle count (default 1) |
| `timeoutMs` | integer |  | Give up after this many milliseconds (default 10000) |

#### `rewind`

Rewind emulation by seconds.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `seconds` | integer | yes | Seconds to rewind |

#### `run_until`

Run until a condition holds, a breakpoint hits, maxFrames pass or timeoutMs elapses. condition is one of: {type:"memoryEquals", memType?, address, size?(1-4, little endian), value, mask?} checked at the end of every frame (memType default workRam); {type:"pcEquals", pc} (number or "BB:AAAA") using a temporary exec breakpoint; {type:"lua", expr, includes?} a Lua expression evaluated at the end of every frame (kit helpers available when included); {type:"breakpoint"} runs until a configured breakpoint hits. Returns stopReason (condition|breakpoint|done=maxFrames reached|timeout|error), met, framesAdvanced, frame, framesPerSecond and the CPU registers.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `condition` | object | yes | Stop condition, see description |
| `maxFrames` | integer |  | Give up after this many frames (default 600) |
| `timeoutMs` | integer |  | Give up after this many milliseconds |
| `turbo` | boolean |  | Run without the frame limiter |

### Memory

#### `read_memory`

Read bytes from memory (at most 65536 per call). format: hex (default hex dump), bytes (JSON array), words_le (JSON array of 16-bit little-endian words) or base64.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `address` | integer | yes | Start address |
| `length` | integer | yes | Byte length |
| `type` | string |  | Memory type name (see list_memory_types), for example snesCgRam, spcRam, snesVideoRam |
| `format` | string |  | hex \| bytes \| words_le \| base64 |

#### `write_memory`

Write bytes to memory.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `address` | integer | yes | Start address |
| `data` | array<integer> | yes | Byte array |
| `type` | string |  | Memory type |

#### `read_word`

Read a 16-bit little-endian word.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `address` | integer | yes | Address |
| `type` | string |  | Memory type |

#### `write_word`

Write a 16-bit little-endian word.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `address` | integer | yes | Address |
| `value` | integer | yes | Value 0-65535 |
| `type` | string |  | Memory type |

#### `list_memory_types`

List every memory type of the loaded console with its size in bytes. The names can be used as the type of read_memory, write_memory, dump_memory, snapshot_memory and others.

_No parameters._

#### `dump_memory`

Write any amount of memory to a file in the results folder (or an absolute path) and return its size and SHA-1.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `type` | string | yes | Memory type name |
| `start` | integer |  | Start address (default 0) |
| `length` | integer |  | Byte length (default: to the end of the memory type) |
| `path` | string | yes | Output file name (relative to the results folder) or absolute path |

#### `snapshot_memory`

Keep a copy of a memory range under a name (at most 16 snapshots, 256 MB total).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Snapshot name |
| `type` | string |  | Memory type (default workRam) |
| `start` | integer |  | Start address (default 0) |
| `length` | integer |  | Length (default: whole memory type) |

#### `diff_snapshots`

Compare two snapshots (b may be "current" to compare against live memory). Returns changed runs [start, end, oldHex, newHex] with absolute addresses.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `a` | string | yes | First snapshot name |
| `b` | string |  | Second snapshot name, or "current" |
| `ranges` | array |  | Only compare these [start,end] address windows (end inclusive) |
| `mergeGap` | integer |  | Merge changed runs closer than this many bytes (default 0) |
| `maxRows` | integer |  | Maximum rows (default 500) |

#### `list_snapshots`

List the stored memory snapshots.

_No parameters._

#### `delete_snapshot`

Delete a memory snapshot (or all with name "*").

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Snapshot name |

#### `get_prg_rom_offset`

Convert CPU address to PRG ROM offset if mapped.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `address` | integer | yes | CPU address |

#### `get_chr_rom_offset`

Convert PPU address to CHR ROM offset if mapped.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `address` | integer | yes | PPU address |

#### `get_access_summary`

Access counters and CDL flags summarised per named range. ranges: [{name, start, end}] (absolute offsets in type, end inclusive), [{name, cpuStart, cpuEnd}] (CPU addresses, converted) or [{name, label, length?}]; or give rangesPath (JSON file with such a list). Returns per range exec/read/write counts, first/last access stamps, the CDL flag union and a verdict (accessed | never_accessed).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `type` | string |  | Memory type of the ranges (default prgRom) |
| `ranges` | array |  | Range list |
| `rangesPath` | string |  | Path of a JSON file holding the range list (array or {ranges:[..]}) |
| `cdl` | boolean |  | Include the CDL flag union (default true for ROM types) |
| `nonzeroOnly` | boolean |  | Only return ranges with at least one access |

#### `reset_access_counters`

Reset the native memory access counters (exec/read/write).

_No parameters._

### Labels and addresses

#### `load_labels`

Import labels from an .mlb (Mesen label file), .sym (BB:AAAA name per line) or .json file ([{name, cpu|prgOffset|offset|wram, length?, comment?}] or {labels:[...]}). Imported labels appear in disassembly, call stacks, resolve_address and get_label_address. Names that are not valid label identifiers are sanitized.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Label file path |
| `format` | string |  | mlb \| sym \| json (default: from the file extension) |
| `replace` | boolean |  | Remove all existing labels first (default false) |

#### `list_labels`

List loaded labels (optionally filtered by a case-insensitive substring of the name).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `filter` | string |  | Name substring |
| `max` | integer |  | Maximum rows (default 200, max 5000) |

#### `resolve_address`

Convert between CPU addresses, PRG ROM offsets, WRAM offsets and labels. Give one of cpu (number or "BB:AAAA"), prgOffset (alias offset), wram, or label. Returns {cpu, prgOffset, wram, label, labelOffset, exact, bankAlias}: label is the label covering the address, or else the nearest preceding label (labelOffset = distance in bytes).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `cpu` | any |  | CPU address: number or "BB:AAAA" string |
| `prgOffset` | integer |  | PRG ROM file offset (without copier header) |
| `offset` | integer |  | Alias of prgOffset |
| `wram` | integer |  | Work RAM offset |
| `label` | string |  | Label name |

#### `resolve_addresses`

Bulk resolve_address. addresses is a list of CPU addresses (numbers or "BB:AAAA" strings) or objects {cpu|prgOffset|wram|label}.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `addresses` | array | yes | Addresses to resolve |

#### `get_label_address`

Resolve debugger label to address.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `label` | string | yes | Label name |

### Debugger

#### `get_emulator_state`

Get CPU/PPU/timing state snapshot.

_No parameters._

#### `set_cpu_state`

Patch CPU and/or PPU registers.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `state` | object | yes | Partial cpu/ppu state object |

#### `set_breakpoints`

Replace the active breakpoints. Each item: {callbackType: read|write|exec (or kinds:[..]), startAddress, endAddress?, memType?, condition?, action?, maxLogged?, name?}. action=break (default) halts emulation; the condition is a native Mesen debugger expression (for example "value == 0x55" or "(value & 0x0200) != 0"). action=count and action=log never halt: hits are counted (and for log recorded with the registers, up to maxLogged, default 1000) by a hidden Lua callback; for these the condition is translated to Lua and supports value, oldvalue, address, pc (24-bit on SNES), a, x, y, sp, d, k, dbr, ps, frame, [addr] byte and {addr} word reads, && || ! != == < > <= >= & | ^ << >> + - * / %. Use get_breakpoint_hits to read counts and logged hits.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `breakpoints` | array | yes | Breakpoint list |

#### `clear_breakpoints`

Clear all breakpoints.

_No parameters._

#### `get_breakpoint_hits`

Counts and logged hits of count/log breakpoints (set_breakpoints). Returns counts per breakpoint id and hit rows {seq, id, frame, kind, address, value, pc, a, x, y, sp, k}.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `id` | integer |  | Only this breakpoint id |
| `sinceSeq` | integer |  | Only rows with seq greater than this |
| `max` | integer |  | Maximum rows (default 500) |
| `reset` | boolean |  | Reset the counters and the hit rows after reading |

#### `get_callstack`

Get the current call stack, with label names when labels are loaded.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `cpuType` | string |  | Optional CPU type |

#### `get_disassembly`

Get disassembly rows from an address.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `address` | integer |  | Start address (defaults to current PC) |
| `rowCount` | integer |  | Number of rows |
| `cpuType` | string |  | Optional CPU type |

#### `get_execution_trace`

Get execution trace rows, with the label of each program counter when labels are loaded.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `startOffset` | integer |  | Trace start offset |
| `maxRowCount` | integer |  | Max rows |

#### `start_trace`

Start writing an execution trace to a file (results folder or absolute path). Optional pcRanges [[start,end]..] (CPU addresses) and condition (native expression) limit what is logged; maxLines caps the file when the trace is stopped. The default format has the frame count, the 24-bit PC, the disassembly and the registers.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Output file |
| `pcRanges` | array |  | [[start,end],..] CPU address windows (numbers or "BB:AAAA") |
| `condition` | string |  | Native condition expression |
| `format` | string |  | Native trace format string (default: frame, PC, disassembly, registers) |
| `maxLines` | integer |  | Truncate the file to this many lines when the trace stops |
| `useLabels` | boolean |  | Use labels in the disassembly |

#### `stop_trace`

Stop the trace file started by start_trace and return its size, line count and SHA-1.

_No parameters._

#### `trace_between`

Triggered trace: run until startPc executes, trace until stopPc executes (or maxFrames), then stop. Uses temporary exec breakpoints; the trace file contains the instructions from startPc up to, not including, the instruction at stopPc.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `startPc` | any | yes | CPU address (number or "BB:AAAA") that starts the trace |
| `stopPc` | any | yes | CPU address (number or "BB:AAAA") that ends the trace |
| `path` | string | yes | Output file |
| `maxFrames` | integer |  | Frame budget for each of the two phases (default 600) |
| `condition` | string |  | Native condition expression |
| `maxLines` | integer |  | Truncate the file to this many lines |
| `turbo` | boolean |  | Run without the frame limiter |

#### `get_profiler_data`

Get profiler function data.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `cpuType` | string |  | Optional CPU type |

### Code/data log (CDL)

#### `get_cdl_data`

Read code/data logger flags. format=summary returns counts; ranges returns merged [start,end,flags] runs (flags are masked by flagMask, default 0x03 = code|data; unreached bytes are skipped unless includeUnreached); bytes_base64 returns raw flag bytes (max 65536).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `type` | string |  | Memory type (default prgRom) |
| `memoryType` | string |  | Alias of type |
| `start` | integer |  | Start offset (default 0) |
| `offset` | integer |  | Alias of start |
| `length` | integer |  | Byte count (default: rest of memory; bytes_base64 is capped at 65536) |
| `format` | string |  | summary \| ranges \| bytes_base64 (default summary) |
| `flagMask` | integer |  | Mask applied to flags for ranges (default 3) |
| `includeUnreached` | boolean |  | Include runs whose masked flags are 0 (default false) |
| `maxRows` | integer |  | Maximum ranges returned (default 20000); use nextStart to continue |

#### `get_cdl_statistics`

Get code/data logger statistics.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `memoryType` | string |  | Optional memory type |

#### `reset_cdl`

Clear the code/data log.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `memoryType` | string |  | Memory type (default prgRom) |

#### `save_cdl`

Save the code/data log to a file (Mesen CDL format, includes the ROM CRC).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Output file path |
| `memoryType` | string |  | Memory type (default prgRom) |

#### `load_cdl`

Load a code/data log file. merge=true unions the code/data bits of the file with the current log instead of replacing it.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | CDL file path |
| `memoryType` | string |  | Memory type (default prgRom) |
| `merge` | boolean |  | Union with the current log (default false = replace) |

### Video and PPU

#### `take_screenshot`

Capture the current frame in-process (no screenshot folder involved). Returns JSON {frame, width, height, path?, sha1} and the PNG image. crop {x,y,w,h} selects a region of the rendered frame, scale resizes (nearest neighbour), path/label write the PNG (label gives <results>/screenshots/<label>_<frame>.png). layers (SNES: bg1 bg2 bg3 bg4 obj) shows only those layers: the server saves the state, renders the next frame with the other layers hidden, and restores the state (the image is then one frame later; renderedFrame says which).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `crop` | object |  | Region {x, y, w, h} in rendered-frame pixels |
| `scale` | number |  | Scale factor 0.25-8 (default 1) |
| `path` | string |  | Write the PNG here (relative paths go to the results folder) |
| `label` | string |  | Caller label used for a deterministic file name |
| `layers` | array<string> |  | Layers to show, for example ["obj"] |
| `returnImage` | boolean |  | Return the image in the response (default true) |

#### `get_ppu_state`

Structured PPU state. On SNES: every register field plus derived values (enabled layers on main/sub screen, colour math, windows, Mode 7 matrix, OAM settings). Other consoles: the raw PPU state.

_No parameters._

#### `get_dma_state`

SNES DMA/HDMA channels 0-7: control flags, B-bus register, A-bus address, size or indirect address, HDMA table address, line counter and enable flags, plus hdmaEnableMask.

_No parameters._

#### `get_oam`

Decoded sprite memory (SNES): 128 sprites with x, y, tile, name table, palette, priority, flips, size and on-screen flag. onlyOnScreen filters; first/count page the list.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `decoded` | boolean |  | Return decoded sprites (default true); false returns the raw 544 bytes as hex |
| `onlyOnScreen` | boolean |  | Only sprites that overlap the screen |
| `first` | integer |  | First sprite index |
| `count` | integer |  | Number of sprites (default 128) |

#### `get_tilemap_info`

Per-background tilemap info (SNES): tilemap and character base addresses, scroll, tilemap size, large tiles, plus the current BG mode.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `cpuType` | string |  | Optional CPU type |

#### `get_tilemap_size`

Get tilemap display size for a layer.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `layer` | integer |  | Layer index |
| `cpuType` | string |  | Optional CPU type |

#### `get_palette_info`

Get palette metadata and colours. With first/count/format the colours are returned as rows {index, rgb888, rgb555?}: rgb555 is the raw CGRAM word on SNES.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `cpuType` | string |  | Optional CPU type |
| `first` | integer |  | First palette index |
| `count` | integer |  | Number of colours (default: all) |
| `format` | string |  | rgb888 \| rgb555 (selects the row format) |

### Input

#### `get_input`

Get current override state for a controller port.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `port` | integer |  | Port index 0-7 |

#### `set_input`

Set the input override of a controller port. input is a button map ({"right": true}) merged into the current override; buttons (list of names) or mask (bits: a=1 b=2 x=4 y=8 l=16 r=32 up=64 down=128 left=256 right=512 select=1024 start=2048) replace it. With frames the buttons are held for exactly that many frames (the call advances them) and then released.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `port` | integer |  | Port index 0-7 |
| `input` | object |  | Button map to bool/number |
| `buttons` | array<string> |  | Button names to hold (replaces the override) |
| `mask` | integer |  | Button bit mask (replaces the override) |
| `frames` | integer |  | Hold for this many frames, then release |
| `turbo` | boolean |  | Run without the frame limiter |

#### `clear_input`

Clear input override for one or all ports.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `port` | integer |  | Optional port |

#### `play_input`

Play a frame-exact input sequence: sequence is [{frames, buttons:[..], port?, ports?:{"0":[..],"1":[..]}}]. Each segment sets the buttons (an empty list releases) and advances exactly `frames` frames. Stops early at a breakpoint. Buttons are released afterwards (release=false keeps the last segment).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `sequence` | array | yes | Segments [{frames, buttons}] |
| `port` | integer |  | Default port for segments (default 0) |
| `turbo` | boolean |  | Run without the frame limiter |
| `release` | boolean |  | Release the buttons afterwards (default true) |
| `thenPause` | boolean |  | Leave the emulator paused afterwards (default true) |

#### `record_input`

Record the input applied by set_input/play_input while frames advance. action=start begins (startState optionally names the state the recording starts from; its SHA-1 goes into the header); action=stop writes the file with the ROM SHA-1.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | yes | start \| stop |
| `path` | string |  | Output file (results folder or absolute); required for start |
| `startState` | string |  | Optional state file the recording starts from |

#### `play_input_file`

Replay a recording made by record_input. The ROM SHA-1 in the header must match the loaded ROM (requireRomMatch). loadStartState loads the recorded start state first (its SHA-1 is verified).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Recording file |
| `requireRomMatch` | boolean |  | Verify the ROM SHA-1 (default true) |
| `loadStartState` | boolean |  | Load the start state named in the header first (default false) |
| `turbo` | boolean |  | Run without the frame limiter |
| `release` | boolean |  | Release the buttons afterwards (default true) |

### Save states

#### `save_state_file`

Save emulator state to a file and write a sidecar <path>.json with the ROM SHA-1, ROM path, frame count, server version, note and caller metadata.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Save-state file path |
| `note` | string |  | Optional free-form note stored in the sidecar |
| `meta` | object |  | Optional free-form object stored in the sidecar (for example the scene id) |

#### `load_state_file`

Load an emulator state file. By default the sidecar written by save_state_file must name the loaded ROM's SHA-1; otherwise the call fails with code rom_mismatch (or no_sidecar) and nothing is loaded.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Save-state file path |
| `requireRomMatch` | boolean |  | Verify the sidecar ROM SHA-1 before loading (default true). false loads any state, as before. |

#### `list_states`

List save states in a folder with their sidecar fields and a valid flag (true when the sidecar ROM SHA-1 equals the loaded ROM).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `dir` | string | yes | Folder to scan for *.mss files |
| `recursive` | boolean |  | Scan sub-folders (default false) |

### Lua scripting and recipes

#### `load_script`

Load and execute a Lua script. Returns a stable scriptId and any log rows produced while loading. The server prepends a one-line prelude that provides emu.mcp.result(table) (JSON result, read it with get_script_result). Reusing a name replaces the script.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `code` | string | yes | Lua source code |
| `name` | string |  | Optional script name |
| `prelude` | boolean |  | Provide emu.mcp.result (default true) |
| `includes` | array<string> |  | Kit modules from the server's Lua lib folder to load before the script, for example ["mesen_kit", "iog_kit"] (see list_recipes for the folder) |

#### `run_script`

Atomic experiment: pause, load a script (with optional kit includes), advance frames, collect log and result, unload the script. Stops early when the script calls emu.mcp.done(). Returns stopReason (done|script_done|breakpoint|timeout), framesAdvanced, the log rows, the emu.mcp.result value and any files written with emu.mcp.writeFile.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `code` | string | yes | Lua source code |
| `name` | string |  | Script name (default run_script) |
| `frames` | integer |  | Frames to advance after loading (0-200000, default 60) |
| `timeoutMs` | integer |  | Give up after this many milliseconds |
| `turbo` | boolean |  | Run without the frame limiter |
| `unloadAfter` | boolean |  | Unload the script afterwards (default true) |
| `maxLog` | integer |  | Maximum log rows returned (default 500) |
| `includes` | array<string> |  | Kit modules to load first, for example ["mesen_kit"] |

#### `get_script_log`

Get script log rows from the server-side ring (5000 rows). Returns {entries:[{seq,frame,script,text}], nextSeq, dropped}. Pass nextSeq back as sinceSeq to read only new rows. frame is the emulator frame when the row was collected (end of the step chunk), not the frame the script logged it.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string |  | Only rows of this script |
| `sinceSeq` | integer |  | Return rows with seq >= sinceSeq (default 0 = everything still in the ring) |
| `max` | integer |  | Maximum rows (default 500, max 5000) |
| `clear` | boolean |  | Empty the ring after reading |

#### `get_script_result`

Get the last emu.mcp.result(table) value of a script (as JSON). Results are limited to about 90 KB.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Script name |

#### `list_scripts`

List the scripts loaded through this server.

_No parameters._

#### `unload_script`

Unload a script by name (removes its callbacks). Its log and result remain readable.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Script name |

#### `list_recipes`

List the Lua kit modules and ready-made recipes shipped with the server (the lib folder), with their arguments.

_No parameters._

#### `run_recipe`

Run a shipped recipe (see list_recipes) like run_script: pause, load kit and recipe, advance frames (stops early when the recipe calls emu.mcp.done()), return log and result, unload.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Recipe name |
| `args` | object |  | Recipe arguments (available to the recipe as the Lua table 'args') |
| `frames` | integer |  | Frames to advance at most (default: the recipe's own default) |
| `timeoutMs` | integer |  | Give up after this many milliseconds |
| `turbo` | boolean |  | Run without the frame limiter |
| `unloadAfter` | boolean |  | Unload the script afterwards (default true) |
| `maxLog` | integer |  | Maximum log rows returned (default 500) |

### Server and events

#### `get_capabilities`

Report the runtime: server and Mesen version, CPU/debug features, tools, the Lua emu.* functions, flags (video/audio/input/scriptIo), limits, results/lib folders and the last self_test result.

_No parameters._

#### `self_test`

Run the capability matrix on the loaded ROM and return pass/fail per feature: memory write/read/exec callbacks, emu.write/read, setInput in inputPolled, Lua savestate round trip, state file round trip, CDL, execution breakpoint, stepping, script log harvest, writeFile sandbox, screen buffer. The emulation state is restored afterwards.

_No parameters._

#### `subscribe_events`

Choose which server events are sent as notifications/mesen/event. Types and payloads: breakpointHit {event:breakpoint_hit, breakpointId, cpuType, address, value, memoryType, operationType, callbackType; count/log breakpoints add action, count, newHits, frame}; romLoaded {event:game_loaded}; stateLoaded {event:state_loaded, path}; frameEnd {event:frame_end, frame} (at most every frameEvery frames, sent when a stepping call returns); scriptLog {event:script_log, nextSeq} (new script log rows); error {event:error, tool, message}; gamePaused, gameResumed, emulationStopped. Default (never subscribed): breakpointHit, romLoaded, gamePaused, gameResumed, emulationStopped. An empty list disables all events.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `types` | array<string> | yes | Event types to send |
| `frameEvery` | integer |  | Minimum frames between frameEnd events (default 60) |

<!-- MCP-API:END -->

## License

Mesen is available under the GPL V3 license.  Full text here: <http://www.gnu.org/licenses/gpl-3.0.en.html>

Copyright (C) 2014-2025 Sour

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
