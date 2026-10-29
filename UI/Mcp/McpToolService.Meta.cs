using Mesen.Config;
using Mesen.Debugger;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mesen.Mcp
{
#pragma warning disable IL2026
#pragma warning disable IL3050
#pragma warning disable IL2070
#pragma warning disable IL2067
#pragma warning disable IL2090
	/// <summary>Event subscription (MCP-18) and capability report / self test (MCP-20).</summary>
	internal sealed partial class McpToolService
	{
		private static readonly Dictionary<string, string> EventTypeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
			["breakpoint_hit"] = "breakpointHit",
			["game_loaded"] = "romLoaded",
			["state_loaded"] = "stateLoaded",
			["frame_end"] = "frameEnd",
			["script_log"] = "scriptLog",
			["error"] = "error",
			["game_paused"] = "gamePaused",
			["game_resumed"] = "gameResumed",
			["emulation_stopped"] = "emulationStopped"
		};

		private static readonly string[] DefaultEventTypes = { "breakpointHit", "romLoaded", "gamePaused", "gameResumed", "emulationStopped" };

		private HashSet<string> _subscribedEvents = new HashSet<string>(DefaultEventTypes, StringComparer.OrdinalIgnoreCase);
		private int _frameEventEvery = 60;
		private long _lastFrameEvent = long.MinValue;
		private long _lastScriptLogEventSeq;
		private JsonArray? _luaFunctionsCache;
		private JsonObject? _lastSelfTest;

		private void AddMetaTools(Dictionary<string, McpToolDefinition> tools)
		{
			AddTool(tools, "subscribe_events",
				"Choose which server events are sent as notifications/mesen/event. Types and payloads: " +
				"breakpointHit {event:breakpoint_hit, breakpointId, cpuType, address, value, memoryType, operationType, callbackType; count/log breakpoints add action, count, newHits, frame}; " +
				"romLoaded {event:game_loaded}; stateLoaded {event:state_loaded, path}; frameEnd {event:frame_end, frame} (at most every frameEvery frames, sent when a stepping call returns); " +
				"scriptLog {event:script_log, nextSeq} (new script log rows); error {event:error, tool, message}; gamePaused, gameResumed, emulationStopped. " +
				"Default (never subscribed): breakpointHit, romLoaded, gamePaused, gameResumed, emulationStopped. An empty list disables all events.",
				ObjectSchema(
					("types", StringArraySchema("Event types to send"), true),
					("frameEvery", IntegerSchema("Minimum frames between frameEnd events (default 60)"), false)
				),
				ToolSubscribeEvents);

			AddTool(tools, "get_capabilities",
				"Report the runtime: server and Mesen version, CPU/debug features, tools, the Lua emu.* functions, flags (video/audio/input/scriptIo), limits, results/lib folders and the last self_test result.",
				EmptySchema(), ToolGetCapabilitiesEx);

			AddTool(tools, "self_test",
				"Run the capability matrix on the loaded ROM and return pass/fail per feature: memory write/read/exec callbacks, emu.write/read, setInput in inputPolled, Lua savestate round trip, " +
				"state file round trip, CDL, execution breakpoint, stepping, script log harvest, writeFile sandbox, screen buffer. The emulation state is restored afterwards.",
				EmptySchema(), ToolSelfTest);
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-18
		// ---------------------------------------------------------------------------------------------

		private void RaiseEvent(JsonObject payload)
		{
			string? eventName = payload["event"]?.GetValue<string>();
			if(eventName == null || !EventTypeNames.TryGetValue(eventName, out string? type) || !_subscribedEvents.Contains(type)) {
				return;
			}
			OnEvent?.Invoke(payload);
		}

		private bool IsSubscribed(string type) => _subscribedEvents.Contains(type);

		private McpToolResult ToolSubscribeEvents(JsonElement args)
		{
			List<string>? types = GetStringList(args, "types");
			if(types == null) {
				throw new InvalidOperationException("types array is required.");
			}

			HashSet<string> known = new HashSet<string>(EventTypeNames.Values, StringComparer.OrdinalIgnoreCase);
			HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach(string type in types) {
				string? match = known.FirstOrDefault(k => k.Equals(type, StringComparison.OrdinalIgnoreCase));
				if(match == null) {
					throw new InvalidOperationException($"Unknown event type '{type}'. Valid: {string.Join(", ", known.OrderBy(k => k))}.");
				}
				set.Add(match);
			}

			_subscribedEvents = set;
			_frameEventEvery = Math.Clamp(GetOptionalInt(args, "frameEvery") ?? _frameEventEvery, 1, 1000000);
			_lastFrameEvent = long.MinValue;
			_lastScriptLogEventSeq = _logStore.NextSeq;
			return Json(new JsonObject() {
				["subscribed"] = new JsonArray(set.OrderBy(s => s).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
				["frameEvery"] = _frameEventEvery,
				["available"] = new JsonArray(known.OrderBy(k => k).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray())
			});
		}

		/// <summary>Called after the script logs were harvested.</summary>
		private void AfterHarvest(CpuType cpu)
		{
			EmitLuaBreakpointEvents();
			if(IsSubscribed("scriptLog") && _logStore.NextSeq > _lastScriptLogEventSeq) {
				long newRows = _logStore.NextSeq - _lastScriptLogEventSeq;
				_lastScriptLogEventSeq = _logStore.NextSeq;
				RaiseEvent(new JsonObject() { ["event"] = "script_log", ["newRows"] = Num(newRows), ["nextSeq"] = Num(_logStore.NextSeq) });
			}
		}

		private void MaybeEmitFrameEvent(CpuType cpu)
		{
			if(!IsSubscribed("frameEnd")) {
				return;
			}
			long frame = GetFrameCount(cpu);
			if(_lastFrameEvent == long.MinValue || frame - _lastFrameEvent >= _frameEventEvery) {
				_lastFrameEvent = frame;
				RaiseEvent(new JsonObject() { ["event"] = "frame_end", ["frame"] = Num(frame) });
			}
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-20
		// ---------------------------------------------------------------------------------------------

		/// <summary>Loads a hidden script, optionally runs frames, returns its emu.mcp.result and unloads it.</summary>
		private JsonNode? RunHiddenScript(string name, string code, int frames, int timeoutMs = 20000)
		{
			CpuType cpu = GetPrimaryCpuType();
			EnsurePaused(2000);
			int id = LoadScriptInternal(name, code, true, null, cpu, out _);
			try {
				HarvestScript(name, id, cpu);
				if(frames > 0 && !_logStore.IsDone(name)) {
					using(BeginTurbo(true)) {
						RunFrames(cpu, frames, timeoutMs, () => _logStore.IsDone(name));
					}
				}
				HarvestScript(name, id, cpu);
				return ParseResult(_logStore.GetResult(name));
			} finally {
				try { DebugApi.RemoveScript(id); } catch { }
				_scriptIds.Remove(name);
				_scriptLogOffsets.Remove(id);
				_logStore.ForgetScript(id);
			}
		}

		private JsonArray GetLuaFunctionNames()
		{
			if(_luaFunctionsCache != null) {
				return _luaFunctionsCache;
			}
			JsonNode? result = RunHiddenScript("__caps",
				"local names = {} for k, v in pairs(emu) do if type(v) == 'function' then names[#names + 1] = k end end table.sort(names) " +
				"local mcp = {} for k, v in pairs(emu.mcp or {}) do if type(v) == 'function' then mcp[#mcp + 1] = k end end table.sort(mcp) " +
				"emu.mcp.result({functions = names, mcp = mcp})", 0);
			JsonArray names = new JsonArray();
			if(result is JsonObject obj && obj["functions"] is JsonArray fns) {
				foreach(JsonNode? fn in fns) {
					names.Add((JsonNode?)JsonValue.Create(fn?.GetValue<string>()));
				}
			}
			_luaFunctionsCache = names;
			return names;
		}

		private McpToolResult ToolGetCapabilitiesEx(JsonElement args)
		{
			JsonObject node = (JsonObject)JsonNode.Parse(ToolGetCapabilities(args).Content[0]!["text"]!.GetValue<string>())!;

			node["mesenVersion"] = EmuApi.GetMesenVersion().ToString();
			node["serverName"] = "mesen2-mcp";
			JsonArray toolRows = new JsonArray();
			foreach(string name in _tools.Keys.OrderBy(k => k, StringComparer.Ordinal)) {
				toolRows.Add((JsonNode)new JsonObject() { ["name"] = name, ["status"] = "available" });
			}
			node["tools"] = toolRows;

			JsonArray functions = GetLuaFunctionNames();
			node["lua"] = new JsonObject() {
				["functions"] = functions.DeepClone(),
				["emuMcp"] = new JsonArray((JsonNode?)"result", (JsonNode?)"done", (JsonNode?)"writeFile"),
				["scriptIo"] = false,
				["note"] = "io, os and require are disabled in scripts; use emu.mcp.writeFile for files."
			};

			if(node["flags"] is JsonObject flags) {
				flags["scriptIo"] = false;
				flags["turboDefault"] = _options.NoVideo && _options.NoAudio;
			}
			if(node["limits"] is JsonObject limits) {
				limits["maxReadBytes"] = MaxReadMemoryBytes;
				limits["maxSnapshots"] = MaxSnapshots;
				limits["scriptNativeLogRows"] = McpScriptLogStore.NativeLogRows;
			}
			node["breakpointConditions"] = new JsonObject() {
				["halting"] = "native Mesen expression (for example: value == 0x55, (value & 0x0200) != 0, a == 5)",
				["countLog"] = "translated to Lua: value, oldvalue, address, pc, a, x, y, sp, d, k, dbr, ps, frame, [addr], {addr}, && || ! == != < > <= >= & | ^ << >> + - * / %"
			};
			node["selectedSlot"] = _activeSlot;
			node["slots"] = new JsonArray(_slots.Keys.OrderBy(k => k).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
			if(_lastSelfTest != null) {
				node["lastSelfTest"] = _lastSelfTest.DeepClone();
			}
			return Json(node);
		}

		private McpToolResult ToolSelfTest(JsonElement _)
		{
			CpuType cpu = GetPrimaryCpuType();
			EnsurePaused(2000);

			JsonArray rows = new JsonArray();
			int failed = 0;
			void Add(string feature, bool pass, string detail = "")
			{
				if(!pass) failed++;
				rows.Add((JsonNode)new JsonObject() { ["feature"] = feature, ["pass"] = pass, ["detail"] = detail });
			}

			string tempState = Path.Combine(Path.GetTempPath(), $"mcp_selftest_{Guid.NewGuid():N}.mss");
			List<InteropBreakpoint> savedBreakpoints = new List<InteropBreakpoint>(_interopBps);
			EmuApi.SaveStateFile(tempState);
			long startFrame = GetFrameCount(cpu);

			try {
				MemoryType ram = cpu.GetSystemRamType();
				int ramSize = DebugApi.GetMemorySize(ram);
				string luaRam = LuaMemTypeName(ram);
				int ramEnd = Math.Max(0, Math.Min(ramSize - 1, 0x1FFF));
				int probe = Math.Max(0, Math.Min(ramSize - 16, 0x1F30));
				int cpuEnd = Math.Max(0, DebugApi.GetMemorySize(cpu.ToMemoryType()) - 1);

				string code =
					"local r = {writeCb = 0, readCb = 0, execCb = 0, inputCb = 0, frames = 0} " +
					"local ram = emu.memType." + luaRam + " " +
					"pcall(function() emu.addMemoryCallback(function() r.writeCb = r.writeCb + 1 end, emu.callbackType.write, 0, " + ramEnd + ") end) " +
					"pcall(function() emu.addMemoryCallback(function() r.readCb = r.readCb + 1 end, emu.callbackType.read, 0, " + ramEnd + ") end) " +
					"pcall(function() emu.addMemoryCallback(function() r.execCb = r.execCb + 1 " +
					"  if r.wantSave then r.wantSave = false " +
					"    local ok, s = pcall(emu.createSavestate) r.createSavestate = ok and type(s) == 'string' and #s > 100 r.createInfo = tostring(ok) .. ':' .. type(s) .. ':' .. (type(s) == 'string' and #s or tostring(s):sub(1, 80)) " +
					"    if r.createSavestate then emu.write(" + probe + ", 0x22, ram) local ok2, e2 = pcall(emu.loadSavestate, s) r.loadSavestateError = (not ok2) and tostring(e2) or nil r.savestateRoundTrip = emu.read(" + probe + ", ram) == 0x11 end " +
					"  end " +
					"end, emu.callbackType.exec, 0, " + cpuEnd + ") end) " +
					"emu.addEventCallback(function() r.inputCb = r.inputCb + 1 local ok, err = pcall(emu.setInput, {a = false}, 0) if not ok then r.setInputError = tostring(err) end end, emu.eventType.inputPolled) " +
					"emu.addEventCallback(function() " +
					"  r.frames = r.frames + 1 " +
					"  if r.frames == 1 then " +
					"    emu.write(" + probe + ", 0xA5, ram) r.rwOk = emu.read(" + probe + ", ram) == 0xA5 " +
					"    emu.write(" + probe + ", 0x11, ram) " +
					"    r.wantSave = true " +
					"  elseif r.frames >= 4 and r.frames < 34 then " +
					"    for i = 1, 40 do emu.log('selftest row ' .. r.frames .. '.' .. i) end " +
					"  elseif r.frames == 34 then " +
					"    emu.mcp.result(r) emu.mcp.done() " +
					"  end " +
					"end, emu.eventType.endFrame)";

				long seqBefore = _logStore.NextSeq;
				JsonObject? lua = null;
				string luaError = "";
				try {
					lua = RunHiddenScript("__selftest", code, 45) as JsonObject;
				} catch(Exception ex) {
					luaError = ex.Message;
				}
				McpScriptLogQuery logQuery = _logStore.Query("__selftest", seqBefore, ScriptLogRingSize);
				int rowCount = logQuery.Entries.Count(e => e.Text.StartsWith("selftest row ", StringComparison.Ordinal));

				if(lua == null) {
					Add("lua_script", false, "the self-test script produced no result " + luaError);
				} else {
					Add("lua_script", true, $"frames={lua["frames"]}");
					Add("write_callback", (lua["writeCb"]?.GetValue<long>() ?? 0) > 0, $"hits={lua["writeCb"]}");
					Add("read_callback", (lua["readCb"]?.GetValue<long>() ?? 0) > 0, $"hits={lua["readCb"]}");
					Add("exec_callback", (lua["execCb"]?.GetValue<long>() ?? 0) > 0, $"hits={lua["execCb"]}");
					Add("emu.write_read", lua["rwOk"]?.GetValue<bool>() == true);
					Add("setInput_in_inputPolled", (lua["inputCb"]?.GetValue<long>() ?? 0) > 0 && lua["setInputError"] == null,
						$"polls={lua["inputCb"]} error={lua["setInputError"]}");
					Add("lua_createSavestate", lua["createSavestate"]?.GetValue<bool>() == true, lua["createInfo"]?.ToString() ?? "");
					Add("lua_savestate_round_trip", lua["savestateRoundTrip"]?.GetValue<bool>() == true, lua["loadSavestateError"]?.ToString() ?? "");
				}
				Add("script_log_harvest_1200_rows", rowCount == 1200, $"harvested={rowCount}");

				// Reload the baseline for the managed checks.
				EmuApi.LoadStateFile(tempState);
				long frameAtBaseline = GetFrameCount(cpu);

				// state file round trip + stepping + breakpoint
				SnesPcSnapshot before = CapturePc(cpu);
				RunStep(cpu, 1, StepType.Step, 5000);
				SnesPcSnapshot afterStep = CapturePc(cpu);
				Add("step_instruction", afterStep.Pc != before.Pc || afterStep.Pc24 != before.Pc24, $"{before.Text} -> {afterStep.Text}");

				EmuApi.LoadStateFile(tempState);
				SnesPcSnapshot restored = CapturePc(cpu);
				Add("state_file_round_trip", restored.Pc24 == before.Pc24 && GetFrameCount(cpu) == frameAtBaseline, $"pc={restored.Text} frame={GetFrameCount(cpu)}");

				StepOutcome bp;
				using(new TempBreakpointScope(this, MakeExecBreakpoint(cpu, afterStep.Address))) {
					bp = RunFrames(cpu, 2, 5000);
				}
				Add("exec_breakpoint", bp.StopReason == "breakpoint" && bp.HasBreak && bp.Break.BreakpointId == TempBreakpointId, $"stopReason={bp.StopReason}");

				EmuApi.LoadStateFile(tempState);
				StepOutcome adv;
				using(BeginTurbo(true)) {
					adv = RunFrames(cpu, 120, 30000);
				}
				double fps = adv.ElapsedMs > 0 ? adv.FramesAdvanced * 1000.0 / adv.ElapsedMs : 0;
				Add("advance_frame", adv.StopReason == "done" && adv.FramesAdvanced == 120, $"frames={adv.FramesAdvanced} fps={fps:F0}");

				try {
					MemoryType prg = cpu.GetPrgRomMemoryType();
					CdlStatistics stats = DebugApi.GetCdlStatistics(prg);
					Add("cdl", stats.TotalBytes > 0 && stats.CodeBytes > 0, $"code={stats.CodeBytes} total={stats.TotalBytes}");
				} catch(Exception ex) {
					Add("cdl", false, ex.Message);
				}

				try {
					JsonNode? writeResult = RunHiddenScript("__selftest_io",
						"local ok = emu.mcp.writeFile('selftest/ok.txt', 'ok') local bad = emu.mcp.writeFile('../selftest_bad.txt', 'x') " +
						"emu.mcp.result({ok = ok == true, rejected = bad == nil, ioNil = (io == nil), osNil = (os == nil)})", 0);
					string probePath = Path.Combine(ResultsDir, "selftest", "ok.txt");
					bool written = File.Exists(probePath);
					Add("writeFile_sandbox", written && writeResult?["rejected"]?.GetValue<bool>() == true && !File.Exists(Path.Combine(Path.GetDirectoryName(ResultsDir) ?? ResultsDir, "selftest_bad.txt")),
						$"written={written} result={writeResult?.ToJsonString()}");
					Add("lua_io_disabled", writeResult?["ioNil"]?.GetValue<bool>() == true || writeResult?["osNil"]?.GetValue<bool>() == true, "io/os are not available to scripts");
					try { if(written) File.Delete(probePath); } catch { }
				} catch(Exception ex) {
					Add("writeFile_sandbox", false, ex.Message);
				}

				try {
					JsonNode? screen = RunHiddenScript("__selftest_screen",
						"local s = emu.getScreenSize() local b = emu.getScreenBuffer() emu.mcp.result({w = s.width, h = s.height, n = #b})", 0);
					Add("screen_buffer", screen?["n"]?.GetValue<long>() > 0, screen?.ToJsonString() ?? "");
				} catch(Exception ex) {
					Add("screen_buffer", false, ex.Message);
				}
			} finally {
				try { EmuApi.LoadStateFile(tempState); } catch { }
				try { File.Delete(tempState); } catch { }
				_interopBps = savedBreakpoints;
				DebugApi.SetBreakpoints(_interopBps.ToArray(), (uint)_interopBps.Count);
			}

			JsonObject node = new JsonObject() {
				["passed"] = rows.Count - failed,
				["failed"] = failed,
				["total"] = rows.Count,
				["frameRestoredTo"] = Num(startFrame),
				["results"] = rows
			};
			_lastSelfTest = (JsonObject)node.DeepClone();
			return Json(node);
		}

		private struct SnesPcSnapshot
		{
			public int Pc;
			public int Pc24;
			public int Address;
			public string Text;
		}

		private SnesPcSnapshot CapturePc(CpuType cpu)
		{
			if(cpu == CpuType.Snes) {
				SnesCpuState s = DebugApi.GetCpuState<SnesCpuState>(cpu);
				int pc24 = (s.K << 16) | s.PC;
				return new SnesPcSnapshot() { Pc = s.PC, Pc24 = pc24, Address = pc24, Text = $"{s.K:X2}:{s.PC:X4}" };
			}
			int pc = (int)DebugApi.GetProgramCounter(cpu, true);
			return new SnesPcSnapshot() { Pc = pc, Pc24 = pc, Address = pc, Text = pc.ToString("X", CultureInfo.InvariantCulture) };
		}
	}
#pragma warning restore IL2090
#pragma warning restore IL2067
#pragma warning restore IL2070
#pragma warning restore IL3050
#pragma warning restore IL2026
}
