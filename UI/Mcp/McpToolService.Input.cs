using Mesen.Config;
using Mesen.Debugger.Labels;
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
	/// <summary>Frame-exact input sequences, recording and replay (MCP-09) and ROM slots (MCP-19).</summary>
	internal sealed partial class McpToolService
	{
		private static readonly string[] ButtonNames = { "a", "b", "x", "y", "l", "r", "up", "down", "left", "right", "select", "start", "u", "d" };
		private static readonly string[] MaskButtonOrder = { "a", "b", "x", "y", "l", "r", "up", "down", "left", "right", "select", "start" };

		private sealed class InputSegment
		{
			public int Frames;
			public Dictionary<int, List<string>> Ports = new Dictionary<int, List<string>>();
		}

		private sealed class InputRecording
		{
			public string Path = "";
			public string? StartStatePath;
			public string? StartStateSha1;
			public long StartFrame;
			public List<InputSegment> Segments = new List<InputSegment>();
		}

		private InputRecording? _recording;

		private void AddInputTools(Dictionary<string, McpToolDefinition> tools)
		{
			AddTool(tools, "set_input",
				"Set the input override of a controller port. input is a button map ({\"right\": true}) merged into the current override; buttons (list of names) or mask (bits: a=1 b=2 x=4 y=8 l=16 r=32 up=64 down=128 left=256 right=512 select=1024 start=2048) replace it. " +
				"With frames the buttons are held for exactly that many frames (the call advances them) and then released.",
				ObjectSchema(
					("port", IntegerSchema("Port index 0-7"), false),
					("input", new JsonObject() { ["type"] = "object", ["description"] = "Button map to bool/number" }, false),
					("buttons", StringArraySchema("Button names to hold (replaces the override)"), false),
					("mask", IntegerSchema("Button bit mask (replaces the override)"), false),
					("frames", IntegerSchema("Hold for this many frames, then release"), false),
					("turbo", BooleanSchema("Run without the frame limiter"), false)
				),
				ToolSetInputEx);

			AddTool(tools, "play_input",
				"Play a frame-exact input sequence: sequence is [{frames, buttons:[..], port?, ports?:{\"0\":[..],\"1\":[..]}}]. Each segment sets the buttons (an empty list releases) and advances exactly `frames` frames. " +
				"Stops early at a breakpoint. Buttons are released afterwards (release=false keeps the last segment).",
				ObjectSchema(
					("sequence", new JsonObject() { ["type"] = "array", ["description"] = "Segments [{frames, buttons}]" }, true),
					("port", IntegerSchema("Default port for segments (default 0)"), false),
					("turbo", BooleanSchema("Run without the frame limiter"), false),
					("release", BooleanSchema("Release the buttons afterwards (default true)"), false),
					("thenPause", BooleanSchema("Leave the emulator paused afterwards (default true)"), false)
				),
				ToolPlayInput);

			AddTool(tools, "record_input",
				"Record the input applied by set_input/play_input while frames advance. action=start begins (startState optionally names the state the recording starts from; its SHA-1 goes into the header); action=stop writes the file with the ROM SHA-1.",
				ObjectSchema(
					("action", StringSchema("start | stop"), true),
					("path", StringSchema("Output file (results folder or absolute); required for start"), false),
					("startState", StringSchema("Optional state file the recording starts from"), false)
				),
				ToolRecordInput);

			AddTool(tools, "play_input_file",
				"Replay a recording made by record_input. The ROM SHA-1 in the header must match the loaded ROM (requireRomMatch). loadStartState loads the recorded start state first (its SHA-1 is verified).",
				ObjectSchema(
					("path", StringSchema("Recording file"), true),
					("requireRomMatch", BooleanSchema("Verify the ROM SHA-1 (default true)"), false),
					("loadStartState", BooleanSchema("Load the start state named in the header first (default false)"), false),
					("turbo", BooleanSchema("Run without the frame limiter"), false),
					("release", BooleanSchema("Release the buttons afterwards (default true)"), false)
				),
				ToolPlayInputFile);

			// --- MCP-19 --------------------------------------------------------------------------
			AddTool(tools, "list_slots", "List the ROM slots of this session (see load_rom slot, switch_slot).", EmptySchema(), ToolListSlots);

			AddTool(tools, "switch_slot",
				"Make a slot active: the active slot's state and labels are saved, the slot's ROM is loaded and its saved state restored (paused). Every tool also accepts a slot argument that does this first. " +
				"Lua scripts and Lua-based breakpoints do not survive a switch.",
				ObjectSchema(("name", StringSchema("Slot name"), true)), ToolSwitchSlot);

			AddTool(tools, "compare_runs",
				"Play the same recorded input on two slots and report the first divergence of the watched values. watch: [{type?: memory type (default workRam), address, size?: 1|2|4, name?}]. " +
				"startStates optionally maps slot name to a state file to load first. Values are sampled after every frame.",
				ObjectSchema(
					("inputFile", StringSchema("Recording from record_input"), true),
					("watch", new JsonObject() { ["type"] = "array", ["description"] = "Values to compare" }, true),
					("slots", StringArraySchema("Two slot names (default: the first two slots)"), false),
					("startStates", AnyObjectSchema("Slot name to state file"), false),
					("frames", IntegerSchema("Limit the compared frames (default: whole recording)"), false),
					("maxDivergences", IntegerSchema("Maximum divergences listed (default 20)"), false)
				),
				ToolCompareRuns);

			// slot argument on every tool
			foreach(McpToolDefinition tool in tools.Values) {
				if(tool.InputSchema["properties"] is JsonObject props && !props.ContainsKey("slot")) {
					props["slot"] = StringSchema("Optional ROM slot to switch to first (see list_slots)");
				}
			}
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-09: buttons
		// ---------------------------------------------------------------------------------------------

		private static bool GetButton(DebugControllerState s, string name)
		{
			return name switch {
				"a" => s.A, "b" => s.B, "x" => s.X, "y" => s.Y, "l" => s.L, "r" => s.R,
				"up" => s.Up, "down" => s.Down, "left" => s.Left, "right" => s.Right,
				"select" => s.Select, "start" => s.Start, "u" => s.U, "d" => s.D,
				_ => false
			};
		}

		private static void SetButton(ref DebugControllerState s, string name, bool value)
		{
			switch(name) {
				case "a": s.A = value; break;
				case "b": s.B = value; break;
				case "x": s.X = value; break;
				case "y": s.Y = value; break;
				case "l": s.L = value; break;
				case "r": s.R = value; break;
				case "up": s.Up = value; break;
				case "down": s.Down = value; break;
				case "left": s.Left = value; break;
				case "right": s.Right = value; break;
				case "select": s.Select = value; break;
				case "start": s.Start = value; break;
				case "u": s.U = value; break;
				case "d": s.D = value; break;
				default: throw new InvalidOperationException($"Unknown button '{name}'. Valid: {string.Join(", ", ButtonNames)}.");
			}
		}

		private static DebugControllerState StateFromButtons(IEnumerable<string> buttons)
		{
			DebugControllerState state = new DebugControllerState();
			foreach(string button in buttons) {
				SetButton(ref state, button.ToLowerInvariant(), true);
			}
			return state;
		}

		private static List<string> ButtonsOf(DebugControllerState state)
		{
			return ButtonNames.Where(n => GetButton(state, n)).ToList();
		}

		private static List<string> ReadButtonList(JsonElement element)
		{
			List<string> list = new List<string>();
			if(element.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement item in element.EnumerateArray()) {
					if(item.ValueKind == JsonValueKind.String) {
						list.Add(item.GetString()!.ToLowerInvariant());
					}
				}
			}
			foreach(string name in list) {
				if(!ButtonNames.Contains(name)) {
					throw new InvalidOperationException($"Unknown button '{name}'. Valid: {string.Join(", ", ButtonNames)}.");
				}
			}
			return list;
		}

		private void ApplyPortButtons(int port, IEnumerable<string> buttons)
		{
			List<string> list = buttons.ToList();
			if(list.Count == 0) {
				ClearInputPort(port);
				return;
			}
			DebugControllerState state = StateFromButtons(list);
			_inputOverrides[port] = state;
			DebugApi.SetInputOverrides((uint)port, state);
		}

		/// <summary>Called when frames were advanced: appends to an active recording.</summary>
		private void NoteFramesAdvanced(long frames)
		{
			if(_recording == null || frames <= 0) {
				return;
			}

			Dictionary<int, List<string>> ports = new Dictionary<int, List<string>>();
			foreach(KeyValuePair<int, DebugControllerState> item in _inputOverrides.OrderBy(i => i.Key)) {
				List<string> buttons = ButtonsOf(item.Value);
				if(buttons.Count > 0) {
					ports[item.Key] = buttons;
				}
			}

			InputSegment? last = _recording.Segments.LastOrDefault();
			if(last != null && SamePorts(last.Ports, ports)) {
				last.Frames += (int)frames;
			} else {
				_recording.Segments.Add(new InputSegment() { Frames = (int)frames, Ports = ports });
			}
		}

		private static bool SamePorts(Dictionary<int, List<string>> a, Dictionary<int, List<string>> b)
		{
			if(a.Count != b.Count) {
				return false;
			}
			foreach(KeyValuePair<int, List<string>> item in a) {
				if(!b.TryGetValue(item.Key, out List<string>? other) || !item.Value.SequenceEqual(other)) {
					return false;
				}
			}
			return true;
		}

		private McpToolResult ToolSetInputEx(JsonElement args)
		{
			int port = Math.Clamp(GetOptionalInt(args, "port") ?? 0, 0, 7);
			int? frames = GetOptionalInt(args, "frames");
			bool hasInput = TryGetProperty(args, "input", out JsonElement inputPatch) && inputPatch.ValueKind == JsonValueKind.Object;
			bool hasButtons = TryGetProperty(args, "buttons", out JsonElement buttonsElement) && buttonsElement.ValueKind == JsonValueKind.Array;
			int? mask = GetOptionalInt(args, "mask");
			if(!hasInput && !hasButtons && mask == null) {
				throw new InvalidOperationException("input, buttons or mask is required.");
			}

			if(hasButtons) {
				ApplyPortButtons(port, ReadButtonList(buttonsElement));
			} else if(mask != null) {
				ApplyPortButtons(port, MaskButtonOrder.Where((n, i) => (mask.Value & (1 << i)) != 0));
			} else if(!inputPatch.EnumerateObject().Any()) {
				ClearInputPort(port);
			} else {
				DebugControllerState state = _inputOverrides.TryGetValue(port, out DebugControllerState existing) ? existing : new DebugControllerState();
				state = ApplyPatch(state, inputPatch);
				_inputOverrides[port] = state;
				DebugApi.SetInputOverrides((uint)port, state);
			}

			if(frames == null) {
				return McpToolResult.Text($"Input override set for port {port}.");
			}

			CpuType cpu = GetPrimaryCpuType();
			int count = Math.Clamp(frames.Value, 1, MaxFramesPerCall);
			bool turbo = ResolveTurbo(args);
			StepOutcome outcome;
			EnsurePaused(2000);
			using(BeginTurbo(turbo)) {
				outcome = RunFrames(cpu, count, GetOptionalInt(args, "timeoutMs") ?? DefaultTimeoutMs(count, turbo));
			}
			ClearInputPort(port);
			JsonObject node = BuildStepResponse(cpu, outcome);
			node["heldFrames"] = count;
			node["released"] = true;
			return Json(node, outcome.StopReason == "timeout");
		}

		private static List<InputSegment> ParseSequence(JsonElement sequence, int defaultPort)
		{
			if(sequence.ValueKind != JsonValueKind.Array) {
				throw new InvalidOperationException("sequence must be an array.");
			}
			List<InputSegment> segments = new List<InputSegment>();
			foreach(JsonElement item in sequence.EnumerateArray()) {
				int frames = GetOptionalInt(item, "frames") ?? throw new InvalidOperationException("Each segment needs frames.");
				if(frames < 0 || frames > MaxFramesPerCall) {
					throw new InvalidOperationException("segment frames must be between 0 and " + MaxFramesPerCall + ".");
				}
				InputSegment segment = new InputSegment() { Frames = frames };
				if(TryGetProperty(item, "ports", out JsonElement portsElement) && portsElement.ValueKind == JsonValueKind.Object) {
					foreach(JsonProperty p in portsElement.EnumerateObject()) {
						segment.Ports[Math.Clamp(int.Parse(p.Name, CultureInfo.InvariantCulture), 0, 7)] = ReadButtonList(p.Value);
					}
				} else {
					int port = Math.Clamp(GetOptionalInt(item, "port") ?? defaultPort, 0, 7);
					segment.Ports[port] = TryGetProperty(item, "buttons", out JsonElement buttons) ? ReadButtonList(buttons) : new List<string>();
				}
				segments.Add(segment);
			}
			return segments;
		}

		private StepOutcome PlaySegments(CpuType cpu, List<InputSegment> segments, bool turbo, bool release, out int segmentsPlayed)
		{
			HashSet<int> touched = new HashSet<int>(segments.SelectMany(s => s.Ports.Keys));
			StepOutcome total = new StepOutcome();
			long frameBefore = GetFrameCount(cpu);
			Stopwatch watch = Stopwatch.StartNew();
			segmentsPlayed = 0;
			EnsurePaused(2000);

			using(BeginTurbo(turbo)) {
				foreach(InputSegment segment in segments) {
					foreach(int port in touched) {
						ApplyPortButtons(port, segment.Ports.TryGetValue(port, out List<string>? buttons) ? buttons : new List<string>());
					}
					if(segment.Frames > 0) {
						StepOutcome part = RunFrames(cpu, segment.Frames, DefaultTimeoutMs(segment.Frames, turbo));
						total.StopReason = part.StopReason;
						total.HasBreak = part.HasBreak;
						total.Break = part.Break;
						if(part.StopReason != "done") {
							break;
						}
					}
					segmentsPlayed++;
				}
			}

			if(release) {
				foreach(int port in touched) {
					ClearInputPort(port);
				}
			}
			total.ElapsedMs = watch.ElapsedMilliseconds;
			total.FramesAdvanced = GetFrameCount(cpu) - frameBefore;
			return total;
		}

		private McpToolResult ToolPlayInput(JsonElement args)
		{
			if(!TryGetProperty(args, "sequence", out JsonElement sequence)) {
				throw new InvalidOperationException("sequence is required.");
			}
			int defaultPort = Math.Clamp(GetOptionalInt(args, "port") ?? 0, 0, 7);
			List<InputSegment> segments = ParseSequence(sequence, defaultPort);
			bool turbo = ResolveTurbo(args);
			bool release = GetOptionalBool(args, "release") ?? true;
			CpuType cpu = GetPrimaryCpuType();

			StepOutcome outcome = PlaySegments(cpu, segments, turbo, release, out int played);
			JsonObject node = BuildStepResponse(cpu, outcome);
			node["segmentsPlayed"] = played;
			node["segmentsTotal"] = segments.Count;
			node["plannedFrames"] = segments.Sum(s => (long)s.Frames);
			if(!(GetOptionalBool(args, "thenPause") ?? true)) {
				DebugApi.ResumeExecution();
				EmuApi.Resume();
			}
			return Json(node, outcome.StopReason == "timeout");
		}

		private McpToolResult ToolRecordInput(JsonElement args)
		{
			string action = (GetRequiredString(args, "action")).ToLowerInvariant();
			CpuType cpu = GetPrimaryCpuType();
			if(action == "start") {
				if(_recording != null) {
					throw new McpToolException("recording_active", $"A recording to {_recording.Path} is already active.");
				}
				string path = ResolveOutputPath(GetRequiredString(args, "path"));
				string? startState = GetOptionalString(args, "startState");
				_recording = new InputRecording() {
					Path = path,
					StartStatePath = startState != null ? Path.GetFullPath(startState) : null,
					StartStateSha1 = startState != null && File.Exists(startState) ? McpRomIdentity.Sha1OfFile(startState) : null,
					StartFrame = GetFrameCount(cpu)
				};
				return Json(new JsonObject() { ["recording"] = true, ["path"] = path, ["startFrame"] = Num(_recording.StartFrame) });
			}
			if(action != "stop") {
				throw new InvalidOperationException("action must be start or stop.");
			}
			if(_recording == null) {
				throw new McpToolException("no_recording", "No recording is active.");
			}

			InputRecording rec = _recording;
			_recording = null;
			JsonArray segments = new JsonArray();
			foreach(InputSegment segment in rec.Segments) {
				JsonObject ports = new JsonObject();
				foreach(KeyValuePair<int, List<string>> p in segment.Ports) {
					ports[p.Key.ToString(CultureInfo.InvariantCulture)] = new JsonArray(p.Value.Select(b => (JsonNode?)JsonValue.Create(b)).ToArray());
				}
				segments.Add((JsonNode)new JsonObject() { ["frames"] = segment.Frames, ["ports"] = ports });
			}

			McpRomIdentity? identity = TryGetRomIdentity();
			JsonObject file = new JsonObject() {
				["format"] = "mesen-mcp-input/1",
				["serverVersion"] = ServerVersion,
				["romSha1"] = identity?.FileSha1,
				["romPath"] = identity?.Path,
				["startState"] = new JsonObject() { ["path"] = rec.StartStatePath, ["sha1"] = rec.StartStateSha1 },
				["startFrame"] = Num(rec.StartFrame),
				["endFrame"] = Num(GetFrameCount(cpu)),
				["totalFrames"] = Num(rec.Segments.Sum(s => (long)s.Frames)),
				["segments"] = segments
			};

			string? folder = Path.GetDirectoryName(rec.Path);
			if(!string.IsNullOrWhiteSpace(folder)) {
				Directory.CreateDirectory(folder);
			}
			File.WriteAllText(rec.Path, file.ToJsonString(_indented));
			return Json(new JsonObject() {
				["recording"] = false,
				["path"] = rec.Path,
				["segments"] = rec.Segments.Count,
				["totalFrames"] = file["totalFrames"]?.DeepClone(),
				["romSha1"] = identity?.FileSha1,
				["sha1"] = McpRomIdentity.Sha1OfFile(rec.Path)
			});
		}

		private McpRomIdentity? TryGetRomIdentity()
		{
			try {
				RomInfo info = EmuApi.GetRomInfo();
				return McpRomIdentity.TryCompute(info.RomPath, GetPrimaryCpuType() == CpuType.Snes);
			} catch {
				return null;
			}
		}

		private (List<InputSegment> segments, JsonElement root, JsonDocument doc) ReadInputFile(string path)
		{
			string full = Path.IsPathRooted(path) ? path : ResolveOutputPath(path);
			if(!File.Exists(full)) {
				throw new FileNotFoundException("Input recording not found.", full);
			}
			JsonDocument doc = JsonDocument.Parse(File.ReadAllText(full));
			JsonElement root = doc.RootElement;
			List<InputSegment> segments = new List<InputSegment>();
			if(TryGetProperty(root, "segments", out JsonElement segs) && segs.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement s in segs.EnumerateArray()) {
					InputSegment seg = new InputSegment() { Frames = GetOptionalInt(s, "frames") ?? 0 };
					if(TryGetProperty(s, "ports", out JsonElement ports) && ports.ValueKind == JsonValueKind.Object) {
						foreach(JsonProperty p in ports.EnumerateObject()) {
							seg.Ports[int.Parse(p.Name, CultureInfo.InvariantCulture)] = ReadButtonList(p.Value);
						}
					}
					segments.Add(seg);
				}
			}
			return (segments, root, doc);
		}

		private McpToolResult ToolPlayInputFile(JsonElement args)
		{
			CpuType cpu = GetPrimaryCpuType();
			(List<InputSegment> segments, JsonElement root, JsonDocument doc) = ReadInputFile(GetRequiredString(args, "path"));
			using(doc) {
				if(GetOptionalBool(args, "requireRomMatch") ?? true) {
					string? recorded = GetOptionalString(root, "romSha1");
					string? current = TryGetRomIdentity()?.FileSha1;
					if(recorded != null && current != null && !recorded.Equals(current, StringComparison.OrdinalIgnoreCase)) {
						throw new McpToolException("rom_mismatch", "The recording was made on a different ROM.", new JsonObject() { ["recordedSha1"] = recorded, ["loadedSha1"] = current });
					}
				}

				if(GetOptionalBool(args, "loadStartState") ?? false) {
					LoadRecordedStartState(root);
				}

				bool turbo = ResolveTurbo(args);
				StepOutcome outcome = PlaySegments(cpu, segments, turbo, GetOptionalBool(args, "release") ?? true, out int played);
				JsonObject node = BuildStepResponse(cpu, outcome);
				node["segmentsPlayed"] = played;
				node["segmentsTotal"] = segments.Count;
				node["plannedFrames"] = segments.Sum(s => (long)s.Frames);
				return Json(node, outcome.StopReason == "timeout");
			}
		}

		private void LoadRecordedStartState(JsonElement root)
		{
			if(!TryGetProperty(root, "startState", out JsonElement start)) {
				throw new McpToolException("no_start_state", "The recording has no start state.");
			}
			string? path = GetOptionalString(start, "path");
			if(string.IsNullOrWhiteSpace(path) || !File.Exists(path)) {
				throw new McpToolException("no_start_state", $"The start state '{path}' does not exist.");
			}
			string? sha1 = GetOptionalString(start, "sha1");
			if(sha1 != null && !McpRomIdentity.Sha1OfFile(path).Equals(sha1, StringComparison.OrdinalIgnoreCase)) {
				throw new McpToolException("state_changed", $"The start state '{path}' differs from the one that was recorded.");
			}
			CheckStateAgainstLoadedRom(path, true, true);
			EmuApi.LoadStateFile(path);
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-19: slots
		// ---------------------------------------------------------------------------------------------

		private sealed class SlotInfo
		{
			public string Name = "";
			public string RomPath = "";
			public string? StatePath;
			public List<CodeLabel>? Labels;
			public string? Sha1;
		}

		private readonly Dictionary<string, SlotInfo> _slots = new Dictionary<string, SlotInfo>(StringComparer.OrdinalIgnoreCase);
		private string _activeSlot = "default";
		private string? _loadingSlot;

		private string SlotStateFolder
		{
			get {
				string dir = Path.Combine(ResultsDir, "slots");
				Directory.CreateDirectory(dir);
				return dir;
			}
		}

		private void RegisterSlotForLoadedRom(string romPath)
		{
			string name = _loadingSlot ?? _activeSlot;
			if(!_slots.TryGetValue(name, out SlotInfo? slot)) {
				slot = new SlotInfo() { Name = name };
				_slots[name] = slot;
			}
			slot.RomPath = romPath;
			slot.StatePath = null;
			slot.Labels = null;
			try { slot.Sha1 = McpRomIdentity.Sha1OfFile(romPath); } catch { slot.Sha1 = null; }
			_activeSlot = name;
		}

		private void PersistActiveSlot()
		{
			if(!_slots.TryGetValue(_activeSlot, out SlotInfo? slot) || !EmuApi.IsRunning()) {
				return;
			}
			string path = Path.Combine(SlotStateFolder, SanitizeFileName(slot.Name) + ".mss");
			EmuApi.SaveStateFile(path);
			slot.StatePath = path;
			slot.Labels = LabelManager.GetAllLabels().ToList();
		}

		private static string SanitizeFileName(string name)
		{
			return new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
		}

		private void SwitchToSlot(string name)
		{
			if(name.Equals(_activeSlot, StringComparison.OrdinalIgnoreCase)) {
				return;
			}
			if(!_slots.TryGetValue(name, out SlotInfo? target)) {
				throw new McpToolException("unknown_slot", $"No slot named '{name}'. Load a ROM into it with load_rom slot=\"{name}\". Slots: {string.Join(", ", _slots.Keys)}.");
			}

			EnsurePaused(2000);
			PersistActiveSlot();
			ClearLuaBreakpointsForSwitch();
			ForgetAllScripts();

			if(!EmuApi.LoadRom(target.RomPath, string.Empty)) {
				throw new InvalidOperationException($"Failed to load the ROM of slot '{name}'.");
			}
			EnsureDebuggerInitialized();

			if(target.StatePath != null && File.Exists(target.StatePath)) {
				EmuApi.LoadStateFile(target.StatePath);
			}
			if(target.Labels != null) {
				try {
					LabelManager.SetLabels(target.Labels, false);
				} catch { }
			}
			_activeSlot = target.Name;
			EmuApi.Pause();
			EnsurePaused(2000);
		}

		private void ClearLuaBreakpointsForSwitch()
		{
			_luaBreakpoints.Clear();
			_bpEventCounts.Clear();
			_interopBps = new List<InteropBreakpoint>();
			_breakpoints.Clear();
			DebugApi.SetBreakpoints(Array.Empty<InteropBreakpoint>(), 0);
		}

		private McpToolResult ToolListSlots(JsonElement _)
		{
			JsonArray rows = new JsonArray();
			foreach(SlotInfo slot in _slots.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)) {
				rows.Add((JsonNode)new JsonObject() {
					["name"] = slot.Name,
					["active"] = slot.Name.Equals(_activeSlot, StringComparison.OrdinalIgnoreCase),
					["romPath"] = slot.RomPath,
					["romSha1"] = slot.Sha1,
					["hasSavedState"] = slot.StatePath != null
				});
			}
			return Json(new JsonObject() { ["activeSlot"] = _activeSlot, ["slots"] = rows });
		}

		private McpToolResult ToolSwitchSlot(JsonElement args)
		{
			string name = GetRequiredString(args, "name");
			SwitchToSlot(name);
			CpuType cpu = GetPrimaryCpuType();
			return Json(new JsonObject() {
				["activeSlot"] = _activeSlot,
				["romPath"] = _slots[_activeSlot].RomPath,
				["frame"] = Num(GetFrameCount(cpu)),
				["cpu"] = BuildCpuSummary(cpu)
			});
		}

		private sealed class WatchSpec
		{
			public string Name = "";
			public MemoryType Type;
			public int Address;
			public int Size = 1;
		}

		private List<WatchSpec> ParseWatches(JsonElement element, CpuType cpu)
		{
			if(element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0) {
				throw new InvalidOperationException("watch must be a non-empty array.");
			}
			List<WatchSpec> list = new List<WatchSpec>();
			foreach(JsonElement item in element.EnumerateArray()) {
				WatchSpec w = new WatchSpec() {
					Type = ResolveMemoryType(GetFirstString(item, "type", "memType") ?? "workRam", cpu),
					Address = GetAddressArg(item, "address") ?? throw new InvalidOperationException("Each watch needs an address."),
					Size = Math.Clamp(GetOptionalInt(item, "size") ?? 1, 1, 4)
				};
				w.Name = GetOptionalString(item, "name") ?? $"{w.Type}:{w.Address:X}";
				list.Add(w);
			}
			return list;
		}

		private static long ReadWatch(WatchSpec w)
		{
			byte[] data = DebugApi.GetMemoryValues(w.Type, (uint)w.Address, (uint)(w.Address + w.Size - 1));
			long value = 0;
			for(int i = 0; i < data.Length; i++) {
				value |= (long)data[i] << (8 * i);
			}
			return value;
		}

		private McpToolResult ToolCompareRuns(JsonElement args)
		{
			CpuType cpu = GetPrimaryCpuType();
			List<string> slotNames = GetStringList(args, "slots") ?? _slots.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Take(2).ToList();
			if(slotNames.Count != 2) {
				throw new McpToolException("need_two_slots", "compare_runs needs exactly two slots (load two ROMs with load_rom slot=...). Slots: " + string.Join(", ", _slots.Keys));
			}

			(List<InputSegment> segments, JsonElement root, JsonDocument doc) = ReadInputFile(GetRequiredString(args, "inputFile"));
			using(doc) {
				if(!TryGetProperty(args, "watch", out JsonElement watchElement)) {
					throw new InvalidOperationException("watch is required.");
				}
				int frameLimit = GetOptionalInt(args, "frames") ?? int.MaxValue;
				int maxDivergences = Math.Clamp(GetOptionalInt(args, "maxDivergences") ?? 20, 1, 1000);

				List<int> framePlan = new List<int>();
				List<InputSegment> perFrame = new List<InputSegment>();
				foreach(InputSegment s in segments) {
					for(int i = 0; i < s.Frames && perFrame.Count < frameLimit; i++) {
						perFrame.Add(s);
					}
				}
				if(perFrame.Count == 0) {
					throw new InvalidOperationException("The recording has no frames.");
				}

				string originalSlot = _activeSlot;
				HashSet<int> touched = new HashSet<int>(segments.SelectMany(s => s.Ports.Keys));
				List<long[][]> samples = new List<long[][]>();
				List<WatchSpec>? watches = null;
				JsonObject startStates = new JsonObject();
				bool turbo = ResolveTurbo(args);

				foreach(string slotName in slotNames) {
					SwitchToSlot(slotName);
					cpu = GetPrimaryCpuType();
					watches ??= ParseWatches(watchElement, cpu);

					if(TryGetProperty(args, "startStates", out JsonElement states) && states.ValueKind == JsonValueKind.Object &&
						TryGetProperty(states, slotName, out JsonElement statePathElement) && statePathElement.ValueKind == JsonValueKind.String) {
						string statePath = statePathElement.GetString()!;
						CheckStateAgainstLoadedRom(statePath, true, true);
						EmuApi.LoadStateFile(statePath);
						startStates[slotName] = statePath;
					}

					long[][] values = new long[perFrame.Count][];
					EnsurePaused(2000);
					using(BeginTurbo(turbo)) {
						for(int f = 0; f < perFrame.Count; f++) {
							foreach(int port in touched) {
								ApplyPortButtons(port, perFrame[f].Ports.TryGetValue(port, out List<string>? buttons) ? buttons : new List<string>());
							}
							StepOutcome part = RunFrames(cpu, 1, 5000);
							if(part.StopReason != "done") {
								Array.Resize(ref values, f);
								break;
							}
							values[f] = watches.Select(ReadWatch).ToArray();
						}
					}
					foreach(int port in touched) {
						ClearInputPort(port);
					}
					samples.Add(values);
				}

				SwitchToSlot(originalSlot);

				int common = Math.Min(samples[0].Length, samples[1].Length);
				JsonArray divergences = new JsonArray();
				long totalDivergentFrames = 0;
				JsonObject? first = null;
				for(int f = 0; f < common; f++) {
					List<int> diffs = new List<int>();
					for(int w = 0; w < watches!.Count; w++) {
						if(samples[0][f][w] != samples[1][f][w]) {
							diffs.Add(w);
						}
					}
					if(diffs.Count == 0) {
						continue;
					}
					totalDivergentFrames++;
					JsonObject row = new JsonObject() { ["frame"] = f + 1 };
					JsonArray items = new JsonArray();
					foreach(int w in diffs) {
						items.Add((JsonNode)new JsonObject() {
							["watch"] = watches[w].Name,
							[slotNames[0]] = Num(samples[0][f][w]),
							[slotNames[1]] = Num(samples[1][f][w])
						});
					}
					row["differences"] = items;
					first ??= (JsonObject)row.DeepClone();
					if(divergences.Count < maxDivergences) {
						divergences.Add((JsonNode)row);
					}
				}

				return Json(new JsonObject() {
					["slots"] = new JsonArray(slotNames.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
					["comparedFrames"] = common,
					["identical"] = totalDivergentFrames == 0,
					["divergentFrames"] = Num(totalDivergentFrames),
					["firstDivergence"] = first,
					["divergences"] = divergences,
					["startStates"] = startStates,
					["note"] = "Frame numbers count from the start of the replay (frame 1 = after the first advanced frame)."
				});
			}
		}
	}
#pragma warning restore IL2090
#pragma warning restore IL2067
#pragma warning restore IL2070
#pragma warning restore IL3050
#pragma warning restore IL2026
}
