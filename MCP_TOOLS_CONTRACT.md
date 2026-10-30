# Mesen MCP Tool Contract (Compatibility Baseline)

This file freezes the expected tool contract from the legacy `mesen-mcp` server so the new in-process C# implementation can preserve behavior while removing the file/Lua bridge transport.

Sources:
- `C:/Work/mesen-mcp/src/tools/*.ts`
- `C:/Work/mesen-mcp/lua/bridge.lua`
- `C:/Work/mesen-mcp/README.md`

## Tool Naming

The following tool names must remain stable for compatibility:

### Emulation
- `load_rom(path, saveStatePath?, saveStateSlot?)`
- `get_rom_info()`
- `pause()`
- `resume()`
- `reset()`
- `stop(exitCode=0)`
- `get_timing_info()`
- `advance_frame(frames=1)` — advances 1-3600 frames; blocks until complete
- `step_instruction(count=1)`
- `step_scanline(count=1)`
- `step_cpu_cycles(count=1)`
- `rewind(seconds)`

### Memory
- `read_memory(address, length, type="cpuDebug")`
- `write_memory(address, data[], type="cpuDebug")`
- `read_word(address, type="cpuDebug")`
- `write_word(address, value, type="cpuDebug")`
- `revert_rom_changes()`
- `get_prg_rom_offset(address)`
- `get_chr_rom_offset(address)`

### Debug
- `get_emulator_state()`
- `set_cpu_state(state)`
- `set_breakpoints(breakpoints[])`
- `clear_breakpoints()`
- `save_state_file(path)`
- `load_state_file(path)`

### Input
- `get_input(port=0)`
- `set_input(port=0, input)`
- `clear_input(port?)`

### Scripting / Misc
- `load_script(code, name?)`
- `get_script_log(clear=false)`
- `get_label_address(label)`
- `take_screenshot()`

## Legacy Parameter Enums To Preserve

### `callbackType`
- `read`
- `write`
- `exec`

### `memory type` string values
- `cpu`
- `cpuDebug`
- `ppu`
- `ppuDebug`
- `oam`
- `prgRom`
- `chrRom`
- `chrRam`
- `workRam`
- `saveRam`
- `nametableRam`
- `spriteRam`
- `bootRom`
- `gbVideoRam`
- `gbWorkRam`
- `gbCartRam`
- `gbHighRam`
- `gbBootRom`

## Event Compatibility

When possible, keep legacy event semantics:
- `breakpoint_hit` event emitted when a configured breakpoint triggers.
- Include hit metadata (`address`, value and access mode details when available).

## Behavior Notes

- `cpuDebug`/`ppuDebug` should be side-effect-free memory access where supported.
- `save_state_file` / `load_state_file` in legacy server used JSON snapshots (not full mapper save states).
- `rewind` depends on emulator rewind capability/settings.
- `take_screenshot` may be unavailable without active video output.

## Migration Intent

The new C# host should keep this external contract stable while improving internals:
- Remove polling file IPC.
- Remove dependence on bridge Lua command handlers for core features where native APIs exist.
- Keep errors explicit and actionable when a tool is unsupported for a console/runtime mode.

## Extensions (server 0.2.0, from `mesen2-mcp-enhancement-requests.md`)

All results below are JSON text. Expected failures use `{"error": {"code": "...", "message": "...", ...}}` with `isError: true`.

### ROM identity (MCP-02)
- `get_rom_info` and `load_rom` return `path`, `fileSizeBytes`, `fileSha1`, `fileCrc32`, `prgRomSizeBytes`, `hasHeaderBytes` and, for SNES, `header` (`mapMode`, `mapLayout`, `speed`, `title`, `checksum`, `complement`). Hashes are of the file on disk (null for archive entries). `get_rom_info` keeps the older keys (`romPath`, `patchPath`, `format`, `consoleType`, `cpuTypes`, `timing`).

### States bound to a ROM (MCP-03)
- `save_state_file(path, note?, meta?)` writes `<path>.json` (`romSha1`, `romPath`, `frame`, `createdUtc`, `serverVersion`, `note`, `meta`).
- `load_state_file(path, requireRomMatch=true)` compares the sidecar SHA-1 with the loaded ROM. Errors: `rom_mismatch` (both hashes returned, nothing loaded), `no_sidecar`, `rom_identity_unavailable`. `requireRomMatch=false` loads anything.
- `load_rom(path, saveStatePath?, saveStateSlot?, requireRomMatch=true)` rejects a state whose sidecar names another ROM (`rom_mismatch`); a state without a sidecar is loaded (legacy states). At startup (`--mcp-savestatepath`) a refused state is skipped and reported as `startupWarning` in `get_rom_info`.
- `list_states(dir, recursive?)` lists `*.mss` with sidecar fields and `valid` (sidecar SHA-1 equals the loaded ROM).

### Stepping (MCP-04)
`advance_frame(frames, timeoutMs?)`, `step_instruction(count, over?, timeoutMs?)`, `step_scanline(count, timeoutMs?)` and `step_cpu_cycles(count, timeoutMs?)` block until the step finishes, a breakpoint hits or the timeout expires, and return
`{stopReason: done|breakpoint|break|timeout, framesAdvanced, instructionsExecuted, frame, elapsedMs, cpu, break?, pcs?}`.
`cpu` is `{k, pc, pc24, a, x, y, sp, d, dbr, ps, psFlags, emulationMode}` on SNES. `stopReason: "break"` is an exception break (BRK, STP, ...). `pcs` (step_instruction with count <= 32) lists the `K:PC` before each executed instruction. `instructionsExecuted` is null where the count is unknown. `timeout` is returned with `isError: true`.

### Script I/O (MCP-05)
- `load_script(code, name?, prelude=true)` returns `{scriptId, name, replaced, initialLog}`. With `prelude` (default) the server prepends a one-line Lua prelude (line numbers are unchanged) that defines `emu.mcp.result(table)`: the table is encoded as JSON and stored as the script's result. Results are limited to about 90 KB (native log buffer).
- `list_scripts`, `unload_script(name)`, `get_script_result(name)`.
- `get_script_log(name?, sinceSeq?, max?, clear?)` returns `{entries:[{seq, frame, script, text}], nextSeq, dropped}`. The server keeps a ring of 5000 rows and harvests the native 500-row log after every step (and in chunks of at most 10 frames at first, adapting to how chatty the scripts are, while scripts are loaded). `frame` is the frame when the row was collected. Pass `nextSeq` back as `sinceSeq`. The native log has no cursor, so when it is full and consecutive rows are identical the harvest is approximate (`approximate: true`).
- `run_script(code, name?, frames=60, timeoutMs?, unloadAfter=true, maxLog=500)`: pause, load, advance, collect log and result, unload. Returns the step fields plus `log` and `result`.
- `load_rom` forgets all scripts (their callbacks do not survive a ROM load).

### Code/data log (MCP-01)
- `get_cdl_data(type|memoryType, start|offset, length, format=summary|ranges|bytes_base64, flagMask=3, includeUnreached=false, maxRows=20000)`.
  - `summary`: `counts` (`code`, `data`, `codeAndData`, `jumpTarget`, `subEntryPoint`, `indexMode8`, `memoryMode8`, `unreached`; NES CHR types report `drawn`) plus the native `statistics`. A byte logged as read data counts as `data`; there is no separate "read" flag in the CDL.
  - `ranges`: merged `[start, end, flags]` runs (end inclusive, flags masked by `flagMask`); pass `flagMask=1` for code-only runs. `truncated`/`nextStart` page through large results.
  - `bytes_base64`: raw flag bytes, at most 65536 per call.
- `reset_cdl`, `save_cdl(path)`, `load_cdl(path, merge=false)`. `merge=true` unions the code/data bits (bits 0-1) of the file into the current log; the other flag bits come from the file.

## Extensions (server 0.3.0, Week 2)

### Labels and address conversion (MCP-13)
- `load_labels(path, format?, replace?)`: `.mlb` (Mesen), `.sym` (`BB:AAAA name`) or `.json` (`[{name, cpu|prgOffset|offset|wram, length?, comment?}]`). Invalid names are sanitized (`renamed` count in the result); `replace=true` clears existing labels first. Returns `parsed`, `invalidRows`, `unmappedAddresses`, `labelsBefore`, `labelsNow`.
- `list_labels(filter?, max?)`, `resolve_address(cpu|prgOffset|offset|wram|label)`, `resolve_addresses(addresses[])` -> `{cpu, memoryType, prgOffset, wram, label, labelOffset, exact, bankAlias[]}`. `label` is the covering label, or the nearest preceding one (`exact=false`, `labelOffset` = distance).
- `get_callstack` and `get_execution_trace` now include label names.
- The IOG `.mlb` files come from the baserom repo: `npm run export -- labels` (B-02, see its `docs/mesen2-exports.md`).

### Turbo, run_until (MCP-10)
- `advance_frame`, `run_script`, `run_recipe`, `run_until` accept `turbo` (no frame limiter while the call runs). Default: on when the server was started with no video and no audio. Responses carry `framesPerSecond` (about 250-370 fps in turbo on the test machine, versus about 60 without).
- `run_until(condition, maxFrames?, timeoutMs?, turbo?)` with conditions `memoryEquals {memType?, address, size?, value, mask?}`, `pcEquals {pc}` (temporary exec breakpoint; user breakpoints are kept), `lua {expr, includes?}`, `breakpoint`. `stopReason`: `condition | breakpoint | done (maxFrames) | timeout`; `met` says whether the condition held.

### Result files (MCP-08)
- Scripts can call `emu.mcp.writeFile(name, bytes, append?)`. Files go to the sandbox folder (`--mcp-results-dir=`, default `%LOCALAPPDATA%/Mesen2Mcp/results`, see 0.5.0); names with `..`, absolute paths or drive letters are refused (the call returns `nil, err`). `run_script` reports them as `filesWritten` (`name`, `path`, `bytes`, `sha1`) and adds `writes` (`writeCalls`, `bytesQueued`, `bytesWritten`, `bytesPending`, `complete`, `warnings`) and `logTruncated` (T-31). `writeFile` has no size cap: the script queues the data and flushes about 48 KB per frame, and `run_script` keeps advancing frames after `frames` (within `timeoutMs`) until the queue is empty. Data still queued when a run ends or a script is unloaded is lost, and the reply says how many bytes (`list_scripts.pendingWriteBytes`, `unload_script.lostWriteBytes`). Relative `path` arguments of `save_cdl`/`load_cdl` resolve into the same folder.
- `get_capabilities` reports `serverVersion`, `resultsDir`, `libDir`, `flags`, `limits`, `labelCount`.

### Lua kit and recipes (MCP-15)
- `run_script`/`load_script` accept `includes: ["mesen_kit", "iog_kit"]`. Modules come from the lib folder (`--mcp-lib-dir=`, else `lua/lib` next to the host, else `tools/lua/lib`) and are loaded on the same line as the prelude so user line numbers do not shift. Headers: `-- requires: a, b`.
- `mesen_kit`: `kit.r8/r16/w8/w16`, `kit.setBit/getBit`, `kit.frame`, `kit.onFrame`, `kit.machine`, `kit.pulse/stopPulse`, `kit.topCounts`, `kit.finish(result)`.
- `iog_kit`: `iog.warp{scene,x,y,facing,scroll,flags,delay,dismissBanner,onReady}`, `iog.setEventFlag/getEventFlag`, `iog.state()`.
- `list_recipes`, `run_recipe(name, args?, frames?, ...)`: `warp` (verified: reaches Edward's Castle in about 360 frames), `census_pc`, `exec_coverage`. In a recipe the Lua table `args` holds the arguments.

## Extensions (server 0.4.0, Week 3)

### Breakpoints (MCP-06)
- `set_breakpoints` items: `callbackType`/`kinds` (`read|write|exec`, comma list), `startAddress`, `endAddress`, `memType`, `condition`, `action` (`break` default | `count` | `log`), `maxLogged`, `name`. `break` items are native breakpoints (the condition is validated with the native expression evaluator; a bad one is `invalid_condition`). `count` and `log` never halt: they run in a hidden Lua script (`__bp_hits`), so their conditions use the supported subset of the Mesen syntax (`value`, `oldvalue`, `address`, `pc`, `a`, `x`, `y`, `sp`, `d`, `k`, `dbr`, `ps`, `frame`, `[addr]`, `{addr}`, `&& || ! != ^ /`).
- `get_breakpoint_hits(id?, sinceSeq?, max?, reset?)`: per breakpoint `count` plus the logged rows (`frame`, `address`, `value`, `pc`, registers, `seq`).

### Memory (MCP-07, MCP-14, MCP-16)
- `list_memory_types`; `read_memory(address, length, type?, format?)` accepts every native memory type (`snesCgRam`, `snesVideoRam`, `snesSpriteRam`, `spcRam`, `spcDspRegisters`, ...) and formats `hex | bytes | words_le | base64` (up to 65536 bytes per call). `dump_memory(type, start?, length?, path)` writes the raw bytes to the results folder and returns the SHA-1. `get_palette_info(first?, count?, format?)`.
- `get_access_summary(type?, ranges[]|rangesPath, cdl?, nonzeroOnly?)`: per named range `exec/read/write` counts, first/last stamps, CDL flag union and a `verdict` (`accessed|never_accessed`). Ranges: `{name, start, end}`, `{name, cpuStart, cpuEnd}` or `{name, label, length?}`. `reset_access_counters`.
- `snapshot_memory(name, type?, start?, length?)`, `diff_snapshots(a, b|"current", ranges?, mergeGap?, maxRows?)` (rows `[start, end, oldHex, newHex]`), `list_snapshots`, `delete_snapshot(name|"*")`. Limits: 16 snapshots, 256 MB.

### Trace (MCP-17)
- `start_trace(path, pcRanges?, condition?, format?, maxLines?, useLabels?)`, `stop_trace` (returns size, lines, SHA-1), `trace_between(startPc, stopPc, path, maxFrames?, condition?, maxLines?, turbo?)`. Trace files go to the results folder.

### Video and PPU state (MCP-11, MCP-12)
- `take_screenshot(crop?, scale?, path?, label?, layers?, returnImage?)`: captured from the PPU frame buffer, so it works without `--mcp-video`. Writes a PNG (`label` gives `screenshots/<label>_<frame>.png`) and, unless `returnImage=false`, returns the image. `layers: ["obj"]` hides the other layers for one frame (SNES, save state / restore round trip; the emulator state is unchanged afterwards).
- `get_ppu_state` (derived mode, layers, windows, colour math plus raw registers), `get_dma_state` (8 channels and HDMA), `get_oam(decoded?, onlyOnScreen?, first?, count?)`, `get_tilemap_info`.

### Input (MCP-09)
- `set_input` also takes `buttons[]`, `mask`, `frames` (hold, then release) and `turbo`.
- `play_input(sequence:[{frames, buttons}], port?, turbo?, release?, thenPause?)`; `record_input(action: start|stop, path, startState?)` records the input of the following advances (file `mesen-mcp-input/1` with the ROM SHA-1); `play_input_file(path, requireRomMatch?, loadStartState?, turbo?)`.

### Events (MCP-18)
- `subscribe_events(types[], frameEvery?)`: `breakpointHit`, `romLoaded`, `stateLoaded`, `frameEnd` (at most every `frameEvery` frames, default 60), `scriptLog`, `error`, `gamePaused`, `gameResumed`, `emulationStopped`. Events arrive as `notifications/mesen/event` notifications (the existing `breakpoint_hit` style events are filtered by this subscription).

### Two ROMs (MCP-19)
- Every tool accepts `slot`. `load_rom(path, slot)` registers the ROM under that name; any other tool with `slot` first switches to it (the core holds one ROM, so the server saves the state and labels of the slot it leaves and restores the other). `list_slots`, `switch_slot(name)`.
- `compare_runs(inputFile, watch[], slots?, startStates?, frames?, maxDivergences?)`: plays the same recording on both slots and reports the first divergence of each watched value (`watch` items: `{name, address, type?, size?}`).

### Capabilities and self test (MCP-20)
- `get_capabilities` also reports `mesenVersion`, the tool list, the Lua functions that exist, flags, limits, slots and `lastSelfTest`.
- `self_test`: checks the features that scripts rely on (memory callbacks, `emu.setInput`, Lua save states, log harvest, stepping, state files, breakpoints, CDL, sandbox, screen buffer). The emulator state is restored afterwards.

## Extensions (server 0.5.0, harness findings: MCP-21..30, tool issues T-01..T-19)

### Folders and capabilities (MCP-21, MCP-22, MCP-30)
- `--mcp-lib-dir=<dir>[;<dir>...]` (repeatable): project kit modules (`<dir>/*.lua`) and recipes (`<dir>/recipes/*.lua`) are searched after the server's own `lua/lib`; the first match wins. `list_recipes` merges them (each entry has its `dir`) and returns `libDirs`.
- The default results folder is `%LOCALAPPDATA%\Mesen2Mcp\results` (`~/.local/share/Mesen2Mcp/results`), no longer a folder under Documents. `get_capabilities` reports `serverVersion`, `resultsDir`, `resultsDirCloudSynced`, `libDir`, `libDirs`, `hostPath`, `baseDirectory`, `homeFolder`, and works without a ROM (`romLoaded: false`, CPU fields omitted; the same for `list_recipes`).
- The host project builds to `McpHost/bin/<Configuration>/net8.0/` whatever RuntimeIdentifier is passed (no more `net8.0/win-x64/`), so a path registered in `mcp.json` is the current build.

### Watchdog (MCP-27, T-12)
- Every call has a watchdog (`timeoutMs` + 15 s, else 120 s, times 4 for the multi-phase tools `compare_runs`, `self_test`, `play_input*`, `trace_between`). A call that exceeds it returns `{error: {code: "timeout", tool, limitMs, paused, recentCalls}}` and pauses the emulator; the last calls are written to stderr. Until the abandoned call ends, other calls return `{error: {code: "busy", stuckTool}}`.
- New `get_server_status`: `{busy, stuckTool, stuckForMs, uptimeSeconds, romLoaded, paused, recentCalls[5]}`; never blocked.
- `step_scanline` adds `scanlinesRequested`, `scanlinesAdvanced`, `scanline` (`framesAdvanced` stays 0 for steps under a frame).
- `get_access_summary` rows add `execBytes`, `execFirstByte` and `hottest {offset, exec}`; `exec` is the sum of per-byte counters and the emulator marks only the opcode byte of each executed instruction (T-15).

### Rewind (MCP-28, T-13, T-14)
- `rewind {seconds | frames, allowPartial?}` jumps back with the instant 10 s/1 min history steps and plays the surplus forward, so it lands exactly `seconds * fps` frames earlier. Reply `{requestedFrames, framesRewound, frameBefore, frameAfter, jumps[], forwardFrames, partial, rewindAvailableFrames?}`. Errors: `rewind_disabled`, `rewind_unavailable`, `rewind_insufficient_history` (all with `rewindAvailableFrames`; the state is restored unless `allowPartial`).
- Cause of the old behaviour: the sub-10-second path started the hold-to-rewind mode and never ended it, which left the core in rewind mode (slow frames, dead Lua callbacks, unpredictable distance).

### Memory and labels (MCP-24, MCP-25, MCP-30)
- `read_word` -> `{memoryType, address, value, hex}`; `write_word` -> `{..., value, hex, readBack, verified}`; `write_memory` -> `{memoryType, address, written, bytes, verified, value?, hex?}` (value/hex for up to 4 bytes, little endian); `get_label_address` -> `{label, address, hex, memoryType, cpuAddress, cpuAddressHex, length}`. `read_memory format:"bytes"` is `{memoryType, address, length, truncated, bytes[]}`.
- `get_breakpoint_hits` rows add `memType`, `absType`, `absOffset`, `wramOffset | prgOffset | sramOffset` and the same for the pc (`pcPrgOffset`, `pcWramOffset`). Breakpoints on `snesMemory` report the bank alias the code used; use `snesPrgRom`/`snesWorkRam` for offsets.
- `set_breakpoints` accepts `action: "halt"` as an alias of `break`.

### Events and scripts (MCP-23, MCP-29, T-08)
- `breakpoint_hit` events are sent only for halting breakpoints and exception breaks (with `source`); step completions and the internal temporary breakpoints are no longer events. Count/log breakpoints send their own `breakpoint_hit` events with `breakpointId`, `name`, `count`, `newHits`.
- `emu.mcp.done()` (and `kit.finish`) may be called at script load: the run ends at once with `script_done`.
- `run_until` accepts `includes` as a top-level argument.
- `mesen_kit`: `kit.wramOffset(addr)`. `iog_kit`/`warp` recipe: `quick = true` (write `sceneNext` only; also used automatically for a scene other than the default without x/y).

### Notes
- Lua note: `emu.createSavestate` works only inside an exec memory callback.
- Lua log limit: the native log keeps the last 500 rows; the server reads it with a 4 MB buffer every 10 frames at first and then adapts to the script. A script that logs 500 or more rows between harvests loses the oldest rows, and `run_script` reports it (`writes.warnings`, `logTruncated`).
- Lua memory callbacks (T-36, measured, unchanged): the address is the full effective CPU address (24-bit for banked accesses; mask with `& 0xFFFF` or use `kit.wramOffset`); a direct-page access is reported at its effective address (`0x36`); write callbacks run before the write (`emu.read` returns the old value); the CPU `pc` seen in a memory callback is the address after the accessing instruction; a bus-type (`snesMemory`) callback on `$7F0000-$7F00FF` got no hits in an idle test, so use the `snesWorkRam` type for `$7F` work RAM.
- Startup flags: `--mcp-rom=<file>` (ROM to load), `--mcp-savestatepath=<file>` (state file to load after the ROM; it must exist and, when it has a sidecar, match the ROM), `--mcp-savestateslot=<1-10>` (load that numbered save-state slot of the ROM instead; exclusive with `--mcp-savestatepath`), `--mcp-results-dir=`, `--mcp-lib-dir=`, `--mcp-video`, `--mcp-audio`, `--mcp-input`. A second server for the rebuilt ROM is the `mesen2-mcp-rebuilt` entry in `mcp.json` (its own `--mcp-rom=`; add `--mcp-savestatepath=<file>` to start from a state). The full tool reference is in [README.md](README.md#tool-reference).

## Extensions (server 0.5.1: T-31, T-36)
- `run_script` / `emu.mcp.writeFile`: no 100 KB cap any more. The server reads the native script log with a 4 MB buffer (the old 100000-byte buffer cut rows and shared its budget between `writeFile` calls), the script flushes queued file data about 48 KB per frame, and the run continues until it has arrived. The reply carries `writes` and `logTruncated`; truncation is reported, never silent.
- MCP-43: `filesWritten` entries carry `truncated: true` (with the bytes kept) when a file was cut off or its data is still queued; `writeFile` never fails for size (see above), and a `true` return means the data is queued in the script.
- MCP-42: the `warp` recipe result has `inputLocked` and `bannerShown`, waits `unlockWait` frames (default 60) for the joypad, and returns `ok: false, reason: "input_locked"` when it is still locked.
- T-42: `reset` now works (it sent a shortcut the core ignores before 0.6.0). `reset {mode: soft|power}` stops the console before the first boot instruction (the server arms a one-shot pause in the reset/game-loaded notification and lets the emulator run to the end of the frame that processes the flag). `load_rom {paused: true}` = load + power cycle + stop at frame 0. `run_script`/`run_recipe {fromReset: "power"|"soft"|true}` reset first, then load the script at the reset vector, so `frames` counts from the boot. A power cycle drops scripts and Lua-backed breakpoints (`scriptsDropped`).
- Lua memory-callback behavior (address width, order versus the write, PC) is documented in the README ("Lua scripting") and in `mesen_kit.lua`.
