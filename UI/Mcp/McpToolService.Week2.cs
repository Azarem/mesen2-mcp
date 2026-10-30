using Mesen.Config;
using Mesen.Debugger;
using Mesen.Debugger.Labels;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mesen.Mcp
{
#pragma warning disable IL2026
#pragma warning disable IL3050
#pragma warning disable IL2070
#pragma warning disable IL2067
#pragma warning disable IL2090
	/// <summary>
	/// Week 2 of mesen2-mcp-enhancement-requests.md:
	/// MCP-13 labels and address conversion, MCP-15 Lua kit and recipes, MCP-10 turbo and run_until,
	/// MCP-08 sandboxed result files.
	/// </summary>
	internal sealed partial class McpToolService
	{
		private const int TempBreakpointId = 100000;

		private List<InteropBreakpoint> _interopBps = new List<InteropBreakpoint>();
		private string? _resultsDir;
		private List<string> _libDirs = new List<string>();
		private bool _libDirResolved;
		private readonly Dictionary<string, long> _filesWritten = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		private static readonly Regex ModuleNameRegex = new Regex("^[A-Za-z0-9_\\-]+$", RegexOptions.Compiled);

		private static JsonObject StringArraySchema(string description) => new JsonObject() {
			["type"] = "array",
			["items"] = new JsonObject() { ["type"] = "string" },
			["description"] = description
		};

		private static JsonObject AnyObjectSchema(string description) => new JsonObject() { ["type"] = "object", ["description"] = description };

		private static List<string>? GetStringList(JsonElement args, string name)
		{
			if(!TryGetProperty(args, name, out JsonElement value) || value.ValueKind != JsonValueKind.Array) {
				return null;
			}
			List<string> list = new List<string>();
			foreach(JsonElement item in value.EnumerateArray()) {
				if(item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())) {
					list.Add(item.GetString()!);
				}
			}
			return list;
		}

		private void AddWeek2Tools(Dictionary<string, McpToolDefinition> tools)
		{
			_logStore.OnWriteRow = HandleWriteRow;

			// --- MCP-13 --------------------------------------------------------------------------
			AddTool(tools, "load_labels",
				"Import labels from an .mlb (Mesen label file), .sym (BB:AAAA name per line) or .json file ([{name, cpu|prgOffset|offset|wram, length?, comment?}] or {labels:[...]}). " +
				"Imported labels appear in disassembly, call stacks, resolve_address and get_label_address. Names that are not valid label identifiers are sanitized.",
				ObjectSchema(
					("path", StringSchema("Label file path"), true),
					("format", StringSchema("mlb | sym | json (default: from the file extension)"), false),
					("replace", BooleanSchema("Remove all existing labels first (default false)"), false)
				), ToolLoadLabels);

			AddTool(tools, "list_labels", "List loaded labels (optionally filtered by a case-insensitive substring of the name).",
				ObjectSchema(
					("filter", StringSchema("Name substring"), false),
					("max", IntegerSchema("Maximum rows (default 200, max 5000)"), false)
				), ToolListLabels);

			AddTool(tools, "resolve_address",
				"Convert between CPU addresses, PRG ROM offsets, WRAM offsets and labels. Give one of cpu (number or \"BB:AAAA\"), prgOffset (alias offset), wram, or label. " +
				"Returns {cpu, prgOffset, wram, label, labelOffset, exact, bankAlias}: label is the label covering the address, or else the nearest preceding label (labelOffset = distance in bytes).",
				ObjectSchema(
					("cpu", new JsonObject() { ["description"] = "CPU address: number or \"BB:AAAA\" string" }, false),
					("prgOffset", IntegerSchema("PRG ROM file offset (without copier header)"), false),
					("offset", IntegerSchema("Alias of prgOffset"), false),
					("wram", IntegerSchema("Work RAM offset"), false),
					("label", StringSchema("Label name"), false)
				), ToolResolveAddress);

			AddTool(tools, "resolve_addresses",
				"Bulk resolve_address. addresses is a list of CPU addresses (numbers or \"BB:AAAA\" strings) or objects {cpu|prgOffset|wram|label}.",
				ObjectSchema(("addresses", new JsonObject() { ["type"] = "array", ["description"] = "Addresses to resolve" }, true)),
				ToolResolveAddresses);

			AddTool(tools, "get_callstack", "Get the current call stack, with label names when labels are loaded.",
				ObjectSchema(("cpuType", StringSchema("Optional CPU type"), false)), ToolGetCallstackLabeled);

			AddTool(tools, "get_execution_trace", "Get execution trace rows, with the label of each program counter when labels are loaded.",
				ObjectSchema(
					("startOffset", IntegerSchema("Trace start offset"), false),
					("maxRowCount", IntegerSchema("Max rows"), false)
				), ToolGetExecutionTraceLabeled);

			// --- MCP-15 --------------------------------------------------------------------------
			AddTool(tools, "list_recipes",
				"List the Lua kit modules and ready-made recipes shipped with the server (the lib folder), with their arguments.",
				EmptySchema(), ToolListRecipes);

			AddTool(tools, "run_recipe",
				"Run a shipped recipe (see list_recipes) like run_script: pause, load kit and recipe, advance frames (stops early when the recipe calls emu.mcp.done()), return log and result, unload.",
				ObjectSchema(
					("name", StringSchema("Recipe name"), true),
					("args", AnyObjectSchema("Recipe arguments (available to the recipe as the Lua table 'args')"), false),
					("frames", IntegerSchema("Frames to advance at most (default: the recipe's own default)"), false),
					("timeoutMs", IntegerSchema("Give up after this many milliseconds"), false),
					("turbo", BooleanSchema("Run without the frame limiter"), false),
					("unloadAfter", BooleanSchema("Unload the script afterwards (default true)"), false),
					("fromReset", StringSchema("Start from a reset: power (cold boot, RAM re-initialized, frame counter 0; true also means power) or soft (reset button, WRAM kept). The script is loaded before the first boot instruction, so it sees all of the boot code. A power cycle unloads other scripts."), false),
					("maxLog", IntegerSchema("Maximum log rows returned (default 500)"), false)
				), ToolRunRecipe);

			AddTool(tools, "run_script",
				"Atomic experiment: pause, load a script (with optional kit includes), advance frames, collect log and result, unload the script. " +
				"Stops early when the script calls emu.mcp.done(). Returns stopReason (done|script_done|breakpoint|timeout), framesAdvanced, the log rows, the emu.mcp.result value, any files written with emu.mcp.writeFile (filesWritten) and a writes report (bytesQueued/bytesWritten/complete/warnings) plus logTruncated when output was lost. Large writeFile data is flushed a few KB per frame and the run continues until it has all arrived (within timeoutMs).",
				ObjectSchema(
					("code", StringSchema("Lua source code"), true),
					("name", StringSchema("Script name (default run_script)"), false),
					("frames", IntegerSchema("Frames to advance after loading (0-200000, default 60)"), false),
					("timeoutMs", IntegerSchema("Give up after this many milliseconds"), false),
					("turbo", BooleanSchema("Run without the frame limiter"), false),
					("unloadAfter", BooleanSchema("Unload the script afterwards (default true)"), false),
					("maxLog", IntegerSchema("Maximum log rows returned (default 500)"), false),
					("fromReset", StringSchema("Start from a reset: power (cold boot, RAM re-initialized, frame counter 0; true also means power) or soft (reset button, WRAM kept). The script is loaded before the first boot instruction, so it sees all of the boot code. A power cycle unloads other scripts."), false),
					("includes", StringArraySchema("Kit modules to load first, for example [\"mesen_kit\"]"), false)
				), ToolRunScript);

			// --- MCP-10 --------------------------------------------------------------------------
			AddTool(tools, "run_until",
				"Run until a condition holds, a breakpoint hits, maxFrames pass or timeoutMs elapses. condition is one of: " +
				"{type:\"memoryEquals\", memType?, address, size?(1-4, little endian), value, mask?} checked at the end of every frame (memType default workRam); " +
				"{type:\"pcEquals\", pc} (number or \"BB:AAAA\") using a temporary exec breakpoint; " +
				"{type:\"lua\", expr, includes?} (includes may also be given as a top-level argument) a Lua expression evaluated at the end of every frame (kit helpers available when included); " +
				"{type:\"breakpoint\"} runs until a configured breakpoint hits. " +
				"Returns stopReason (condition|breakpoint|done=maxFrames reached|timeout|error), met, framesAdvanced, frame, framesPerSecond and the CPU registers.",
				ObjectSchema(
					("condition", AnyObjectSchema("Stop condition, see description"), true),
					("maxFrames", IntegerSchema("Give up after this many frames (default 600)"), false),
					("timeoutMs", IntegerSchema("Give up after this many milliseconds"), false),
					("turbo", BooleanSchema("Run without the frame limiter"), false),
					("includes", StringArraySchema("Kit modules for a lua condition (same as condition.includes)"), false)
				), ToolRunUntil);
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-10: turbo
		// ---------------------------------------------------------------------------------------------

		private bool ResolveTurbo(JsonElement args)
		{
			return GetOptionalBool(args, "turbo") ?? (_options.NoVideo && _options.NoAudio);
		}

		private static int DefaultTimeoutMs(long frames, bool turbo)
		{
			long ms = turbo ? 30000 + frames * 20 : Math.Max(10000, frames * 500);
			return (int)Math.Min(ms, int.MaxValue / 2);
		}

		private sealed class TurboScope : IDisposable
		{
			private readonly uint? _previous;

			public TurboScope(bool enable)
			{
				EmulationConfig config = ConfigManager.Config.Emulation;
				if(enable && config.EmulationSpeed != 0) {
					_previous = config.EmulationSpeed;
					config.EmulationSpeed = 0;
					config.ApplyConfig();
				}
			}

			public void Dispose()
			{
				if(_previous != null) {
					EmulationConfig config = ConfigManager.Config.Emulation;
					config.EmulationSpeed = _previous.Value;
					config.ApplyConfig();
				}
			}
		}

		private static IDisposable BeginTurbo(bool enable) => new TurboScope(enable);

		private sealed class TempBreakpointScope : IDisposable
		{
			private readonly McpToolService _owner;

			public TempBreakpointScope(McpToolService owner, InteropBreakpoint temp)
			{
				_owner = owner;
				List<InteropBreakpoint> list = new List<InteropBreakpoint>(owner._interopBps) { temp };
				DebugApi.SetBreakpoints(list.ToArray(), (uint)list.Count);
			}

			public void Dispose()
			{
				DebugApi.SetBreakpoints(_owner._interopBps.ToArray(), (uint)_owner._interopBps.Count);
			}
		}

		private McpToolResult ToolRunUntil(JsonElement args)
		{
			if(!TryGetProperty(args, "condition", out JsonElement condition) || condition.ValueKind != JsonValueKind.Object) {
				throw new InvalidOperationException("condition object is required.");
			}
			string type = (GetOptionalString(condition, "type") ?? "").ToLowerInvariant();
			int maxFrames = Math.Clamp(GetOptionalInt(args, "maxFrames") ?? 600, 1, MaxFramesPerCall);
			bool turbo = ResolveTurbo(args);
			int timeoutMs = GetOptionalInt(args, "timeoutMs") ?? DefaultTimeoutMs(maxFrames, turbo);
			CpuType cpu = GetPrimaryCpuType();

			EnsurePaused(2000);

			StepOutcome outcome;
			string? luaExpr = null;
			TempBreakpointScope? tempBreakpoint = null;
			string scriptName = "__run_until";

			try {
				switch(type) {
					case "breakpoint":
						break;

					case "pceq":
					case "pcequals": {
						if(!TryGetProperty(condition, "pc", out JsonElement pcElement)) {
							throw new InvalidOperationException("condition.pc is required for pcEquals.");
						}
						int pc = ParseCpuAddress(pcElement);
						tempBreakpoint = new TempBreakpointScope(this, new InteropBreakpoint() {
							Id = TempBreakpointId,
							CpuType = cpu,
							MemoryType = ResolveMemoryType("cpuDebug", cpu),
							Type = BreakpointTypeFlags.Execute,
							StartAddress = pc,
							EndAddress = pc,
							Enabled = true,
							MarkEvent = false,
							IgnoreDummyOperations = true,
							Condition = new byte[1000]
						});
						break;
					}

					case "memoryequals": {
						string memName = GetOptionalString(condition, "memType") ?? "workRam";
						MemoryType memType = ResolveMemoryType(memName, cpu);
						string luaMem = "emu.memType." + char.ToLowerInvariant(memType.ToString()[0]) + memType.ToString().Substring(1);
						long address = GetOptionalLong(condition, "address") ?? throw new InvalidOperationException("condition.address is required.");
						int size = Math.Clamp(GetOptionalInt(condition, "size") ?? 1, 1, 4);
						long value = GetOptionalLong(condition, "value") ?? throw new InvalidOperationException("condition.value is required.");
						long defaultMask = size == 4 ? 0xFFFFFFFFL : (1L << (size * 8)) - 1;
						long mask = GetOptionalLong(condition, "mask") ?? defaultMask;
						luaExpr = $"(function() local v = 0 for i = {size - 1}, 0, -1 do v = (v << 8) | emu.read({address} + i, {luaMem}) end return (v & {mask}) == ({value} & {mask}) end)()";
						break;
					}

					case "lua": {
						luaExpr = GetOptionalString(condition, "expr") ?? throw new InvalidOperationException("condition.expr is required for lua conditions.");
						break;
					}

					default:
						throw new InvalidOperationException("condition.type must be memoryEquals, pcEquals, lua or breakpoint.");
				}

				using(BeginTurbo(turbo)) {
					if(luaExpr != null) {
						string code =
							"local fired = false emu.addEventCallback(function() if fired then return end " +
							"local ok, met = pcall(function() return " + luaExpr + " end) " +
							"if not ok then fired = true emu.mcp.result({met = false, error = tostring(met)}) emu.mcp.done() return end " +
							"if met then fired = true emu.mcp.result({met = true}) emu.mcp.done() end end, emu.eventType.endFrame)";
						LoadScriptInternal(scriptName, code, true, GetStringList(args, "includes") ?? GetStringList(condition, "includes"), cpu, out _);
						HarvestScriptLogs(cpu);
						outcome = RunFrames(cpu, maxFrames, timeoutMs, () => _logStore.IsDone(scriptName));
					} else {
						outcome = RunFrames(cpu, maxFrames, timeoutMs);
					}
				}
			} finally {
				tempBreakpoint?.Dispose();
			}

			bool met = false;
			JsonNode? scriptResult = null;
			if(luaExpr != null) {
				if(_scriptIds.TryGetValue(scriptName, out int id)) {
					HarvestScript(scriptName, id, cpu);
					DebugApi.RemoveScript(id);
					_scriptIds.Remove(scriptName);
					_scriptLogOffsets.Remove(id);
					_logStore.ForgetScript(id);
				}
				scriptResult = ParseResult(_logStore.GetResult(scriptName));
				met = scriptResult?["met"]?.GetValue<bool>() ?? false;
				if(outcome.StopReason == "script_done") {
					outcome.StopReason = met ? "condition" : "error";
				}
			} else if(type != "breakpoint" && outcome.HasBreak && outcome.StopReason == "breakpoint" && outcome.Break.BreakpointId == TempBreakpointId) {
				met = true;
				outcome.StopReason = "condition";
			}

			JsonObject node = BuildStepResponse(cpu, outcome);
			node["met"] = met;
			if(node["break"] is JsonObject breakNode && outcome.Break.BreakpointId == TempBreakpointId) {
				breakNode.Remove("breakpointId");
			}
			if(scriptResult != null && !met) {
				node["scriptResult"] = scriptResult;
			}
			return Json(node, outcome.StopReason == "timeout" || outcome.StopReason == "error");
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-08: sandboxed result files
		// ---------------------------------------------------------------------------------------------

		private string ResultsDir {
			get {
				if(_resultsDir == null) {
					string dir = _options.ResultsDir ?? DefaultResultsDir();
					_resultsDir = Path.GetFullPath(dir);
				}
				return _resultsDir;
			}
		}

		/// <summary>
		/// Default results folder: per-user local app data (never a cloud-synced Documents folder, which made result files
		/// disappear under OneDrive redirection).
		/// </summary>
		private static string DefaultResultsDir()
		{
			string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			if(string.IsNullOrWhiteSpace(baseDir)) {
				baseDir = Path.GetTempPath();
			}
			return Path.Combine(baseDir, "Mesen2Mcp", "results");
		}

		private static bool LooksCloudSynced(string path)
		{
			return path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ||
				path.Contains("Dropbox", StringComparison.OrdinalIgnoreCase) ||
				path.Contains("iCloud", StringComparison.OrdinalIgnoreCase) ||
				path.Contains("Google Drive", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>Resolves a relative name inside the results folder; rejects anything that could leave it.</summary>
		private string ResolveResultPath(string relativeName)
		{
			if(string.IsNullOrWhiteSpace(relativeName) ||
				relativeName.Contains("..", StringComparison.Ordinal) ||
				Path.IsPathRooted(relativeName) ||
				relativeName.StartsWith("/", StringComparison.Ordinal) || relativeName.StartsWith("\\", StringComparison.Ordinal) ||
				(relativeName.Length >= 2 && relativeName[1] == ':') ||
				relativeName.IndexOfAny(Path.GetInvalidFileNameChars().Where(c => c != '/' && c != '\\').ToArray()) >= 0) {
				throw new McpToolException("invalid_path", $"'{relativeName}' is not a valid result file name (no '..', absolute paths or drive letters).");
			}

			string root = ResultsDir;
			string full = Path.GetFullPath(Path.Combine(root, relativeName));
			string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
			if(!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
				throw new McpToolException("invalid_path", $"'{relativeName}' resolves outside the results folder.");
			}
			return full;
		}

		/// <summary>Absolute paths are used as given; relative paths go to the results folder.</summary>
		private string ResolveOutputPath(string path)
		{
			return Path.IsPathRooted(path) ? Path.GetFullPath(path) : ResolveResultPath(path);
		}

		private void HandleWriteRow(string script, string json)
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			string name = doc.RootElement.GetProperty("name").GetString() ?? "";
			bool append = doc.RootElement.GetProperty("append").GetBoolean();
			string text = doc.RootElement.GetProperty("text").GetString() ?? "";

			string full = ResolveResultPath(name);
			Directory.CreateDirectory(Path.GetDirectoryName(full)!);
			byte[] bytes = Encoding.Latin1.GetBytes(text);
			using(FileStream stream = new FileStream(full, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read)) {
				stream.Write(bytes, 0, bytes.Length);
			}
			_filesWritten[name] = new FileInfo(full).Length;
			_logStore.NoteWriteReceived(script, bytes.Length);
		}

		/// <summary>
		/// A script flushes emu.mcp.writeFile data a few KB per frame (the native log keeps the last 500 rows between harvests).
		/// After the requested frames, keep running until everything has arrived, within the time budget.
		/// </summary>
		private void DrainScriptWrites(string name, int scriptId, CpuType cpu, StepOutcome outcome, int budgetMs)
		{
			Stopwatch watch = Stopwatch.StartNew();
			int rounds = 0;
			while(_logStore.PendingWriteBytes(name) > 0 && watch.ElapsedMilliseconds < budgetMs && rounds++ < 200000) {
				StepOutcome part = RunFrames(cpu, 5, Math.Max(1000, budgetMs - (int)watch.ElapsedMilliseconds));
				HarvestScript(name, scriptId, cpu);
				outcome.FramesAdvanced += part.FramesAdvanced;
				outcome.ElapsedMs += part.ElapsedMs;
				if(part.StopReason != "done") {
					outcome.StopReason = part.StopReason;
					break;
				}
			}
		}

		/// <summary>The writeFile accounting and overflow warnings of one script, for the run_script reply.</summary>
		private JsonObject? BuildWriteReport(string name)
		{
			McpScriptLogStore.WriteState state = _logStore.GetWriteState(name);
			if(state.Calls == 0 && state.PendingRowBytes == 0 && !state.LogFull && !state.RowsDropped && state.CutRows == 0) {
				return null;
			}
			JsonArray warnings = new JsonArray();
			if(state.CutRows > 0) {
				warnings.Add(JsonValue.Create($"{state.CutRows} writeFile row(s) were cut off by a full native log buffer; the affected files are incomplete."));
			}
			if(state.PendingRowBytes > 0) {
				warnings.Add(JsonValue.Create($"{state.PendingRowBytes} bytes of writeFile data were still queued in the script when the run ended (they flush a few KB per frame): run more frames, or raise timeoutMs."));
			} else if(state.MissingBytes > 0) {
				warnings.Add(JsonValue.Create($"{state.MissingBytes} bytes passed to emu.mcp.writeFile never arrived (native log overflow)."));
			}
			if(state.LogFull) {
				warnings.Add(JsonValue.Create("The native script log buffer (4 MB) was full at a harvest, so log rows may be missing. Log less per frame (compact lines)."));
			}
			if(state.RowsDropped) {
				warnings.Add(JsonValue.Create("The script logged 500 or more rows between two harvests (the native log keeps only the last 500 rows), so the oldest rows were dropped. Log fewer, longer lines, or use emu.mcp.result / emu.mcp.writeFile for bulk data."));
			}
			return new JsonObject() {
				["writeCalls"] = Num(state.Calls),
				["bytesQueued"] = Num(state.QueuedBytes),
				["bytesWritten"] = Num(state.ReceivedBytes),
				["bytesPending"] = Num(state.PendingRowBytes),
				["complete"] = !state.Truncated && state.PendingRowBytes == 0 && state.MissingBytes == 0,
				["warnings"] = warnings
			};
		}

		/// <summary>Flags the files of a script whose data was cut off or is still queued (MCP-43): truncated=true, with the bytes kept.</summary>
		private void MarkTruncatedFiles(JsonArray files, string script)
		{
			McpScriptLogStore.WriteState state = _logStore.GetWriteState(script);
			HashSet<string> flagged = new HashSet<string>(state.CutFiles, StringComparer.OrdinalIgnoreCase);
			if(state.PendingRowBytes > 0) {
				flagged.UnionWith(state.PendingFiles);
			}
			foreach(string file in flagged) {
				JsonObject? entry = files.OfType<JsonObject>().FirstOrDefault(f => string.Equals(f["name"]?.GetValue<string>(), file, StringComparison.OrdinalIgnoreCase));
				if(entry == null) {
					string full;
					try {
						full = ResolveResultPath(file);
					} catch(Exception) {
						continue;
					}
					entry = new JsonObject() {
						["name"] = file,
						["path"] = full,
						["bytes"] = Num(File.Exists(full) ? new FileInfo(full).Length : 0),
						["sha1"] = File.Exists(full) ? McpRomIdentity.Sha1OfFile(full) : null
					};
					files.Add((JsonNode)entry);
				}
				entry["truncated"] = true;
			}
		}

		private JsonArray BuildFilesWritten(Dictionary<string, long> before)
		{
			JsonArray files = new JsonArray();
			foreach(KeyValuePair<string, long> file in _filesWritten.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)) {
				if(before.TryGetValue(file.Key, out long oldSize) && oldSize == file.Value) {
					continue;
				}
				string full = ResolveResultPath(file.Key);
				files.Add((JsonNode)new JsonObject() {
					["name"] = file.Key,
					["path"] = full,
					["bytes"] = Num(file.Value),
					["sha1"] = File.Exists(full) ? McpRomIdentity.Sha1OfFile(full) : null
				});
			}
			return files;
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-15: kit modules, recipes
		// ---------------------------------------------------------------------------------------------

		/// <summary>All lib folders in search order: the server's own first, then the --mcp-lib-dir folders.</summary>
		private IReadOnlyList<string> LibDirs {
			get {
				if(!_libDirResolved) {
					_libDirResolved = true;
					_libDirs = FindLibDirs();
				}
				return _libDirs;
			}
		}

		/// <summary>First lib folder (kept for the legacy single-folder fields).</summary>
		private string? LibDir => LibDirs.Count > 0 ? LibDirs[0] : null;

		private List<string> FindLibDirs()
		{
			List<string> result = new List<string>();
			void Add(string path)
			{
				string full = Path.GetFullPath(path);
				if(Directory.Exists(full) && !result.Contains(full, StringComparer.OrdinalIgnoreCase)) {
					result.Add(full);
				}
			}

			//The server's own folder: shipped next to the host or in a source checkout.
			List<string> builtIn = new List<string>();
			builtIn.Add(Path.Combine(AppContext.BaseDirectory, "lua", "lib"));
			string? dir = AppContext.BaseDirectory;
			for(int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++) {
				builtIn.Add(Path.Combine(dir, "tools", "lua", "lib"));
				dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
			}
			builtIn.Add(Path.Combine(Program.OriginalFolder, "tools", "lua", "lib"));
			string? own = builtIn.FirstOrDefault(Directory.Exists);
			if(own != null) {
				Add(own);
			}

			//Project folders (--mcp-lib-dir) come after, so a project cannot shadow the shipped kit by accident.
			foreach(string extra in _options.LibDirs) {
				Add(extra);
			}
			return result;
		}

		/// <summary>Finds a file inside the lib folders (first match wins). subFolder is "" for kit modules and "recipes" for recipes.</summary>
		private string? FindLibFile(string subFolder, string name)
		{
			foreach(string lib in LibDirs) {
				string path = Path.Combine(lib, subFolder, name + ".lua");
				if(File.Exists(path)) {
					return path;
				}
			}
			return null;
		}

		private static string LuaQuote(string text)
		{
			StringBuilder sb = new StringBuilder(text.Length + 16);
			sb.Append('"');
			foreach(char c in text) {
				switch(c) {
					case '\\': sb.Append("\\\\"); break;
					case '"': sb.Append("\\\""); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': break;
					case '\t': sb.Append("\\t"); break;
					default:
						if(c < 0x20) {
							sb.Append("\\").Append(((int)c).ToString(CultureInfo.InvariantCulture).PadLeft(3, '0'));
						} else {
							sb.Append(c);
						}
						break;
				}
			}
			sb.Append('"');
			return sb.ToString();
		}

		private static Dictionary<string, string> ParseLuaHeader(string source)
		{
			Dictionary<string, string> header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach(string rawLine in source.Replace("\r\n", "\n").Split('\n')) {
				string line = rawLine.Trim();
				if(line.Length == 0) {
					continue;
				}
				if(!line.StartsWith("--", StringComparison.Ordinal)) {
					break;
				}
				Match m = Regex.Match(line, @"^--\s*([A-Za-z]+)\s*:\s*(.*)$");
				if(m.Success) {
					header[m.Groups[1].Value] = m.Groups[2].Value.Trim();
				}
			}
			return header;
		}

		private static List<string> SplitList(string? value)
		{
			return string.IsNullOrWhiteSpace(value)
				? new List<string>()
				: value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
		}

		private string ReadModuleSource(string module)
		{
			if(!ModuleNameRegex.IsMatch(module)) {
				throw new McpToolException("invalid_include", $"'{module}' is not a valid kit module name.");
			}
			if(LibDirs.Count == 0) {
				throw new McpToolException("no_lib_dir", "No Lua lib folder was found. Ship tools/lua/lib next to the host or start the server with --mcp-lib-dir=<folder>[;<folder>].");
			}
			string? path = FindLibFile("", module);
			if(path == null) {
				throw new McpToolException("unknown_include", $"Kit module '{module}' was not found in: {string.Join("; ", LibDirs)}.");
			}
			return File.ReadAllText(path);
		}

		private void CollectModules(string module, List<string> ordered, HashSet<string> visiting)
		{
			if(ordered.Contains(module, StringComparer.OrdinalIgnoreCase)) {
				return;
			}
			if(!visiting.Add(module)) {
				throw new McpToolException("include_cycle", $"Kit module '{module}' requires itself.");
			}
			foreach(string dependency in SplitList(ParseLuaHeader(ReadModuleSource(module)).GetValueOrDefault("requires"))) {
				CollectModules(dependency, ordered, visiting);
			}
			visiting.Remove(module);
			ordered.Add(module);
		}

		/// <summary>
		/// Builds the Lua source: prelude, kit modules (each loaded through load() from an escaped one-line string,
		/// so user code keeps its line numbers), then the user code.
		/// </summary>
		private string BuildScriptSource(string code, bool withPrelude, IReadOnlyList<string>? includes)
		{
			StringBuilder sb = new StringBuilder();
			if(withPrelude) {
				sb.Append(McpScriptLogStore.LuaPrelude);
			}

			if(includes != null && includes.Count > 0) {
				List<string> ordered = new List<string>();
				foreach(string include in includes) {
					CollectModules(include, ordered, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
				}
				foreach(string module in ordered) {
					sb.Append("do local f, e = load(").Append(LuaQuote(ReadModuleSource(module))).Append(", \"=").Append(module).Append("\") ");
					sb.Append("if f then local ok, err = pcall(f) if not ok then emu.log(\"[kit] ").Append(module).Append(": \" .. tostring(err)) end ");
					sb.Append("else emu.log(\"[kit] ").Append(module).Append(": \" .. tostring(e)) end end ");
				}
			}

			sb.Append(code);
			return sb.ToString();
		}

		private static string ToLuaLiteral(JsonElement element)
		{
			switch(element.ValueKind) {
				case JsonValueKind.Object: {
					StringBuilder sb = new StringBuilder("{");
					bool first = true;
					foreach(JsonProperty p in element.EnumerateObject()) {
						if(p.Value.ValueKind == JsonValueKind.Null) {
							continue;
						}
						if(!first) sb.Append(',');
						first = false;
						sb.Append('[').Append(LuaQuote(p.Name)).Append("]=").Append(ToLuaLiteral(p.Value));
					}
					return sb.Append('}').ToString();
				}
				case JsonValueKind.Array: {
					StringBuilder sb = new StringBuilder("{");
					bool first = true;
					foreach(JsonElement item in element.EnumerateArray()) {
						if(!first) sb.Append(',');
						first = false;
						sb.Append(item.ValueKind == JsonValueKind.Null ? "false" : ToLuaLiteral(item));
					}
					return sb.Append('}').ToString();
				}
				case JsonValueKind.String: return LuaQuote(element.GetString() ?? "");
				case JsonValueKind.Number: return element.GetRawText();
				case JsonValueKind.True: return "true";
				case JsonValueKind.False: return "false";
				default: return "nil";
			}
		}

		private McpToolResult ToolListRecipes(JsonElement _)
		{
			JsonArray modules = new JsonArray();
			JsonArray recipes = new JsonArray();
			HashSet<string> seenModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			HashSet<string> seenRecipes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach(string lib in LibDirs) {
				foreach(string file in Directory.GetFiles(lib, "*.lua").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
					string moduleName = Path.GetFileNameWithoutExtension(file);
					if(!seenModules.Add(moduleName)) {
						continue;
					}
					Dictionary<string, string> header = ParseLuaHeader(File.ReadAllText(file));
					modules.Add((JsonNode)new JsonObject() {
						["name"] = moduleName,
						["description"] = header.GetValueOrDefault("description"),
						["requires"] = new JsonArray(SplitList(header.GetValueOrDefault("requires")).Select(r => (JsonNode?)r).ToArray()),
						["dir"] = lib
					});
				}

				string recipeDir = Path.Combine(lib, "recipes");
				if(Directory.Exists(recipeDir)) {
					foreach(string file in Directory.GetFiles(recipeDir, "*.lua").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
						string recipeName = Path.GetFileNameWithoutExtension(file);
						if(!seenRecipes.Add(recipeName)) {
							continue;
						}
						Dictionary<string, string> header = ParseLuaHeader(File.ReadAllText(file));
						recipes.Add((JsonNode)new JsonObject() {
							["name"] = recipeName,
							["description"] = header.GetValueOrDefault("description"),
							["includes"] = new JsonArray(SplitList(header.GetValueOrDefault("includes")).Select(r => (JsonNode?)r).ToArray()),
							["defaultFrames"] = header.TryGetValue("frames", out string? frames) && int.TryParse(frames, out int f) ? JsonValue.Create(f) : null,
							["args"] = header.GetValueOrDefault("args"),
							["dir"] = lib
						});
					}
				}
			}

			return Json(new JsonObject() {
				["libDir"] = LibDir,
				["libDirs"] = new JsonArray(LibDirs.Select(d => (JsonNode?)d).ToArray()),
				["resultsDir"] = ResultsDir,
				["modules"] = modules,
				["recipes"] = recipes
			});
		}

		private McpToolResult ToolRunRecipe(JsonElement args)
		{
			string name = GetRequiredString(args, "name");
			if(!ModuleNameRegex.IsMatch(name)) {
				throw new McpToolException("unknown_recipe", $"'{name}' is not a valid recipe name.");
			}
			if(LibDirs.Count == 0) {
				throw new McpToolException("no_lib_dir", "No Lua lib folder was found. Ship tools/lua/lib next to the host or start the server with --mcp-lib-dir=<folder>[;<folder>].");
			}
			string? path = FindLibFile("recipes", name);
			if(path == null) {
				throw new McpToolException("unknown_recipe", $"Recipe '{name}' was not found in any <libDir>/recipes folder ({string.Join("; ", LibDirs)}). Use list_recipes.");
			}

			string source = File.ReadAllText(path);
			Dictionary<string, string> header = ParseLuaHeader(source);
			List<string> includes = SplitList(header.GetValueOrDefault("includes"));
			int defaultFrames = header.TryGetValue("frames", out string? framesText) && int.TryParse(framesText, out int f) ? f : 600;

			string argsLiteral = TryGetProperty(args, "args", out JsonElement argsElement) && argsElement.ValueKind == JsonValueKind.Object
				? ToLuaLiteral(argsElement) : "{}";
			string code = "local args = " + argsLiteral + " " + source;

			int frames = Math.Clamp(GetOptionalInt(args, "frames") ?? defaultFrames, 0, MaxFramesPerCall);
			bool turbo = ResolveTurbo(args);
			int timeoutMs = GetOptionalInt(args, "timeoutMs") ?? DefaultTimeoutMs(frames, turbo);
			bool unloadAfter = GetOptionalBool(args, "unloadAfter") ?? true;
			int maxLog = Math.Clamp(GetOptionalInt(args, "maxLog") ?? 500, 1, ScriptLogRingSize);

			JsonObject node = RunScriptCore("recipe_" + name, code, includes, frames, timeoutMs, turbo, unloadAfter, maxLog, ParseFromReset(args));
			node["recipe"] = name;
			return Json(node, node["stopReason"]?.GetValue<string>() == "timeout");
		}

		private McpToolResult ToolRunScript(JsonElement args)
		{
			string code = GetRequiredString(args, "code");
			string name = GetOptionalString(args, "name") ?? "run_script";
			int frames = Math.Clamp(GetOptionalInt(args, "frames") ?? 60, 0, MaxFramesPerCall);
			bool turbo = ResolveTurbo(args);
			int timeoutMs = GetOptionalInt(args, "timeoutMs") ?? DefaultTimeoutMs(frames, turbo);
			bool unloadAfter = GetOptionalBool(args, "unloadAfter") ?? true;
			int maxLog = Math.Clamp(GetOptionalInt(args, "maxLog") ?? 500, 1, ScriptLogRingSize);

			JsonObject node = RunScriptCore(name, code, GetStringList(args, "includes"), frames, timeoutMs, turbo, unloadAfter, maxLog, ParseFromReset(args));
			return Json(node, node["stopReason"]?.GetValue<string>() == "timeout");
		}

		/// <summary>fromReset: true or "power" = cold boot, "soft" = reset button, absent/false = none.</summary>
		private static string? ParseFromReset(JsonElement args)
		{
			if(args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("fromReset", out JsonElement value)) {
				return null;
			}
			switch(value.ValueKind) {
				case JsonValueKind.True: return "power";
				case JsonValueKind.String:
					string text = (value.GetString() ?? "").Trim().ToLowerInvariant();
					if(text == "" || text == "false" || text == "none") {
						return null;
					}
					if(text == "true") {
						return "power";
					}
					if(text == "power" || text == "soft") {
						return text;
					}
					throw new McpToolException("invalid_argument", "fromReset must be \"power\", \"soft\" or a boolean.");
				default: return null;
			}
		}

		private JsonObject RunScriptCore(string name, string code, IReadOnlyList<string>? includes, int frames, int timeoutMs, bool turbo, bool unloadAfter, int maxLog, string? fromReset = null)
		{
			CpuType cpu = GetPrimaryCpuType();
			EnsurePaused(2000);

			Dictionary<string, long> filesBefore = new Dictionary<string, long>(_filesWritten, StringComparer.OrdinalIgnoreCase);
			JsonObject? resetInfo = fromReset != null ? ResetConsole(fromReset == "power") : null;
			long startSeq = _logStore.NextSeq;
			int id = LoadScriptInternal(name, code, true, includes, cpu, out _);
			HarvestScript(name, id, cpu);

			StepOutcome outcome;
			using(BeginTurbo(turbo)) {
				outcome = frames > 0 && !_logStore.IsDone(name)
					? RunFrames(cpu, frames, timeoutMs, () => _logStore.IsDone(name))
					: new StepOutcome();
			}
			if(outcome.StopReason == "done" && _logStore.IsDone(name)) {
				outcome.StopReason = "script_done";
			}
			HarvestScript(name, id, cpu);
			if(_logStore.PendingWriteBytes(name) > 0) {
				using(BeginTurbo(true)) {
					DrainScriptWrites(name, id, cpu, outcome, Math.Min(Math.Max(timeoutMs, 10000), 120000));
				}
				HarvestScript(name, id, cpu);
			}

			McpScriptLogQuery log = _logStore.Query(name, startSeq, maxLog);
			string? rawResult = _logStore.GetResult(name);

			if(unloadAfter) {
				DebugApi.RemoveScript(id);
				_scriptIds.Remove(name);
				_scriptLogOffsets.Remove(id);
				_logStore.ForgetScript(id);
			}

			JsonObject node = BuildStepResponse(cpu, outcome);
			node["name"] = name;
			node["scriptId"] = id;
			node["unloaded"] = unloadAfter;
			if(resetInfo != null) {
				node["reset"] = resetInfo;
			}
			node["log"] = McpScriptLogStore.ToJson(log);
			node["hasResult"] = rawResult != null;
			node["result"] = ParseResult(rawResult);
			JsonArray files = BuildFilesWritten(filesBefore);
			MarkTruncatedFiles(files, name);
			if(files.Count > 0) {
				node["filesWritten"] = files;
			}
			JsonObject? writes = BuildWriteReport(name);
			if(writes != null) {
				node["writes"] = writes;
			}
			McpScriptLogStore.WriteState writeState = _logStore.GetWriteState(name);
			node["logTruncated"] = writeState.Truncated || writeState.PendingRowBytes > 0;
			return node;
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-13: labels and address conversion
		// ---------------------------------------------------------------------------------------------

		private static int ParseCpuAddress(JsonElement element)
		{
			if(element.ValueKind == JsonValueKind.Number) {
				return element.GetInt32();
			}
			if(element.ValueKind != JsonValueKind.String) {
				throw new InvalidOperationException("Address must be a number or a \"BB:AAAA\" string.");
			}
			return ParseCpuAddress(element.GetString() ?? "");
		}

		private static int ParseCpuAddress(string text)
		{
			string s = text.Trim().Replace("$", "").Replace("0x", "", StringComparison.OrdinalIgnoreCase);
			int colon = s.IndexOf(':');
			try {
				if(colon >= 0) {
					int bank = Convert.ToInt32(s.Substring(0, colon), 16);
					int addr = Convert.ToInt32(s.Substring(colon + 1), 16);
					return (bank << 16) | (addr & 0xFFFF);
				}
				return Convert.ToInt32(s, 16);
			} catch(Exception) {
				throw new InvalidOperationException($"Invalid address '{text}'. Use a number or \"BB:AAAA\" (hex).");
			}
		}

		private static string FormatCpuAddress(CpuType cpu, int address)
		{
			if(address < 0) {
				return "";
			}
			return cpu == CpuType.Snes ? $"{(address >> 16) & 0xFF:X2}:{address & 0xFFFF:X4}" : address.ToString("X4", CultureInfo.InvariantCulture);
		}

		private Dictionary<MemoryType, CodeLabel[]> BuildLabelIndex()
		{
			return LabelManager.GetAllLabels()
				.Where(l => l.Label.Length > 0)
				.GroupBy(l => l.MemoryType)
				.ToDictionary(g => g.Key, g => g.OrderBy(l => l.Address).ToArray());
		}

		private static CodeLabel? FindCoveringOrPrecedingLabel(Dictionary<MemoryType, CodeLabel[]> index, MemoryType type, int address, out bool covering)
		{
			covering = false;
			if(address < 0 || !index.TryGetValue(type, out CodeLabel[]? labels) || labels.Length == 0) {
				return null;
			}

			int lo = 0, hi = labels.Length - 1, best = -1;
			while(lo <= hi) {
				int mid = (lo + hi) / 2;
				if(labels[mid].Address <= (uint)address) {
					best = mid;
					lo = mid + 1;
				} else {
					hi = mid - 1;
				}
			}
			if(best < 0) {
				return null;
			}

			// Several labels can share an address range; walk back a little to find one that covers it.
			for(int i = best; i >= 0 && i > best - 8; i--) {
				if((uint)address < labels[i].Address + labels[i].Length) {
					covering = true;
					return labels[i];
				}
			}
			return (uint)address - labels[best].Address <= 0x8000 ? labels[best] : null;
		}

		private JsonObject ResolveAddressNode(JsonElement item, CpuType cpu, Dictionary<MemoryType, CodeLabel[]> index)
		{
			AddressInfo abs = new AddressInfo() { Address = -1, Type = MemoryType.None };
			int cpuAddress = -1;

			if(item.ValueKind == JsonValueKind.Number || item.ValueKind == JsonValueKind.String) {
				cpuAddress = ParseCpuAddress(item);
				abs = DebugApi.GetAbsoluteAddress(new AddressInfo() { Address = cpuAddress, Type = cpu.ToMemoryType() });
			} else if(item.ValueKind == JsonValueKind.Object) {
				if(TryGetProperty(item, "cpu", out JsonElement cpuElement)) {
					cpuAddress = ParseCpuAddress(cpuElement);
					abs = DebugApi.GetAbsoluteAddress(new AddressInfo() { Address = cpuAddress, Type = cpu.ToMemoryType() });
				} else if(GetFirstInt(item, "prgOffset", "offset") is int prg) {
					abs = new AddressInfo() { Address = prg, Type = cpu.GetPrgRomMemoryType() };
				} else if(GetOptionalInt(item, "wram") is int wram) {
					abs = new AddressInfo() { Address = wram, Type = cpu.GetSystemRamType() };
				} else if(GetOptionalString(item, "label") is string labelName) {
					CodeLabel? label = LabelManager.GetLabel(labelName) ?? throw new McpToolException("unknown_label", $"Label not found: {labelName}");
					abs = label.GetAbsoluteAddress();
				} else {
					throw new InvalidOperationException("Give one of cpu, prgOffset, wram or label.");
				}
			} else {
				throw new InvalidOperationException("Each address must be a number, a \"BB:AAAA\" string or an object.");
			}

			if(cpuAddress < 0 && abs.Address >= 0) {
				AddressInfo rel = DebugApi.GetRelativeAddress(abs, cpu);
				cpuAddress = rel.Address;
			}

			bool isPrg = abs.Type == cpu.GetPrgRomMemoryType();
			bool isWram = abs.Type == cpu.GetSystemRamType();
			JsonObject node = new JsonObject() {
				["cpu"] = cpuAddress >= 0 ? FormatCpuAddress(cpu, cpuAddress) : null,
				["cpuAddress"] = cpuAddress >= 0 ? Num(cpuAddress) : null,
				["memoryType"] = abs.Address >= 0 ? abs.Type.ToString() : null,
				["absAddress"] = abs.Address >= 0 ? Num(abs.Address) : null,
				["prgOffset"] = isPrg ? Num(abs.Address) : null,
				["wram"] = isWram ? Num(abs.Address) : null
			};

			CodeLabel? found = FindCoveringOrPrecedingLabel(index, abs.Type, abs.Address, out bool covering);
			node["label"] = found?.Label;
			node["labelOffset"] = found != null ? Num((long)(abs.Address - found.Address)) : null;
			node["exact"] = found != null && covering;

			if(cpu == CpuType.Snes && cpuAddress >= 0 && abs.Address >= 0) {
				JsonArray aliases = new JsonArray();
				int offsetInBank = cpuAddress & 0xFFFF;
				for(int bank = 0; bank < 256; bank++) {
					int candidate = (bank << 16) | offsetInBank;
					if(candidate == cpuAddress) {
						continue;
					}
					AddressInfo other = DebugApi.GetAbsoluteAddress(new AddressInfo() { Address = candidate, Type = cpu.ToMemoryType() });
					if(other.Type == abs.Type && other.Address == abs.Address) {
						aliases.Add((JsonNode)JsonValue.Create(FormatCpuAddress(cpu, candidate))!);
					}
				}
				node["bankAlias"] = aliases;
			}
			return node;
		}

		private McpToolResult ToolResolveAddress(JsonElement args)
		{
			CpuType cpu = GetPrimaryCpuType();
			return Json(ResolveAddressNode(args, cpu, BuildLabelIndex()));
		}

		private McpToolResult ToolResolveAddresses(JsonElement args)
		{
			if(!TryGetProperty(args, "addresses", out JsonElement list) || list.ValueKind != JsonValueKind.Array) {
				throw new InvalidOperationException("addresses array is required.");
			}
			CpuType cpu = GetPrimaryCpuType();
			Dictionary<MemoryType, CodeLabel[]> index = BuildLabelIndex();
			JsonArray results = new JsonArray();
			foreach(JsonElement item in list.EnumerateArray()) {
				try {
					results.Add((JsonNode)ResolveAddressNode(item, cpu, index));
				} catch(Exception ex) {
					results.Add((JsonNode)new JsonObject() { ["error"] = ex.Message, ["input"] = item.GetRawText() });
				}
			}
			return Json(new JsonObject() { ["count"] = results.Count, ["results"] = results });
		}

		private string? LabelFor(CpuType cpu, int cpuAddress, Dictionary<MemoryType, CodeLabel[]> index)
		{
			if(cpuAddress < 0) {
				return null;
			}
			AddressInfo abs = DebugApi.GetAbsoluteAddress(new AddressInfo() { Address = cpuAddress, Type = cpu.ToMemoryType() });
			CodeLabel? label = FindCoveringOrPrecedingLabel(index, abs.Type, abs.Address, out _);
			if(label == null) {
				return null;
			}
			long offset = abs.Address - label.Address;
			return offset == 0 ? label.Label : $"{label.Label}+0x{offset:X}";
		}

		private McpToolResult ToolGetCallstackLabeled(JsonElement args)
		{
			CpuType cpu = ResolveCpuType(GetOptionalString(args, "cpuType")) ?? GetPrimaryCpuType();
			StackFrameInfo[] stack = DebugApi.GetCallstack(cpu);
			Dictionary<MemoryType, CodeLabel[]> index = BuildLabelIndex();
			JsonArray rows = new JsonArray();
			foreach(StackFrameInfo frame in stack) {
				rows.Add((JsonNode)new JsonObject() {
					["source"] = frame.Source,
					["target"] = frame.Target,
					["return"] = frame.Return,
					["sourceLabel"] = LabelFor(cpu, (int)frame.Source, index),
					["targetLabel"] = LabelFor(cpu, (int)frame.Target, index),
					["returnLabel"] = LabelFor(cpu, (int)frame.Return, index),
					["flags"] = frame.Flags.ToString(),
					["absSource"] = frame.AbsSource.Address,
					["absTarget"] = frame.AbsTarget.Address,
					["absReturn"] = frame.AbsReturn.Address
				});
			}
			return Json(new JsonObject() { ["cpuType"] = cpu.ToString(), ["frames"] = rows });
		}

		private McpToolResult ToolGetExecutionTraceLabeled(JsonElement args)
		{
			uint startOffset = (uint)Math.Max(0, GetOptionalInt(args, "startOffset") ?? 0);
			uint maxRows = (uint)Math.Clamp(GetOptionalInt(args, "maxRowCount") ?? 200, 1, 10000);
			TraceRow[] trace = DebugApi.GetExecutionTrace(startOffset, maxRows);
			Dictionary<MemoryType, CodeLabel[]> index = BuildLabelIndex();

			JsonArray rows = new JsonArray();
			foreach(TraceRow row in trace) {
				rows.Add((JsonNode)new JsonObject() {
					["cpuType"] = row.Type.ToString(),
					["programCounter"] = row.ProgramCounter,
					["label"] = LabelFor(row.Type, (int)row.ProgramCounter, index),
					["byteCode"] = row.GetByteCodeStr(),
					["output"] = row.GetOutput()
				});
			}
			return Json(new JsonObject() { ["rows"] = rows });
		}

		private McpToolResult ToolListLabels(JsonElement args)
		{
			string? filter = GetOptionalString(args, "filter");
			int max = Math.Clamp(GetOptionalInt(args, "max") ?? 200, 1, 5000);
			CpuType cpu = GetPrimaryCpuType();

			List<CodeLabel> all = LabelManager.GetAllLabels();
			IEnumerable<CodeLabel> matches = all.Where(l => l.Label.Length > 0 &&
				(string.IsNullOrEmpty(filter) || l.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)))
				.OrderBy(l => l.MemoryType).ThenBy(l => l.Address);

			JsonArray rows = new JsonArray();
			int total = 0;
			foreach(CodeLabel label in matches) {
				total++;
				if(rows.Count >= max) {
					continue;
				}
				AddressInfo rel = DebugApi.GetRelativeAddress(label.GetAbsoluteAddress(), cpu);
				rows.Add((JsonNode)new JsonObject() {
					["name"] = label.Label,
					["memoryType"] = label.MemoryType.ToString(),
					["address"] = Num((long)label.Address),
					["length"] = Num((long)label.Length),
					["cpu"] = rel.Address >= 0 ? FormatCpuAddress(cpu, rel.Address) : null,
					["comment"] = label.Comment.Length > 0 ? label.Comment : null
				});
			}
			return Json(new JsonObject() { ["totalLabels"] = all.Count, ["matches"] = total, ["labels"] = rows });
		}

		private McpToolResult ToolLoadLabels(JsonElement args)
		{
			string path = Path.GetFullPath(GetRequiredString(args, "path"));
			if(!File.Exists(path)) {
				throw new FileNotFoundException("Label file not found.", path);
			}
			string format = (GetOptionalString(args, "format") ?? Path.GetExtension(path).TrimStart('.')).ToLowerInvariant();
			bool replace = GetOptionalBool(args, "replace") ?? false;
			CpuType cpu = GetPrimaryCpuType();

			List<CodeLabel> labels = new List<CodeLabel>();
			int invalid = 0, unmapped = 0, renamed = 0;
			List<string> samples = new List<string>();

			string Sanitize(string name)
			{
				string clean = LabelManager.InvalidLabelRegex.Replace(name.Trim(), "_");
				if(clean.Length == 0 || char.IsDigit(clean[0])) {
					clean = "_" + clean;
				}
				if(clean != name) {
					renamed++;
				}
				return clean;
			}

			bool AddByCpuAddress(string name, int cpuAddress, uint length, string comment)
			{
				AddressInfo abs = DebugApi.GetAbsoluteAddress(new AddressInfo() { Address = cpuAddress, Type = cpu.ToMemoryType() });
				if(abs.Address < 0 || abs.Type == MemoryType.None) {
					unmapped++;
					return false;
				}
				labels.Add(new CodeLabel() { Address = (uint)abs.Address, MemoryType = abs.Type, Label = Sanitize(name), Length = Math.Max(1, length), Comment = comment });
				return true;
			}

			switch(format) {
				case "mlb": {
					foreach(string row in File.ReadAllLines(path, Encoding.UTF8)) {
						if(string.IsNullOrWhiteSpace(row)) {
							continue;
						}
						CodeLabel? label = CodeLabel.FromString(row);
						if(label == null) {
							invalid++;
						} else {
							labels.Add(label);
						}
					}
					break;
				}

				case "sym": {
					Regex symRow = new Regex(@"^\s*([0-9A-Fa-f]{2}):([0-9A-Fa-f]{4})\s+(\S+)");
					foreach(string row in File.ReadAllLines(path, Encoding.UTF8)) {
						string line = row.Trim();
						if(line.Length == 0 || line[0] == ';' || line[0] == '[') {
							continue;
						}
						Match m = symRow.Match(line);
						if(!m.Success) {
							invalid++;
							continue;
						}
						int address = (Convert.ToInt32(m.Groups[1].Value, 16) << 16) | Convert.ToInt32(m.Groups[2].Value, 16);
						AddByCpuAddress(m.Groups[3].Value, address, 1, "");
					}
					break;
				}

				case "json": {
					using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
					JsonElement list = doc.RootElement;
					if(list.ValueKind == JsonValueKind.Object && list.TryGetProperty("labels", out JsonElement inner)) {
						list = inner;
					}
					if(list.ValueKind != JsonValueKind.Array) {
						throw new InvalidOperationException("JSON label file must be an array or an object with a 'labels' array.");
					}
					foreach(JsonElement item in list.EnumerateArray()) {
						string? name = GetFirstString(item, "name", "label");
						if(string.IsNullOrEmpty(name)) {
							invalid++;
							continue;
						}
						uint length = (uint)Math.Max(1, GetOptionalInt(item, "length") ?? 1);
						string comment = GetOptionalString(item, "comment") ?? "";
						if(TryGetProperty(item, "cpu", out JsonElement cpuElement)) {
							AddByCpuAddress(name, ParseCpuAddress(cpuElement), length, comment);
						} else if(GetFirstInt(item, "prgOffset", "offset") is int prg) {
							labels.Add(new CodeLabel() { Address = (uint)prg, MemoryType = cpu.GetPrgRomMemoryType(), Label = Sanitize(name), Length = length, Comment = comment });
						} else if(GetOptionalInt(item, "wram") is int wram) {
							labels.Add(new CodeLabel() { Address = (uint)wram, MemoryType = cpu.GetSystemRamType(), Label = Sanitize(name), Length = length, Comment = comment });
						} else {
							invalid++;
						}
					}
					break;
				}

				default:
					throw new InvalidOperationException("format must be mlb, sym or json.");
			}

			// Mutate the label manager without its change events: they would touch UI-only state (assert breakpoints).
			if(replace) {
				foreach(CodeLabel existing in LabelManager.GetAllLabels()) {
					LabelManager.DeleteLabel(existing, false);
				}
				DebugApi.ClearLabels();
			}

			int before = LabelManager.GetAllLabels().Count;
			HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
			int duplicates = 0;
			foreach(CodeLabel label in labels) {
				if(label.Label.Length > 0 && !seen.Add(label.Label)) {
					duplicates++;
				}
			}
			LabelManager.SetLabels(labels, false);
			int after = LabelManager.GetAllLabels().Count;

			foreach(CodeLabel label in labels.Take(5)) {
				samples.Add(label.ToString());
			}

			return Json(new JsonObject() {
				["path"] = path,
				["format"] = format,
				["replaced"] = replace,
				["parsed"] = labels.Count,
				["invalidRows"] = invalid,
				["unmappedAddresses"] = unmapped,
				["renamed"] = renamed,
				["duplicateNames"] = duplicates,
				["labelsBefore"] = before,
				["labelsNow"] = after,
				["sample"] = new JsonArray(samples.Select(x => (JsonNode?)x).ToArray())
			});
		}
	}
#pragma warning restore IL2090
#pragma warning restore IL2067
#pragma warning restore IL2070
#pragma warning restore IL3050
#pragma warning restore IL2026
}
