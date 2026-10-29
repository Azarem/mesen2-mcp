using Mesen.Config;
using Mesen.Debugger;
using Mesen.Debugger.Labels;
using Mesen.Interop;
using System;
using System.Collections.Generic;
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
	/// Debugger-side tools: breakpoints with conditions/counting (MCP-06), memory types and dumps (MCP-07),
	/// access counter summaries (MCP-14), memory snapshots (MCP-16) and trace files (MCP-17).
	/// </summary>
	internal sealed partial class McpToolService
	{
		private const string BpScriptName = "__bp_hits";
		private const string BpRowPrefix = "@@BP@@";
		private const int MaxReadMemoryBytes = 65536;
		private const int MaxSnapshots = 16;
		private const long MaxSnapshotBytes = 256L * 1024 * 1024;

		private readonly List<LuaBreakpointSpec> _luaBreakpoints = new List<LuaBreakpointSpec>();
		private long _bpEventSeq;
		private readonly Dictionary<int, long> _bpEventCounts = new Dictionary<int, long>();
		private readonly Dictionary<string, McpMemorySnapshot> _snapshotStore = new Dictionary<string, McpMemorySnapshot>(StringComparer.OrdinalIgnoreCase);

		private sealed class LuaBreakpointSpec
		{
			public int Id;
			public string? Name;
			public string Action = "count";
			public string Kinds = "exec";
			public int Start;
			public int End;
			public string MemType = "cpuDebug";
			public string? Condition;
			public int MaxLogged = 1000;
		}

		private sealed class McpMemorySnapshot
		{
			public string Name = "";
			public MemoryType Type;
			public int Start;
			public byte[] Data = Array.Empty<byte>();
			public long Frame;
			public DateTime Created = DateTime.UtcNow;
		}

		private void AddDebugTools(Dictionary<string, McpToolDefinition> tools)
		{
			// --- MCP-06 --------------------------------------------------------------------------
			AddTool(tools, "set_breakpoints",
				"Replace the active breakpoints. Each item: {callbackType: read|write|exec (or kinds:[..]), startAddress, endAddress?, memType?, condition?, action?, maxLogged?, name?}. " +
				"action=break (default) halts emulation; the condition is a native Mesen debugger expression (for example \"value == 0x55\" or \"(value & 0x0200) != 0\"). " +
				"action=count and action=log never halt: hits are counted (and for log recorded with the registers, up to maxLogged, default 1000) by a hidden Lua callback; " +
				"for these the condition is translated to Lua and supports value, oldvalue, address, pc (24-bit on SNES), a, x, y, sp, d, k, dbr, ps, frame, [addr] byte and {addr} word reads, && || ! != == < > <= >= & | ^ << >> + - * / %. " +
				"Use get_breakpoint_hits to read counts and logged hits.",
				ObjectSchema(("breakpoints", new JsonObject() { ["type"] = "array", ["description"] = "Breakpoint list" }, true)),
				ToolSetBreakpoints);

			AddTool(tools, "get_breakpoint_hits",
				"Counts and logged hits of count/log breakpoints (set_breakpoints). Returns counts per breakpoint id and hit rows {seq, id, frame, kind, address, value, pc, a, x, y, sp, k}.",
				ObjectSchema(
					("id", IntegerSchema("Only this breakpoint id"), false),
					("sinceSeq", IntegerSchema("Only rows with seq greater than this"), false),
					("max", IntegerSchema("Maximum rows (default 500)"), false),
					("reset", BooleanSchema("Reset the counters and the hit rows after reading"), false)
				),
				ToolGetBreakpointHits);

			// --- MCP-07 --------------------------------------------------------------------------
			AddTool(tools, "list_memory_types",
				"List every memory type of the loaded console with its size in bytes. The names can be used as the type of read_memory, write_memory, dump_memory, snapshot_memory and others.",
				EmptySchema(), ToolListMemoryTypes);

			AddTool(tools, "read_memory",
				"Read bytes from memory (at most 65536 per call). format: hex (default hex dump), bytes (JSON array), words_le (JSON array of 16-bit little-endian words) or base64.",
				ObjectSchema(
					("address", IntegerSchema("Start address"), true),
					("length", IntegerSchema("Byte length"), true),
					("type", StringSchema("Memory type name (see list_memory_types), for example snesCgRam, spcRam, snesVideoRam"), false),
					("format", StringSchema("hex | bytes | words_le | base64"), false)
				),
				ToolReadMemory);

			AddTool(tools, "dump_memory",
				"Write any amount of memory to a file in the results folder (or an absolute path) and return its size and SHA-1.",
				ObjectSchema(
					("type", StringSchema("Memory type name"), true),
					("start", IntegerSchema("Start address (default 0)"), false),
					("length", IntegerSchema("Byte length (default: to the end of the memory type)"), false),
					("path", StringSchema("Output file name (relative to the results folder) or absolute path"), true)
				),
				ToolDumpMemory);

			AddTool(tools, "get_palette_info",
				"Get palette metadata and colours. With first/count/format the colours are returned as rows {index, rgb888, rgb555?}: rgb555 is the raw CGRAM word on SNES.",
				ObjectSchema(
					("cpuType", StringSchema("Optional CPU type"), false),
					("first", IntegerSchema("First palette index"), false),
					("count", IntegerSchema("Number of colours (default: all)"), false),
					("format", StringSchema("rgb888 | rgb555 (selects the row format)"), false)
				),
				ToolGetPaletteInfoEx);

			// --- MCP-14 --------------------------------------------------------------------------
			AddTool(tools, "get_access_summary",
				"Access counters and CDL flags summarised per named range. ranges: [{name, start, end}] (absolute offsets in type, end inclusive), [{name, cpuStart, cpuEnd}] (CPU addresses, converted) or [{name, label, length?}]; " +
				"or give rangesPath (JSON file with such a list). Returns per range exec/read/write counts, first/last access stamps, the CDL flag union and a verdict (accessed | never_accessed).",
				ObjectSchema(
					("type", StringSchema("Memory type of the ranges (default prgRom)"), false),
					("ranges", new JsonObject() { ["type"] = "array", ["description"] = "Range list" }, false),
					("rangesPath", StringSchema("Path of a JSON file holding the range list (array or {ranges:[..]})"), false),
					("cdl", BooleanSchema("Include the CDL flag union (default true for ROM types)"), false),
					("nonzeroOnly", BooleanSchema("Only return ranges with at least one access"), false)
				),
				ToolGetAccessSummary);

			AddTool(tools, "reset_access_counters", "Reset the native memory access counters (exec/read/write).", EmptySchema(), ToolResetAccessCounters);

			// --- MCP-16 --------------------------------------------------------------------------
			AddTool(tools, "snapshot_memory",
				"Keep a copy of a memory range under a name (at most 16 snapshots, 256 MB total).",
				ObjectSchema(
					("name", StringSchema("Snapshot name"), true),
					("type", StringSchema("Memory type (default workRam)"), false),
					("start", IntegerSchema("Start address (default 0)"), false),
					("length", IntegerSchema("Length (default: whole memory type)"), false)
				),
				ToolSnapshotMemory);

			AddTool(tools, "diff_snapshots",
				"Compare two snapshots (b may be \"current\" to compare against live memory). Returns changed runs [start, end, oldHex, newHex] with absolute addresses.",
				ObjectSchema(
					("a", StringSchema("First snapshot name"), true),
					("b", StringSchema("Second snapshot name, or \"current\""), false),
					("ranges", new JsonObject() { ["type"] = "array", ["description"] = "Only compare these [start,end] address windows (end inclusive)" }, false),
					("mergeGap", IntegerSchema("Merge changed runs closer than this many bytes (default 0)"), false),
					("maxRows", IntegerSchema("Maximum rows (default 500)"), false)
				),
				ToolDiffSnapshots);

			AddTool(tools, "list_snapshots", "List the stored memory snapshots.", EmptySchema(), ToolListSnapshots);
			AddTool(tools, "delete_snapshot", "Delete a memory snapshot (or all with name \"*\").", ObjectSchema(("name", StringSchema("Snapshot name"), true)), ToolDeleteSnapshot);

			// --- MCP-17 --------------------------------------------------------------------------
			AddTool(tools, "start_trace",
				"Start writing an execution trace to a file (results folder or absolute path). Optional pcRanges [[start,end]..] (CPU addresses) and condition (native expression) limit what is logged; maxLines caps the file when the trace is stopped. " +
				"The default format has the frame count, the 24-bit PC, the disassembly and the registers.",
				ObjectSchema(
					("path", StringSchema("Output file"), true),
					("pcRanges", new JsonObject() { ["type"] = "array", ["description"] = "[[start,end],..] CPU address windows (numbers or \"BB:AAAA\")" }, false),
					("condition", StringSchema("Native condition expression"), false),
					("format", StringSchema("Native trace format string (default: frame, PC, disassembly, registers)"), false),
					("maxLines", IntegerSchema("Truncate the file to this many lines when the trace stops"), false),
					("useLabels", BooleanSchema("Use labels in the disassembly"), false)
				),
				ToolStartTrace);

			AddTool(tools, "stop_trace", "Stop the trace file started by start_trace and return its size, line count and SHA-1.", EmptySchema(), ToolStopTrace);

			AddTool(tools, "trace_between",
				"Triggered trace: run until startPc executes, trace until stopPc executes (or maxFrames), then stop. Uses temporary exec breakpoints; the trace file contains the instructions from startPc up to, not including, the instruction at stopPc.",
				ObjectSchema(
					("startPc", new JsonObject() { ["description"] = "CPU address (number or \"BB:AAAA\") that starts the trace" }, true),
					("stopPc", new JsonObject() { ["description"] = "CPU address (number or \"BB:AAAA\") that ends the trace" }, true),
					("path", StringSchema("Output file"), true),
					("maxFrames", IntegerSchema("Frame budget for each of the two phases (default 600)"), false),
					("condition", StringSchema("Native condition expression"), false),
					("maxLines", IntegerSchema("Truncate the file to this many lines"), false),
					("turbo", BooleanSchema("Run without the frame limiter"), false)
				),
				ToolTraceBetween);
		}

		// ---------------------------------------------------------------------------------------------
		// Small helpers
		// ---------------------------------------------------------------------------------------------

		private static int? GetAddressArg(JsonElement element, params string[] names)
		{
			foreach(string name in names) {
				if(TryGetProperty(element, name, out JsonElement value) && value.ValueKind != JsonValueKind.Null) {
					return ParseCpuAddress(value);
				}
			}
			return null;
		}

		private static string Hex(byte[] data, int start, int length)
		{
			StringBuilder sb = new StringBuilder(length * 2);
			for(int i = 0; i < length; i++) {
				sb.Append(data[start + i].ToString("X2", CultureInfo.InvariantCulture));
			}
			return sb.ToString();
		}

		private void ResetScriptDependentState()
		{
			_luaBreakpoints.Clear();
			_bpEventCounts.Clear();
			_traceFile = null;
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-06: breakpoints
		// ---------------------------------------------------------------------------------------------

		private static BreakpointTypeFlags ParseBreakpointKinds(JsonElement entry, out string kindsText)
		{
			List<string> kinds = new List<string>();
			if(TryGetProperty(entry, "kinds", out JsonElement kindsElement) && kindsElement.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement kind in kindsElement.EnumerateArray()) {
					if(kind.ValueKind == JsonValueKind.String) {
						kinds.Add(kind.GetString()!);
					}
				}
			}
			string? callbackType = GetFirstString(entry, "callbackType", "type");
			if(callbackType != null) {
				kinds.AddRange(callbackType.Split(new[] { '|', ',', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries));
			}
			if(kinds.Count == 0) {
				kinds.Add("exec");
			}

			BreakpointTypeFlags flags = 0;
			List<string> normalized = new List<string>();
			foreach(string kind in kinds) {
				switch(kind.ToLowerInvariant()) {
					case "read": flags |= BreakpointTypeFlags.Read; normalized.Add("read"); break;
					case "write": flags |= BreakpointTypeFlags.Write; normalized.Add("write"); break;
					case "readwrite": flags |= BreakpointTypeFlags.Read | BreakpointTypeFlags.Write; normalized.Add("read"); normalized.Add("write"); break;
					case "exec":
					case "execute": flags |= BreakpointTypeFlags.Execute; normalized.Add("exec"); break;
					default: throw new InvalidOperationException($"Unknown breakpoint kind '{kind}'. Use read, write or exec.");
				}
			}
			kindsText = string.Join("|", normalized.Distinct());
			return flags;
		}

		private McpToolResult ToolSetBreakpoints(JsonElement args)
		{
			if(!TryGetProperty(args, "breakpoints", out JsonElement breakpointArray) || breakpointArray.ValueKind != JsonValueKind.Array) {
				throw new InvalidOperationException("breakpoints array is required.");
			}

			CpuType cpu = GetPrimaryCpuType();
			List<InteropBreakpoint> interop = new List<InteropBreakpoint>();
			_breakpoints.Clear();
			_luaBreakpoints.Clear();
			_bpEventCounts.Clear();
			int index = 0;

			foreach(JsonElement entry in breakpointArray.EnumerateArray()) {
				BreakpointTypeFlags typeFlags = ParseBreakpointKinds(entry, out string kindsText);
				int startAddress = GetAddressArg(entry, "startAddress", "start", "address") ?? throw new InvalidOperationException("Each breakpoint needs startAddress.");
				int endAddress = GetAddressArg(entry, "endAddress", "end") ?? startAddress;
				string memTypeName = GetOptionalString(entry, "memType") ?? "cpuDebug";
				MemoryType memType = ResolveMemoryType(memTypeName, cpu);
				string action = (GetOptionalString(entry, "action") ?? "break").ToLowerInvariant();
				string? condition = GetOptionalString(entry, "condition");
				string? name = GetOptionalString(entry, "name");

				if(action != "break" && action != "count" && action != "log") {
					throw new InvalidOperationException("action must be break, count or log.");
				}

				if(action == "break") {
					byte[] conditionBytes = new byte[1000];
					if(!string.IsNullOrWhiteSpace(condition)) {
						byte[] raw = Encoding.UTF8.GetBytes(condition!);
						if(raw.Length > 999) {
							throw new InvalidOperationException("condition is too long (999 bytes maximum).");
						}
						DebugApi.EvaluateExpression(condition!, cpu, out EvalResultType resultType, false);
						if(resultType == EvalResultType.Invalid) {
							throw new McpToolException("invalid_condition", $"Breakpoint {index}: the condition '{condition}' is not a valid expression.");
						}
						Array.Copy(raw, conditionBytes, raw.Length);
					}

					interop.Add(new InteropBreakpoint() {
						Id = index,
						CpuType = cpu,
						MemoryType = memType,
						Type = typeFlags,
						StartAddress = startAddress,
						EndAddress = endAddress,
						Enabled = true,
						MarkEvent = false,
						IgnoreDummyOperations = true,
						Condition = conditionBytes
					});
					_breakpoints.Add(new McpBreakpointSpec() {
						Id = index,
						CallbackType = kindsText,
						MemType = memTypeName
					});
				} else {
					_luaBreakpoints.Add(new LuaBreakpointSpec() {
						Id = index,
						Name = name,
						Action = action,
						Kinds = kindsText,
						Start = startAddress,
						End = endAddress,
						MemType = memTypeName,
						Condition = string.IsNullOrWhiteSpace(condition) ? null : condition,
						MaxLogged = Math.Clamp(GetOptionalInt(entry, "maxLogged") ?? 1000, 0, 100000)
					});
				}
				index++;
			}

			_interopBps = interop;
			DebugApi.SetBreakpoints(interop.ToArray(), (uint)interop.Count);
			LoadLuaBreakpointScript(cpu);

			return Json(new JsonObject() {
				["set"] = index,
				["halting"] = interop.Count,
				["counting"] = _luaBreakpoints.Count(b => b.Action == "count"),
				["logging"] = _luaBreakpoints.Count(b => b.Action == "log")
			});
		}

		private McpToolResult ToolClearBreakpoints(JsonElement _)
		{
			int removed = _breakpoints.Count + _luaBreakpoints.Count;
			_breakpoints.Clear();
			_luaBreakpoints.Clear();
			_bpEventCounts.Clear();
			_interopBps = new List<InteropBreakpoint>();
			DebugApi.SetBreakpoints(Array.Empty<InteropBreakpoint>(), 0);
			UnloadLuaBreakpointScript();
			return McpToolResult.Text($"Removed {removed} breakpoint(s).");
		}

		private void UnloadLuaBreakpointScript()
		{
			if(_scriptIds.TryGetValue(BpScriptName, out int id)) {
				try { DebugApi.RemoveScript(id); } catch { }
				_scriptIds.Remove(BpScriptName);
				_scriptLogOffsets.Remove(id);
				_logStore.ForgetScript(id);
			}
		}

		private void LoadLuaBreakpointScript(CpuType cpu)
		{
			UnloadLuaBreakpointScript();
			if(_luaBreakpoints.Count == 0) {
				return;
			}

			StringBuilder sb = new StringBuilder();
			sb.Append("local COUNTS, LOGGED, DIRTY, FRAME = {}, {}, false, 0\n");
			sb.Append("local BASE = ").Append(GetFrameCount(cpu).ToString(CultureInfo.InvariantCulture)).Append("\n");
			sb.Append("local function field(st, k) local v = st[k] if type(v) ~= 'number' then return 0 end return math.floor(v) end\n");
			sb.Append("local function regs() local st = emu.getState() local k = field(st, 'cpu.k') local pc = field(st, 'cpu.pc') ")
			  .Append("return { pc = (k << 16) | pc, a = field(st, 'cpu.a'), x = field(st, 'cpu.x'), y = field(st, 'cpu.y'), sp = field(st, 'cpu.sp'), d = field(st, 'cpu.d'), k = k, dbr = field(st, 'cpu.dbr'), ps = field(st, 'cpu.ps') } end\n");
			sb.Append("local function hit(id, kind, address, value, log, max)\n");
			sb.Append("  COUNTS['c' .. id] = (COUNTS['c' .. id] or 0) + 1 DIRTY = true\n");
			sb.Append("  if log and (LOGGED[id] or 0) < max then LOGGED[id] = (LOGGED[id] or 0) + 1 local r = regs()\n");
			sb.Append("    emu.log(string.format('").Append(BpRowPrefix).Append("{\"id\":%d,\"frame\":%d,\"kind\":\"%s\",\"address\":%d,\"value\":%d,\"pc\":%d,\"a\":%d,\"x\":%d,\"y\":%d,\"sp\":%d,\"d\":%d,\"k\":%d,\"dbr\":%d,\"ps\":%d}', ")
			  .Append("id, BASE + FRAME, kind, address, value or 0, r.pc, r.a, r.x, r.y, r.sp, r.d, r.k, r.dbr, r.ps)) end\n");
			sb.Append("end\n");
			sb.Append("emu.addEventCallback(function() FRAME = FRAME + 1 if DIRTY then DIRTY = false emu.mcp.result({ counts = COUNTS, logged = LOGGED, frame = BASE + FRAME }) end end, emu.eventType.endFrame)\n");

			foreach(LuaBreakpointSpec bp in _luaBreakpoints) {
				MemoryType memType = ResolveMemoryType(bp.MemType, cpu);
				string luaMem = LuaMemTypeName(memType);
				string condition = bp.Condition != null ? TranslateConditionToLua(bp.Condition, memType) : "true";
				bool log = bp.Action == "log";
				foreach(string kind in bp.Kinds.Split('|')) {
					string callbackType = kind switch { "read" => "read", "write" => "write", _ => "exec" };
					sb.Append("emu.addMemoryCallback(function(address, value) ")
					  .Append("local oldvalue = emu.read(address, emu.memType.").Append(luaMem).Append(") ")
					  .Append("local ok, met = pcall(function() return ").Append(condition).Append(" end) ")
					  .Append("if ok and met then hit(").Append(bp.Id).Append(", '").Append(kind).Append("', address, value, ").Append(log ? "true" : "false").Append(", ").Append(bp.MaxLogged).Append(") end ")
					  .Append("end, emu.callbackType.").Append(callbackType).Append(", ")
					  .Append(bp.Start.ToString(CultureInfo.InvariantCulture)).Append(", ").Append(bp.End.ToString(CultureInfo.InvariantCulture))
					  .Append(", nil, emu.memType.").Append(luaMem).Append(")\n");
				}
			}

			LoadScriptInternal(BpScriptName, sb.ToString(), true, null, cpu, out _);
			HarvestScript(BpScriptName, _scriptIds[BpScriptName], cpu);
		}

		private static string LuaMemTypeName(MemoryType type)
		{
			string s = type.ToString();
			return char.ToLowerInvariant(s[0]) + s.Substring(1);
		}

		private static readonly Regex ConditionTokenRegex = new Regex(
			@"\s*(?:(?<num>0[xX][0-9A-Fa-f]+|\$[0-9A-Fa-f]+|\d+)|(?<id>[A-Za-z_][A-Za-z_0-9]*)|(?<op>&&|\|\||==|!=|<=|>=|<<|>>|[-+*/%&|^~!<>()\[\]{}]))",
			RegexOptions.Compiled);

		/// <summary>Translates a Mesen-style condition expression to a Lua boolean/number expression.</summary>
		private string TranslateConditionToLua(string expression, MemoryType memType)
		{
			string luaMem = LuaMemTypeName(memType);
			StringBuilder sb = new StringBuilder();
			bool usesRegs = false;
			int pos = 0;
			int depth = 0;
			Stack<char> closers = new Stack<char>();

			while(pos < expression.Length) {
				if(char.IsWhiteSpace(expression[pos])) {
					pos++;
					continue;
				}
				Match m = ConditionTokenRegex.Match(expression, pos);
				if(!m.Success || m.Index != pos && expression.Substring(pos, m.Index - pos).Trim().Length > 0) {
					throw new McpToolException("invalid_condition", $"Cannot parse the condition '{expression}' near '{expression.Substring(pos)}'.");
				}
				pos = m.Index + m.Length;

				if(m.Groups["num"].Success) {
					string num = m.Groups["num"].Value;
					sb.Append(num.StartsWith("$", StringComparison.Ordinal) ? "0x" + num.Substring(1) : num).Append(' ');
				} else if(m.Groups["id"].Success) {
					string id = m.Groups["id"].Value.ToLowerInvariant();
					switch(id) {
						case "value": sb.Append("value "); break;
						case "oldvalue": sb.Append("oldvalue "); break;
						case "address": sb.Append("address "); break;
						case "iswrite": sb.Append("(false) "); break;
						case "isread": sb.Append("(false) "); break;
						case "a": case "x": case "y": case "sp": case "d": case "k": case "dbr": case "ps": case "pc":
							usesRegs = true;
							sb.Append("R.").Append(id).Append(' ');
							break;
						case "db": usesRegs = true; sb.Append("R.dbr "); break;
						case "p": usesRegs = true; sb.Append("R.ps "); break;
						case "frame":
						case "framecount": sb.Append("(BASE + FRAME) "); break;
						default:
							throw new McpToolException("invalid_condition", $"Unknown name '{m.Groups["id"].Value}' in the condition '{expression}' (supported for count/log breakpoints: value, oldvalue, address, pc, a, x, y, sp, d, k, dbr, ps, frame).");
					}
				} else {
					string op = m.Groups["op"].Value;
					switch(op) {
						case "&&": sb.Append("and "); break;
						case "||": sb.Append("or "); break;
						case "!=": sb.Append("~= "); break;
						case "!": sb.Append("not "); break;
						case "^": sb.Append("~ "); break;
						case "/": sb.Append("// "); break;
						case "[":
							sb.Append("emu.read(("); closers.Push('['); depth++;
							break;
						case "{":
							sb.Append("emu.read16(("); closers.Push('{'); depth++;
							break;
						case "]":
						case "}":
							if(closers.Count == 0) throw new McpToolException("invalid_condition", "Unbalanced brackets in the condition.");
							closers.Pop(); depth--;
							sb.Append("), emu.memType.").Append(luaMem).Append(") ");
							break;
						default: sb.Append(op).Append(' '); break;
					}
				}
			}

			if(closers.Count != 0) {
				throw new McpToolException("invalid_condition", "Unbalanced brackets in the condition.");
			}

			string body = sb.ToString();
			if(usesRegs) {
				body = "(function() local R = regs() return " + body + " end)()";
			}
			return body;
		}

		private McpToolResult ToolGetBreakpointHits(JsonElement args)
		{
			CpuType cpu = GetPrimaryCpuType();
			int? id = GetOptionalInt(args, "id");
			long sinceSeq = GetOptionalLong(args, "sinceSeq") ?? 0;
			int max = Math.Clamp(GetOptionalInt(args, "max") ?? 500, 1, ScriptLogRingSize);
			bool reset = GetOptionalBool(args, "reset") ?? false;

			if(_scriptIds.ContainsKey(BpScriptName)) {
				HarvestScriptLogs(cpu);
			}

			JsonObject counts = new JsonObject();
			string? rawResult = _logStore.GetResult(BpScriptName);
			JsonObject? resultNode = ParseResult(rawResult) as JsonObject;
			JsonObject? countsNode = resultNode?["counts"] as JsonObject;
			JsonArray countRows = new JsonArray();
			foreach(LuaBreakpointSpec bp in _luaBreakpoints) {
				if(id != null && bp.Id != id) {
					continue;
				}
				long count = 0;
				if(countsNode != null && countsNode["c" + bp.Id] is JsonNode c) {
					count = c.GetValue<long>();
				}
				countRows.Add((JsonNode)new JsonObject() {
					["id"] = bp.Id,
					["name"] = bp.Name,
					["action"] = bp.Action,
					["kinds"] = bp.Kinds,
					["start"] = bp.Start,
					["end"] = bp.End,
					["condition"] = bp.Condition,
					["count"] = Num(count)
				});
			}

			JsonArray hits = new JsonArray();
			long lastSeq = sinceSeq;
			McpScriptLogQuery query = _logStore.Query(BpScriptName, sinceSeq, ScriptLogRingSize);
			foreach(McpScriptLogEntry entry in query.Entries) {
				if(!entry.Text.StartsWith(BpRowPrefix, StringComparison.Ordinal)) {
					continue;
				}
				JsonNode? row;
				try { row = JsonNode.Parse(entry.Text.Substring(BpRowPrefix.Length)); } catch { continue; }
				if(row is not JsonObject obj) {
					continue;
				}
				if(id != null && obj["id"]?.GetValue<int>() != id) {
					continue;
				}
				if(hits.Count >= max) {
					break;
				}
				obj["seq"] = Num(entry.Seq);
				obj["pcHex"] = obj["pc"] is JsonNode pcNode ? $"{pcNode.GetValue<long>():X6}" : null;
				hits.Add((JsonNode)obj);
				lastSeq = entry.Seq;
			}

			JsonObject node = new JsonObject() {
				["counts"] = countRows,
				["hits"] = hits,
				["nextSeq"] = Num(hits.Count > 0 ? lastSeq : _logStore.NextSeq - 1),
				["frame"] = Num(GetFrameCount(cpu)),
				["nativeBreakpoints"] = _breakpoints.Count,
				["note"] = "Halting (action=break) breakpoints are reported through stopReason/break of the stepping tools."
			};
			if(reset) {
				LoadLuaBreakpointScript(cpu);
				_logStore.Clear();
				node["reset"] = true;
			}
			return Json(node);
		}

		/// <summary>Emits one breakpoint_hit event per count/log breakpoint whose count grew (called after harvesting).</summary>
		private void EmitLuaBreakpointEvents()
		{
			if(_luaBreakpoints.Count == 0 || !_scriptIds.ContainsKey(BpScriptName)) {
				return;
			}
			JsonObject? resultNode = ParseResult(_logStore.GetResult(BpScriptName)) as JsonObject;
			if(resultNode?["counts"] is not JsonObject countsNode) {
				return;
			}

			foreach(LuaBreakpointSpec bp in _luaBreakpoints) {
				if(countsNode["c" + bp.Id] is not JsonNode c) {
					continue;
				}
				long count = c.GetValue<long>();
				_bpEventCounts.TryGetValue(bp.Id, out long previous);
				if(count > previous) {
					_bpEventCounts[bp.Id] = count;
					JsonObject payload = new JsonObject() {
						["event"] = "breakpoint_hit",
						["breakpointId"] = bp.Id,
						["action"] = bp.Action,
						["name"] = bp.Name,
						["callbackType"] = bp.Kinds,
						["count"] = Num(count),
						["newHits"] = Num(count - previous),
						["frame"] = resultNode["frame"]?.DeepClone()
					};
					RaiseEvent(payload);
				}
			}
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-07: memory types, read/dump, palette
		// ---------------------------------------------------------------------------------------------

		private static string CamelName(MemoryType type)
		{
			string s = type.ToString();
			return char.ToLowerInvariant(s[0]) + s.Substring(1);
		}

		/// <summary>Any MemoryType enum name (case-insensitive) is accepted, plus a few aliases.</summary>
		private MemoryType ParseMemoryTypeEnum(string name, CpuType cpu)
		{
			string key = name.Trim();
			switch(key.ToLowerInvariant()) {
				case "cgram":
				case "palette":
					return cpu switch {
						CpuType.Snes => MemoryType.SnesCgRam,
						CpuType.Nes => MemoryType.NesPaletteRam,
						CpuType.Pce => MemoryType.PcePaletteRam,
						CpuType.Sms => MemoryType.SmsPaletteRam,
						CpuType.Gba => MemoryType.GbaPaletteRam,
						_ => throw new InvalidOperationException($"No palette memory for CPU {cpu}.")
					};
				case "vram":
				case "videoram":
					return cpu.GetVramMemoryType();
				case "spcram": return MemoryType.SpcRam;
				case "dsp":
				case "dspregisters": return MemoryType.SpcDspRegisters;
				case "register":
				case "registers": return MemoryType.SnesRegister;
			}

			foreach(MemoryType value in Enum.GetValues<MemoryType>()) {
				if(value != MemoryType.None && string.Equals(value.ToString(), key, StringComparison.OrdinalIgnoreCase)) {
					return value;
				}
			}
			throw new InvalidOperationException($"Unknown memory type: {name}. Use list_memory_types for the valid names.");
		}

		private McpToolResult ToolListMemoryTypes(JsonElement _)
		{
			JsonArray rows = new JsonArray();
			foreach(MemoryType type in Enum.GetValues<MemoryType>()) {
				if(type == MemoryType.None) {
					continue;
				}
				int size;
				try { size = DebugApi.GetMemorySize(type); } catch { continue; }
				if(size <= 0) {
					continue;
				}
				rows.Add((JsonNode)new JsonObject() {
					["name"] = CamelName(type),
					["enumName"] = type.ToString(),
					["sizeBytes"] = size
				});
			}
			return Json(new JsonObject() {
				["cpuType"] = GetPrimaryCpuType().ToString(),
				["memoryTypes"] = rows,
				["aliases"] = new JsonObject() {
					["cpu"] = "main CPU address space (debug view)",
					["workRam"] = "console work RAM",
					["prgRom"] = "program ROM",
					["cgram"] = "palette memory of the console",
					["vram"] = "video RAM",
					["oam"] = "sprite RAM"
				}
			});
		}

		private McpToolResult ToolReadMemory(JsonElement args)
		{
			int address = GetRequiredInt(args, "address");
			int length = GetRequiredInt(args, "length");
			if(length <= 0) {
				throw new InvalidOperationException("length must be > 0.");
			}
			string format = (GetOptionalString(args, "format") ?? "hex").ToLowerInvariant();

			CpuType cpu = GetPrimaryCpuType();
			MemoryType memType = ResolveMemoryType(GetOptionalString(args, "type") ?? "cpuDebug", cpu);
			int size = DebugApi.GetMemorySize(memType);
			if(address < 0 || address >= size) {
				throw new InvalidOperationException("address is out of range for selected memory type.");
			}

			bool truncated = length > MaxReadMemoryBytes;
			int count = Math.Min(Math.Min(length, size - address), MaxReadMemoryBytes);
			byte[] data = DebugApi.GetMemoryValues(memType, (uint)address, (uint)(address + count - 1));

			switch(format) {
				case "hex": {
					string text = FormatHexDump(data, address, memType);
					return McpToolResult.Text(truncated ? text + $"\n(truncated to {MaxReadMemoryBytes} bytes)" : text);
				}
				case "bytes": {
					JsonArray bytes = new JsonArray();
					foreach(byte b in data) {
						bytes.Add(JsonValue.Create((int)b));
					}
					return Json(new JsonObject() { ["memoryType"] = memType.ToString(), ["address"] = address, ["length"] = count, ["truncated"] = truncated, ["bytes"] = bytes });
				}
				case "words_le": {
					JsonArray words = new JsonArray();
					for(int i = 0; i + 1 < data.Length; i += 2) {
						words.Add(JsonValue.Create(data[i] | (data[i + 1] << 8)));
					}
					return Json(new JsonObject() { ["memoryType"] = memType.ToString(), ["address"] = address, ["length"] = count, ["truncated"] = truncated, ["words"] = words });
				}
				case "base64":
					return Json(new JsonObject() { ["memoryType"] = memType.ToString(), ["address"] = address, ["length"] = count, ["truncated"] = truncated, ["data"] = Convert.ToBase64String(data) });
				default:
					throw new InvalidOperationException("format must be hex, bytes, words_le or base64.");
			}
		}

		private McpToolResult ToolDumpMemory(JsonElement args)
		{
			CpuType cpu = GetPrimaryCpuType();
			MemoryType memType = ResolveMemoryType(GetRequiredString(args, "type"), cpu);
			int size = DebugApi.GetMemorySize(memType);
			int start = Math.Max(0, GetOptionalInt(args, "start") ?? 0);
			if(start >= size) {
				throw new InvalidOperationException($"start {start} is outside {memType} (size {size}).");
			}
			int length = Math.Clamp(GetOptionalInt(args, "length") ?? (size - start), 1, size - start);

			string path = ResolveOutputPath(GetRequiredString(args, "path"));
			string? folder = Path.GetDirectoryName(path);
			if(!string.IsNullOrWhiteSpace(folder)) {
				Directory.CreateDirectory(folder);
			}
			byte[] data = DebugApi.GetMemoryValues(memType, (uint)start, (uint)(start + length - 1));
			File.WriteAllBytes(path, data);

			return Json(new JsonObject() {
				["path"] = path,
				["memoryType"] = memType.ToString(),
				["start"] = start,
				["length"] = length,
				["sizeBytes"] = Num(new FileInfo(path).Length),
				["sha1"] = McpRomIdentity.Sha1OfBytes(data)
			});
		}

		private McpToolResult ToolGetPaletteInfoEx(JsonElement args)
		{
			CpuType cpu = ResolveCpuType(GetOptionalString(args, "cpuType")) ?? GetPrimaryCpuType();
			DebugPaletteInfo info = DebugApi.GetPaletteInfo(cpu, new GetPaletteInfoOptions());
			uint[] rgb = info.GetRgbPalette();

			string? format = GetOptionalString(args, "format")?.ToLowerInvariant();
			int? first = GetOptionalInt(args, "first");
			int? countArg = GetOptionalInt(args, "count");

			JsonObject node = new JsonObject() {
				["cpuType"] = cpu.ToString(),
				["paletteMemoryType"] = info.PaletteMemType.ToString(),
				["colorCount"] = info.ColorCount,
				["bgColorCount"] = info.BgColorCount,
				["spriteColorCount"] = info.SpriteColorCount,
				["colorsPerPalette"] = info.ColorsPerPalette
			};

			if(format == null && first == null && countArg == null) {
				node["rgbPalette"] = new JsonArray(rgb.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
				return Json(node);
			}

			if(format != null && format != "rgb888" && format != "rgb555") {
				throw new InvalidOperationException("format must be rgb888 or rgb555.");
			}
			int start = Math.Clamp(first ?? 0, 0, rgb.Length);
			int count = Math.Clamp(countArg ?? (rgb.Length - start), 0, rgb.Length - start);

			byte[]? raw = null;
			if(cpu == CpuType.Snes && info.PaletteMemType == MemoryType.SnesCgRam) {
				raw = DebugApi.GetMemoryState(MemoryType.SnesCgRam);
			}

			JsonArray colors = new JsonArray();
			for(int i = start; i < start + count; i++) {
				uint argb = rgb[i];
				int r = (int)((argb >> 16) & 0xFF), g = (int)((argb >> 8) & 0xFF), b = (int)(argb & 0xFF);
				JsonObject row = new JsonObject() {
					["index"] = i,
					["rgb888"] = $"{r:X2}{g:X2}{b:X2}"
				};
				if(raw != null && i * 2 + 1 < raw.Length) {
					row["rgb555"] = raw[i * 2] | (raw[i * 2 + 1] << 8);
				} else {
					row["rgb555"] = ((r >> 3) & 0x1F) | (((g >> 3) & 0x1F) << 5) | (((b >> 3) & 0x1F) << 10);
				}
				colors.Add((JsonNode)row);
			}
			node["first"] = start;
			node["count"] = count;
			node["format"] = format ?? "rgb888";
			node["rgb555Source"] = raw != null ? "raw CGRAM words" : "converted from rgb888";
			node["colors"] = colors;
			return Json(node);
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-14: access counters by range
		// ---------------------------------------------------------------------------------------------

		private McpToolResult ToolResetAccessCounters(JsonElement _)
		{
			DebugApi.ResetMemoryAccessCounts();
			return Json(new JsonObject() { ["reset"] = true });
		}

		private McpToolResult ToolGetAccessSummary(JsonElement args)
		{
			CpuType cpu = GetPrimaryCpuType();
			MemoryType memType = ResolveMemoryType(GetOptionalString(args, "type") ?? "prgRom", cpu);
			int size = DebugApi.GetMemorySize(memType);
			bool nonzeroOnly = GetOptionalBool(args, "nonzeroOnly") ?? false;
			bool wantCdl = GetOptionalBool(args, "cdl") ?? (memType == cpu.GetPrgRomMemoryType());

			JsonElement ranges;
			JsonDocument? fileDoc = null;
			try {
				string? rangesPath = GetOptionalString(args, "rangesPath");
				if(rangesPath != null) {
					string full = Path.IsPathRooted(rangesPath) ? rangesPath : ResolveOutputPath(rangesPath);
					fileDoc = JsonDocument.Parse(File.ReadAllText(full));
					ranges = fileDoc.RootElement;
					if(ranges.ValueKind == JsonValueKind.Object && TryGetProperty(ranges, "ranges", out JsonElement inner)) {
						ranges = inner;
					}
				} else if(!TryGetProperty(args, "ranges", out ranges)) {
					throw new InvalidOperationException("ranges or rangesPath is required.");
				}
				if(ranges.ValueKind != JsonValueKind.Array) {
					throw new InvalidOperationException("ranges must be a JSON array.");
				}

				JsonArray rows = new JsonArray();
				int unresolved = 0;
				long neverAccessed = 0;
				int index = 0;
				foreach(JsonElement range in ranges.EnumerateArray()) {
					string name = GetOptionalString(range, "name") ?? $"range{index}";
					index++;
					if(!TryResolveRange(range, cpu, memType, size, out int start, out int end, out string? error)) {
						unresolved++;
						rows.Add((JsonNode)new JsonObject() { ["name"] = name, ["error"] = error });
						continue;
					}

					int length = end - start + 1;
					AddressCounters[] counters = DebugApi.GetMemoryAccessCounts((uint)start, (uint)length, memType);
					long exec = 0, read = 0, write = 0;
					ulong firstStamp = ulong.MaxValue, lastStamp = 0;
					int touched = 0;
					foreach(AddressCounters c in counters) {
						exec += c.ExecCounter; read += c.ReadCounter; write += c.WriteCounter;
						if(c.ExecCounter + c.ReadCounter + c.WriteCounter > 0) {
							touched++;
						}
						foreach(ulong stamp in new[] { c.ExecStamp, c.ReadStamp, c.WriteStamp }) {
							if(stamp != 0) {
								firstStamp = Math.Min(firstStamp, stamp);
								lastStamp = Math.Max(lastStamp, stamp);
							}
						}
					}

					int cdlUnion = 0;
					int cdlCodeBytes = 0, cdlDataBytes = 0;
					if(wantCdl) {
						foreach(CdlFlags f in DebugApi.GetCdlData((uint)start, (uint)length, memType)) {
							cdlUnion |= (byte)f;
							if(((byte)f & (byte)CdlFlags.Code) != 0) cdlCodeBytes++;
							if(((byte)f & (byte)CdlFlags.Data) != 0) cdlDataBytes++;
						}
					}

					bool accessed = touched > 0 || cdlCodeBytes > 0 || cdlDataBytes > 0;
					if(!accessed) {
						neverAccessed++;
					}
					if(nonzeroOnly && !accessed) {
						continue;
					}

					JsonObject row = new JsonObject() {
						["name"] = name,
						["start"] = start,
						["end"] = end,
						["length"] = length,
						["exec"] = Num(exec),
						["read"] = Num(read),
						["write"] = Num(write),
						["bytesTouched"] = touched,
						["firstStamp"] = touched > 0 ? Num((long)Math.Min(firstStamp, long.MaxValue)) : null,
						["lastStamp"] = touched > 0 ? Num((long)Math.Min(lastStamp, long.MaxValue)) : null,
						["verdict"] = accessed ? "accessed" : "never_accessed"
					};
					if(wantCdl) {
						row["cdlFlags"] = cdlUnion;
						row["cdlCodeBytes"] = cdlCodeBytes;
						row["cdlDataBytes"] = cdlDataBytes;
					}
					rows.Add((JsonNode)row);
				}

				return Json(new JsonObject() {
					["memoryType"] = memType.ToString(),
					["rangeCount"] = index,
					["neverAccessed"] = Num(neverAccessed),
					["unresolved"] = unresolved,
					["rows"] = rows,
					["note"] = "Counters are cumulative since the last reset_access_counters (or ROM load). Stamps are master clock values."
				});
			} finally {
				fileDoc?.Dispose();
			}
		}

		private bool TryResolveRange(JsonElement range, CpuType cpu, MemoryType memType, int size, out int start, out int end, out string? error)
		{
			start = 0;
			end = 0;
			error = null;
			try {
				int? cpuStart = GetAddressArg(range, "cpuStart", "cpu");
				string? labelName = GetOptionalString(range, "label");
				if(labelName != null) {
					CodeLabel? label = LabelManager.GetLabel(labelName);
					if(label == null) {
						error = $"label '{labelName}' not found";
						return false;
					}
					int length = Math.Max(1, (GetOptionalInt(range, "length") ?? (int)label.Length));
					if(label.MemoryType == memType) {
						start = (int)label.Address;
					} else {
						AddressInfo abs = DebugApi.GetAbsoluteAddress(new AddressInfo() { Address = (int)label.Address, Type = label.MemoryType });
						if(abs.Type != memType) {
							error = $"label '{labelName}' lives in {label.MemoryType}, not {memType}";
							return false;
						}
						start = abs.Address;
					}
					end = start + length - 1;
				} else if(cpuStart != null) {
					int cpuEnd = GetAddressArg(range, "cpuEnd") ?? cpuStart.Value;
					AddressInfo a = DebugApi.GetAbsoluteAddress(new AddressInfo() { Address = cpuStart.Value, Type = cpu.ToMemoryType() });
					AddressInfo b = DebugApi.GetAbsoluteAddress(new AddressInfo() { Address = cpuEnd, Type = cpu.ToMemoryType() });
					if(a.Type != memType || b.Type != memType) {
						error = $"CPU range {cpuStart:X6}-{cpuEnd:X6} is not mapped to {memType}";
						return false;
					}
					start = a.Address;
					end = b.Address;
				} else {
					start = GetFirstInt(range, "start", "startAddress") ?? throw new InvalidOperationException("start is required");
					end = GetFirstInt(range, "end", "endAddress") ?? start;
				}
			} catch(Exception ex) when(ex is not McpToolException) {
				error = ex.Message;
				return false;
			}

			if(start < 0 || start >= size || end < start) {
				error = $"range {start:X}-{end:X} is outside {memType} (size {size:X})";
				return false;
			}
			end = Math.Min(end, size - 1);
			return true;
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-16: snapshots
		// ---------------------------------------------------------------------------------------------

		private McpToolResult ToolSnapshotMemory(JsonElement args)
		{
			string name = GetRequiredString(args, "name");
			CpuType cpu = GetPrimaryCpuType();
			MemoryType memType = ResolveMemoryType(GetOptionalString(args, "type") ?? "workRam", cpu);
			int size = DebugApi.GetMemorySize(memType);
			int start = Math.Max(0, GetOptionalInt(args, "start") ?? 0);
			if(start >= size) {
				throw new InvalidOperationException($"start {start} is outside {memType} (size {size}).");
			}
			int length = Math.Clamp(GetOptionalInt(args, "length") ?? (size - start), 1, size - start);

			long total = _snapshotStore.Where(s => !s.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Sum(s => (long)s.Value.Data.Length) + length;
			if((_snapshotStore.Count >= MaxSnapshots && !_snapshotStore.ContainsKey(name)) || total > MaxSnapshotBytes) {
				throw new McpToolException("snapshot_limit", $"Snapshot limit reached ({MaxSnapshots} snapshots, {MaxSnapshotBytes / (1024 * 1024)} MB). Delete some with delete_snapshot.");
			}

			byte[] data = DebugApi.GetMemoryValues(memType, (uint)start, (uint)(start + length - 1));
			_snapshotStore[name] = new McpMemorySnapshot() {
				Name = name,
				Type = memType,
				Start = start,
				Data = data,
				Frame = GetFrameCount(cpu)
			};
			return Json(new JsonObject() {
				["name"] = name,
				["memoryType"] = memType.ToString(),
				["start"] = start,
				["length"] = length,
				["frame"] = Num(_snapshotStore[name].Frame),
				["sha1"] = McpRomIdentity.Sha1OfBytes(data)
			});
		}

		private McpToolResult ToolListSnapshots(JsonElement _)
		{
			JsonArray rows = new JsonArray();
			foreach(McpMemorySnapshot s in _snapshotStore.Values.OrderBy(s => s.Created)) {
				rows.Add((JsonNode)new JsonObject() {
					["name"] = s.Name,
					["memoryType"] = s.Type.ToString(),
					["start"] = s.Start,
					["length"] = s.Data.Length,
					["frame"] = Num(s.Frame)
				});
			}
			return Json(new JsonObject() { ["snapshots"] = rows });
		}

		private McpToolResult ToolDeleteSnapshot(JsonElement args)
		{
			string name = GetRequiredString(args, "name");
			int removed;
			if(name == "*") {
				removed = _snapshotStore.Count;
				_snapshotStore.Clear();
			} else {
				removed = _snapshotStore.Remove(name) ? 1 : 0;
			}
			return Json(new JsonObject() { ["deleted"] = removed });
		}

		private McpToolResult ToolDiffSnapshots(JsonElement args)
		{
			string aName = GetRequiredString(args, "a");
			string bName = GetOptionalString(args, "b") ?? "current";
			if(!_snapshotStore.TryGetValue(aName, out McpMemorySnapshot? a)) {
				throw new McpToolException("unknown_snapshot", $"No snapshot named '{aName}'.");
			}

			McpMemorySnapshot b;
			if(bName.Equals("current", StringComparison.OrdinalIgnoreCase)) {
				b = new McpMemorySnapshot() {
					Name = "current",
					Type = a.Type,
					Start = a.Start,
					Data = DebugApi.GetMemoryValues(a.Type, (uint)a.Start, (uint)(a.Start + a.Data.Length - 1))
				};
			} else if(!_snapshotStore.TryGetValue(bName, out b!)) {
				throw new McpToolException("unknown_snapshot", $"No snapshot named '{bName}'.");
			}
			if(a.Type != b.Type) {
				throw new McpToolException("snapshot_mismatch", $"Snapshots have different memory types ({a.Type} vs {b.Type}).");
			}

			int lo = Math.Max(a.Start, b.Start);
			int hi = Math.Min(a.Start + a.Data.Length, b.Start + b.Data.Length) - 1;
			if(hi < lo) {
				throw new McpToolException("snapshot_mismatch", "The snapshots do not overlap.");
			}

			List<(int, int)> windows = new List<(int, int)>();
			if(TryGetProperty(args, "ranges", out JsonElement rangesElement) && rangesElement.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement window in rangesElement.EnumerateArray()) {
					if(window.ValueKind == JsonValueKind.Array && window.GetArrayLength() >= 2) {
						windows.Add((Math.Max(lo, window[0].GetInt32()), Math.Min(hi, window[1].GetInt32())));
					}
				}
			} else {
				windows.Add((lo, hi));
			}

			int mergeGap = Math.Max(0, GetOptionalInt(args, "mergeGap") ?? 0);
			int maxRows = Math.Clamp(GetOptionalInt(args, "maxRows") ?? 500, 1, 20000);
			JsonArray rows = new JsonArray();
			long changedBytes = 0;
			bool truncated = false;

			foreach((int ws, int we) in windows) {
				int i = ws;
				while(i <= we) {
					if(a.Data[i - a.Start] == b.Data[i - b.Start]) {
						i++;
						continue;
					}
					int runStart = i;
					int runEnd = i;
					int j = i + 1;
					while(j <= we && j - runEnd <= mergeGap + 1) {
						if(a.Data[j - a.Start] != b.Data[j - b.Start]) {
							runEnd = j;
						}
						j++;
					}
					for(int k = runStart; k <= runEnd; k++) {
						if(a.Data[k - a.Start] != b.Data[k - b.Start]) changedBytes++;
					}
					if(rows.Count >= maxRows) {
						truncated = true;
						break;
					}
					int len = runEnd - runStart + 1;
					rows.Add(new JsonArray(
						JsonValue.Create(runStart),
						JsonValue.Create(runEnd),
						JsonValue.Create(Hex(a.Data, runStart - a.Start, len)),
						JsonValue.Create(Hex(b.Data, runStart - b.Start, len))));
					i = runEnd + 1;
				}
				if(truncated) {
					break;
				}
			}

			return Json(new JsonObject() {
				["a"] = a.Name,
				["b"] = b.Name,
				["memoryType"] = a.Type.ToString(),
				["compared"] = new JsonArray(JsonValue.Create(lo), JsonValue.Create(hi)),
				["changedBytes"] = Num(changedBytes),
				["rowFormat"] = "[start, end, oldHex, newHex], end inclusive",
				["rowCount"] = rows.Count,
				["truncated"] = truncated,
				["rows"] = rows
			});
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-17: trace files
		// ---------------------------------------------------------------------------------------------

		private sealed class TraceFileInfo
		{
			public string Path = "";
			public int MaxLines;
			public CpuType Cpu;
			public DateTime Started = DateTime.UtcNow;
		}

		private TraceFileInfo? _traceFile;

		private static string BuildPcCondition(JsonElement ranges, CpuType cpu)
		{
			List<string> parts = new List<string>();
			foreach(JsonElement range in ranges.EnumerateArray()) {
				int start, end;
				if(range.ValueKind == JsonValueKind.Array && range.GetArrayLength() >= 1) {
					start = ParseCpuAddress(range[0]);
					end = range.GetArrayLength() > 1 ? ParseCpuAddress(range[1]) : start;
				} else {
					start = ParseCpuAddress(range);
					end = start;
				}
				parts.Add(start == end ? $"(pc == 0x{start:X})" : $"(pc >= 0x{start:X} && pc <= 0x{end:X})");
			}
			return string.Join(" || ", parts);
		}

		private static string DefaultTraceFormat(CpuType cpu)
		{
			switch(cpu) {
				case CpuType.Snes:
					return "[FrameCount,6] [PC,6h] [Disassembly][Align,24] A:[A,4h] X:[X,4h] Y:[Y,4h] S:[SP,4h] D:[D,4h] DB:[DB,2h] P:[P,8] ";
				case CpuType.Nes:
				case CpuType.Pce:
					return "[FrameCount,6] [PC,4h] [Disassembly][Align,24] A:[A,2h] X:[X,2h] Y:[Y,2h] S:[SP,2h] P:[P,8] ";
				default:
					return "[PC,h] [Disassembly]";
			}
		}

		private void BeginTrace(string path, string? condition, string? format, int maxLines, bool useLabels, CpuType cpu)
		{
			if(_traceFile != null) {
				throw new McpToolException("trace_active", $"A trace is already writing to {_traceFile.Path}. Call stop_trace first.");
			}

			string full = ResolveOutputPath(path);
			string? folder = Path.GetDirectoryName(full);
			if(!string.IsNullOrWhiteSpace(folder)) {
				Directory.CreateDirectory(folder);
			}

			InteropTraceLoggerOptions options = new InteropTraceLoggerOptions() {
				Enabled = true,
				IndentCode = false,
				UseLabels = useLabels,
				Condition = new byte[1000],
				Format = new byte[1000]
			};
			byte[] formatBytes = Encoding.UTF8.GetBytes(format ?? DefaultTraceFormat(cpu));
			if(formatBytes.Length > 999) {
				throw new InvalidOperationException("format is too long.");
			}
			Array.Copy(formatBytes, options.Format, formatBytes.Length);
			if(!string.IsNullOrWhiteSpace(condition)) {
				byte[] conditionBytes = Encoding.UTF8.GetBytes(condition!);
				if(conditionBytes.Length > 999) {
					throw new InvalidOperationException("condition is too long.");
				}
				DebugApi.EvaluateExpression(condition!, cpu, out EvalResultType resultType, false);
				if(resultType == EvalResultType.Invalid) {
					throw new McpToolException("invalid_condition", $"The condition '{condition}' is not a valid expression.");
				}
				Array.Copy(conditionBytes, options.Condition, conditionBytes.Length);
			}

			if(File.Exists(full)) {
				File.Delete(full);
			}
			DebugApi.SetTraceOptions(cpu, options);
			DebugApi.StartLogTraceToFile(full);
			_traceFile = new TraceFileInfo() { Path = full, MaxLines = maxLines, Cpu = cpu };
		}

		private JsonObject EndTrace()
		{
			if(_traceFile == null) {
				throw new McpToolException("no_trace", "No trace is active.");
			}
			TraceFileInfo info = _traceFile;
			_traceFile = null;
			DebugApi.StopLogTraceToFile();
			try {
				DebugApi.SetTraceOptions(info.Cpu, new InteropTraceLoggerOptions() { Enabled = false, Condition = new byte[1000], Format = new byte[1000] });
			} catch { }

			int lines = 0;
			bool trimmed = false;
			if(File.Exists(info.Path)) {
				string[] all = File.ReadAllLines(info.Path);
				lines = all.Length;
				if(info.MaxLines > 0 && all.Length > info.MaxLines) {
					File.WriteAllLines(info.Path, all.Take(info.MaxLines));
					lines = info.MaxLines;
					trimmed = true;
				}
			}

			JsonObject node = new JsonObject() {
				["path"] = info.Path,
				["lines"] = lines,
				["trimmedToMaxLines"] = trimmed
			};
			if(File.Exists(info.Path)) {
				node["sizeBytes"] = Num(new FileInfo(info.Path).Length);
				node["sha1"] = McpRomIdentity.Sha1OfFile(info.Path);
			}
			return node;
		}

		private McpToolResult ToolStartTrace(JsonElement args)
		{
			CpuType cpu = GetPrimaryCpuType();
			string? condition = GetOptionalString(args, "condition");
			if(TryGetProperty(args, "pcRanges", out JsonElement ranges) && ranges.ValueKind == JsonValueKind.Array && ranges.GetArrayLength() > 0) {
				string pcCondition = BuildPcCondition(ranges, cpu);
				condition = string.IsNullOrWhiteSpace(condition) ? pcCondition : $"({pcCondition}) && ({condition})";
			}

			BeginTrace(GetRequiredString(args, "path"), condition, GetOptionalString(args, "format"), GetOptionalInt(args, "maxLines") ?? 0, GetOptionalBool(args, "useLabels") ?? true, cpu);
			return Json(new JsonObject() {
				["tracing"] = true,
				["path"] = _traceFile!.Path,
				["condition"] = condition,
				["note"] = "Advance emulation (advance_frame, run_until, ...) to produce trace lines; call stop_trace when done."
			});
		}

		private McpToolResult ToolStopTrace(JsonElement _)
		{
			return Json(EndTrace());
		}

		private McpToolResult ToolTraceBetween(JsonElement args)
		{
			CpuType cpu = GetPrimaryCpuType();
			if(!TryGetProperty(args, "startPc", out JsonElement startElement) || !TryGetProperty(args, "stopPc", out JsonElement stopElement)) {
				throw new InvalidOperationException("startPc and stopPc are required.");
			}
			int startPc = ParseCpuAddress(startElement);
			int stopPc = ParseCpuAddress(stopElement);
			int maxFrames = Math.Clamp(GetOptionalInt(args, "maxFrames") ?? 600, 1, MaxFramesPerCall);
			bool turbo = ResolveTurbo(args);
			int timeoutMs = GetOptionalInt(args, "timeoutMs") ?? DefaultTimeoutMs(maxFrames, turbo);

			EnsurePaused(2000);

			StepOutcome phase1;
			using(BeginTurbo(turbo)) {
				using(new TempBreakpointScope(this, MakeExecBreakpoint(cpu, startPc))) {
					phase1 = RunFrames(cpu, maxFrames, timeoutMs);
				}
			}
			if(phase1.StopReason != "breakpoint") {
				JsonObject miss = BuildStepResponse(cpu, phase1);
				miss["triggered"] = false;
				miss["reason"] = "startPc was not reached";
				return Json(miss);
			}

			BeginTrace(GetRequiredString(args, "path"), GetOptionalString(args, "condition"), null, GetOptionalInt(args, "maxLines") ?? 0, true, cpu);
			StepOutcome phase2;
			try {
				using(BeginTurbo(turbo)) {
					//The break at startPc leaves the CPU on that instruction: step once so the next stop is a later hit.
					RunStep(cpu, 1, StepType.Step, 5000);
					using(new TempBreakpointScope(this, MakeExecBreakpoint(cpu, stopPc))) {
						phase2 = RunFrames(cpu, maxFrames, timeoutMs);
					}
				}
			} catch {
				try { EndTrace(); } catch { }
				throw;
			}

			JsonObject traceInfo = EndTrace();
			JsonObject node = BuildStepResponse(cpu, phase2);
			node["triggered"] = true;
			node["stopReached"] = phase2.StopReason == "breakpoint";
			node["trace"] = traceInfo;
			return Json(node);
		}

		private InteropBreakpoint MakeExecBreakpoint(CpuType cpu, int address)
		{
			return new InteropBreakpoint() {
				Id = TempBreakpointId,
				CpuType = cpu,
				MemoryType = cpu.ToMemoryType(),
				Type = BreakpointTypeFlags.Execute,
				StartAddress = address,
				EndAddress = address,
				Enabled = true,
				MarkEvent = false,
				IgnoreDummyOperations = true,
				Condition = new byte[1000]
			};
		}
	}
#pragma warning restore IL2090
#pragma warning restore IL2067
#pragma warning restore IL2070
#pragma warning restore IL3050
#pragma warning restore IL2026
}
