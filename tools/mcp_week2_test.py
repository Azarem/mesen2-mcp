#!/usr/bin/env python3
"""Regression test for the Week 2 server features (MCP-13, MCP-15, MCP-10, MCP-08) against a real MesenMcpHost.

    python tools/mcp_week2_test.py [--rom PATH] [--host DLL]
"""
import argparse
import json
import os
import shutil
import sys
import tempfile

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

    tmp = tempfile.mkdtemp(prefix="mcp_w2_")
    results_dir = os.path.join(tmp, "results")
    c = Client(os.path.abspath(args.host), [f"--mcp-results-dir={results_dir}"])
    try:
        c.request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                 "clientInfo": {"name": "w2", "version": "1"}})
        c.proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        c.proc.stdin.flush()

        err, res = call_json(c, "load_rom", path=args.rom)
        check("load_rom", not err and res.get("loaded"), str(res)[:150])
        c.call("pause")

        err, caps = call_json(c, "get_capabilities")
        check("capabilities: version/limits/flags", caps.get("serverVersion") and "limits" in caps and "flags" in caps, str(caps)[:200])
        check("capabilities: resultsDir", caps.get("resultsDir", "").lower().endswith("results"), str(caps.get("resultsDir")))

        # ---- MCP-13 labels ----------------------------------------------------------------------
        mlb = os.path.join(tmp, "test.mlb")
        with open(mlb, "w") as f:
            f.write("SnesWorkRam:0642:sceneNext:next scene\n")
            f.write("SnesWorkRam:09A2-09A3:playerX\n")
            f.write("SnesPrgRom:0000:FirstByte\n")
        sym = os.path.join(tmp, "test.sym")
        with open(sym, "w") as f:
            f.write("[labels]\n00:8000 Reset_Sym\n")
        err, ld = call_json(c, "load_labels", path=mlb, replace=True)
        check("load_labels mlb", not err and ld.get("parsed") == 3 and ld.get("labelsNow") == 3, str(ld)[:250])
        err, r = call_json(c, "resolve_address", label="sceneNext")
        check("resolve label -> wram", not err and r.get("wram") == 0x642, str(r)[:250])
        err, r = call_json(c, "resolve_address", cpu="7E:0642")
        check("resolve cpu -> label", not err and r.get("label") == "sceneNext" and r.get("exact"), str(r)[:250])
        err, r = call_json(c, "resolve_address", wram=0x09A3)
        check("resolve wram inside label range", not err and r.get("label") == "playerX", str(r)[:250])
        err, r = call_json(c, "resolve_address", prgOffset=0)
        cpu0 = r.get("cpu")
        check("resolve prgOffset -> cpu + label", not err and cpu0 and r.get("label") == "FirstByte", str(r)[:250])
        err, r = call_json(c, "resolve_addresses", addresses=["7E:0642", 0x7E09A2, {"prgOffset": 0}])
        check("resolve_addresses bulk", not err and len(r.get("results", r.get("addresses", []))) == 3, str(r)[:250])
        err, r = call_json(c, "list_labels", filter="player")
        check("list_labels filter", not err and any(l.get("name") == "playerX" for l in r.get("labels", [])), str(r)[:250])
        err, ld = call_json(c, "load_labels", path=sym)
        check("load_labels sym", not err and ld.get("parsed") == 1 and ld.get("labelsNow") == 4, str(ld)[:250])
        err, ld = call_json(c, "load_labels", path=os.path.join(tmp, "missing.mlb"))
        check("load_labels missing file errors", err)
        err, r = call_json(c, "list_labels")
        check("labels kept after sym import", len(r.get("labels", [])) == 4, str(r)[:250])

        # ---- MCP-08 results dir / writeFile ------------------------------------------------------
        code = ('emu.mcp.writeFile("sub/x.bin", "\\0\\1\\255abc")\n'
                'emu.mcp.result({a = 1, s = "hi"})\n'
                'emu.mcp.done()')
        err, rs = call_json(c, "run_script", code=code, frames=100)
        check("run_script stops on done", not err and rs.get("stopReason") == "script_done" and rs.get("framesAdvanced", 99) < 5, str(rs)[:300])
        check("run_script result", rs.get("result", {}).get("a") == 1, str(rs.get("result")))
        written = os.path.join(results_dir, "sub", "x.bin")
        check("writeFile bytes exact", os.path.exists(written) and open(written, "rb").read() == b"\x00\x01\xffabc",
              str(open(written, "rb").read() if os.path.exists(written) else None))
        check("filesWritten reported", any("x.bin" in str(f) for f in rs.get("filesWritten", [])), str(rs.get("filesWritten")))

        err, rs = call_json(c, "run_script", code='local ok, e = emu.mcp.writeFile("../evil.txt", "x") emu.mcp.result({ok = ok == nil, e = e}) emu.mcp.done()', frames=10)
        check("writeFile rejects ..", rs.get("result", {}).get("ok") is True and not os.path.exists(os.path.join(tmp, "evil.txt")), str(rs)[:200])

        # ---- MCP-15 kit includes -----------------------------------------------------------------
        err, rs = call_json(c, "run_script", includes=["mesen_kit"], frames=5,
                            code='kit.w8(0x1F00, 0x77) emu.mcp.result({v = kit.r8(0x1F00)}) emu.mcp.done()')
        check("includes mesen_kit", not err and rs.get("result", {}).get("v") == 0x77, str(rs)[:300])
        err, rs = call_json(c, "run_script", includes=["nope"], code="emu.mcp.done()", frames=1)
        check("unknown include errors", err, str(rs)[:200])
        err, lr = call_json(c, "list_recipes")
        names = {r.get("name") for r in lr.get("recipes", [])}
        check("list_recipes", {"warp", "census_pc", "exec_coverage"} <= names and len(lr.get("modules", [])) >= 2, str(lr)[:300])

        # ---- MCP-10 turbo / run_until ------------------------------------------------------------
        err, af = call_json(c, "advance_frame", frames=600, turbo=True)
        fps = af.get("framesPerSecond", 0)
        check("turbo advance_frame fast", not err and af.get("framesAdvanced") == 600 and fps > 200, f"fps={fps}")

        err, rs = call_json(c, "run_script", frames=0, unloadAfter=False, name="setter",
                            code='local n = 0 emu.addEventCallback(function() n = n + 1 if n == 50 then emu.write(0x1F10, 0x5A, emu.memType.snesWorkRam) end end, emu.eventType.endFrame)')
        err, ru = call_json(c, "run_until", condition={"type": "memoryEquals", "address": 0x1F10, "value": 0x5A}, maxFrames=500)
        check("run_until memoryEquals", not err and ru.get("met") is True and ru.get("stopReason") == "condition"
              and 45 <= ru.get("framesAdvanced", 0) <= 60, str(ru)[:300])
        err, ru = call_json(c, "run_until", condition={"type": "memoryEquals", "address": 0x1F11, "value": 0x99}, maxFrames=30)
        check("run_until gives up at maxFrames", not err and ru.get("met") is False and ru.get("framesAdvanced") == 30, str(ru)[:300])
        c.call("unload_script", name="setter")

        err, text, _ = c.call("read_word", address=0xFFEA)
        nmi = int(text.split()[0], 16)
        err, ru = call_json(c, "run_until", condition={"type": "pcEquals", "pc": nmi}, maxFrames=30)
        check("run_until pcEquals", not err and ru.get("met") is True and ru.get("cpu", {}).get("pc") == nmi, str(ru)[:300])
        err, br = call_json(c, "get_capabilities")
        err, ru2 = call_json(c, "run_until", condition={"type": "pcEquals", "pc": nmi}, maxFrames=5)
        err, text, _ = c.call("set_breakpoints", breakpoints=[{"callbackType": "exec", "startAddress": nmi}])
        err, ru3 = call_json(c, "run_until", condition={"type": "breakpoint"}, maxFrames=30)
        check("run_until breakpoint (and temp bp restores user bps)", ru3.get("stopReason") == "breakpoint", str(ru3)[:300])
        c.call("clear_breakpoints")
        err, ru = call_json(c, "run_until", condition={"type": "lua", "expr": "kit.frame >= 10", "includes": ["mesen_kit"]}, maxFrames=40)
        check("run_until lua expr", not err and ru.get("met") is True and 9 <= ru.get("framesAdvanced", 0) <= 12, str(ru)[:300])
        err, ru = call_json(c, "run_until", condition={"type": "bogus"}, maxFrames=5)
        check("run_until unknown condition errors", err, str(ru)[:200])

        # ---- recipes ---------------------------------------------------------------------------
        err, rc = call_json(c, "run_recipe", name="census_pc", frames=120, args={"top": 5})
        res = rc.get("result", {})
        check("recipe census_pc", not err and res.get("samples") == 120 and 1 <= len(res.get("top", [])) <= 5, str(rc)[:400])
        err, rc = call_json(c, "run_recipe", name="exec_coverage", frames=60, args={"top": 5, "start": 0x8000, "stop": 0xFFFF})
        res = rc.get("result", {})
        check("recipe exec_coverage", not err and res.get("totalExecutions", 0) > 0 and len(res.get("top", [])) > 0, str(rc)[:400])
        err, rc = call_json(c, "run_recipe", name="nope")
        check("unknown recipe errors", err)

        err, _, _ = c.call("load_rom", path=args.rom)
        c.call("pause")
        err, rc = call_json(c, "run_recipe", name="warp", turbo=True)
        res = rc.get("result", {})
        check("recipe warp -> scene 0x0A", not err and rc.get("stopReason") == "script_done" and res.get("scene") == 0x0A and res.get("ok"),
              str(rc)[:500])
        print("warp frames:", rc.get("framesAdvanced"), "fps:", rc.get("framesPerSecond"))
    finally:
        c.close() if hasattr(c, "close") else c.proc.kill()
        shutil.rmtree(tmp, ignore_errors=True)

    failed = [n for n, ok in results if not ok]
    print(f"\n{len(results) - len(failed)}/{len(results)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
