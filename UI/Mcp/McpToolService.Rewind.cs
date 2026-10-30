using Mesen.Config;
using Mesen.Config.Shortcuts;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Mesen.Mcp
{
	/// <summary>
	/// MCP-28: rewind with a defined result. The emulator's rewind history is stored in 30-frame blocks and the only
	/// instant jumps it offers are "10 s" (631 frames) and "1 min" (3631 frames). The smaller shortcut (hold-to-rewind)
	/// switches the core into a rewinding mode that is left on until a key release, which is why an earlier version
	/// ran slowly, lost Lua callbacks and moved an unpredictable distance. This version only uses the instant jumps
	/// and plays the surplus forward, so the emulator is always back in normal mode afterwards.
	/// </summary>
	internal sealed partial class McpToolService
	{
		private const int RewindTenSecondsFrames = 631;
		private const int RewindOneMinuteFrames = 3631;

		private McpToolResult ToolRewind(JsonElement args)
		{
			CpuType cpu = GetPrimaryCpuType();
			int? seconds = GetOptionalInt(args, "seconds");
			int? framesArg = GetOptionalInt(args, "frames");
			if(seconds == null && framesArg == null) {
				throw new InvalidOperationException("Give seconds or frames.");
			}
			bool allowPartial = GetOptionalBool(args, "allowPartial") ?? false;

			double fps = 60.0988;
			try { fps = EmuApi.GetTimingInfo(cpu).Fps; } catch { }
			long requested = framesArg ?? (long)Math.Round(Math.Max(1, seconds!.Value) * fps);
			if(requested < 1 || requested > 60 * 60 * 60) {
				throw new McpToolException("invalid_argument", "The rewind distance must be between 1 frame and 1 hour.");
			}

			if(ConfigManager.Config.Preferences.RewindBufferSize == 0) {
				throw new McpToolException("rewind_disabled", "The rewind buffer is disabled (Preferences.RewindBufferSize is 0), so no history is recorded.", new JsonObject() { ["rewindAvailableFrames"] = 0 });
			}
			if(!HistoryApi.HistoryViewerEnabled()) {
				throw new McpToolException("rewind_unavailable", "There is no rewind history yet: it needs at least about 2 seconds (60 frames) of emulation since the ROM was loaded or reset. Advance frames and try again.", new JsonObject() { ["rewindAvailableFrames"] = 0 });
			}

			EnsurePaused(2000);
			long frameBefore = GetFrameCount(cpu);
			string safety = Path.Combine(Path.GetTempPath(), $"mcp_rewind_{Guid.NewGuid():N}.mss");
			EmuApi.SaveStateFile(safety);

			try {
				long covered = 0;
				JsonArray jumps = new JsonArray();
				bool exhausted = false;
				while(covered < requested) {
					long remaining = requested - covered;
					bool minute = remaining >= RewindOneMinuteFrames;
					long before = GetFrameCount(cpu);
					EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = minute ? EmulatorShortcut.RewindOneMin : EmulatorShortcut.RewindTenSecs });
					long after = WaitForFrameChange(cpu, before);
					long delta = before - after;
					jumps.Add((JsonNode)new JsonObject() { ["jump"] = minute ? "1min" : "10s", ["framesRewound"] = Num(delta) });
					if(delta <= 0) {
						exhausted = true;
						break;
					}
					covered += delta;
					//A jump that moved less than a full step means the history ran out.
					if(delta < (minute ? RewindOneMinuteFrames : RewindTenSecondsFrames) - 40) {
						exhausted = true;
						break;
					}
				}

				if(covered < requested && exhausted) {
					if(!allowPartial || covered == 0) {
						//Put everything back; the jumps consumed history but the emulation state is unchanged.
						EmuApi.LoadStateFile(safety);
						throw new McpToolException(covered == 0 ? "rewind_unavailable" : "rewind_insufficient_history",
							$"The rewind history reaches back {covered} frames, less than the {requested} requested. The emulation state was restored. Use allowPartial to rewind as far as possible.",
							new JsonObject() {
								["rewindAvailableFrames"] = Num(covered),
								["requestedFrames"] = Num(requested),
								["frame"] = Num(GetFrameCount(cpu))
							});
					}
				}

				long forward = Math.Max(0, covered - requested);
				if(forward > 0) {
					RunFrames(cpu, (int)forward, DefaultTimeoutMs(forward, true));
				}

				long frameAfter = GetFrameCount(cpu);
				return Json(new JsonObject() {
					["requestedFrames"] = Num(requested),
					["requestedSeconds"] = seconds,
					["framesRewound"] = Num(frameBefore - frameAfter),
					["frameBefore"] = Num(frameBefore),
					["frameAfter"] = Num(frameAfter),
					["jumps"] = jumps,
					["forwardFrames"] = Num(forward),
					["partial"] = covered < requested,
					["rewindAvailableFrames"] = covered < requested ? Num(covered) : null,
					["granularityFrames"] = 30
				});
			} finally {
				try { File.Delete(safety); } catch { }
			}
		}

		/// <summary>Rewind shortcuts are queued to the emulation thread; wait until the frame counter moves (or give up).</summary>
		private long WaitForFrameChange(CpuType cpu, long before)
		{
			Stopwatch watch = Stopwatch.StartNew();
			long frame = GetFrameCount(cpu);
			while(frame == before && watch.ElapsedMilliseconds < 1500) {
				Thread.Sleep(5);
				frame = GetFrameCount(cpu);
			}
			Thread.Sleep(5);
			return GetFrameCount(cpu);
		}
	}
}
