using Mesen.Config.Shortcuts;
using Mesen.Debugger.Labels;
using Mesen.Interop;
using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Mesen.Mcp
{
	/// <summary>
	/// T-42: resets that stop at the first instruction of the boot code, so scripts can watch it.
	/// The native reset (or power cycle) is a flag the emulation thread handles at the end of a frame. The server
	/// arms a one-shot pause, requests the reset, lets the emulator run, and pauses from inside the reset
	/// notification, i.e. before the first boot instruction executes (the same trick the debugger window uses).
	/// A power cycle builds a new debugger: scripts and Lua-backed breakpoints are dropped, native breakpoints and labels are re-applied.
	/// </summary>
	internal sealed partial class McpToolService
	{
		private volatile bool _resetPauseArmed;
		private readonly ManualResetEventSlim _resetObserved = new ManualResetEventSlim(false);

		private void OnResetNotification(bool powerCycle)
		{
			if(!_resetPauseArmed) {
				return;
			}
			_resetPauseArmed = false;
			try {
				EmuApi.Pause();
			} finally {
				_resetObserved.Set();
			}
		}

		private McpToolResult ToolReset(JsonElement args)
		{
			string mode = (GetOptionalString(args, "mode") ?? "soft").ToLowerInvariant();
			if(mode != "soft" && mode != "power") {
				throw new McpToolException("invalid_argument", "mode must be soft or power.");
			}
			return Json(ResetConsole(mode == "power"));
		}

		/// <summary>
		/// Resets (or power cycles) the console and leaves it paused before the first boot instruction.
		/// A soft reset keeps WRAM; a power cycle re-initializes RAM and restarts the frame counter.
		/// </summary>
		private JsonObject ResetConsole(bool power)
		{
			CpuType cpu = GetPrimaryCpuType();
			long frameBefore = GetFrameCount(cpu);
			bool wasPaused = EmuApi.IsPaused();

			_resetObserved.Reset();
			_resetPauseArmed = true;
			try {
				EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = power ? EmulatorShortcut.ExecPowerCycle : EmulatorShortcut.ExecReset });
				if(wasPaused) {
					//The flag is handled when the frame ends, so the emulator has to run for a moment.
					DebugApi.ResumeExecution();
					EmuApi.Resume();
				}
				if(!_resetObserved.Wait(10000)) {
					try { EmuApi.Pause(); } catch { }
					throw new McpToolException("reset_failed", "The emulator did not process the reset within 10 s.");
				}
			} finally {
				_resetPauseArmed = false;
			}
			//The pause request was made inside the reset notification; the break itself happens at the next instruction.
			//Do not call Pause again here: a second step request would run one more instruction.
			Stopwatch pauseWatch = Stopwatch.StartNew();
			while(!EmuApi.IsPaused() && pauseWatch.ElapsedMilliseconds < 5000) {
				Thread.Sleep(1);
			}
			if(!EmuApi.IsPaused()) {
				EnsurePaused(2000);
			}

			bool scriptsDropped = false;
			if(power) {
				//A power cycle creates a new debugger: scripts and their Lua breakpoints are gone.
				scriptsDropped = _scriptIds.Count > 0;
				ForgetAllScripts();
				try { LabelManager.RefreshLabels(false); } catch { }
				try { DebugApi.SetBreakpoints(_interopBps.ToArray(), (uint)_interopBps.Count); } catch { }
			}

			JsonObject node = new JsonObject() {
				["mode"] = power ? "power" : "soft",
				["frameBefore"] = Num(frameBefore),
				["frameAfter"] = Num(GetFrameCount(cpu)),
				["paused"] = EmuApi.IsPaused(),
				["cpu"] = BuildCpuSummary(cpu)
			};
			if(scriptsDropped) {
				node["scriptsDropped"] = true;
			}
			return node;
		}
	}
}
