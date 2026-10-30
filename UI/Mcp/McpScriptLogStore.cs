using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mesen.Mcp
{
	internal sealed class McpScriptLogEntry
	{
		public long Seq;
		public long Frame;
		public string Script = "";
		public string Text = "";
	}

	internal sealed class McpScriptLogQuery
	{
		public List<McpScriptLogEntry> Entries { get; } = new List<McpScriptLogEntry>();
		public long NextSeq { get; set; }
		public long Dropped { get; set; }
	}

	/// <summary>
	/// Server-side ring buffer for Lua script output.
	///
	/// The native core only keeps the last 500 rows per script and offers no cursor, so the server
	/// "harvests" the native log after each emulation step and appends the new rows to a global ring
	/// with a monotonically increasing sequence number. Rows that start with <see cref="ResultPrefix"/>
	/// are not log rows: they carry the JSON result produced by emu.mcp.result() (see
	/// <see cref="LuaPrelude"/>) and are stored per script instead.
	/// </summary>
	internal sealed class McpScriptLogStore
	{
		public const string ResultPrefix = "@@MCP_RESULT@@";
		public const string WritePrefix = "@@MCP_WRITE@@";
		public const string DonePrefix = "@@MCP_DONE@@";
		public const string WriteQueuePrefix = "@@MCP_WQ@@";
		/// <summary>Size of the buffer the interop layer reads the native script log into.</summary>
		public const int NativeLogBytes = 4 * 1024 * 1024;

		/// <summary>What one harvest found: how much new output, as a share of the native log capacity.</summary>
		public readonly struct HarvestStats
		{
			public readonly int NewRows;
			public readonly int NewBytes;
			public readonly bool NearlyFull;

			public HarvestStats(int newRows, int newBytes, bool nearlyFull)
			{
				NewRows = newRows;
				NewBytes = newBytes;
				NearlyFull = nearlyFull;
			}

			/// <summary>0..1+: how much of the native log (500 rows) one harvest consumed.</summary>
			public double Fill => Math.Max(NewRows / (double)NativeLogRows, NewBytes / (double)NativeLogBytes);
		}
		public const int NativeLogRows = 500;

		/// <summary>
		/// Prepended (on the same line as the first line of user code, so line numbers do not change)
		/// to every script loaded through the server. Provides emu.mcp.result(table): the table is encoded
		/// as JSON and emitted as a special log row, which the server turns into the script's result.
		/// </summary>
		public static readonly string LuaPrelude = LuaPreludeSource.Replace("\r", "").Replace("\n", " ") + " ";

		private const string LuaPreludeSource = """
pcall(function()
local function q(s, bytes)
  return '"' .. (tostring(s):gsub(bytes and '[%c"\\\128-\255]' or '[%c"\\]', function(c)
    if c == '"' then return '\\"' elseif c == '\\' then return '\\\\' end
    return string.format('\\u%04x', string.byte(c))
  end)) .. '"'
end
local function enc(v, d)
  local t = type(v)
  if t == 'nil' then return 'null'
  elseif t == 'boolean' then return tostring(v)
  elseif t == 'number' then
    if v ~= v or v == math.huge or v == -math.huge then return 'null' end
    if math.type and math.type(v) == 'integer' then return string.format('%d', v) end
    return string.format('%.14g', v)
  elseif t == 'string' then return q(v)
  elseif t == 'table' then
    if d > 24 then return 'null' end
    local n = 0
    for _ in pairs(v) do n = n + 1 end
    local p = {}
    if n == #v then
      for i = 1, #v do p[#p + 1] = enc(v[i], d + 1) end
      return '[' .. table.concat(p, ',') .. ']'
    end
    local keys = {}
    for k in pairs(v) do keys[#keys + 1] = k end
    table.sort(keys, function(a, b) return tostring(a) < tostring(b) end)
    for _, k in ipairs(keys) do p[#p + 1] = q(k) .. ':' .. enc(v[k], d + 1) end
    return '{' .. table.concat(p, ',') .. '}'
  end
  return q(tostring(v))
end
emu.mcp = emu.mcp or {}
emu.mcp.result = function(v) emu.log('@@MCP_RESULT@@' .. enc(v, 0)) end
emu.mcp.done = function() emu.log('@@MCP_DONE@@') pcall(emu.breakExecution) end
local WQ, WH, WP, FB, SF, WCALLS, WTOTAL, WREF = {}, 1, 0, 0, false, 0, 0, nil
local WNQ, WNC = {}, {}
local WBUDGET, WPIECE = 48000, 4000
local function wstatus()
  SF = true
  local names, n = {}, 0
  for k in pairs(WNC) do
    if n < 20 then n = n + 1 names[n] = q(k) end
  end
  emu.log('@@MCP_WQ@@{"p":' .. WP .. ',"calls":' .. WCALLS .. ',"bytes":' .. WTOTAL .. ',"f":[' .. table.concat(names, ',') .. ']}')
end
local function wdrain()
  while WH <= #WQ and (FB == 0 or FB + #WQ[WH] <= WBUDGET) do
    local row = WQ[WH]
    local rn = WNQ[WH]
    WQ[WH] = false
    WH = WH + 1
    WNC[rn] = WNC[rn] - 1
    if WNC[rn] <= 0 then WNC[rn] = nil end
    WP = WP - #row
    FB = FB + #row
    emu.log(row)
  end
  if WH > #WQ then WQ, WNQ, WH, WP = {}, {}, 1, 0 end
end
local function wframe()
  FB, SF = 0, false
  if WP > 0 then
    wdrain()
    wstatus()
  end
end
emu.mcp.writeFile = function(name, text, append)
  if type(name) ~= 'string' or name == '' then return nil, 'name must be a non-empty string' end
  if name:find('..', 1, true) or name:find('^[/\\]') or name:find('^%a:') or name:find('[%c<>|"?*]') then
    return nil, 'invalid file name: ' .. name
  end
  text = (text == nil) and '' or tostring(text)
  if not WREF then
    local ok, ref = pcall(emu.addEventCallback, wframe, emu.eventType.endFrame)
    if ok then WREF = ref end
  end
  local pos, first = 1, true
  repeat
    local piece = text:sub(pos, pos + WPIECE - 1)
    local row = '@@MCP_WRITE@@' .. '{"name":' .. q(name) .. ',"append":' .. tostring((append and true or not first) and true or false) .. ',"text":' .. q(piece, true) .. '}'
    WQ[#WQ + 1] = row
    WNQ[#WQ] = name
    WNC[name] = (WNC[name] or 0) + 1
    WP = WP + #row
    first, pos = false, pos + WPIECE
  until pos > #text
  WCALLS, WTOTAL = WCALLS + 1, WTOTAL + #text
  wdrain()
  if WP > 0 and not SF then wstatus() end
  return true
end
end)
""";

		private readonly int _capacity;
		private readonly McpScriptLogEntry[] _ring;
		private int _head;
		private int _count;
		private long _nextSeq = 1;

		private readonly Dictionary<int, List<string>> _snapshots = new Dictionary<int, List<string>>();
		private readonly Dictionary<string, string> _results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> _done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>Per-script accounting of emu.mcp.writeFile and of log overflow, reported in the run_script reply.</summary>
		public sealed class WriteState
		{
			/// <summary>Bytes of write rows the script still holds back (it flushes a few KB per frame).</summary>
			public long PendingRowBytes;
			/// <summary>Number of writeFile calls and bytes passed to them, as last reported by the script.</summary>
			public long Calls, QueuedBytes;
			/// <summary>File bytes that arrived at the server.</summary>
			public long ReceivedBytes;
			/// <summary>A write row could not be parsed (cut off by a full native log).</summary>
			public int CutRows;
			/// <summary>The native log was (nearly) full at a harvest, or more than 500 rows arrived in one window.</summary>
			public bool LogFull, RowsDropped;
			/// <summary>Files whose data is still queued in the script, and files that lost a row to a cut-off log.</summary>
			public HashSet<string> PendingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			public HashSet<string> CutFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			public long MissingBytes => Math.Max(0, QueuedBytes - ReceivedBytes);
			public bool Truncated => CutRows > 0 || (PendingRowBytes == 0 && MissingBytes > 0) || LogFull || RowsDropped;
		}

		private readonly Dictionary<string, WriteState> _writes = new Dictionary<string, WriteState>(StringComparer.OrdinalIgnoreCase);

		public WriteState GetWriteState(string name)
		{
			if(!_writes.TryGetValue(name, out WriteState? state)) {
				state = new WriteState();
				_writes[name] = state;
			}
			return state;
		}

		public long PendingWriteBytes(string name) => _writes.TryGetValue(name, out WriteState? s) ? s.PendingRowBytes : 0;

		public void NoteWriteReceived(string name, long bytes) => GetWriteState(name).ReceivedBytes += bytes;

		/// <summary>Called for every emu.mcp.writeFile row with (scriptName, json). Set by the tool service.</summary>
		public Action<string, string>? OnWriteRow { get; set; }

		/// <summary>True once the script called emu.mcp.done().</summary>
		public bool IsDone(string name) => _done.Contains(name);

		public McpScriptLogStore(int capacity = 5000)
		{
			_capacity = capacity;
			_ring = new McpScriptLogEntry[capacity];
		}

		public long NextSeq => _nextSeq;

		/// <summary>True when a harvest could not be matched exactly against the previous snapshot.</summary>
		public bool Approximate { get; private set; }

		public void RegisterScript(int scriptId, string name)
		{
			_snapshots[scriptId] = new List<string>();
			_results.Remove(name);
			_done.Remove(name);
			_writes.Remove(name);
		}

		public void ForgetScript(int scriptId)
		{
			_snapshots.Remove(scriptId);
		}

		public void ForgetAllScripts()
		{
			_snapshots.Clear();
		}

		public void Clear()
		{
			_head = 0;
			_count = 0;
			Array.Clear(_ring, 0, _ring.Length);
		}

		public string? GetResult(string name)
		{
			return _results.TryGetValue(name, out string? value) ? value : null;
		}

		/// <summary>
		/// Appends the rows of a native script log that have not been seen yet.
		/// </summary>
		public HarvestStats Harvest(int scriptId, string scriptName, string nativeLog, long frame)
		{
			List<string> current = SplitRows(nativeLog);
			if(!_snapshots.TryGetValue(scriptId, out List<string>? previous)) {
				previous = new List<string>();
			}

			int firstNew = FindFirstNewRow(previous, current);
			int newBytes = 0;
			for(int i = firstNew; i < current.Count; i++) {
				string row = current[i];
				newBytes += row.Length + 1;
				if(row.StartsWith(ResultPrefix, StringComparison.Ordinal)) {
					_results[scriptName] = row.Substring(ResultPrefix.Length);
				} else if(row.StartsWith(DonePrefix, StringComparison.Ordinal)) {
					_done.Add(scriptName);
				} else if(row.StartsWith(WriteQueuePrefix, StringComparison.Ordinal)) {
					ParseWriteQueueRow(scriptName, row.Substring(WriteQueuePrefix.Length));
				} else if(row.StartsWith(WritePrefix, StringComparison.Ordinal)) {
					try {
						OnWriteRow?.Invoke(scriptName, row.Substring(WritePrefix.Length));
					} catch(JsonException) {
						GetWriteState(scriptName).CutRows++;
						System.Text.RegularExpressions.Match nameMatch = System.Text.RegularExpressions.Regex.Match(row, @"^@@MCP_WRITE@@\{""name"":""((?:[^""\\]|\\.)*)""");
						if(nameMatch.Success) {
							GetWriteState(scriptName).CutFiles.Add(nameMatch.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\""));
						}
						Append(scriptName, frame, "[mcp] a writeFile row was cut off because the native script log buffer (4 MB) was full; part of that file is missing.");
					} catch(Exception ex) {
						Append(scriptName, frame, "[mcp] writeFile failed: " + ex.Message);
					}
				} else {
					Append(scriptName, frame, row);
				}
			}

			bool nearlyFull = nativeLog.Length >= NativeLogBytes - 8000;
			if(nearlyFull) {
				GetWriteState(scriptName).LogFull = true;
			}
			if(current.Count >= NativeLogRows && (firstNew == 0 || previous.Count == 0)) {
				GetWriteState(scriptName).RowsDropped = true;
			}
			if(nearlyFull) {
				Append(scriptName, frame, "[mcp] warning: the native script log buffer (4 MB) is full; output may have been cut off. Log less per frame.");
			}
			_snapshots[scriptId] = current;
			return new HarvestStats(current.Count - firstNew, newBytes, nearlyFull);
		}

		private void ParseWriteQueueRow(string scriptName, string json)
		{
			try {
				using JsonDocument doc = JsonDocument.Parse(json);
				WriteState state = GetWriteState(scriptName);
				state.PendingRowBytes = doc.RootElement.GetProperty("p").GetInt64();
				state.Calls = doc.RootElement.GetProperty("calls").GetInt64();
				state.QueuedBytes = doc.RootElement.GetProperty("bytes").GetInt64();
				state.PendingFiles.Clear();
				if(doc.RootElement.TryGetProperty("f", out JsonElement files) && files.ValueKind == JsonValueKind.Array) {
					foreach(JsonElement file in files.EnumerateArray()) {
						state.PendingFiles.Add(file.GetString() ?? "");
					}
				}
			} catch(JsonException) {
			}
		}

		public McpScriptLogQuery Query(string? scriptName, long sinceSeq, int max)
		{
			McpScriptLogQuery query = new McpScriptLogQuery();
			long firstSeq = _count == 0 ? _nextSeq : _ring[_head].Seq;
			long from = Math.Max(1, sinceSeq);
			query.Dropped = Math.Max(0, firstSeq - from);
			query.NextSeq = _nextSeq;

			for(int i = 0; i < _count; i++) {
				McpScriptLogEntry entry = _ring[(_head + i) % _capacity];
				if(entry.Seq < from) {
					continue;
				}
				if(scriptName != null && !string.Equals(entry.Script, scriptName, StringComparison.OrdinalIgnoreCase)) {
					continue;
				}
				if(query.Entries.Count >= max) {
					query.NextSeq = entry.Seq;
					break;
				}
				query.Entries.Add(entry);
			}
			return query;
		}

		public static JsonObject ToJson(McpScriptLogQuery query)
		{
			JsonArray entries = new JsonArray();
			foreach(McpScriptLogEntry e in query.Entries) {
				entries.Add((JsonNode)new JsonObject() {
					["seq"] = e.Seq,
					["frame"] = e.Frame,
					["script"] = e.Script,
					["text"] = e.Text
				});
			}
			return new JsonObject() {
				["entries"] = entries,
				["nextSeq"] = query.NextSeq,
				["dropped"] = query.Dropped
			};
		}

		private void Append(string script, long frame, string text)
		{
			McpScriptLogEntry entry = new McpScriptLogEntry() { Seq = _nextSeq++, Frame = frame, Script = script, Text = text };
			if(_count < _capacity) {
				_ring[(_head + _count) % _capacity] = entry;
				_count++;
			} else {
				_ring[_head] = entry;
				_head = (_head + 1) % _capacity;
			}
		}

		private static List<string> SplitRows(string log)
		{
			List<string> rows = new List<string>();
			if(string.IsNullOrEmpty(log)) {
				return rows;
			}
			foreach(string line in log.Replace("\r\n", "\n").Split('\n')) {
				if(line.Length > 0) {
					rows.Add(line);
				}
			}
			return rows;
		}

		/// <summary>
		/// Returns the index in <paramref name="current"/> of the first row that was not in <paramref name="previous"/>.
		/// While the native deque has not reached its 500-row cap, rows are only ever appended, so the answer is exact.
		/// Once it is full, the oldest rows are dropped and the overlap is searched for; identical consecutive rows can
		/// make that ambiguous (flagged through <see cref="Approximate"/>).
		/// </summary>
		private int FindFirstNewRow(List<string> previous, List<string> current)
		{
			if(previous.Count == 0) {
				return 0;
			}

			//Nothing can have been dropped while the native deque is below its cap.
			if(current.Count < NativeLogRows && current.Count >= previous.Count) {
				return previous.Count;
			}

			Approximate = true;
			for(int shift = 0; shift < previous.Count; shift++) {
				int overlap = previous.Count - shift;
				if(overlap > current.Count) {
					continue;
				}
				bool match = true;
				for(int i = 0; i < overlap; i++) {
					if(!string.Equals(previous[shift + i], current[i], StringComparison.Ordinal)) {
						match = false;
						break;
					}
				}
				if(match) {
					return overlap;
				}
			}
			return 0;
		}
	}
}
