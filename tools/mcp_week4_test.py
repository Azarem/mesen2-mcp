#!/usr/bin/env python3
"""Regression test for the 0.5.0 server features (MCP-21..30, tool issues T-01..T-19) against a real MesenMcpHost.

    python tools/mcp_week4_test.py [--rom PATH] [--host DLL]
"""
import argparse
import json
import os
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_iog_e2e_test import Client, DEFAULT_HOST, DEFAULT_ROM  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rom", default=DEFAULT_ROM)
    ap.add_argument("--host", default=DEFAULT_HOST)
    args = ap.parse_args()

    results = []

    def check(name, ok, detail=""):
        results.append((name, bool(ok)))
        print(f"[{'PASS' if ok else 'FAIL'}] {name}" + (f" - {detail}" if detail else ""))

    def call_json(c, tool, **kw):
        err, text, _ = c.call(tool, **kw)
        try:
            return err, json.loads(text)
        except json.JSONDecodeError:
            return err, {"raw": text}

    os.environ["MESEN_MCP_TEST_TOOLS"] = "1"
    os.environ["MESEN_MCP_WATCHDOG_GRACE_MS"] = "500"
    tmp = tempfile.mkdtemp(prefix="mcp_w4_")
    proj1 = os.path.join(tmp, "proj1")
    proj2 = os.path.join(tmp, "proj2")
    os.makedirs(os.path.join(proj1, "recipes"))
    os.makedirs(proj2)
    with open(os.path.join(proj1, "proj_kit.lua"), "w") as f:
        f.write("-- description: project kit\n-- requires: mesen_kit\nproj = {answer = 42}\n")
    with open(os.path.join(proj1, "recipes", "proj_recipe.lua"), "w") as f:
        f.write("-- description: project recipe\n-- includes: proj_kit\n-- frames: 5\nkit.finish({answer = proj.answer})\n")
    with open(os.path.join(proj2, "other_kit.lua"), "w") as f:
        f.write("-- description: second project kit\nother = {name = 'two'}\n")

    results_dir = os.path.join(tmp, "results")
    c = Client(os.path.abspath(args.host),
               [f"--mcp-results-dir={results_dir}", f"--mcp-lib-dir={proj1};{proj2}"])
    try:
        c.request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                 "clientInfo": {"name": "w4", "version": "1"}})
        c.proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        c.proc.stdin.flush()

        # ---- MCP-30 / MCP-22 capabilities without a ROM ---------------------------------------------
        err, cap = call_json(c, "get_capabilities")
        check("get_capabilities works without a ROM", not err and cap.get("romLoaded") is False and cap.get("serverVersion"), str(cap)[:300])
        check("capabilities report lib dirs", len(cap.get("libDirs", [])) >= 3 and cap["libDirs"][-1].endswith("proj2"), str(cap.get("libDirs")))
        check("capabilities report results dir", cap.get("resultsDir") == os.path.abspath(results_dir), str(cap.get("resultsDir")))
        check("capabilities report host path", bool(cap.get("hostPath")) and bool(cap.get("baseDirectory")), str(cap.get("hostPath")))
        err, st = call_json(c, "get_server_status")
        check("get_server_status without ROM", not err and st.get("busy") is False and st.get("romLoaded") is False, str(st)[:200])

        # ---- MCP-21 several lib folders ------------------------------------------------------------
        err, lr = call_json(c, "list_recipes")
        mods = [m["name"] for m in lr.get("modules", [])]
        recs = [r["name"] for r in lr.get("recipes", [])]
        check("list_recipes merges the folders", {"mesen_kit", "proj_kit", "other_kit"} <= set(mods) and "proj_recipe" in recs and "warp" in recs, f"{mods} {recs}")

        err, r = call_json(c, "load_rom", path=args.rom)
        check("load_rom", not err and r.get("loaded"), str(r)[:100])
        c.call("pause")
        err, cap = call_json(c, "get_capabilities")
        check("capabilities with ROM", cap.get("romLoaded") is True and cap.get("cpuType") == "Snes", str(cap)[:200])

        err, rs = call_json(c, "run_script", code="emu.mcp.result({v = proj.answer, o = other.name}) emu.mcp.done()",
                            includes=["proj_kit", "other_kit"], frames=50)
        check("includes search the project folders", not err and rs.get("result", {}).get("v") == 42 and rs["result"].get("o") == "two", str(rs)[:300])
        err, rr = call_json(c, "run_recipe", name="proj_recipe")
        check("run_recipe from a project folder", not err and rr.get("result", {}).get("answer") == 42, str(rr)[:300])

        # ---- MCP-23 done() / finish() at load ------------------------------------------------------
        err, rs = call_json(c, "run_script", code="emu.mcp.result({loaded = true}) emu.mcp.done()", frames=100)
        check("emu.mcp.done at load ends the run at once", not err and rs.get("stopReason") == "script_done" and rs.get("framesAdvanced", 99) <= 1 and rs.get("result", {}).get("loaded") is True, str(rs)[:300])
        err, rs = call_json(c, "run_script", code="kit.finish({x = 7})", includes=["mesen_kit"], frames=100)
        check("kit.finish at load works", not err and rs.get("stopReason") == "script_done" and rs.get("result", {}).get("x") == 7, str(rs)[:300])
        err, rs = call_json(c, "run_script", code="emu.mcp.done() emu.mcp.result({after = true})", frames=100)
        check("code after done() at load still runs", not err and rs.get("result", {}).get("after") is True, str(rs)[:300])

        # ---- MCP-24 JSON memory tools --------------------------------------------------------------
        err, w = call_json(c, "write_memory", address=0x7E1F20, data=[0x34, 0x12])
        check("write_memory returns JSON", not err and w.get("written") == 2 and w.get("value") == 0x1234 and w.get("hex") == "0x1234" and w.get("verified") is True, str(w))
        err, w = call_json(c, "write_word", address=0x7E1F22, value=0xBEEF)
        check("write_word returns JSON", not err and w.get("value") == 0xBEEF and w.get("hex") == "0xBEEF" and w.get("verified") is True, str(w))
        err, w = call_json(c, "read_word", address=0x7E1F22)
        check("read_word returns JSON", not err and w.get("value") == 0xBEEF and w.get("hex") == "0xBEEF", str(w))
        err, w = call_json(c, "read_memory", address=0x7E1F20, length=4, format="bytes")
        check("read_memory bytes object", not err and w.get("bytes") == [0x34, 0x12, 0xEF, 0xBE], str(w))

        # ---- MCP-30 label address JSON -------------------------------------------------------------
        mlb = os.path.join(tmp, "x.mlb")
        with open(mlb, "w") as f:
            f.write("SnesWorkRam:642:sceneNext\nSnesPrgRom:0:FirstByte\n")
        call_json(c, "load_labels", path=mlb, replace=True)
        err, la = call_json(c, "get_label_address", label="sceneNext")
        check("get_label_address returns JSON", not err and la.get("address") == 0x642 and la.get("hex") == "0x642" and la.get("memoryType") == "snesWorkRam", str(la))
        err, la = call_json(c, "get_label_address", label="nope")
        check("get_label_address unknown label errors", err)

        # ---- MCP-27 step_scanline ------------------------------------------------------------------
        err, sl = call_json(c, "step_scanline", count=10)
        check("step_scanline reports scanlines", not err and sl.get("scanlinesAdvanced") == 10 and sl.get("scanlinesRequested") == 10 and sl.get("framesAdvanced") == 0 and "scanline" in sl, str(sl)[:300])

        # ---- MCP-29 events -------------------------------------------------------------------------
        call_json(c, "subscribe_events", types=["breakpointHit"])
        before = c.notifications
        call_json(c, "advance_frame", frames=10, turbo=True)
        call_json(c, "step_instruction", count=5)
        check("no breakpoint_hit events for step completions", c.notifications == before, f"{c.notifications - before} notifications")
        call_json(c, "subscribe_events", types=[])

        # ---- MCP-25 breakpoint hit offsets ---------------------------------------------------------
        err, r = call_json(c, "set_breakpoints", breakpoints=[
            {"callbackType": "exec", "startAddress": 0x8000, "endAddress": 0xFFFF, "action": "log", "memType": "snesMemory", "name": "alias", "maxLogged": 5},
            {"callbackType": "write", "startAddress": 0x0000, "endAddress": 0x1FFF, "action": "log", "memType": "snesWorkRam", "name": "wram", "maxLogged": 5}])
        check("set_breakpoints log", not err, str(r)[:200])
        call_json(c, "advance_frame", frames=30, turbo=True)
        err, h = call_json(c, "get_breakpoint_hits")
        rows = h.get("hits", [])
        ex = next((x for x in rows if x.get("id") == 0), {})
        wr = next((x for x in rows if x.get("id") == 1), {})
        check("exec hit has prgOffset", "prgOffset" in ex and ex.get("memType") == "snesMemory" and "pcPrgOffset" in ex, str(ex)[:300])
        check("wram hit has wramOffset", wr.get("wramOffset") == wr.get("address") and wr.get("memType") == "snesWorkRam", str(wr)[:300])
        call_json(c, "clear_breakpoints")

        # T-18: halt alias
        err, r = call_json(c, "set_breakpoints", breakpoints=[{"callbackType": "exec", "startAddress": 0x8000, "action": "halt"}])
        check("action halt is an alias of break", not err and r.get("halting") == 1, str(r)[:200])
        call_json(c, "clear_breakpoints")

        # T-08: top-level includes for run_until
        err, ru = call_json(c, "run_until", condition={"type": "lua", "expr": "kit.frame >= 3"}, includes=["mesen_kit"], maxFrames=30)
        check("run_until accepts top-level includes", not err and ru.get("met") is True, str(ru)[:300])

        # ---- MCP-27 get_access_summary extras ------------------------------------------------------
        call_json(c, "reset_access_counters")
        call_json(c, "advance_frame", frames=30, turbo=True)
        err, r = call_json(c, "get_access_summary", type="prgRom", ranges=[{"name": "all", "start": 0, "end": 0x1FFFFF}])
        row = (r.get("rows") or [{}])[0]
        check("access summary hottest/execBytes", "hottest" in row and row.get("execBytes", 0) >= 1 and "execFirstByte" in row, str(row)[:300])

        # ---- MCP-28 rewind -------------------------------------------------------------------------
        call_json(c, "advance_frame", frames=1500, turbo=True)
        err, t0 = call_json(c, "get_timing_info")
        f0 = t0.get("frameCount")
        err, rw = call_json(c, "rewind", seconds=3)
        check("rewind 3 s lands exactly 3 s earlier", not err and rw.get("framesRewound") == rw.get("requestedFrames") and 175 <= rw.get("framesRewound") <= 185, str(rw)[:400])
        err, t1 = call_json(c, "get_timing_info")
        check("frame counter matches the rewind reply", t1.get("frameCount") == rw.get("frameAfter") and f0 - t1.get("frameCount") == rw.get("framesRewound"), f"{f0} -> {t1.get('frameCount')}")
        err, rw2 = call_json(c, "rewind", frames=45)
        check("rewind by frames is exact", not err and rw2.get("framesRewound") == 45, str(rw2)[:300])
        code = ("local n = 0 emu.addMemoryCallback(function() n = n + 1 end, emu.callbackType.exec, 0x000000, 0xFFFFFF) "
                "emu.addEventCallback(function() emu.mcp.result({n = n}) end, emu.eventType.endFrame)")
        t_start = time.time()
        err, rs = call_json(c, "run_script", code=code, frames=8, turbo=True)
        elapsed = time.time() - t_start
        check("Lua exec callbacks work after a rewind", not err and rs.get("result", {}).get("n", 0) > 1000 and elapsed < 5, f"{rs.get('result')} in {elapsed:.2f}s")
        err, adv = call_json(c, "advance_frame", frames=120, turbo=True)
        check("speed is normal after a rewind", not err and adv.get("framesPerSecond", 0) > 100, str(adv.get("framesPerSecond")))
        err, big = call_json(c, "rewind", seconds=3000)
        check("rewind refuses when the history is too short", err and big.get("error", {}).get("code") == "rewind_insufficient_history" and big["error"].get("rewindAvailableFrames", 0) > 0, str(big)[:300])
        err, t2 = call_json(c, "get_timing_info")
        check("refused rewind leaves the state alone", t2.get("frameCount") == adv.get("frame"), f"{t2.get('frameCount')} vs {adv.get('frame')}")
        err, part = call_json(c, "rewind", seconds=3000, allowPartial=True)
        check("allowPartial rewinds as far as possible", not err and part.get("partial") is True and part.get("framesRewound", 0) > 0, str(part)[:300])
        err, r = call_json(c, "load_rom", path=args.rom)
        c.call("pause")
        err, nohist = call_json(c, "rewind", seconds=2)
        check("rewind without history is refused", err and nohist.get("error", {}).get("code") in ("rewind_unavailable", "rewind_insufficient_history"), str(nohist)[:300])
        err, t3 = call_json(c, "get_timing_info")
        check("refusal does not move the emulation", t3.get("frameCount", 99) <= 2, str(t3.get("frameCount")))

        # ---- MCP-26 quick warp (from the title screen, no coordinates) -----------------------------
        err, wp = call_json(c, "run_recipe", name="warp", args={"scene": 0x15, "delay": 450}, frames=1500, timeoutMs=60000)
        wres = wp.get("result", {})
        check("warp without x/y uses the quick warp", not err and wres.get("scene") == 0x15 and wres.get("quick") is True and wres.get("ok") is True, str(wres))
        err, wq = call_json(c, "run_recipe", name="warp", args={"scene": 0x08, "quick": True, "delay": 100, "flags": [0x3F]}, frames=1500, timeoutMs=60000)
        wres = wq.get("result", {})
        check("warp quick=true reaches the scene", not err and wres.get("scene") == 0x08 and wres.get("quick") is True, str(wres))

        # ---- MCP-27 watchdog -----------------------------------------------------------------------
        t_start = time.time()
        try:
            err, hang = call_json(c, "__test_hang", ms=6000, timeoutMs=1000)
            waited = time.time() - t_start
            check("hung call is answered by the watchdog", err and hang.get("error", {}).get("code") == "timeout" and waited < 4, f"{waited:.1f}s {str(hang)[:250]}")
            check("timeout reply lists the recent calls", len(hang.get("error", {}).get("recentCalls", [])) >= 3, str(hang)[:300])
            err, st = call_json(c, "get_server_status")
            check("get_server_status shows the stuck call", st.get("busy") is True and st.get("stuckTool") == "__test_hang", str(st)[:250])
            err, busy = call_json(c, "read_word", address=0x7E0000)
            check("calls during a stuck call return busy", err and busy.get("error", {}).get("code") == "busy", str(busy)[:200])
            time.sleep(5.5)
            err, ok = call_json(c, "read_word", address=0x7E0000)
            check("server recovers when the stuck call ends", not err and "value" in ok, str(ok)[:200])
        except Exception as ex:  # noqa: BLE001
            check("watchdog test ran", False, repr(ex))
    finally:
        c.close()

    check("stdout stayed clean", not c.non_json_stdout, str(c.non_json_stdout[:3]))
    passed = sum(1 for _, ok in results if ok)
    print(f"\n{passed}/{len(results)} passed")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
