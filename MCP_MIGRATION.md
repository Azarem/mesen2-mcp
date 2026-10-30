# Mesen2 C# MCP Migration Guide

This repository now includes an in-process C# MCP server host based on the same UI/Interop runtime.

## Install

Download the zip for your platform from the [releases](https://github.com/Azarem/mesen2-mcp/releases/latest), extract it and register `dotnet <folder>/MesenMcpHost.dll` with your MCP client. Step-by-step instructions for Cursor, Claude Desktop, Claude Code, VS Code and Codex CLI are in the [README](README.md#installation).

## Start MCP Server Mode

Preferred (stable stdio host):

- `dotnet McpHost/bin/Release/net8.0/MesenMcpHost.dll`

Alternate (UI executable path):

- `Mesen --mcp-server`
- Optional runtime flags:
  - `--mcp-video` to enable video output (required for screenshots)
  - `--mcp-audio` to enable audio
  - `--mcp-input` to enable live input devices
  - `--mcp-rom=<absolute-path>` to preload a ROM
  - `--mcp-savestatepath=<file>` to load a save-state file after the ROM (must exist; a sidecar naming another ROM is refused and reported as `startupWarning`)
  - `--mcp-savestateslot=<1-10>` to load the ROM's numbered save-state slot instead (exclusive with `--mcp-savestatepath`)
  - `--mcp-results-dir=<dir>` sandbox folder for result files and relative output paths (default `%LOCALAPPDATA%/Mesen2Mcp/results`)
  - `--mcp-lib-dir=<dir>[;<dir>...]` extra folders with Lua kit modules and recipes (repeatable; searched after the server's own `lua/lib` next to the host, else `tools/lua/lib`)

The complete tool reference (all 80 tools with parameters) is in [README.md](README.md#tool-reference); the behaviour of the extensions is in [MCP_TOOLS_CONTRACT.md](MCP_TOOLS_CONTRACT.md).

## Compatibility Matrix (Legacy -> C#)

### Emulation
- `load_rom`: implemented
- `get_rom_info`: implemented
- `pause`: implemented
- `resume`: implemented
- `reset`: implemented
- `stop`: implemented (also exits MCP process)
- `get_timing_info`: implemented
- `advance_frame`: implemented
- `step_instruction`: implemented
- `step_scanline`: implemented
- `step_cpu_cycles`: implemented
- `rewind`: implemented (shortcut-based rewind requests)

### Memory
- `read_memory`: implemented
- `write_memory`: implemented
- `read_word`: implemented
- `write_word`: implemented
- `revert_rom_changes`: implemented via debugger undo history
- `get_prg_rom_offset`: implemented
- `get_chr_rom_offset`: implemented for NES

### Debug
- `get_emulator_state`: implemented (CPU/PPU/timing snapshot)
- `set_cpu_state`: implemented (partial CPU/PPU field patching)
- `set_breakpoints`: implemented
- `clear_breakpoints`: implemented
- `save_state_file`: implemented (native state file)
- `load_state_file`: implemented (native state file)

### Input
- `get_input`: implemented (current MCP override state per port)
- `set_input`: implemented (persistent override per port)
- `clear_input`: implemented

### Scripting / Misc
- `load_script`: implemented (native debugger script loader)
- `get_script_log`: implemented
- `get_label_address`: implemented
- `take_screenshot`: implemented (reads generated screenshot file, returns image content)

## Notable Behavior Differences

- Legacy Lua bridge JSON snapshot behavior for `save_state_file`/`load_state_file` is replaced with native save-state files.
- `rewind` uses the native rewind history: whole 10 s / 1 min jumps plus a forward replay of the surplus, so it lands exactly `seconds` (or `frames`) earlier and reports the frames rewound (server 0.5.0). It refuses when there is no history.
- `read_word`, `write_word`, `write_memory` and `get_label_address` return JSON (0.5.0).
- `get_chr_rom_offset` is currently NES-focused and returns unsupported for non-NES systems.

## New Native Tools

Added beyond the legacy contract (see the README tool reference for parameters):

- Debugger and state: `get_capabilities`, `get_disassembly`, `get_execution_trace`, `get_profiler_data`, `get_callstack`, `get_breakpoint_hits`, `start_trace`, `stop_trace`, `trace_between`, `self_test`
- Code/data log: `get_cdl_statistics`, `get_cdl_data`, `reset_cdl`, `save_cdl`, `load_cdl`
- Video and PPU: `get_palette_info`, `get_tilemap_size`, `get_tilemap_info`, `get_ppu_state`, `get_dma_state`, `get_oam`
- Memory: `list_memory_types`, `dump_memory`, `snapshot_memory`, `diff_snapshots`, `list_snapshots`, `delete_snapshot`, `get_access_summary`, `reset_access_counters`
- Labels: `load_labels`, `list_labels`, `resolve_address`, `resolve_addresses`
- Execution and input: `run_until`, `play_input`, `record_input`, `play_input_file`
- Scripting: `run_script`, `get_script_result`, `list_scripts`, `unload_script`, `list_recipes`, `run_recipe`
- States and slots: `list_states`, `list_slots`, `switch_slot`, `compare_runs`
- Events: `subscribe_events`

## Events

The MCP host emits notification events with method `notifications/mesen/event`. By default (before `subscribe_events` is called): `breakpoint_hit`, `game_loaded`, `game_paused`, `game_resumed`, `emulation_stopped`. `subscribe_events` can also enable `state_loaded`, `frame_end`, `script_log` and `error`, or disable all events. Payloads are listed under `subscribe_events` in the README.
