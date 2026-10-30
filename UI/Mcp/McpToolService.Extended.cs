using Mesen.Config;
using Mesen.Debugger.Labels;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Mesen.Mcp
{
#pragma warning disable IL2026
#pragma warning disable IL3050
#pragma warning disable IL2070
#pragma warning disable IL2067
#pragma warning disable IL2090
	/// <summary>
	/// Tools added for the IOG audit investigation (see mesen2-mcp-enhancement-requests.md):
	/// ROM identity (MCP-02), state/ROM binding (MCP-03), script I/O (MCP-05), CDL (MCP-01),
	/// synchronous stepping (MCP-04).
	///
	/// Note on JSON: the UI project is AOT-compatible with reflection-based serialization disabled,
	/// so payloads are built from JsonNode values only. Never call JsonArray.Add(int/byte/...):
	/// that resolves to the generic Add&lt;T&gt; overload, which needs a TypeInfoResolver. Use
	/// JsonValue.Create(...) instead.
	/// </summary>
	internal sealed partial class McpToolService
	{
		/// <summary>The version of the host (the mcp-version.txt file at the repo root, through McpHost.csproj).</summary>
		internal static readonly string ServerVersion = ReadServerVersion();

		private static string ReadServerVersion()
		{
			string? version = System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
				.OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
			if(string.IsNullOrEmpty(version)) {
				return "0.0.0";
			}
			int plus = version.IndexOf('+');
			return plus >= 0 ? version.Substring(0, plus) : version;
		}

		private const int ScriptLogRingSize = 5000;
		private const int MaxFramesPerCall = 200000;
		private const int MaxCdlBase64Bytes = 65536;
		private const int ScriptLogChunkFrames = 10;
		private const int MaxLogChunkFrames = 600;

		private static readonly JsonSerializerOptions _indented = new JsonSerializerOptions() { WriteIndented = true };

		private readonly McpScriptLogStore _logStore = new McpScriptLogStore(ScriptLogRingSize);
		private readonly object _breakLock = new object();
		private BreakEvent _completionBreak;
		private bool _hasCompletionBreak;
		private string? _startupWarning;

		private static McpToolResult Json(JsonNode node, bool isError = false)
		{
			return McpToolResult.Text(node.ToJsonString(_indented), isError);
		}

		private static McpToolResult ErrorJson(string code, string message, JsonObject? details = null)
		{
			JsonObject error = new JsonObject() {
				["code"] = code,
				["message"] = message
			};
			if(details != null) {
				foreach(KeyValuePair<string, JsonNode?> item in details.ToList()) {
					details.Remove(item.Key);
					error[item.Key] = item.Value;
				}
			}
			return Json(new JsonObject() { ["error"] = error }, true);
		}

		private static JsonNode? Num(long value) => JsonValue.Create(value);
		private static JsonNode? Num(int value) => JsonValue.Create(value);

		private static long? GetOptionalLong(JsonElement element, string name)
		{
			return TryGetProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
		}

		private static string? GetFirstString(JsonElement element, params string[] names)
		{
			foreach(string name in names) {
				string? value = GetOptionalString(element, name);
				if(value != null) {
					return value;
				}
			}
			return null;
		}

		private static int? GetFirstInt(JsonElement element, params string[] names)
		{
			foreach(string name in names) {
				int? value = GetOptionalInt(element, name);
				if(value != null) {
					return value;
				}
			}
			return null;
		}

		private long GetFrameCount(CpuType cpu)
		{
			return EmuApi.GetTimingInfo(cpu).FrameCount;
		}

		// ---------------------------------------------------------------------------------------------
		// Tool registration for the P0 set
		// ---------------------------------------------------------------------------------------------

		private void AddExtendedTools(Dictionary<string, McpToolDefinition> tools)
		{
			AddTool(tools, "load_rom",
				"Load a ROM file and optionally restore a save state path or slot. Returns the ROM identity (size, SHA-1, CRC32, header). " +
				"A state that has a sidecar from another ROM is rejected (code rom_mismatch) unless requireRomMatch is false.",
				ObjectSchema(
					("path", StringSchema("Absolute ROM path"), true),
					("saveStatePath", StringSchema("Optional save-state file path"), false),
					("saveStateSlot", IntegerSchema("Optional save-state slot 1-10"), false),
					("requireRomMatch", BooleanSchema("Reject a save state whose sidecar names a different ROM (default true). A state without a sidecar is always loaded here."), false)
				),
				ToolLoadRom);

			AddTool(tools, "get_rom_info",
				"Get info about the loaded ROM: path, file size, SHA-1, CRC32, SNES header (map mode, speed, title, checksum), PRG size and console timing.",
				EmptySchema(), ToolGetRomInfo);

			AddTool(tools, "save_state_file",
				"Save emulator state to a file and write a sidecar <path>.json with the ROM SHA-1, ROM path, frame count, server version, note and caller metadata.",
				ObjectSchema(
					("path", StringSchema("Save-state file path"), true),
					("note", StringSchema("Optional free-form note stored in the sidecar"), false),
					("meta", new JsonObject() { ["type"] = "object", ["description"] = "Optional free-form object stored in the sidecar (for example the scene id)" }, false)
				),
				ToolSaveStateFile);

			AddTool(tools, "load_state_file",
				"Load an emulator state file. By default the sidecar written by save_state_file must name the loaded ROM's SHA-1; otherwise the call fails with code rom_mismatch (or no_sidecar) and nothing is loaded.",
				ObjectSchema(
					("path", StringSchema("Save-state file path"), true),
					("requireRomMatch", BooleanSchema("Verify the sidecar ROM SHA-1 before loading (default true). false loads any state, as before."), false)
				),
				ToolLoadStateFile);

			AddTool(tools, "list_states",
				"List save states in a folder with their sidecar fields and a valid flag (true when the sidecar ROM SHA-1 equals the loaded ROM).",
				ObjectSchema(
					("dir", StringSchema("Folder to scan for *.mss files"), true),
					("recursive", BooleanSchema("Scan sub-folders (default false)"), false)
				),
				ToolListStates);

			AddTool(tools, "advance_frame",
				"Advance by one or more frames (default 1, max 200000). Blocks until done, a breakpoint hits, or timeoutMs elapses. " +
				"Returns stopReason (done|breakpoint|break|script_done|timeout), framesAdvanced, frame, elapsedMs, framesPerSecond and the CPU registers. " +
				"turbo removes the frame limiter (default: on when the server runs without video and audio).",
				ObjectSchema(
					("frames", IntegerSchema("Number of frames to advance (1-200000, default 1)"), false),
					("timeoutMs", IntegerSchema("Give up after this many milliseconds (default: 30000 + frames*20 with turbo, max(10000, frames*500) without)"), false),
					("turbo", BooleanSchema("Run without the frame limiter"), false)
				),
				ToolAdvanceFrame);

			AddTool(tools, "step_instruction",
				"Step CPU instructions synchronously. Returns stopReason, instructionsExecuted, frame and the CPU registers. For count <= 32 each executed instruction's address is listed in pcs.",
				ObjectSchema(
					("count", IntegerSchema("Instruction count (default 1)"), false),
					("over", BooleanSchema("Step over calls (default false)"), false),
					("timeoutMs", IntegerSchema("Give up after this many milliseconds (default 10000)"), false)
				),
				ToolStepInstruction);

			AddTool(tools, "step_scanline",
				"Step PPU scanlines synchronously. Returns stopReason, frame and the CPU registers.",
				ObjectSchema(
					("count", IntegerSchema("Scanline count (default 1)"), false),
					("timeoutMs", IntegerSchema("Give up after this many milliseconds (default 10000)"), false)
				),
				ToolStepScanline);

			AddTool(tools, "step_cpu_cycles",
				"Step CPU cycles synchronously. Returns stopReason, frame and the CPU registers.",
				ObjectSchema(
					("count", IntegerSchema("CPU cycle count (default 1)"), false),
					("timeoutMs", IntegerSchema("Give up after this many milliseconds (default 10000)"), false)
				),
				ToolStepCpuCycles);

			AddTool(tools, "load_script",
				"Load and execute a Lua script. Returns a stable scriptId and any log rows produced while loading. " +
				"The server prepends a one-line prelude that provides emu.mcp.result(table) (JSON result, read it with get_script_result). Reusing a name replaces the script.",
				ObjectSchema(
					("code", StringSchema("Lua source code"), true),
					("name", StringSchema("Optional script name"), false),
					("prelude", BooleanSchema("Provide emu.mcp.result (default true)"), false),
					("includes", StringArraySchema("Kit modules from the server's Lua lib folder to load before the script, for example [\"mesen_kit\", \"iog_kit\"] (see list_recipes for the folder)"), false)
				),
				ToolLoadScript);

			AddTool(tools, "list_scripts", "List the scripts loaded through this server.", EmptySchema(), ToolListScripts);

			AddTool(tools, "unload_script", "Unload a script by name (removes its callbacks). Its log and result remain readable.",
				ObjectSchema(("name", StringSchema("Script name"), true)), ToolUnloadScript);

			AddTool(tools, "get_script_log",
				"Get script log rows from the server-side ring (5000 rows). Returns {entries:[{seq,frame,script,text}], nextSeq, dropped}. Pass nextSeq back as sinceSeq to read only new rows. " +
				"frame is the emulator frame when the row was collected (end of the step chunk), not the frame the script logged it.",
				ObjectSchema(
					("name", StringSchema("Only rows of this script"), false),
					("sinceSeq", IntegerSchema("Return rows with seq >= sinceSeq (default 0 = everything still in the ring)"), false),
					("max", IntegerSchema("Maximum rows (default 500, max 5000)"), false),
					("clear", BooleanSchema("Empty the ring after reading"), false)
				),
				ToolGetScriptLog);

			AddTool(tools, "get_script_result",
				"Get the last emu.mcp.result(table) value of a script (as JSON). Results are limited to about 90 KB.",
				ObjectSchema(("name", StringSchema("Script name"), true)), ToolGetScriptResult);


			AddTool(tools, "get_cdl_data",
				"Read code/data logger flags. format=summary returns counts; ranges returns merged [start,end,flags] runs (flags are masked by flagMask, default 0x03 = code|data; unreached bytes are skipped unless includeUnreached); bytes_base64 returns raw flag bytes (max 65536).",
				ObjectSchema(
					("type", StringSchema("Memory type (default prgRom)"), false),
					("memoryType", StringSchema("Alias of type"), false),
					("start", IntegerSchema("Start offset (default 0)"), false),
					("offset", IntegerSchema("Alias of start"), false),
					("length", IntegerSchema("Byte count (default: rest of memory; bytes_base64 is capped at 65536)"), false),
					("format", StringSchema("summary | ranges | bytes_base64 (default summary)"), false),
					("flagMask", IntegerSchema("Mask applied to flags for ranges (default 3)"), false),
					("includeUnreached", BooleanSchema("Include runs whose masked flags are 0 (default false)"), false),
					("maxRows", IntegerSchema("Maximum ranges returned (default 20000); use nextStart to continue"), false)
				),
				ToolGetCdlData);

			AddTool(tools, "reset_cdl", "Clear the code/data log.",
				ObjectSchema(("memoryType", StringSchema("Memory type (default prgRom)"), false)), ToolResetCdl);

			AddTool(tools, "save_cdl", "Save the code/data log to a file (Mesen CDL format, includes the ROM CRC).",
				ObjectSchema(
					("path", StringSchema("Output file path"), true),
					("memoryType", StringSchema("Memory type (default prgRom)"), false)
				), ToolSaveCdl);

			AddTool(tools, "load_cdl", "Load a code/data log file. merge=true unions the code/data bits of the file with the current log instead of replacing it.",
				ObjectSchema(
					("path", StringSchema("CDL file path"), true),
					("memoryType", StringSchema("Memory type (default prgRom)"), false),
					("merge", BooleanSchema("Union with the current log (default false = replace)"), false)
				), ToolLoadCdl);
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-02: ROM identity
		// ---------------------------------------------------------------------------------------------

		private McpRomIdentity? GetCurrentRomIdentity()
		{
			RomInfo info = EmuApi.GetRomInfo();
			return McpRomIdentity.TryCompute(info.RomPath, info.ConsoleType == ConsoleType.Snes);
		}

		private JsonObject BuildRomIdentityNode()
		{
			RomInfo info = EmuApi.GetRomInfo();
			CpuType cpu = GetPrimaryCpuType();
			McpRomIdentity? identity = McpRomIdentity.TryCompute(info.RomPath, info.ConsoleType == ConsoleType.Snes);

			JsonObject node = new JsonObject() {
				["path"] = identity?.Path ?? info.RomPath,
				["fileSizeBytes"] = identity != null ? Num(identity.FileSizeBytes) : null,
				["fileSha1"] = identity?.FileSha1,
				["fileCrc32"] = identity?.FileCrc32,
				["prgRomSizeBytes"] = Num(DebugApi.GetMemorySize(cpu.GetPrgRomMemoryType())),
				["header"] = identity?.Header?.DeepClone(),
				["hasHeaderBytes"] = identity != null ? JsonValue.Create(identity.HasHeaderBytes) : null
			};
			if(identity == null) {
				node["identityNote"] = "ROM is not a plain file on disk (archive entry?); hash unavailable.";
			}
			return node;
		}

		private McpToolResult ToolGetRomInfo(JsonElement _)
		{
			RomInfo info = EmuApi.GetRomInfo();
			CpuType cpu = GetPrimaryCpuType();

			JsonObject node = BuildRomIdentityNode();
			node["romPath"] = info.RomPath;
			node["patchPath"] = info.PatchPath;
			node["format"] = info.Format.ToString();
			node["consoleType"] = info.ConsoleType.ToString();
			node["cpuTypes"] = new JsonArray(info.CpuTypes.Select(c => (JsonNode?)c.ToString()).ToArray());
			node["timing"] = BuildTimingNode(cpu);
			node["serverVersion"] = ServerVersion;
			if(_startupWarning != null) {
				node["startupWarning"] = _startupWarning;
			}
			return Json(node);
		}

		// ---------------------------------------------------------------------------------------------
		// load_rom (+ MCP-03 state check)
		// ---------------------------------------------------------------------------------------------

		private McpToolResult ToolLoadRom(JsonElement args)
		{
			string path = GetRequiredString(args, "path");
			string? saveStatePath = GetOptionalString(args, "saveStatePath");
			int? saveStateSlot = GetOptionalInt(args, "saveStateSlot");
			bool requireRomMatch = GetOptionalBool(args, "requireRomMatch") ?? true;

			JsonObject? stateInfo = LoadRomInternal(path, saveStatePath, saveStateSlot, requireRomMatch, lenientWhenNoSidecar: true);

			JsonObject node = new JsonObject() {
				["loaded"] = true,
				["message"] = "ROM loaded and emulation resumed.",
				["rom"] = BuildRomIdentityNode()
			};
			if(stateInfo != null) {
				node["state"] = stateInfo;
			}
			return Json(node);
		}

		private void ForgetAllScripts()
		{
			foreach(KeyValuePair<string, int> script in _scriptIds.ToList()) {
				try { DebugApi.RemoveScript(script.Value); } catch { }
			}
			_scriptIds.Clear();
			_scriptLogOffsets.Clear();
			_logStore.ForgetAllScripts();
			ResetScriptDependentState();
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-03: save states bound to a ROM
		// ---------------------------------------------------------------------------------------------

		private static string SidecarPath(string statePath) => statePath + ".json";

		private static JsonObject? ReadSidecar(string statePath)
		{
			string sidecar = SidecarPath(statePath);
			if(!File.Exists(sidecar)) {
				return null;
			}
			try {
				return JsonNode.Parse(File.ReadAllText(sidecar)) as JsonObject;
			} catch {
				return null;
			}
		}

		/// <summary>
		/// Verifies that the state at <paramref name="statePath"/> belongs to the loaded ROM.
		/// Throws McpToolException(rom_mismatch | no_sidecar | rom_identity_unavailable) when it does not.
		/// Returns a description of the check for the tool response.
		/// </summary>
		private JsonObject CheckStateAgainstLoadedRom(string statePath, bool requireRomMatch, bool lenientWhenNoSidecar)
		{
			JsonObject? sidecar = ReadSidecar(statePath);
			McpRomIdentity? current = GetCurrentRomIdentity();
			string? stateSha1 = sidecar?["romSha1"]?.GetValue<string>();

			JsonObject result = new JsonObject() {
				["path"] = statePath,
				["loadedRomSha1"] = current?.FileSha1,
				["stateRomSha1"] = stateSha1,
				["hasSidecar"] = sidecar != null
			};

			if(!requireRomMatch) {
				result["romCheck"] = "skipped";
				return result;
			}

			if(sidecar == null || stateSha1 == null) {
				if(lenientWhenNoSidecar) {
					result["romCheck"] = "unverified (no sidecar)";
					return result;
				}
				throw new McpToolException("no_sidecar",
					$"State '{statePath}' has no sidecar ({SidecarPath(statePath)}), so its ROM cannot be verified. Pass requireRomMatch=false to load it anyway.",
					result);
			}

			if(current == null) {
				throw new McpToolException("rom_identity_unavailable",
					"The loaded ROM is not a plain file, so its SHA-1 cannot be computed. Pass requireRomMatch=false to load the state anyway.", result);
			}

			if(!string.Equals(stateSha1, current.FileSha1, StringComparison.OrdinalIgnoreCase)) {
				result["stateRomPath"] = sidecar["romPath"]?.DeepClone();
				result["loadedRomPath"] = current.Path;
				throw new McpToolException("rom_mismatch",
					$"State was saved with ROM SHA-1 {stateSha1} but the loaded ROM is {current.FileSha1}. Nothing was loaded.", result);
			}

			result["romCheck"] = "match";
			return result;
		}

		private McpToolResult ToolSaveStateFile(JsonElement args)
		{
			string path = Path.GetFullPath(GetRequiredString(args, "path"));
			string? folder = Path.GetDirectoryName(path);
			if(!string.IsNullOrWhiteSpace(folder)) {
				Directory.CreateDirectory(folder);
			}

			CpuType cpu = GetPrimaryCpuType();
			McpRomIdentity? rom = GetCurrentRomIdentity();
			long frame = GetFrameCount(cpu);

			EmuApi.SaveStateFile(path);
			if(!File.Exists(path)) {
				throw new InvalidOperationException($"State file was not created: {path}");
			}

			JsonNode? meta = null;
			if(TryGetProperty(args, "meta", out JsonElement metaElement) && metaElement.ValueKind == JsonValueKind.Object) {
				meta = JsonNode.Parse(metaElement.GetRawText());
			}

			JsonObject sidecar = new JsonObject() {
				["schema"] = 1,
				["romSha1"] = rom?.FileSha1,
				["romCrc32"] = rom?.FileCrc32,
				["romPath"] = rom?.Path ?? EmuApi.GetRomInfo().RomPath,
				["romFileSizeBytes"] = rom != null ? Num(rom.FileSizeBytes) : null,
				["frame"] = Num(frame),
				["createdUtc"] = DateTime.UtcNow.ToString("o"),
				["serverVersion"] = ServerVersion,
				["note"] = GetOptionalString(args, "note"),
				["meta"] = meta
			};
			File.WriteAllText(SidecarPath(path), sidecar.ToJsonString(_indented));

			return Json(new JsonObject() {
				["saved"] = true,
				["path"] = path,
				["sidecar"] = SidecarPath(path),
				["sizeBytes"] = Num(new FileInfo(path).Length),
				["romSha1"] = rom?.FileSha1,
				["frame"] = Num(frame)
			});
		}

		private McpToolResult ToolLoadStateFile(JsonElement args)
		{
			string path = Path.GetFullPath(GetRequiredString(args, "path"));
			bool requireRomMatch = GetOptionalBool(args, "requireRomMatch") ?? true;
			if(!File.Exists(path)) {
				throw new FileNotFoundException("State file not found.", path);
			}

			JsonObject check = CheckStateAgainstLoadedRom(path, requireRomMatch, lenientWhenNoSidecar: false);
			EmuApi.LoadStateFile(path);

			check["loaded"] = true;
			JsonObject? sidecar = ReadSidecar(path);
			if(sidecar != null) {
				check["sidecar"] = sidecar;
			}
			check["frame"] = Num(GetFrameCount(GetPrimaryCpuType()));
			return Json(check);
		}

		private McpToolResult ToolListStates(JsonElement args)
		{
			string dir = GetRequiredString(args, "dir");
			bool recursive = GetOptionalBool(args, "recursive") ?? false;
			if(!Directory.Exists(dir)) {
				throw new DirectoryNotFoundException($"Folder not found: {dir}");
			}

			McpRomIdentity? current = GetCurrentRomIdentity();
			JsonArray states = new JsonArray();
			int validCount = 0;

			IEnumerable<string> files = Directory.EnumerateFiles(dir, "*.mss",
				recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
			foreach(string file in files) {
				FileInfo fileInfo = new FileInfo(file);
				JsonObject? sidecar = ReadSidecar(file);
				string? sha1 = sidecar?["romSha1"]?.GetValue<string>();
				bool valid = sha1 != null && current != null && string.Equals(sha1, current.FileSha1, StringComparison.OrdinalIgnoreCase);
				if(valid) {
					validCount++;
				}

				states.Add(new JsonObject() {
					["path"] = fileInfo.FullName,
					["sizeBytes"] = Num(fileInfo.Length),
					["modifiedUtc"] = fileInfo.LastWriteTimeUtc.ToString("o"),
					["hasSidecar"] = sidecar != null,
					["valid"] = valid,
					["romSha1"] = sha1,
					["romPath"] = sidecar?["romPath"]?.DeepClone(),
					["frame"] = sidecar?["frame"]?.DeepClone(),
					["createdUtc"] = sidecar?["createdUtc"]?.DeepClone(),
					["serverVersion"] = sidecar?["serverVersion"]?.DeepClone(),
					["note"] = sidecar?["note"]?.DeepClone(),
					["meta"] = sidecar?["meta"]?.DeepClone()
				});
			}

			return Json(new JsonObject() {
				["dir"] = dir,
				["loadedRomSha1"] = current?.FileSha1,
				["count"] = states.Count,
				["validCount"] = validCount,
				["states"] = states
			});
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-04: synchronous stepping
		// ---------------------------------------------------------------------------------------------

		private sealed class StepOutcome
		{
			public string StopReason = "done";
			public bool HasBreak;
			public BreakEvent Break;
			public long FramesAdvanced;
			public long? InstructionsExecuted;
			public long ElapsedMs;
			public JsonArray? Pcs;
		}

		/// <summary>
		/// Pause and internal-operation breaks are not completions of a step request: a stale one
		/// (for example from the pause tool) must not end a step early.
		/// </summary>
		private static bool IsStepCompletion(BreakSource source)
		{
			return source != BreakSource.Pause && source != BreakSource.InternalOperation;
		}

		private static string ClassifyBreak(BreakSource source)
		{
			switch(source) {
				case BreakSource.Breakpoint:
					return "breakpoint";
				case BreakSource.Unspecified:
				case BreakSource.CpuStep:
				case BreakSource.PpuStep:
				case BreakSource.Irq:
				case BreakSource.Nmi:
					return "done";
				default:
					// Exception-style breaks (BRK, COP, STP, uninitialized read, ...).
					return (int)source > (int)BreakSource.InternalOperation ? "break" : "done";
			}
		}

		private void HandleStepCompletionNotification(BreakEvent br)
		{
			if(!IsStepCompletion(br.Source)) {
				return;
			}
			lock(_breakLock) {
				_completionBreak = br;
				_hasCompletionBreak = true;
			}
			_stepDoneEvent.Set();
		}

		/// <summary>Runs one native step request and waits for it to complete, break, or time out.</summary>
		private StepOutcome RunStep(CpuType cpu, int count, StepType type, int timeoutMs)
		{
			StepOutcome outcome = new StepOutcome();
			long frameBefore = GetFrameCount(cpu);
			Stopwatch watch = Stopwatch.StartNew();

			lock(_breakLock) {
				_hasCompletionBreak = false;
			}
			_stepDoneEvent.Reset();
			DebugApi.Step(cpu, count, type);

			if(!_stepDoneEvent.Wait(Math.Max(1, timeoutMs))) {
				outcome.StopReason = "timeout";
				try { EmuApi.Pause(); } catch { }
			} else {
				lock(_breakLock) {
					if(_hasCompletionBreak) {
						outcome.HasBreak = true;
						outcome.Break = _completionBreak;
						outcome.StopReason = ClassifyBreak(_completionBreak.Source);
					}
				}
			}

			outcome.ElapsedMs = watch.ElapsedMilliseconds;
			outcome.FramesAdvanced = GetFrameCount(cpu) - frameBefore;
			return outcome;
		}

		/// <summary>
		/// Advances frames. While scripts are loaded the advance is chunked and the native script
		/// logs (capped at 500 rows / 100000 bytes each) are harvested between chunks so no output is lost;
		/// the chunk size adapts to how chatty the scripts are.
		/// </summary>
		/// <param name="shouldStop">Checked after every chunk (after harvesting); true ends the run with stopReason "script_done".</param>
		private StepOutcome RunFrames(CpuType cpu, int frames, int timeoutMs, Func<bool>? shouldStop = null)
		{
			StepOutcome total = new StepOutcome();
			long frameBefore = GetFrameCount(cpu);
			Stopwatch watch = Stopwatch.StartNew();
			int remaining = frames;
			int chunkFrames = ScriptLogChunkFrames;

			while(remaining > 0) {
				int chunk = _scriptIds.Count > 0 ? Math.Min(remaining, chunkFrames) : remaining;
				int budget = timeoutMs - (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds);
				StepOutcome part = RunStep(cpu, chunk, StepType.PpuFrame, Math.Max(1, budget));
				double fill = HarvestScriptLogs(cpu);

				//Aim for about a quarter of the native log per chunk.
				double perFrame = fill / Math.Max(1, chunk);
				chunkFrames = perFrame < 0.00005 ? MaxLogChunkFrames : Math.Clamp((int)(0.25 / perFrame), 1, MaxLogChunkFrames);

				total.StopReason = part.StopReason;
				total.HasBreak = part.HasBreak;
				total.Break = part.Break;
				if(part.StopReason != "done") {
					break;
				}
				if(shouldStop != null && shouldStop()) {
					total.StopReason = "script_done";
					break;
				}
				remaining -= chunk;
			}

			total.ElapsedMs = watch.ElapsedMilliseconds;
			total.FramesAdvanced = GetFrameCount(cpu) - frameBefore;
			NoteFramesAdvanced(total.FramesAdvanced);
			MaybeEmitFrameEvent(cpu);
			return total;
		}

		private JsonObject BuildCpuSummary(CpuType cpu)
		{
			if(cpu == CpuType.Snes) {
				SnesCpuState s = DebugApi.GetCpuState<SnesCpuState>(cpu);
				return new JsonObject() {
					["k"] = (int)s.K,
					["pc"] = (int)s.PC,
					["pc24"] = ((int)s.K << 16) | s.PC,
					["a"] = (int)s.A,
					["x"] = (int)s.X,
					["y"] = (int)s.Y,
					["sp"] = (int)s.SP,
					["d"] = (int)s.D,
					["dbr"] = (int)s.DBR,
					["ps"] = Convert.ToInt32(s.PS),
					["psFlags"] = s.PS.ToString(),
					["emulationMode"] = s.EmulationMode
				};
			}
			return GetCpuStateNode(cpu);
		}

		private JsonObject BuildStepResponse(CpuType cpu, StepOutcome outcome)
		{
			JsonObject node = new JsonObject() {
				["stopReason"] = outcome.StopReason,
				["framesAdvanced"] = Num(outcome.FramesAdvanced),
				["instructionsExecuted"] = outcome.InstructionsExecuted != null ? Num(outcome.InstructionsExecuted.Value) : null,
				["frame"] = Num(GetFrameCount(cpu)),
				["elapsedMs"] = Num(outcome.ElapsedMs),
				["framesPerSecond"] = outcome.ElapsedMs > 0 && outcome.FramesAdvanced > 0
					? JsonValue.Create(Math.Round(outcome.FramesAdvanced * 1000.0 / outcome.ElapsedMs, 1)) : null,
				["cpu"] = BuildCpuSummary(cpu)
			};

			if(outcome.Pcs != null) {
				node["pcs"] = outcome.Pcs;
			}

			if(outcome.HasBreak && outcome.StopReason != "done") {
				BreakEvent br = outcome.Break;
				JsonObject detail = new JsonObject() {
					["source"] = br.Source.ToString(),
					["breakpointId"] = br.BreakpointId,
					["address"] = Num(br.Operation.Address),
					["value"] = Num(br.Operation.Value),
					["memoryType"] = br.Operation.MemType.ToString(),
					["operationType"] = br.Operation.Type.ToString()
				};
				McpBreakpointSpec? spec = _breakpoints.FirstOrDefault(b => b.Id == br.BreakpointId);
				if(spec != null) {
					detail["callbackType"] = spec.CallbackType;
				}
				node["break"] = detail;
			}
			return node;
		}

		private McpToolResult StepResult(CpuType cpu, StepOutcome outcome)
		{
			return Json(BuildStepResponse(cpu, outcome), outcome.StopReason == "timeout");
		}

		private McpToolResult ToolAdvanceFrame(JsonElement args)
		{
			int frames = Math.Clamp(GetOptionalInt(args, "frames") ?? 1, 1, MaxFramesPerCall);
			bool turbo = ResolveTurbo(args);
			int timeoutMs = GetOptionalInt(args, "timeoutMs") ?? DefaultTimeoutMs(frames, turbo);
			CpuType cpu = GetPrimaryCpuType();

			StepOutcome outcome;
			using(BeginTurbo(turbo)) {
				outcome = RunFrames(cpu, frames, timeoutMs);
			}
			return StepResult(cpu, outcome);
		}

		private McpToolResult ToolStepInstruction(JsonElement args)
		{
			int count = Math.Clamp(GetOptionalInt(args, "count") ?? 1, 1, 10000000);
			bool over = GetOptionalBool(args, "over") ?? false;
			int timeoutMs = GetOptionalInt(args, "timeoutMs") ?? 10000;
			StepType type = over ? StepType.StepOver : StepType.Step;
			CpuType cpu = GetPrimaryCpuType();

			StepOutcome outcome;
			if(count <= 32) {
				// Step one instruction at a time so the executed addresses and an exact count are known.
				outcome = new StepOutcome() { InstructionsExecuted = 0, Pcs = new JsonArray() };
				long frameBefore = GetFrameCount(cpu);
				Stopwatch watch = Stopwatch.StartNew();
				for(int i = 0; i < count; i++) {
					outcome.Pcs.Add(BuildPcEntry(cpu));
					int budget = timeoutMs - (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds);
					StepOutcome part = RunStep(cpu, 1, type, Math.Max(1, budget));
					outcome.StopReason = part.StopReason;
					outcome.HasBreak = part.HasBreak;
					outcome.Break = part.Break;
					if(part.StopReason == "done") {
						outcome.InstructionsExecuted++;
					} else {
						break;
					}
				}
				outcome.ElapsedMs = watch.ElapsedMilliseconds;
				outcome.FramesAdvanced = GetFrameCount(cpu) - frameBefore;
			} else {
				outcome = RunStep(cpu, count, type, timeoutMs);
				outcome.InstructionsExecuted = outcome.StopReason == "done" ? count : null;
			}

			if(_scriptIds.Count > 0) {
				HarvestScriptLogs(cpu);
			}
			return StepResult(cpu, outcome);
		}

		private JsonNode? BuildPcEntry(CpuType cpu)
		{
			if(cpu == CpuType.Snes) {
				SnesCpuState s = DebugApi.GetCpuState<SnesCpuState>(cpu);
				return JsonValue.Create($"{s.K:X2}:{s.PC:X4}");
			}
			return JsonValue.Create($"{DebugApi.GetProgramCounter(cpu, true):X}");
		}

		private McpToolResult ToolStepScanline(JsonElement args)
		{
			int count = Math.Clamp(GetOptionalInt(args, "count") ?? 1, 1, 10000000);
			int timeoutMs = GetOptionalInt(args, "timeoutMs") ?? 10000;
			CpuType cpu = GetPrimaryCpuType();
			StepOutcome outcome = RunStep(cpu, count, StepType.PpuScanline, timeoutMs);
			if(_scriptIds.Count > 0) {
				HarvestScriptLogs(cpu);
			}
			return StepResult(cpu, outcome);
		}

		private McpToolResult ToolStepCpuCycles(JsonElement args)
		{
			int count = Math.Clamp(GetOptionalInt(args, "count") ?? 1, 1, 100000000);
			int timeoutMs = GetOptionalInt(args, "timeoutMs") ?? 10000;
			CpuType cpu = GetPrimaryCpuType();
			StepOutcome outcome = RunStep(cpu, count, StepType.CpuCycleStep, timeoutMs);
			if(_scriptIds.Count > 0) {
				HarvestScriptLogs(cpu);
			}
			return StepResult(cpu, outcome);
		}

		/// <summary>Make sure emulation is halted so a script starts from a stable state.</summary>
		private void EnsurePaused(int timeoutMs)
		{
			if(EmuApi.IsPaused()) {
				return;
			}
			EmuApi.Pause();
			Stopwatch watch = Stopwatch.StartNew();
			while(!EmuApi.IsPaused() && watch.ElapsedMilliseconds < timeoutMs) {
				Thread.Sleep(5);
			}
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-05: script I/O
		// ---------------------------------------------------------------------------------------------

		/// <returns>How full the busiest script's native log got in this harvest (0..1+), used to adapt the chunk size.</returns>
		private double HarvestScriptLogs(CpuType cpu)
		{
			double fill = 0;
			if(_scriptIds.Count == 0) {
				return fill;
			}
			long frame = GetFrameCount(cpu);
			foreach(KeyValuePair<string, int> script in _scriptIds) {
				string log = DebugApi.GetScriptLog(script.Value) ?? "";
				fill = Math.Max(fill, _logStore.Harvest(script.Value, script.Key, log, frame).Fill);
			}
			AfterHarvest(cpu);
			return fill;
		}

		private void HarvestScript(string name, int scriptId, CpuType cpu)
		{
			string log = DebugApi.GetScriptLog(scriptId) ?? "";
			_logStore.Harvest(scriptId, name, log, GetFrameCount(cpu));
		}

		private int LoadScriptInternal(string name, string code, bool withPrelude, IReadOnlyList<string>? includes, CpuType cpu, out bool replaced)
		{
			replaced = false;
			if(_scriptIds.TryGetValue(name, out int existingId)) {
				HarvestScript(name, existingId, cpu);
				DebugApi.RemoveScript(existingId);
				_logStore.ForgetScript(existingId);
				_scriptIds.Remove(name);
				_scriptLogOffsets.Remove(existingId);
				replaced = true;
			}

			string source = BuildScriptSource(code, withPrelude, includes);
			int id = DebugApi.LoadScript(name, Program.OriginalFolder, source, -1);
			if(id < 0) {
				throw new InvalidOperationException("Failed to load script.");
			}
			_scriptIds[name] = id;
			_scriptLogOffsets[id] = 0;
			_logStore.RegisterScript(id, name);
			return id;
		}

		private McpToolResult ToolLoadScript(JsonElement args)
		{
			string code = GetRequiredString(args, "code");
			string name = GetOptionalString(args, "name") ?? $"dynamic_script_{DateTime.UtcNow.Ticks}";
			bool prelude = GetOptionalBool(args, "prelude") ?? true;
			CpuType cpu = GetPrimaryCpuType();

			long startSeq = _logStore.NextSeq;
			int id = LoadScriptInternal(name, code, prelude, GetStringList(args, "includes"), cpu, out bool replaced);
			HarvestScript(name, id, cpu);

			McpScriptLogQuery initial = _logStore.Query(name, startSeq, 200);
			JsonObject node = new JsonObject() {
				["scriptId"] = id,
				["name"] = name,
				["replaced"] = replaced,
				["initialLog"] = McpScriptLogStore.ToJson(initial)
			};
			return Json(node);
		}

		private McpToolResult ToolListScripts(JsonElement _)
		{
			JsonArray scripts = new JsonArray();
			foreach(KeyValuePair<string, int> script in _scriptIds.OrderBy(s => s.Value)) {
				scripts.Add(new JsonObject() {
					["name"] = script.Key,
					["scriptId"] = script.Value,
					["hasResult"] = _logStore.GetResult(script.Key) != null
				});
			}
			return Json(new JsonObject() { ["scripts"] = scripts, ["nextSeq"] = _logStore.NextSeq });
		}

		private McpToolResult ToolUnloadScript(JsonElement args)
		{
			string name = GetRequiredString(args, "name");
			if(!_scriptIds.TryGetValue(name, out int id)) {
				return ErrorJson("unknown_script", $"No script named '{name}' is loaded.");
			}

			CpuType cpu = GetPrimaryCpuType();
			HarvestScript(name, id, cpu);
			DebugApi.RemoveScript(id);
			_scriptIds.Remove(name);
			_scriptLogOffsets.Remove(id);
			_logStore.ForgetScript(id);
			return Json(new JsonObject() { ["unloaded"] = true, ["name"] = name, ["scriptId"] = id });
		}

		private McpToolResult ToolGetScriptLog(JsonElement args)
		{
			string? name = GetOptionalString(args, "name");
			long sinceSeq = GetOptionalLong(args, "sinceSeq") ?? 0;
			int max = Math.Clamp(GetOptionalInt(args, "max") ?? 500, 1, ScriptLogRingSize);
			bool clear = GetOptionalBool(args, "clear") ?? false;

			HarvestScriptLogs(GetPrimaryCpuType());
			McpScriptLogQuery query = _logStore.Query(name, sinceSeq, max);
			JsonObject node = McpScriptLogStore.ToJson(query);
			if(_logStore.Approximate) {
				node["approximate"] = true;
			}
			if(clear) {
				_logStore.Clear();
			}
			return Json(node);
		}

		private static JsonNode? ParseResult(string? raw)
		{
			if(raw == null) {
				return null;
			}
			try {
				return JsonNode.Parse(raw);
			} catch {
				return JsonValue.Create(raw);
			}
		}

		private McpToolResult ToolGetScriptResult(JsonElement args)
		{
			string name = GetRequiredString(args, "name");
			if(_scriptIds.TryGetValue(name, out int id)) {
				HarvestScript(name, id, GetPrimaryCpuType());
			}
			string? raw = _logStore.GetResult(name);
			return Json(new JsonObject() {
				["name"] = name,
				["loaded"] = _scriptIds.ContainsKey(name),
				["hasResult"] = raw != null,
				["result"] = ParseResult(raw)
			});
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-01: code/data log
		// ---------------------------------------------------------------------------------------------

		private MemoryType ResolveCdlMemoryType(JsonElement args)
		{
			CpuType cpu = GetPrimaryCpuType();
			string name = GetFirstString(args, "type", "memoryType") ?? "prgRom";
			return ResolveMemoryType(name, cpu);
		}

		private static byte[] CdlToBytes(CdlFlags[] flags)
		{
			byte[] bytes = new byte[flags.Length];
			for(int i = 0; i < flags.Length; i++) {
				bytes[i] = (byte)flags[i];
			}
			return bytes;
		}

		private static JsonObject CdlStatsNode(CdlStatistics stats)
		{
			return new JsonObject() {
				["codeBytes"] = Num((long)stats.CodeBytes),
				["dataBytes"] = Num((long)stats.DataBytes),
				["totalBytes"] = Num((long)stats.TotalBytes),
				["jumpTargetCount"] = Num((long)stats.JumpTargetCount),
				["functionCount"] = Num((long)stats.FunctionCount)
			};
		}

		private McpToolResult ToolGetCdlData(JsonElement args)
		{
			MemoryType memType = ResolveCdlMemoryType(args);
			string format = (GetOptionalString(args, "format") ?? "summary").ToLowerInvariant();
			int size = DebugApi.GetMemorySize(memType);
			int start = Math.Max(0, GetFirstInt(args, "start", "offset") ?? 0);
			if(start >= size) {
				throw new InvalidOperationException($"start {start} is outside {memType} (size {size}).");
			}

			int length = GetOptionalInt(args, "length") ?? (size - start);
			length = Math.Clamp(length, 1, size - start);
			bool truncated = false;
			if(format == "bytes_base64" && length > MaxCdlBase64Bytes) {
				length = MaxCdlBase64Bytes;
				truncated = true;
			}

			byte[] flags = CdlToBytes(DebugApi.GetCdlData((uint)start, (uint)length, memType));
			JsonObject node = new JsonObject() {
				["memoryType"] = memType.ToString(),
				["format"] = format,
				["start"] = start,
				["length"] = flags.Length,
				["memorySize"] = size
			};

			bool isNesChr = memType == MemoryType.NesChrRom || memType == MemoryType.NesChrRam;

			switch(format) {
				case "summary": {
					long code = 0, data = 0, both = 0, jump = 0, sub = 0, idx8 = 0, mem8 = 0, drawn = 0, unreached = 0;
					foreach(byte f in flags) {
						if(f == 0) unreached++;
						if(isNesChr) {
							if((f & 0x01) != 0) drawn++;
							continue;
						}
						bool isCode = (f & (byte)CdlFlags.Code) != 0;
						bool isData = (f & (byte)CdlFlags.Data) != 0;
						if(isCode) code++;
						if(isData) data++;
						if(isCode && isData) both++;
						if((f & (byte)CdlFlags.JumpTarget) != 0) jump++;
						if((f & (byte)CdlFlags.SubEntryPoint) != 0) sub++;
						if((f & (byte)CdlFlags.IndexMode8) != 0) idx8++;
						if((f & (byte)CdlFlags.MemoryMode8) != 0) mem8++;
					}
					JsonObject counts = new JsonObject() {
						["total"] = flags.Length,
						["unreached"] = Num(unreached)
					};
					if(isNesChr) {
						counts["drawn"] = Num(drawn);
					} else {
						counts["code"] = Num(code);
						counts["data"] = Num(data);
						counts["codeAndData"] = Num(both);
						counts["jumpTarget"] = Num(jump);
						counts["subEntryPoint"] = Num(sub);
						counts["indexMode8"] = Num(idx8);
						counts["memoryMode8"] = Num(mem8);
					}
					node["counts"] = counts;
					node["statistics"] = CdlStatsNode(DebugApi.GetCdlStatistics(memType));
					break;
				}

				case "ranges": {
					int mask = (GetOptionalInt(args, "flagMask") ?? 0x03) & 0xFF;
					bool includeUnreached = GetOptionalBool(args, "includeUnreached") ?? false;
					int maxRows = Math.Clamp(GetOptionalInt(args, "maxRows") ?? 20000, 1, 200000);

					JsonArray rows = new JsonArray();
					long codeBytes = 0;
					foreach(byte f in flags) {
						if(!isNesChr && (f & (byte)CdlFlags.Code) != 0) codeBytes++;
					}

					int runStart = 0;
					int? nextStart = null;
					for(int i = 1; i <= flags.Length; i++) {
						if(i < flags.Length && (flags[i] & mask) == (flags[runStart] & mask)) {
							continue;
						}
						int value = flags[runStart] & mask;
						if(value != 0 || includeUnreached) {
							if(rows.Count >= maxRows) {
								nextStart = start + runStart;
								break;
							}
							rows.Add(new JsonArray(
								JsonValue.Create(start + runStart),
								JsonValue.Create(start + i - 1),
								JsonValue.Create(value)));
						}
						runStart = i;
					}

					node["flagMask"] = mask;
					node["rowFormat"] = "[start,end,flags] with end inclusive; offsets are absolute in the memory type";
					node["rows"] = rows;
					node["rowCount"] = rows.Count;
					node["codeBytesInWindow"] = Num(codeBytes);
					node["truncated"] = nextStart != null;
					if(nextStart != null) {
						node["nextStart"] = nextStart.Value;
					}
					break;
				}

				case "bytes_base64":
					node["data"] = Convert.ToBase64String(flags);
					node["truncated"] = truncated;
					break;

				default:
					throw new InvalidOperationException("format must be summary, ranges or bytes_base64.");
			}

			return Json(node);
		}

		private McpToolResult ToolResetCdl(JsonElement args)
		{
			MemoryType memType = ResolveCdlMemoryType(args);
			DebugApi.ResetCdl(memType);
			return Json(new JsonObject() {
				["reset"] = true,
				["memoryType"] = memType.ToString(),
				["statistics"] = CdlStatsNode(DebugApi.GetCdlStatistics(memType))
			});
		}

		private McpToolResult ToolSaveCdl(JsonElement args)
		{
			MemoryType memType = ResolveCdlMemoryType(args);
			string path = ResolveOutputPath(GetRequiredString(args, "path"));
			string? folder = Path.GetDirectoryName(path);
			if(!string.IsNullOrWhiteSpace(folder)) {
				Directory.CreateDirectory(folder);
			}
			if(File.Exists(path)) {
				File.Delete(path);
			}

			DebugApi.SaveCdlFile(memType, path);
			if(!File.Exists(path)) {
				throw new InvalidOperationException($"CDL file was not written: {path}");
			}

			return Json(new JsonObject() {
				["saved"] = true,
				["path"] = path,
				["memoryType"] = memType.ToString(),
				["sizeBytes"] = Num(new FileInfo(path).Length),
				["sha1"] = McpRomIdentity.Sha1OfFile(path),
				["statistics"] = CdlStatsNode(DebugApi.GetCdlStatistics(memType))
			});
		}

		private McpToolResult ToolLoadCdl(JsonElement args)
		{
			MemoryType memType = ResolveCdlMemoryType(args);
			string path = ResolveOutputPath(GetRequiredString(args, "path"));
			bool merge = GetOptionalBool(args, "merge") ?? false;
			if(!File.Exists(path)) {
				throw new FileNotFoundException("CDL file not found.", path);
			}

			int size = DebugApi.GetMemorySize(memType);
			byte[]? before = merge ? CdlToBytes(DebugApi.GetCdlData(0, (uint)size, memType)) : null;

			DebugApi.LoadCdlFile(memType, path);

			if(before != null) {
				// The native loader replaces the log. Restore the union of the code/data bits
				// (MarkBytesAs rewrites bits 0-1 of each byte and keeps the others).
				byte[] loaded = CdlToBytes(DebugApi.GetCdlData(0, (uint)size, memType));
				int i = 0;
				while(i < size) {
					byte union = (byte)((loaded[i] | before[i]) & 0x03);
					if((loaded[i] & 0x03) == union) {
						i++;
						continue;
					}

					int end = i;
					while(end + 1 < size &&
						(byte)((loaded[end + 1] | before[end + 1]) & 0x03) == union &&
						(loaded[end + 1] & 0x03) != union) {
						end++;
					}
					DebugApi.MarkBytesAs(memType, (uint)i, (uint)end, (CdlFlags)union);
					i = end + 1;
				}
			}

			return Json(new JsonObject() {
				["loaded"] = true,
				["path"] = path,
				["merged"] = merge,
				["memoryType"] = memType.ToString(),
				["statistics"] = CdlStatsNode(DebugApi.GetCdlStatistics(memType))
			});
		}
	}

	/// <summary>An expected tool failure with a machine-readable code (reported as a JSON error result).</summary>
	internal sealed class McpToolException : Exception
	{
		public string Code { get; }
		public JsonObject? Details { get; }

		public McpToolException(string code, string message, JsonObject? details = null) : base(message)
		{
			Code = code;
			Details = details;
		}
	}
#pragma warning restore IL2090
#pragma warning restore IL2067
#pragma warning restore IL2070
#pragma warning restore IL3050
#pragma warning restore IL2026
}
