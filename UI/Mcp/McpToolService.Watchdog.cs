using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Mcp
{
	/// <summary>
	/// MCP-27: every tool call runs under a watchdog. A call that does not return within its limit (a hung native
	/// step, a script that never yields) is answered with {error:{code:"timeout"}} and the emulator is paused, so the
	/// MCP client never has to give up on the host. The abandoned call keeps running on its worker thread; until it ends,
	/// further calls are answered with {error:{code:"busy"}} instead of queueing behind it.
	/// </summary>
	internal sealed partial class McpToolService
	{
		/// <summary>Extra time given on top of a call's timeoutMs before the watchdog answers (MESEN_MCP_WATCHDOG_GRACE_MS overrides it).</summary>
		private static readonly int WatchdogGraceMs = int.TryParse(Environment.GetEnvironmentVariable("MESEN_MCP_WATCHDOG_GRACE_MS"), out int grace) && grace >= 0 ? grace : 15000;
		private const int WatchdogDefaultMs = 120000;
		private const int WatchdogMaxMs = 3600000;

		private sealed class RecentCall
		{
			public string Tool = "";
			public string Args = "";
			public DateTime StartedUtc;
			public long ElapsedMs = -1;
			public string Outcome = "running";
		}

		private readonly object _recentLock = new object();
		private readonly List<RecentCall> _recentCalls = new List<RecentCall>();
		private Task<McpToolResult>? _stuckCall;
		private RecentCall? _stuckInfo;
		private readonly DateTime _startedUtc = DateTime.UtcNow;

		private static readonly HashSet<string> MultiPhaseTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
			"compare_runs", "self_test", "play_input_file", "play_input", "trace_between"
		};

		public McpToolResult CallTool(string name, JsonElement args)
		{
			if(name.Equals("get_server_status", StringComparison.OrdinalIgnoreCase)) {
				//Never blocked by a stuck call: this is how a client finds out what is going on.
				return ToolGetServerStatus(args);
			}

			Task<McpToolResult>? stuck = _stuckCall;
			if(stuck != null) {
				if(!stuck.IsCompleted) {
					RecentCall? info = _stuckInfo;
					return ErrorJson("busy", $"The previous call '{info?.Tool}' did not return in time and is still running inside the emulator. " +
						"Wait for it to finish (get_server_status shows its state) or restart the MCP server.", new JsonObject() {
							["stuckTool"] = info?.Tool,
							["stuckForMs"] = info != null ? (long)(DateTime.UtcNow - info.StartedUtc).TotalMilliseconds : null
						});
				}
				_stuckCall = null;
				_stuckInfo = null;
			}

			string argsText;
			JsonElement argsClone;
			try {
				argsClone = args.ValueKind == JsonValueKind.Undefined ? default : args.Clone();
				argsText = args.ValueKind == JsonValueKind.Undefined ? "" : args.GetRawText();
			} catch {
				argsClone = default;
				argsText = "";
			}
			if(argsText.Length > 200) {
				argsText = argsText.Substring(0, 200) + "...";
			}

			RecentCall call = new RecentCall() { Tool = name, Args = argsText, StartedUtc = DateTime.UtcNow };
			lock(_recentLock) {
				_recentCalls.Add(call);
				while(_recentCalls.Count > 5) {
					_recentCalls.RemoveAt(0);
				}
			}
			McpDebugLog.Info($"call {name} {argsText}");

			int limit = ComputeWatchdogLimitMs(name, argsClone);
			Task<McpToolResult> task = Task.Factory.StartNew(() => CallToolCore(name, argsClone), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

			bool finished;
			try {
				finished = task.Wait(limit);
			} catch(AggregateException ex) {
				call.Outcome = "exception";
				call.ElapsedMs = (long)(DateTime.UtcNow - call.StartedUtc).TotalMilliseconds;
				Exception inner = ex.InnerException ?? ex;
				return McpToolResult.Text($"Error: {inner.Message}", true);
			}

			call.ElapsedMs = (long)(DateTime.UtcNow - call.StartedUtc).TotalMilliseconds;
			if(finished) {
				McpToolResult result = task.Result;
				call.Outcome = result.IsError ? "error" : "ok";
				return result;
			}

			call.Outcome = "watchdog_timeout";
			_stuckCall = task;
			_stuckInfo = call;
			string recent = DescribeRecentCalls(3);
			McpDebugLog.Always($"tool '{name}' exceeded {limit} ms; answering with a timeout and pausing. Last calls: {recent}");

			bool paused = TryPauseWithDeadline(2000);
			return ErrorJson("timeout", $"Tool '{name}' did not return within {limit} ms. The emulator was {(paused ? "paused" : "asked to pause")}. " +
				"The call may still be running inside the emulator; further calls return 'busy' until it ends. Use get_server_status to check.", new JsonObject() {
					["tool"] = name,
					["limitMs"] = limit,
					["paused"] = paused,
					["recentCalls"] = BuildRecentCallsNode(3)
				});
		}

		private static int ComputeWatchdogLimitMs(string name, JsonElement args)
		{
			long limit;
			int? timeout = GetOptionalInt(args, "timeoutMs");
			if(timeout != null) {
				limit = (long)timeout.Value + WatchdogGraceMs;
			} else {
				long frames = Math.Max(Math.Max(GetOptionalInt(args, "frames") ?? 0, GetOptionalInt(args, "maxFrames") ?? 0), GetOptionalInt(args, "count") ?? 0);
				limit = Math.Max(WatchdogDefaultMs, DefaultTimeoutMs(frames, false) + WatchdogGraceMs);
				if(MultiPhaseTools.Contains(name)) {
					limit *= 4;
				}
			}
			return (int)Math.Clamp(limit, 1000, WatchdogMaxMs);
		}

		private bool TryPauseWithDeadline(int deadlineMs)
		{
			//EmuApi.Pause() can itself block when the core is wedged, so it runs on its own thread.
			Task pause = Task.Run(() => {
				try { EmuApi.Pause(); } catch { }
			});
			Stopwatch watch = Stopwatch.StartNew();
			if(!pause.Wait(deadlineMs)) {
				return false;
			}
			while(watch.ElapsedMilliseconds < deadlineMs) {
				try { if(EmuApi.IsPaused()) { return true; } } catch { return false; }
				Thread.Sleep(10);
			}
			return false;
		}

		private JsonArray BuildRecentCallsNode(int count)
		{
			JsonArray rows = new JsonArray();
			lock(_recentLock) {
				foreach(RecentCall c in _recentCalls.Skip(Math.Max(0, _recentCalls.Count - count))) {
					rows.Add((JsonNode)new JsonObject() {
						["tool"] = c.Tool,
						["args"] = c.Args,
						["startedUtc"] = c.StartedUtc.ToString("O"),
						["elapsedMs"] = c.ElapsedMs >= 0 ? c.ElapsedMs : (long)(DateTime.UtcNow - c.StartedUtc).TotalMilliseconds,
						["outcome"] = c.Outcome
					});
				}
			}
			return rows;
		}

		private string DescribeRecentCalls(int count)
		{
			return string.Join(" | ", BuildRecentCallsNode(count).Select(n => $"{n!["tool"]} {n["args"]} ({n["elapsedMs"]} ms, {n["outcome"]})"));
		}

		/// <summary>Test hook (only registered when MESEN_MCP_TEST_TOOLS=1): blocks the tool call for ms milliseconds.</summary>
		private McpToolResult ToolTestHang(JsonElement args)
		{
			Thread.Sleep(Math.Clamp(GetOptionalInt(args, "ms") ?? 1000, 0, 120000));
			return Json(new JsonObject() { ["slept"] = true });
		}

		private McpToolResult ToolGetServerStatus(JsonElement _)
		{
			RecentCall? stuck = _stuckInfo;
			bool busy = _stuckCall != null && !_stuckCall.IsCompleted;
			JsonObject node = new JsonObject() {
				["serverVersion"] = ServerVersion,
				["uptimeSeconds"] = (long)(DateTime.UtcNow - _startedUtc).TotalSeconds,
				["busy"] = busy,
				["stuckTool"] = busy ? stuck?.Tool : null,
				["stuckForMs"] = busy && stuck != null ? (long)(DateTime.UtcNow - stuck.StartedUtc).TotalMilliseconds : null,
				["romLoaded"] = SafeIsRunning(),
				["paused"] = SafeIsPaused(),
				["recentCalls"] = BuildRecentCallsNode(5)
			};
			return Json(node);
		}

		private static bool SafeIsRunning()
		{
			try { return EmuApi.IsRunning(); } catch { return false; }
		}

		private static bool? SafeIsPaused()
		{
			try { return EmuApi.IsRunning() ? EmuApi.IsPaused() : null; } catch { return null; }
		}
	}
}
