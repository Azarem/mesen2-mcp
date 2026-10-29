using Mesen.Config;
using Mesen.Config.Shortcuts;
using Mesen.Debugger;
using Mesen.Debugger.Labels;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
	internal sealed partial class McpToolService : IDisposable
	{
		private readonly McpRuntimeOptions _options;
		private readonly Dictionary<string, McpToolDefinition> _tools;
		private readonly SemaphoreSlim _toolLock = new SemaphoreSlim(1, 1);
		private readonly Dictionary<int, DebugControllerState> _inputOverrides = new Dictionary<int, DebugControllerState>();
		private readonly Dictionary<string, int> _scriptIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<int, int> _scriptLogOffsets = new Dictionary<int, int>();
		private readonly List<McpBreakpointSpec> _breakpoints = new List<McpBreakpointSpec>();
		private readonly NotificationListener _listener;
		private readonly object _debuggerInitLock = new object();
		private readonly ManualResetEventSlim _stepDoneEvent = new ManualResetEventSlim(false);
		private bool _disposed;
		private bool _debuggerInitialized;

		public event Action<JsonObject>? OnEvent;

		public bool ShouldExit { get; private set; }
		public int ExitCode { get; private set; }

		public McpToolService(McpRuntimeOptions options)
		{
			_options = options;
			_tools = BuildTools();
			_listener = new NotificationListener();
			_listener.OnNotification += Listener_OnNotification;
		}

		public void Initialize()
		{
			if(!string.IsNullOrWhiteSpace(_options.StartupRomPath)) {
				try {
					LoadRomInternal(_options.StartupRomPath!, _options.SaveStatePath, _options.SaveStateSlot, requireRomMatch: true, lenientWhenNoSidecar: true);
				} catch(McpToolException ex) {
					//The ROM itself is loaded; only the startup save state was refused.
					_startupWarning = $"Startup save state not loaded ({ex.Code}): {ex.Message}";
					McpDebugLog.Info(_startupWarning);
					EmuApi.Resume();
				}
			}
		}

		public void RequestExit(int exitCode)
		{
			ShouldExit = true;
			ExitCode = exitCode;
		}

		public JsonArray ListTools()
		{
			JsonArray tools = new JsonArray();
			foreach(McpToolDefinition tool in _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal)) {
				tools.Add(new JsonObject() {
					["name"] = tool.Name,
					["description"] = tool.Description,
					["inputSchema"] = tool.InputSchema.DeepClone()
				});
			}
			return tools;
		}

		public McpToolResult CallTool(string name, JsonElement args)
		{
			if(!_tools.TryGetValue(name, out McpToolDefinition? tool)) {
				return McpToolResult.Text($"Unknown tool: {name}", true);
			}

			string? unknownArgument = FindUnknownArgument(tool.InputSchema, args);
			if(unknownArgument != null) {
				string valid = tool.InputSchema["properties"] is JsonObject props && props.Count > 0
					? string.Join(", ", props.Select(p => p.Key))
					: "(none)";
				return McpToolResult.Text($"Unknown argument '{unknownArgument}' for tool '{name}'. Valid arguments: {valid}.", true);
			}

			_toolLock.Wait();
			try {
				if(!name.Equals("load_rom", StringComparison.OrdinalIgnoreCase) &&
					!name.Equals("stop", StringComparison.OrdinalIgnoreCase) &&
					!EmuApi.IsRunning()) {
					return McpToolResult.Text("No ROM is running. Call load_rom first.", true);
				}

				if(!name.Equals("load_rom", StringComparison.OrdinalIgnoreCase) &&
					!name.Equals("stop", StringComparison.OrdinalIgnoreCase)) {
					EnsureDebuggerInitialized();
				}

				JsonElement safeArgs = args.ValueKind == JsonValueKind.Undefined ? default : args;

				//Optional ROM slot: load_rom registers the ROM under it, every other tool switches to it first.
				string? slot = GetOptionalString(safeArgs, "slot");
				if(slot != null && !name.Equals("switch_slot", StringComparison.OrdinalIgnoreCase) && !name.Equals("list_slots", StringComparison.OrdinalIgnoreCase) &&
					!name.Equals("stop", StringComparison.OrdinalIgnoreCase)) {
					if(name.Equals("load_rom", StringComparison.OrdinalIgnoreCase)) {
						if(!slot.Equals(_activeSlot, StringComparison.OrdinalIgnoreCase)) {
							EnsurePaused(2000);
							PersistActiveSlot();
						}
						_loadingSlot = slot;
					} else {
						SwitchToSlot(slot);
					}
				}

				McpToolResult result = tool.Handler(safeArgs);
				if(!result.IsError && name.Equals("load_state_file", StringComparison.OrdinalIgnoreCase)) {
					RaiseEvent(new JsonObject() { ["event"] = "state_loaded", ["path"] = GetOptionalString(safeArgs, "path") });
				}
				if(result.IsError) {
					RaiseEvent(new JsonObject() { ["event"] = "error", ["tool"] = name, ["message"] = result.Content.Count > 0 ? result.Content[0]?["text"]?.GetValue<string>() : null });
				}
				return result;
			} catch(McpToolException ex) {
				RaiseEvent(new JsonObject() { ["event"] = "error", ["tool"] = name, ["code"] = ex.Code, ["message"] = ex.Message });
				return ErrorJson(ex.Code, ex.Message, ex.Details);
			} catch(Exception ex) {
				RaiseEvent(new JsonObject() { ["event"] = "error", ["tool"] = name, ["message"] = ex.Message });
				return McpToolResult.Text($"Error: {ex.Message}", true);
			} finally {
				_loadingSlot = null;
				_toolLock.Release();
			}
		}

		private static string? FindUnknownArgument(JsonObject schema, JsonElement args)
		{
			if(args.ValueKind != JsonValueKind.Object ||
				schema["additionalProperties"] is not JsonValue additional ||
				!additional.TryGetValue(out bool allowed) || allowed ||
				schema["properties"] is not JsonObject properties) {
				return null;
			}

			foreach(JsonProperty argument in args.EnumerateObject()) {
				bool known = false;
				foreach(KeyValuePair<string, JsonNode?> property in properties) {
					if(string.Equals(property.Key, argument.Name, StringComparison.OrdinalIgnoreCase)) {
						known = true;
						break;
					}
				}
				if(!known) {
					return argument.Name;
				}
			}
			return null;
		}

		private Dictionary<string, McpToolDefinition> BuildTools()
		{
			Dictionary<string, McpToolDefinition> tools = new Dictionary<string, McpToolDefinition>(StringComparer.OrdinalIgnoreCase);


			AddTool(tools, "pause", "Pause emulation.", EmptySchema(), _ => { EmuApi.Pause(); return McpToolResult.Text("Emulation paused."); });
			AddTool(tools, "resume", "Resume emulation.", EmptySchema(), _ => { DebugApi.ResumeExecution(); EmuApi.Resume(); return McpToolResult.Text("Emulation resumed."); });
			AddTool(tools, "reset", "Soft reset emulation.", EmptySchema(), _ => {
				EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = EmulatorShortcut.Reset });
				return McpToolResult.Text("Emulation reset.");
			});
			AddTool(tools, "stop", "Stop emulation and exit MCP server.", ObjectSchema(("exitCode", IntegerSchema("Exit code"), false)), ToolStop);
			AddTool(tools, "get_timing_info", "Get frame, scanline and cycle timing info.", EmptySchema(), ToolGetTimingInfo);
			AddTool(tools, "rewind", "Rewind emulation by seconds.", ObjectSchema(("seconds", IntegerSchema("Seconds to rewind"), true)), ToolRewind);

			AddTool(tools, "read_memory", "Read bytes from memory.", ObjectSchema(
				("address", IntegerSchema("Start address"), true),
				("length", IntegerSchema("Byte length"), true),
				("type", StringSchema("Memory type"), false)
			), ToolReadMemory);
			AddTool(tools, "write_memory", "Write bytes to memory.", ObjectSchema(
				("address", IntegerSchema("Start address"), true),
				("data", new JsonObject() { ["type"] = "array", ["items"] = IntegerSchema("Byte"), ["description"] = "Byte array" }, true),
				("type", StringSchema("Memory type"), false)
			), ToolWriteMemory);
			AddTool(tools, "read_word", "Read a 16-bit little-endian word.", ObjectSchema(
				("address", IntegerSchema("Address"), true),
				("type", StringSchema("Memory type"), false)
			), ToolReadWord);
			AddTool(tools, "write_word", "Write a 16-bit little-endian word.", ObjectSchema(
				("address", IntegerSchema("Address"), true),
				("value", IntegerSchema("Value 0-65535"), true),
				("type", StringSchema("Memory type"), false)
			), ToolWriteWord);
			AddTool(tools, "revert_rom_changes", "Undo memory edits when undo history is available.", EmptySchema(), ToolRevertRomChanges);
			AddTool(tools, "get_prg_rom_offset", "Convert CPU address to PRG ROM offset if mapped.", ObjectSchema(("address", IntegerSchema("CPU address"), true)), ToolGetPrgRomOffset);
			AddTool(tools, "get_chr_rom_offset", "Convert PPU address to CHR ROM offset if mapped.", ObjectSchema(("address", IntegerSchema("PPU address"), true)), ToolGetChrRomOffset);

			AddTool(tools, "get_emulator_state", "Get CPU/PPU/timing state snapshot.", EmptySchema(), ToolGetEmulatorState);
			AddTool(tools, "set_cpu_state", "Patch CPU and/or PPU registers.", ObjectSchema(("state", new JsonObject() { ["type"] = "object", ["description"] = "Partial cpu/ppu state object" }, true)), ToolSetCpuState);
			AddTool(tools, "set_breakpoints", "Replace active breakpoints.", ObjectSchema(("breakpoints", new JsonObject() { ["type"] = "array", ["description"] = "Breakpoint list" }, true)), ToolSetBreakpoints);
			AddTool(tools, "clear_breakpoints", "Clear all breakpoints.", EmptySchema(), ToolClearBreakpoints);

			AddTool(tools, "get_input", "Get current override state for a controller port.", ObjectSchema(("port", IntegerSchema("Port index 0-7"), false)), ToolGetInput);
			AddTool(tools, "clear_input", "Clear input override for one or all ports.", ObjectSchema(("port", IntegerSchema("Optional port"), false)), ToolClearInput);

			AddTool(tools, "get_label_address", "Resolve debugger label to address.", ObjectSchema(("label", StringSchema("Label name"), true)), ToolGetLabelAddress);

			AddTool(tools, "get_capabilities", "Report current runtime and CPU/debug feature capabilities.", EmptySchema(), ToolGetCapabilities);
			AddTool(tools, "get_disassembly", "Get disassembly rows from an address.", ObjectSchema(
				("address", IntegerSchema("Start address (defaults to current PC)"), false),
				("rowCount", IntegerSchema("Number of rows"), false),
				("cpuType", StringSchema("Optional CPU type"), false)
			), ToolGetDisassembly);
			AddTool(tools, "get_profiler_data", "Get profiler function data.", ObjectSchema(("cpuType", StringSchema("Optional CPU type"), false)), ToolGetProfilerData);
			AddTool(tools, "get_cdl_statistics", "Get code/data logger statistics.", ObjectSchema(("memoryType", StringSchema("Optional memory type"), false)), ToolGetCdlStatistics);
			AddTool(tools, "get_tilemap_size", "Get tilemap display size for a layer.", ObjectSchema(
				("layer", IntegerSchema("Layer index"), false),
				("cpuType", StringSchema("Optional CPU type"), false)
			), ToolGetTilemapSize);

			AddExtendedTools(tools);
			AddWeek2Tools(tools);
			AddDebugTools(tools);
			AddVideoTools(tools);
			AddMetaTools(tools);
			AddInputTools(tools);
			return tools;
		}

		private static void AddTool(Dictionary<string, McpToolDefinition> tools, string name, string description, JsonObject schema, Func<JsonElement, McpToolResult> handler)
		{
			tools[name] = new McpToolDefinition() {
				Name = name,
				Description = description,
				InputSchema = schema,
				Handler = handler
			};
		}

		private static JsonObject EmptySchema()
		{
			return new JsonObject() {
				["type"] = "object",
				["properties"] = new JsonObject(),
				["additionalProperties"] = false
			};
		}

		private static JsonObject StringSchema(string description) => new JsonObject() { ["type"] = "string", ["description"] = description };
		private static JsonObject IntegerSchema(string description) => new JsonObject() { ["type"] = "integer", ["description"] = description };
		private static JsonObject BooleanSchema(string description) => new JsonObject() { ["type"] = "boolean", ["description"] = description };

		private static JsonObject ObjectSchema(params (string name, JsonObject schema, bool required)[] properties)
		{
			JsonObject propertyObj = new JsonObject();
			JsonArray required = new JsonArray();
			foreach((string name, JsonObject schema, bool isRequired) in properties) {
				propertyObj[name] = schema;
				if(isRequired) {
					required.Add(name);
				}
			}

			return new JsonObject() {
				["type"] = "object",
				["properties"] = propertyObj,
				["required"] = required,
				["additionalProperties"] = false
			};
		}

		private JsonObject? LoadRomInternal(string romPath, string? saveStatePath, int? saveStateSlot, bool requireRomMatch, bool lenientWhenNoSidecar)
		{
			if(!File.Exists(romPath)) {
				throw new FileNotFoundException("ROM file not found.", romPath);
			}

			if(!string.IsNullOrWhiteSpace(saveStatePath) && saveStateSlot != null) {
				throw new InvalidOperationException("saveStatePath and saveStateSlot are mutually exclusive.");
			}

			//Scripts do not survive a ROM load: drop the server-side bookkeeping with them.
			ForgetAllScripts();

			if(!EmuApi.LoadRom(romPath, string.Empty)) {
				throw new InvalidOperationException("Failed to load ROM.");
			}
			EnsureDebuggerInitialized();

			if(saveStateSlot != null) {
				saveStatePath = ResolveStateSlot(romPath, saveStateSlot.Value);
			}

			JsonObject? stateInfo = null;
			if(!string.IsNullOrWhiteSpace(saveStatePath)) {
				if(!File.Exists(saveStatePath)) {
					throw new FileNotFoundException("Save-state file not found.", saveStatePath);
				}
				//Throws rom_mismatch before anything is loaded when the state belongs to another ROM.
				stateInfo = CheckStateAgainstLoadedRom(saveStatePath, requireRomMatch, lenientWhenNoSidecar);
				EmuApi.LoadStateFile(saveStatePath);
				stateInfo["loaded"] = true;
			}

			RegisterSlotForLoadedRom(romPath);
			EmuApi.Resume();
			return stateInfo;
		}

		private McpToolResult ToolStop(JsonElement args)
		{
			int exitCode = GetOptionalInt(args, "exitCode") ?? 0;
			try {
				EmuApi.Stop();
			} catch { }
			RequestExit(exitCode);
			return McpToolResult.Text("Emulation stopped. MCP server exiting.");
		}

		private McpToolResult ToolGetTimingInfo(JsonElement _)
		{
			CpuType cpu = GetPrimaryCpuType();
			TimingInfo timing = EmuApi.GetTimingInfo(cpu);
			JsonObject node = new JsonObject() {
				["cpuType"] = cpu.ToString(),
				["fps"] = timing.Fps,
				["frameCount"] = timing.FrameCount,
				["scanlineCount"] = timing.ScanlineCount,
				["firstScanline"] = timing.FirstScanline,
				["cycleCount"] = timing.CycleCount,
				["masterClock"] = timing.MasterClock,
				["masterClockRate"] = timing.MasterClockRate
			};
			return McpToolResult.Text(node.ToJsonString(new JsonSerializerOptions() { WriteIndented = true }));
		}

		private McpToolResult ToolRewind(JsonElement args)
		{
			int seconds = Math.Max(1, GetRequiredInt(args, "seconds"));
			int remaining = seconds;
			while(remaining >= 60) {
				EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = EmulatorShortcut.RewindOneMin });
				remaining -= 60;
			}
			while(remaining >= 10) {
				EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = EmulatorShortcut.RewindTenSecs });
				remaining -= 10;
			}
			while(remaining > 0) {
				EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = EmulatorShortcut.Rewind });
				remaining--;
			}
			return McpToolResult.Text($"Queued rewind for {seconds} second(s).");
		}

		private McpToolResult ToolWriteMemory(JsonElement args)
		{
			int address = GetRequiredInt(args, "address");
			CpuType cpu = GetPrimaryCpuType();
			MemoryType memType = ResolveMemoryType(GetOptionalString(args, "type") ?? "cpuDebug", cpu);
			byte[] data = GetByteArray(args, "data");
			if(data.Length == 0) {
				throw new InvalidOperationException("data array cannot be empty.");
			}

			int size = DebugApi.GetMemorySize(memType);
			if(address < 0 || address + data.Length > size) {
				throw new InvalidOperationException("write range exceeds memory bounds.");
			}
			DebugApi.SetMemoryValues(memType, (uint)address, data, data.Length);
			return McpToolResult.Text($"Wrote {data.Length} byte(s) to 0x{address:X} ({memType}).");
		}

		private McpToolResult ToolReadWord(JsonElement args)
		{
			int address = GetRequiredInt(args, "address");
			CpuType cpu = GetPrimaryCpuType();
			MemoryType memType = ResolveMemoryType(GetOptionalString(args, "type") ?? "cpuDebug", cpu);
			byte[] data = DebugApi.GetMemoryValues(memType, (uint)address, (uint)(address + 1));
			int value = data[0] | (data[1] << 8);
			return McpToolResult.Text($"0x{value:X4} ({value})");
		}

		private McpToolResult ToolWriteWord(JsonElement args)
		{
			int address = GetRequiredInt(args, "address");
			int value = GetRequiredInt(args, "value");
			CpuType cpu = GetPrimaryCpuType();
			MemoryType memType = ResolveMemoryType(GetOptionalString(args, "type") ?? "cpuDebug", cpu);
			byte[] data = new byte[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };
			DebugApi.SetMemoryValues(memType, (uint)address, data, data.Length);
			return McpToolResult.Text($"Wrote 0x{value:X4} at 0x{address:X} ({memType}).");
		}

		private McpToolResult ToolRevertRomChanges(JsonElement _)
		{
			int count = 0;
			while(DebugApi.HasUndoHistory() && count < 100000) {
				DebugApi.PerformUndo();
				count++;
			}
			return McpToolResult.Text($"Performed {count} undo operation(s).");
		}

		private McpToolResult ToolGetPrgRomOffset(JsonElement args)
		{
			int address = GetRequiredInt(args, "address");
			CpuType cpu = GetPrimaryCpuType();
			AddressInfo abs = DebugApi.GetAbsoluteAddress(new AddressInfo() {
				Address = address,
				Type = cpu.ToMemoryType()
			});

			int value = abs.Type == cpu.GetPrgRomMemoryType() ? abs.Address : -1;
			return McpToolResult.Text(value >= 0 ? $"0x{value:X} ({value})" : "-1");
		}

		private McpToolResult ToolGetChrRomOffset(JsonElement args)
		{
			int address = GetRequiredInt(args, "address");
			CpuType cpu = GetPrimaryCpuType();
			if(cpu != CpuType.Nes) {
				throw new InvalidOperationException("get_chr_rom_offset is currently supported for NES only.");
			}

			AddressInfo abs = DebugApi.GetAbsoluteAddress(new AddressInfo() {
				Address = address,
				Type = MemoryType.NesPpuMemory
			});

			int value = (abs.Type == MemoryType.NesChrRom || abs.Type == MemoryType.NesChrRam) ? abs.Address : -1;
			return McpToolResult.Text(value >= 0 ? $"0x{value:X} ({value})" : "-1");
		}

		private McpToolResult ToolGetEmulatorState(JsonElement _)
		{
			CpuType cpu = GetPrimaryCpuType();
			JsonObject node = new JsonObject() {
				["cpuType"] = cpu.ToString(),
				["cpu"] = GetCpuStateNode(cpu),
				["ppu"] = GetPpuStateNode(cpu),
				["timing"] = BuildTimingNode(cpu)
			};
			return McpToolResult.Text(node.ToJsonString(new JsonSerializerOptions() { WriteIndented = true }));
		}

		private McpToolResult ToolSetCpuState(JsonElement args)
		{
			if(!TryGetProperty(args, "state", out JsonElement statePatch) || statePatch.ValueKind != JsonValueKind.Object) {
				throw new InvalidOperationException("state object is required.");
			}

			CpuType cpu = GetPrimaryCpuType();
			if(TryGetProperty(statePatch, "cpu", out JsonElement cpuPatch) && cpuPatch.ValueKind == JsonValueKind.Object) {
				ApplyCpuPatch(cpu, cpuPatch);
			}
			if(TryGetProperty(statePatch, "ppu", out JsonElement ppuPatch) && ppuPatch.ValueKind == JsonValueKind.Object) {
				ApplyPpuPatch(cpu, ppuPatch);
			}
			return McpToolResult.Text("CPU/PPU state updated.");
		}

		private McpToolResult ToolGetInput(JsonElement args)
		{
			int port = Math.Clamp(GetOptionalInt(args, "port") ?? 0, 0, 7);
			DebugControllerState state = _inputOverrides.TryGetValue(port, out DebugControllerState saved) ? saved : new DebugControllerState();
			JsonObject node = ControllerStateToNode(state);
			node["port"] = port;
			return McpToolResult.Text(node.ToJsonString(new JsonSerializerOptions() { WriteIndented = true }));
		}

		private McpToolResult ToolClearInput(JsonElement args)
		{
			int? port = GetOptionalInt(args, "port");
			if(port != null) {
				ClearInputPort(Math.Clamp(port.Value, 0, 7));
				return McpToolResult.Text($"Input override cleared for port {port.Value}.");
			}

			for(int i = 0; i < 8; i++) {
				ClearInputPort(i);
			}
			return McpToolResult.Text("Input overrides cleared for all ports.");
		}

		private void ClearInputPort(int port)
		{
			_inputOverrides.Remove(port);
			DebugApi.SetInputOverrides((uint)port, new DebugControllerState());
		}

		private static void AppendLines(List<string> lines, string text)
		{
			if(string.IsNullOrWhiteSpace(text)) {
				return;
			}
			foreach(string line in text.Replace("\r\n", "\n").Split('\n')) {
				if(line.Length > 0) {
					lines.Add(line);
				}
			}
		}

		private McpToolResult ToolGetLabelAddress(JsonElement args)
		{
			string label = GetRequiredString(args, "label");
			CodeLabel? result = LabelManager.GetLabel(label);
			if(result == null) {
				throw new InvalidOperationException($"Label not found: {label}");
			}
			return McpToolResult.Text($"0x{result.Address:X} ({result.Address})");
		}

		private McpToolResult ToolGetCapabilities(JsonElement _)
		{
			CpuType cpu = GetPrimaryCpuType();
			JsonObject node = new JsonObject() {
				["cpuType"] = cpu.ToString(),
				["consoleType"] = cpu.GetConsoleType().ToString(),
				["supportsAssembler"] = cpu.SupportsAssembler(),
				["supportsCallStack"] = cpu.SupportsCallStack(),
				["supportsFunctionList"] = cpu.SupportsFunctionList(),
				["supportsMemoryMappings"] = cpu.SupportsMemoryMappings(),
				["hasDummyOperations"] = cpu.HasDummyOperations(),
				["memoryTypes"] = new JsonArray(GetSupportedMemoryTypeNames(cpu).Select(x => (JsonNode?)x).ToArray()),
				["serverVersion"] = ServerVersion,
				["resultsDir"] = ResultsDir,
				["libDir"] = LibDir,
				["flags"] = new JsonObject() {
					["video"] = !_options.NoVideo,
					["audio"] = !_options.NoAudio,
					["input"] = !_options.NoInput,
					["turboDefault"] = _options.NoVideo && _options.NoAudio
				},
				["limits"] = new JsonObject() {
					["maxFramesPerCall"] = MaxFramesPerCall,
					["scriptLogRing"] = ScriptLogRingSize,
					["maxCdlBytes"] = MaxCdlBase64Bytes
				},
				["labelCount"] = LabelManager.GetAllLabels().Count
			};
			return McpToolResult.Text(node.ToJsonString(new JsonSerializerOptions() { WriteIndented = true }));
		}

		private McpToolResult ToolGetDisassembly(JsonElement args)
		{
			CpuType cpu = ResolveCpuType(GetOptionalString(args, "cpuType")) ?? GetPrimaryCpuType();
			int address = GetOptionalInt(args, "address") ?? (int)DebugApi.GetProgramCounter(cpu, true);
			int rowCount = Math.Clamp(GetOptionalInt(args, "rowCount") ?? 40, 1, 1000);

			CodeLineData[] rows = DebugApi.GetDisassemblyOutput(cpu, (uint)address, (uint)rowCount);
			JsonArray outRows = new JsonArray();
			foreach(CodeLineData row in rows) {
				outRows.Add(new JsonObject() {
					["address"] = row.Address,
					["text"] = row.Text,
					["byteCode"] = row.ByteCodeStr.Trim(),
					["comment"] = row.Comment
				});
			}

			JsonObject root = new JsonObject() {
				["cpuType"] = cpu.ToString(),
				["rows"] = outRows
			};
			return McpToolResult.Text(root.ToJsonString(new JsonSerializerOptions() { WriteIndented = true }));
		}

		private McpToolResult ToolGetProfilerData(JsonElement args)
		{
			CpuType cpu = ResolveCpuType(GetOptionalString(args, "cpuType")) ?? GetPrimaryCpuType();
			ProfiledFunction[] data = new ProfiledFunction[20000];
			int count = DebugApi.GetProfilerData(cpu, ref data);
			count = Math.Clamp(count, 0, data.Length);
			JsonArray rows = new JsonArray();
			for(int i = 0; i < count; i++) {
				ProfiledFunction f = data[i];
				rows.Add(new JsonObject() {
					["address"] = f.Address.Address,
					["memoryType"] = f.Address.Type.ToString(),
					["exclusiveCycles"] = (long)f.ExclusiveCycles,
					["inclusiveCycles"] = (long)f.InclusiveCycles,
					["callCount"] = (long)f.CallCount,
					["minCycles"] = (long)f.MinCycles,
					["maxCycles"] = (long)f.MaxCycles
				});
			}
			return McpToolResult.Text(new JsonObject() { ["cpuType"] = cpu.ToString(), ["rows"] = rows }.ToJsonString(new JsonSerializerOptions() { WriteIndented = true }));
		}

		private McpToolResult ToolGetCdlStatistics(JsonElement args)
		{
			CpuType cpu = GetPrimaryCpuType();
			MemoryType memType = ResolveMemoryType(GetOptionalString(args, "memoryType") ?? "prgRom", cpu);
			CdlStatistics stats = DebugApi.GetCdlStatistics(memType);
			JsonObject node = new JsonObject() {
				["memoryType"] = memType.ToString(),
				["codeBytes"] = stats.CodeBytes,
				["dataBytes"] = stats.DataBytes,
				["totalBytes"] = stats.TotalBytes,
				["jumpTargetCount"] = stats.JumpTargetCount,
				["functionCount"] = stats.FunctionCount,
				["drawnChrBytes"] = stats.DrawnChrBytes,
				["totalChrBytes"] = stats.TotalChrBytes
			};
			return McpToolResult.Text(node.ToJsonString(new JsonSerializerOptions() { WriteIndented = true }));
		}

		private McpToolResult ToolGetTilemapSize(JsonElement args)
		{
			CpuType cpu = ResolveCpuType(GetOptionalString(args, "cpuType")) ?? GetPrimaryCpuType();
			byte layer = (byte)Math.Clamp(GetOptionalInt(args, "layer") ?? 0, 0, 7);

			GetTilemapOptions options = new GetTilemapOptions() { Layer = layer };
			BaseState ppuState = DebugApi.GetPpuState(cpu);
			FrameInfo size = DebugApi.GetTilemapSize(cpu, options, ppuState);
			JsonObject node = new JsonObject() {
				["cpuType"] = cpu.ToString(),
				["layer"] = layer,
				["width"] = size.Width,
				["height"] = size.Height
			};
			return McpToolResult.Text(node.ToJsonString(new JsonSerializerOptions() { WriteIndented = true }));
		}

		private string ResolveStateSlot(string romPath, int slot)
		{
			string romBase = Path.GetFileNameWithoutExtension(romPath);
			return Path.Combine(ConfigManager.HomeFolder, "SaveStates", $"{romBase}_{slot}.mss");
		}

		private void Listener_OnNotification(NotificationEventArgs e)
		{
			switch(e.NotificationType) {
				case ConsoleNotificationType.CodeBreak:
					BreakEvent br = System.Runtime.InteropServices.Marshal.PtrToStructure<BreakEvent>(e.Parameter);
					HandleStepCompletionNotification(br);
					JsonObject breakPayload = new JsonObject() {
						["event"] = "breakpoint_hit",
						["breakpointId"] = br.BreakpointId,
						["cpuType"] = br.SourceCpu.ToString(),
						["address"] = br.Operation.Address,
						["value"] = br.Operation.Value,
						["memoryType"] = br.Operation.MemType.ToString(),
						["operationType"] = br.Operation.Type.ToString()
					};

					McpBreakpointSpec? spec = _breakpoints.FirstOrDefault(b => b.Id == br.BreakpointId);
					if(spec != null) {
						breakPayload["callbackType"] = spec.CallbackType;
						breakPayload["configuredMemType"] = spec.MemType;
					}
					RaiseEvent(breakPayload);
					break;

				case ConsoleNotificationType.GameLoaded:
					RaiseEvent(new JsonObject() { ["event"] = "game_loaded" });
					break;
				case ConsoleNotificationType.GamePaused:
					RaiseEvent(new JsonObject() { ["event"] = "game_paused" });
					break;
				case ConsoleNotificationType.GameResumed:
					RaiseEvent(new JsonObject() { ["event"] = "game_resumed" });
					break;
				case ConsoleNotificationType.EmulationStopped:
					RaiseEvent(new JsonObject() { ["event"] = "emulation_stopped" });
					break;
			}
		}

		private void EnsureDebuggerInitialized()
		{
			if(_debuggerInitialized) {
				return;
			}

			lock(_debuggerInitLock) {
				if(_debuggerInitialized) {
					return;
				}
				DebugApi.InitializeDebugger();
				_debuggerInitialized = true;
			}
		}

		private CpuType GetPrimaryCpuType()
		{
			RomInfo info = EmuApi.GetRomInfo();
			return info.ConsoleType switch {
				ConsoleType.Nes => CpuType.Nes,
				ConsoleType.Snes => CpuType.Snes,
				ConsoleType.Gameboy => CpuType.Gameboy,
				ConsoleType.PcEngine => CpuType.Pce,
				ConsoleType.Sms => CpuType.Sms,
				ConsoleType.Gba => CpuType.Gba,
				ConsoleType.Ws => CpuType.Ws,
				_ => info.CpuTypes.FirstOrDefault()
			};
		}

		private static CpuType? ResolveCpuType(string? name)
		{
			if(string.IsNullOrWhiteSpace(name)) {
				return null;
			}
			foreach(CpuType value in Enum.GetValues<CpuType>()) {
				if(string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase)) {
					return value;
				}
			}
			throw new InvalidOperationException($"Unknown CPU type: {name}");
		}

		private static IEnumerable<string> GetSupportedMemoryTypeNames(CpuType cpu)
		{
			yield return "cpu";
			yield return "cpuDebug";
			yield return "ppu";
			yield return "ppuDebug";
			yield return "oam";
			yield return "prgRom";
			yield return "workRam";
			yield return "saveRam";
			yield return "spriteRam";
			yield return "nametableRam";
			yield return "bootRom";
			if(cpu == CpuType.Nes) {
				yield return "chrRom";
				yield return "chrRam";
			}
			if(cpu == CpuType.Gameboy) {
				yield return "gbVideoRam";
				yield return "gbWorkRam";
				yield return "gbCartRam";
				yield return "gbHighRam";
				yield return "gbBootRom";
			}
		}

		private MemoryType ResolveMemoryType(string name, CpuType cpu)
		{
			string key = name.Trim();
			MemoryType type = key.ToLowerInvariant() switch {
				"cpu" => cpu.ToMemoryType(),
				"cpudebug" => cpu.ToMemoryType(),
				"ppu" => cpu.GetVramMemoryType(),
				"ppudebug" => cpu.GetVramMemoryType(),
				"oam" => cpu.GetSpriteRamMemoryType(),
				"prgrom" => cpu.GetPrgRomMemoryType(),
				"workram" => cpu.GetSystemRamType(),
				"saveram" => cpu switch {
					CpuType.Snes => MemoryType.SnesSaveRam,
					CpuType.Gameboy => MemoryType.GbCartRam,
					CpuType.Nes => MemoryType.NesSaveRam,
					CpuType.Pce => MemoryType.PceSaveRam,
					CpuType.Sms => MemoryType.SmsCartRam,
					CpuType.Gba => MemoryType.GbaSaveRam,
					CpuType.Ws => MemoryType.WsCartRam,
					_ => cpu.GetSystemRamType()
				},
				"nametableram" => cpu switch {
					CpuType.Nes => MemoryType.NesNametableRam,
					_ => cpu.GetVramMemoryType()
				},
				"spriteram" => cpu.GetSpriteRamMemoryType(),
				"bootrom" => cpu switch {
					CpuType.Gameboy => MemoryType.GbBootRom,
					CpuType.Sms => MemoryType.SmsBootRom,
					CpuType.Gba => MemoryType.GbaBootRom,
					CpuType.Ws => MemoryType.WsBootRom,
					_ => cpu.GetPrgRomMemoryType()
				},
				"chrrom" => cpu == CpuType.Nes ? MemoryType.NesChrRom : cpu.GetVramMemoryType(),
				"chrram" => cpu == CpuType.Nes ? MemoryType.NesChrRam : cpu.GetVramMemoryType(),
				"gbvideoram" => MemoryType.GbVideoRam,
				"gbworkram" => MemoryType.GbWorkRam,
				"gbcartram" => MemoryType.GbCartRam,
				"gbhighram" => MemoryType.GbHighRam,
				"gbbootrom" => MemoryType.GbBootRom,
				_ => ParseMemoryTypeEnum(name, cpu)
			};

			if(DebugApi.GetMemorySize(type) <= 0) {
				throw new InvalidOperationException($"Memory type '{name}' is unavailable for CPU {cpu}.");
			}
			return type;
		}

		private static string FormatHexDump(byte[] data, int baseAddress, MemoryType memType)
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine($"Memory dump ({memType}, {data.Length} bytes @ 0x{baseAddress:X}):");
			for(int i = 0; i < data.Length; i += 16) {
				int chunkLength = Math.Min(16, data.Length - i);
				sb.Append((baseAddress + i).ToString("X4", CultureInfo.InvariantCulture));
				sb.Append(": ");
				for(int j = 0; j < 16; j++) {
					if(j < chunkLength) {
						sb.Append(data[i + j].ToString("X2", CultureInfo.InvariantCulture));
					} else {
						sb.Append("  ");
					}
					if(j < 15) sb.Append(' ');
				}
				sb.Append(" ");
				for(int j = 0; j < chunkLength; j++) {
					byte value = data[i + j];
					sb.Append(value >= 0x20 && value <= 0x7E ? (char)value : '.');
				}
				sb.AppendLine();
			}
			return sb.ToString().TrimEnd();
		}

		private JsonObject BuildTimingNode(CpuType cpu)
		{
			TimingInfo timing = EmuApi.GetTimingInfo(cpu);
			return new JsonObject() {
				["fps"] = timing.Fps,
				["frameCount"] = timing.FrameCount,
				["scanlineCount"] = timing.ScanlineCount,
				["firstScanline"] = timing.FirstScanline,
				["cycleCount"] = timing.CycleCount,
				["masterClock"] = timing.MasterClock,
				["masterClockRate"] = timing.MasterClockRate
			};
		}

		private JsonObject GetCpuStateNode(CpuType cpu)
		{
			return cpu switch {
				CpuType.Snes => StructToJsonNode(DebugApi.GetCpuState<SnesCpuState>(cpu)),
				CpuType.Nes => StructToJsonNode(DebugApi.GetCpuState<NesCpuState>(cpu)),
				CpuType.Gameboy => StructToJsonNode(DebugApi.GetCpuState<GbCpuState>(cpu)),
				CpuType.Pce => StructToJsonNode(DebugApi.GetCpuState<PceCpuState>(cpu)),
				CpuType.Sms => StructToJsonNode(DebugApi.GetCpuState<SmsCpuState>(cpu)),
				CpuType.Gba => StructToJsonNode(DebugApi.GetCpuState<GbaCpuState>(cpu)),
				CpuType.Ws => StructToJsonNode(DebugApi.GetCpuState<WsCpuState>(cpu)),
				_ => StructToJsonNode(DebugApi.GetCpuState<NesCpuState>(CpuType.Nes))
			};
		}

		private JsonObject GetPpuStateNode(CpuType cpu)
		{
			return cpu switch {
				CpuType.Snes => StructToJsonNode(DebugApi.GetPpuState<SnesPpuState>(cpu)),
				CpuType.Nes => StructToJsonNode(DebugApi.GetPpuState<NesPpuState>(cpu)),
				CpuType.Gameboy => StructToJsonNode(DebugApi.GetPpuState<GbPpuState>(cpu)),
				CpuType.Pce => StructToJsonNode(DebugApi.GetPpuState<PceVideoState>(cpu)),
				CpuType.Sms => StructToJsonNode(DebugApi.GetPpuState<SmsVdpState>(cpu)),
				CpuType.Gba => StructToJsonNode(DebugApi.GetPpuState<GbaPpuState>(cpu)),
				CpuType.Ws => StructToJsonNode(DebugApi.GetPpuState<WsPpuState>(cpu)),
				_ => new JsonObject()
			};
		}

		private void ApplyCpuPatch(CpuType cpu, JsonElement patch)
		{
			switch(cpu) {
				case CpuType.Snes:
					SnesCpuState snes = DebugApi.GetCpuState<SnesCpuState>(cpu);
					snes = ApplyPatch(snes, patch);
					DebugApi.SetCpuState(snes, cpu);
					break;
				case CpuType.Nes:
					NesCpuState nes = DebugApi.GetCpuState<NesCpuState>(cpu);
					nes = ApplyPatch(nes, patch);
					DebugApi.SetCpuState(nes, cpu);
					break;
				case CpuType.Gameboy:
					GbCpuState gb = DebugApi.GetCpuState<GbCpuState>(cpu);
					gb = ApplyPatch(gb, patch);
					DebugApi.SetCpuState(gb, cpu);
					break;
				case CpuType.Pce:
					PceCpuState pce = DebugApi.GetCpuState<PceCpuState>(cpu);
					pce = ApplyPatch(pce, patch);
					DebugApi.SetCpuState(pce, cpu);
					break;
				case CpuType.Sms:
					SmsCpuState sms = DebugApi.GetCpuState<SmsCpuState>(cpu);
					sms = ApplyPatch(sms, patch);
					DebugApi.SetCpuState(sms, cpu);
					break;
				case CpuType.Gba:
					GbaCpuState gba = DebugApi.GetCpuState<GbaCpuState>(cpu);
					gba = ApplyPatch(gba, patch);
					DebugApi.SetCpuState(gba, cpu);
					break;
				case CpuType.Ws:
					WsCpuState ws = DebugApi.GetCpuState<WsCpuState>(cpu);
					ws = ApplyPatch(ws, patch);
					DebugApi.SetCpuState(ws, cpu);
					break;
				default:
					throw new InvalidOperationException($"CPU patching is not supported for {cpu}.");
			}
		}

		private void ApplyPpuPatch(CpuType cpu, JsonElement patch)
		{
			switch(cpu) {
				case CpuType.Snes:
					SnesPpuState snes = DebugApi.GetPpuState<SnesPpuState>(cpu);
					snes = ApplyPatch(snes, patch);
					DebugApi.SetPpuState(snes, cpu);
					break;
				case CpuType.Nes:
					NesPpuState nes = DebugApi.GetPpuState<NesPpuState>(cpu);
					nes = ApplyPatch(nes, patch);
					DebugApi.SetPpuState(nes, cpu);
					break;
				case CpuType.Gameboy:
					GbPpuState gb = DebugApi.GetPpuState<GbPpuState>(cpu);
					gb = ApplyPatch(gb, patch);
					DebugApi.SetPpuState(gb, cpu);
					break;
				case CpuType.Pce:
					PceVideoState pce = DebugApi.GetPpuState<PceVideoState>(cpu);
					pce = ApplyPatch(pce, patch);
					DebugApi.SetPpuState(pce, cpu);
					break;
				case CpuType.Sms:
					SmsVdpState sms = DebugApi.GetPpuState<SmsVdpState>(cpu);
					sms = ApplyPatch(sms, patch);
					DebugApi.SetPpuState(sms, cpu);
					break;
				case CpuType.Gba:
					GbaPpuState gba = DebugApi.GetPpuState<GbaPpuState>(cpu);
					gba = ApplyPatch(gba, patch);
					DebugApi.SetPpuState(gba, cpu);
					break;
				case CpuType.Ws:
					WsPpuState ws = DebugApi.GetPpuState<WsPpuState>(cpu);
					ws = ApplyPatch(ws, patch);
					DebugApi.SetPpuState(ws, cpu);
					break;
				default:
					throw new InvalidOperationException($"PPU patching is not supported for {cpu}.");
			}
		}

		private static T ApplyPatch<T>(T state, JsonElement patch) where T : struct
		{
			object boxed = state;
			Type type = typeof(T);
			Dictionary<string, JsonElement> values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
			foreach(JsonProperty property in patch.EnumerateObject()) {
				values[property.Name] = property.Value;
			}

			foreach(FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
				if(!values.TryGetValue(field.Name, out JsonElement value)) {
					continue;
				}

				if(TryConvertJsonToType(value, field.FieldType, out object? converted)) {
					field.SetValue(boxed, converted);
				}
			}

			return (T)boxed;
		}

		private static JsonObject StructToJsonNode<T>(T data)
		{
			return StructToJsonNodeObject(typeof(T), data!);
		}

		private static JsonObject StructToJsonNodeObject(Type type, object data)
		{
			JsonObject result = new JsonObject();
			foreach(FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
				object? value = field.GetValue(data);
				result[field.Name] = ConvertValueToJsonNode(value);
			}
			return result;
		}

		private static JsonNode? ConvertValueToJsonNode(object? value)
		{
			if(value == null) {
				return null;
			}

			Type type = value.GetType();
			if(type.IsEnum) {
				return JsonValue.Create(value.ToString());
			}

			if(type == typeof(string)) return JsonValue.Create((string)value);
			if(type == typeof(bool)) return JsonValue.Create((bool)value);
			if(type == typeof(byte)) return JsonValue.Create((byte)value);
			if(type == typeof(sbyte)) return JsonValue.Create((sbyte)value);
			if(type == typeof(short)) return JsonValue.Create((short)value);
			if(type == typeof(ushort)) return JsonValue.Create((ushort)value);
			if(type == typeof(int)) return JsonValue.Create((int)value);
			if(type == typeof(uint)) return JsonValue.Create((uint)value);
			if(type == typeof(long)) return JsonValue.Create((long)value);
			if(type == typeof(ulong)) return JsonValue.Create((ulong)value);
			if(type == typeof(float)) return JsonValue.Create((float)value);
			if(type == typeof(double)) return JsonValue.Create((double)value);

			if(type.IsArray && value is Array array) {
				JsonArray result = new JsonArray();
				foreach(object? entry in array) {
					result.Add(ConvertValueToJsonNode(entry));
				}
				return result;
			}

			if(type.IsValueType && !type.IsPrimitive) {
				return StructToJsonNodeObject(type, value);
			}

			return JsonValue.Create(value.ToString());
		}

		private static bool TryConvertJsonToType(JsonElement value, Type targetType, out object? converted)
		{
			try {
				if(targetType == typeof(string)) { converted = value.GetString() ?? ""; return true; }
				if(targetType == typeof(bool)) { converted = value.ValueKind == JsonValueKind.Number ? value.GetInt32() != 0 : value.GetBoolean(); return true; }
				if(targetType == typeof(byte)) { converted = (byte)value.GetInt32(); return true; }
				if(targetType == typeof(sbyte)) { converted = (sbyte)value.GetInt32(); return true; }
				if(targetType == typeof(short)) { converted = (short)value.GetInt32(); return true; }
				if(targetType == typeof(ushort)) { converted = (ushort)value.GetInt32(); return true; }
				if(targetType == typeof(int)) { converted = value.GetInt32(); return true; }
				if(targetType == typeof(uint)) { converted = value.GetUInt32(); return true; }
				if(targetType == typeof(long)) { converted = value.GetInt64(); return true; }
				if(targetType == typeof(ulong)) { converted = value.GetUInt64(); return true; }
				if(targetType == typeof(float)) { converted = value.GetSingle(); return true; }
				if(targetType == typeof(double)) { converted = value.GetDouble(); return true; }

				if(targetType.IsEnum) {
					if(value.ValueKind == JsonValueKind.String) {
						converted = Enum.Parse(targetType, value.GetString() ?? "", true);
					} else {
						converted = Enum.ToObject(targetType, value.GetInt32());
					}
					return true;
				}

				if(targetType.IsValueType && value.ValueKind == JsonValueKind.Object) {
					object boxed = Activator.CreateInstance(targetType)!;
					Dictionary<string, JsonElement> map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
					foreach(JsonProperty property in value.EnumerateObject()) {
						map[property.Name] = property.Value;
					}

					foreach(FieldInfo field in targetType.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
						if(!map.TryGetValue(field.Name, out JsonElement nested)) {
							continue;
						}
						if(TryConvertJsonToType(nested, field.FieldType, out object? nestedValue)) {
							field.SetValue(boxed, nestedValue);
						}
					}

					converted = boxed;
					return true;
				}
			} catch {
				// ignored
			}

			converted = null;
			return false;
		}

		private static JsonObject ControllerStateToNode(DebugControllerState state)
		{
			return new JsonObject() {
				["a"] = state.A,
				["b"] = state.B,
				["x"] = state.X,
				["y"] = state.Y,
				["l"] = state.L,
				["r"] = state.R,
				["u"] = state.U,
				["d"] = state.D,
				["up"] = state.Up,
				["down"] = state.Down,
				["left"] = state.Left,
				["right"] = state.Right,
				["select"] = state.Select,
				["start"] = state.Start
			};
		}

		private static byte[] GetByteArray(JsonElement args, string name)
		{
			if(!TryGetProperty(args, name, out JsonElement data) || data.ValueKind != JsonValueKind.Array) {
				throw new InvalidOperationException($"{name} array is required.");
			}

			List<byte> bytes = new List<byte>();
			foreach(JsonElement entry in data.EnumerateArray()) {
				bytes.Add((byte)Math.Clamp(entry.GetInt32(), 0, 255));
			}
			return bytes.ToArray();
		}

		private static string GetRequiredString(JsonElement element, string name)
		{
			string? value = GetOptionalString(element, name);
			if(string.IsNullOrWhiteSpace(value)) {
				throw new InvalidOperationException($"Missing required string: {name}");
			}
			return value!;
		}

		private static int GetRequiredInt(JsonElement element, string name)
		{
			int? value = GetOptionalInt(element, name);
			if(value == null) {
				throw new InvalidOperationException($"Missing required integer: {name}");
			}
			return value.Value;
		}

		private static string? GetOptionalString(JsonElement element, string name)
		{
			return TryGetProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
		}

		private static int? GetOptionalInt(JsonElement element, string name)
		{
			return TryGetProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
		}

		private static bool? GetOptionalBool(JsonElement element, string name)
		{
			if(!TryGetProperty(element, name, out JsonElement value)) {
				return null;
			}
			return value.ValueKind switch {
				JsonValueKind.True => true,
				JsonValueKind.False => false,
				JsonValueKind.Number => value.GetInt32() != 0,
				_ => null
			};
		}

		private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
		{
			value = default;
			if(element.ValueKind != JsonValueKind.Object) {
				return false;
			}

			foreach(JsonProperty property in element.EnumerateObject()) {
				if(string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) {
					value = property.Value;
					return true;
				}
			}
			return false;
		}

		public void Dispose()
		{
			if(_disposed) {
				return;
			}
			_disposed = true;

			foreach(KeyValuePair<string, int> script in _scriptIds) {
				try { DebugApi.RemoveScript(script.Value); } catch { }
			}
			if(_debuggerInitialized) {
				try { DebugApi.ReleaseDebugger(); } catch { }
			}
			_listener.OnNotification -= Listener_OnNotification;
			_listener.Dispose();
			_stepDoneEvent.Dispose();
			_toolLock.Dispose();
		}
	}

	internal sealed class McpBreakpointSpec
	{
		public int Id { get; set; }
		public string CallbackType { get; set; } = "exec";
		public string MemType { get; set; } = "cpuDebug";
	}
#pragma warning restore IL2090
#pragma warning restore IL2067
#pragma warning restore IL2070
#pragma warning restore IL3050
#pragma warning restore IL2026
}
