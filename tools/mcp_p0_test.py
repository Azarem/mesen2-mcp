#!/usr/bin/env python3
"""Regression test for the P0 server features (MCP-01 to MCP-05) against a real MesenMcpHost.

Needs two different ROMs (default: the IOG original and the rebuilt ROM). Run from the repo root:

    python tools/mcp_p0_test.py [--rom-a PATH] [--rom-b PATH] [--host DLL]
"""
import argparse
import hashlib
import json
import os
import sys
import tempfile
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_iog_e2e_test import Client, DEFAULT_HOST  # noqa: E402

ROM_A = r"C:\Work\gaia-iog-baserom\rebuilt\Illusion of Gaia - Rebuilt.smc"
ROM_B = r"C:\Work\gaia-iog-baserom\rebuilt\Illusion of Gaia.smc"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rom-a", default=ROM_A)
    ap.add_argument("--rom-b", default=ROM_B)
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

    tmp = tempfile.mkdtemp(prefix="mcp_p0_")
    c = Client(os.path.abspath(args.host))
    try:
        c.request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                 "clientInfo": {"name": "p0", "version": "1"}})
        c.proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        c.proc.stdin.flush()

        # ---- MCP-02 ----------------------------------------------------------------------------
        err, res = call_json(c, "load_rom", path=args.rom_a)
        check("load_rom ok", not err and res.get("loaded"), str(res)[:200])
        data = open(args.rom_a, "rb").read()
        sha_a = hashlib.sha1(data).hexdigest()
        rom = res.get("rom", {})
        check("load_rom returns identity", rom.get("fileSha1") == sha_a and rom.get("fileSizeBytes") == len(data))
        err, info = call_json(c, "get_rom_info")
        check("get_rom_info sha1/size/crc",
              info.get("fileSha1") == sha_a and info.get("fileSizeBytes") == len(data)
              and info.get("fileCrc32") == "%08x" % (zlib.crc32(data) & 0xFFFFFFFF),
              f"size={info.get('fileSizeBytes')} sha1={info.get('fileSha1')}")
        check("get_rom_info header", isinstance(info.get("header"), dict) and info["header"].get("title"),
              str(info.get("header")))
        check("get_rom_info prgRomSizeBytes", (info.get("prgRomSizeBytes") or 0) > 0, str(info.get("prgRomSizeBytes")))

        err, _, _ = c.call("pause")

        # ---- MCP-04 ----------------------------------------------------------------------------
        err, st = call_json(c, "step_instruction", count=3)
        pcs = st.get("pcs", [])
        check("step_instruction returns state", not err and st.get("stopReason") == "done"
              and st.get("instructionsExecuted") == 3 and "cpu" in st, str(st)[:300])
        # pcs lists the address *before* each instruction; with a final state the 4 addresses must differ
        all_pcs = pcs + ["%02X:%04X" % (st["cpu"]["k"], st["cpu"]["pc"])]
        check("step_instruction distinct PCs", len(set(all_pcs)) == 4, str(all_pcs))

        err, af = call_json(c, "advance_frame", frames=60)
        check("advance_frame done", not err and af.get("stopReason") == "done" and af.get("framesAdvanced") == 60, str(af)[:300])

        err, sl = call_json(c, "step_scanline", count=3)
        check("step_scanline synchronous", not err and sl.get("stopReason") == "done" and "cpu" in sl, str(sl)[:200])
        err, cy = call_json(c, "step_cpu_cycles", count=20)
        check("step_cpu_cycles synchronous", not err and cy.get("stopReason") == "done" and "cpu" in cy, str(cy)[:200])

        # NMI vector (native mode, bank 0 vector at $00FFEA)
        err, text, _ = c.call("read_word", address=0xFFEA)
        nmi = int(text.split()[0], 16)
        err, _, _ = c.call("set_breakpoints", breakpoints=[{"callbackType": "exec", "startAddress": nmi}])
        err, br = call_json(c, "advance_frame", frames=5)
        check("advance_frame reports breakpoint",
              br.get("stopReason") == "breakpoint" and br.get("framesAdvanced", 5) < 5 and br["cpu"]["pc"] == nmi,
              f"nmi={nmi:04X} -> {str(br)[:300]}")
        c.call("clear_breakpoints")
        err, af = call_json(c, "advance_frame", frames=2)
        check("advance_frame after clearing breakpoints", af.get("stopReason") == "done", str(af)[:200])

        # ---- MCP-01 ----------------------------------------------------------------------------
        err, af = call_json(c, "advance_frame", frames=300)
        err, summ = call_json(c, "get_cdl_data", format="summary")
        check("get_cdl_data summary works", not err and "counts" in summ, str(summ)[:300])
        err, stats = call_json(c, "get_cdl_statistics")
        check("cdl summary code bytes == get_cdl_statistics",
              summ.get("counts", {}).get("code") == stats.get("codeBytes"),
              f"{summ.get('counts', {}).get('code')} vs {stats.get('codeBytes')}")
        err, rng = call_json(c, "get_cdl_data", format="ranges")
        rows = rng.get("rows", [])
        check("get_cdl_data ranges non-empty", not err and len(rows) > 0, f"{len(rows)} rows")
        code_bytes = 0
        err, rng_code = call_json(c, "get_cdl_data", format="ranges", flagMask=1)
        for s, e, f in rng_code.get("rows", []):
            code_bytes += e - s + 1
        check("ranges code total == statistics", code_bytes == stats.get("codeBytes"), f"{code_bytes} vs {stats.get('codeBytes')}")
        err, b64 = call_json(c, "get_cdl_data", format="bytes_base64", start=0, length=1024)
        check("bytes_base64 works", not err and len(b64.get("data", "")) > 0)

        cdl_path = os.path.join(tmp, "a.cdl")
        err, sv = call_json(c, "save_cdl", path=cdl_path)
        check("save_cdl", not err and sv.get("saved") and os.path.exists(cdl_path), str(sv)[:200])
        before = sv["statistics"]["codeBytes"]
        err, rs = call_json(c, "reset_cdl")
        check("reset_cdl clears", rs["statistics"]["codeBytes"] == 0, str(rs)[:200])
        err, ld = call_json(c, "load_cdl", path=cdl_path)
        check("load_cdl restores totals", not err and ld["statistics"]["codeBytes"] == before,
              f"{ld['statistics']['codeBytes']} vs {before}")
        # merge: reset, advance a bit (small new coverage), merge the saved file
        c.call("reset_cdl")
        c.call("advance_frame", frames=5)
        err, ld = call_json(c, "load_cdl", path=cdl_path, merge=True)
        check("load_cdl merge is a union", not err and ld["statistics"]["codeBytes"] >= before,
              f"{ld['statistics']['codeBytes']} >= {before}")

        # ---- MCP-05 ----------------------------------------------------------------------------
        script = """
local frames = 0
local cnt = 0
emu.addEventCallback(function() frames = frames + 1 emu.log('frame ' .. frames) end, emu.eventType.endFrame)
emu.addEventCallback(function() emu.mcp.result({frames = frames, list = {1,2,3}, name = 'x"y', nested = {a = true}}) end, emu.eventType.endFrame)
"""
        err, rs = call_json(c, "run_script", name="t1", code=script, frames=5)
        res = rs.get("result") or {}
        check("run_script returns result", not err and res.get("frames") == 5 and res.get("list") == [1, 2, 3]
              and res.get("name") == 'x"y' and res.get("nested") == {"a": True}, str(rs)[:400])
        entries = [e for e in rs.get("log", {}).get("entries", []) if e["text"].startswith("frame")]
        check("run_script returns log rows", len(entries) == 5 and entries[0]["text"] == "frame 1", str(entries)[:300])
        check("run_script unloads", rs.get("unloaded") is True)
        err, ls = call_json(c, "list_scripts")
        check("list_scripts empty after run_script", ls.get("scripts") == [], str(ls))

        counter_script = """
local n = 0
emu.addEventCallback(function() n = n + 1 emu.mcp.result({n = n}) end, emu.eventType.endFrame)
"""
        err, rs1 = call_json(c, "run_script", name="counter", code=counter_script, frames=3)
        err, rs2 = call_json(c, "run_script", name="other", code="emu.log('hi')", frames=3)
        err, gr = call_json(c, "get_script_result", name="counter")
        check("no callback of the first script survives", (gr.get("result") or {}).get("n") == 3, str(gr))

        err, ld = call_json(c, "load_script", name="ticker", code="local n=0 emu.addEventCallback(function() n=n+1 emu.log('t'..n) end, emu.eventType.endFrame)")
        check("load_script returns scriptId", not err and isinstance(ld.get("scriptId"), int), str(ld))
        c.call("advance_frame", frames=40)
        err, lg = call_json(c, "get_script_log", name="ticker")
        ticks = [e for e in lg.get("entries", []) if e["text"].startswith("t") and e["text"][1:].isdigit()]
        n1 = len(ticks)
        check("get_script_log entries", n1 == 40 and ticks[-1]["text"] == "t40", f"{n1} rows; {str(lg)[:200]}")
        nxt = lg["nextSeq"]
        c.call("advance_frame", frames=10)
        err, lg2 = call_json(c, "get_script_log", name="ticker", sinceSeq=nxt)
        check("get_script_log sinceSeq cursor", len(lg2["entries"]) == 10 and lg2["entries"][0]["text"] == "t41", str(lg2)[:200])
        err, un = call_json(c, "unload_script", name="ticker")
        check("unload_script", not err and un.get("unloaded"))
        c.call("advance_frame", frames=5)
        err, lg3 = call_json(c, "get_script_log", name="ticker", sinceSeq=lg2["nextSeq"])
        check("unloaded script is silent", lg3["entries"] == [], str(lg3)[:200])

        # more than 500 rows between harvests must not be lost (advance is chunked)
        err, rs = call_json(c, "run_script", name="spam", code="""
local n = 0
emu.addEventCallback(function() for i = 1, 5 do n = n + 1 emu.log('r' .. n) end end, emu.eventType.endFrame)
""", frames=200, maxLog=2000)
        e = [x for x in rs.get("log", {}).get("entries", []) if x["text"].startswith("r")]
        check("1000 log rows over 200 frames kept", len(e) == 1000 and e[-1]["text"] == "r1000", f"{len(e)} rows")

        # ---- MCP-03 ----------------------------------------------------------------------------
        state_a = os.path.join(tmp, "sA.mss")
        err, sv = call_json(c, "save_state_file", path=state_a, note="test", meta={"scene": 1})
        check("save_state_file writes sidecar", not err and os.path.exists(state_a + ".json"), str(sv)[:200])
        side = json.load(open(state_a + ".json"))
        check("sidecar has sha1/meta", side["romSha1"] == sha_a and side["meta"] == {"scene": 1} and side["note"] == "test")
        err, ls = call_json(c, "load_state_file", path=state_a)
        check("load_state_file same ROM ok", not err and ls.get("loaded") and ls.get("romCheck") == "match", str(ls)[:200])

        legacy = os.path.join(tmp, "legacy.mss")
        c.call("save_state_file", path=legacy)
        os.remove(legacy + ".json")
        err, ls = call_json(c, "load_state_file", path=legacy)
        check("state without sidecar rejected", err and ls.get("error", {}).get("code") == "no_sidecar", str(ls)[:200])
        err, ls = call_json(c, "load_state_file", path=legacy, requireRomMatch=False)
        check("requireRomMatch=false loads it", not err and ls.get("loaded"), str(ls)[:200])

        err, res = call_json(c, "load_rom", path=args.rom_b)
        check("load ROM B", not err, str(res)[:200])
        err, ls = call_json(c, "load_state_file", path=state_a)
        check("rom_mismatch on other ROM", err and ls.get("error", {}).get("code") == "rom_mismatch"
              and ls["error"].get("stateRomSha1") == sha_a, str(ls)[:300])
        err, lst = call_json(c, "list_states", dir=tmp)
        flags = {os.path.basename(s["path"]): s["valid"] for s in lst.get("states", [])}
        check("list_states marks validity", flags.get("sA.mss") is False and flags.get("legacy.mss") is False, str(flags))
        err, res = call_json(c, "load_rom", path=args.rom_a, saveStatePath=state_a)
        check("load_rom with matching state", not err and res.get("state", {}).get("romCheck") == "match", str(res)[:200])
        err, lst = call_json(c, "list_states", dir=tmp)
        flags = {os.path.basename(s["path"]): s["valid"] for s in lst.get("states", [])}
        check("list_states valid for loaded ROM", flags.get("sA.mss") is True, str(flags))
        err, res = call_json(c, "load_rom", path=args.rom_b, saveStatePath=state_a)
        check("load_rom rejects mismatching state", err and res.get("error", {}).get("code") == "rom_mismatch", str(res)[:200])
    finally:
        rc = c.close()
        check("clean shutdown", rc in (0, None) or rc == 0, f"rc={rc}")
        if c.non_json_stdout:
            check("stdout carries only JSON", False, str(c.non_json_stdout[:3]))
        if c.stderr_lines:
            print("--- stderr ---")
            print("\n".join(c.stderr_lines[-20:]))

    failed = [n for n, ok in results if not ok]
    print(f"\n{len(results) - len(failed)}/{len(results)} checks passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
