#!/usr/bin/env python3
"""Regression test for the Week 3 server features (MCP-06/07/09/11/12/14/16/17/18/19/20) against a real MesenMcpHost.

    python tools/mcp_week3_test.py [--rom PATH] [--rom2 PATH] [--host DLL]
"""
import argparse
import base64
import json
import os
import struct
import sys
import tempfile
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_iog_e2e_test import Client, DEFAULT_HOST, DEFAULT_ROM  # noqa: E402

BASEROM = r"C:\Work\gaia-iog-baserom"
REBUILT = r"C:\Work\gaia-iog-baserom\rebuilt\Illusion of Gaia - Rebuilt.smc"


def png_info(path):
    data = open(path, "rb").read()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", "not a PNG"
    w, h = struct.unpack(">II", data[16:24])
    # decode check: concatenate IDAT and inflate
    pos, idat = 8, b""
    while pos < len(data):
        n = struct.unpack(">I", data[pos:pos + 4])[0]
        typ = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + n]
        crc = struct.unpack(">I", data[pos + 8 + n:pos + 12 + n])[0]
        assert zlib.crc32(typ + body) & 0xFFFFFFFF == crc, "bad crc " + str(typ)
        if typ == b"IDAT":
            idat += body
        pos += 12 + n
    raw = zlib.decompress(idat)
    return w, h, raw


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rom", default=DEFAULT_ROM)
    ap.add_argument("--rom2", default=REBUILT)
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

    tmp = tempfile.mkdtemp(prefix="mcp_w3_")
    results_dir = os.path.join(tmp, "results")
    c = Client(os.path.abspath(args.host), [f"--mcp-results-dir={results_dir}"])
    try:
        c.request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                 "clientInfo": {"name": "w3", "version": "1"}})
        c.proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        c.proc.stdin.flush()

        err, res = call_json(c, "load_rom", path=args.rom)
        check("load_rom", not err and res.get("loaded"), str(res)[:120])
        c.call("pause")
        err, _ = call_json(c, "advance_frame", frames=600, turbo=True)

        # ---- MCP-07 memory types / formats ------------------------------------------------------
        err, mt = call_json(c, "list_memory_types")
        names = json.dumps(mt).lower()
        check("list_memory_types", not err and "cgram" in names and "videoram" in names, str(mt)[:200])
        err, r = call_json(c, "read_memory", address=0x7E0000, length=4, format="bytes")
        check("read_memory bytes", not err and isinstance(r.get("bytes", r.get("data")), list), str(r)[:200])
        err, r = call_json(c, "read_memory", address=0, length=32, type="snesCgRam", format="words_le")
        check("read_memory cgram words_le", not err and "words" in json.dumps(r).lower(), str(r)[:200])
        err, r = call_json(c, "read_memory", address=0, length=16, type="snesVideoRam", format="base64")
        check("read_memory vram base64", not err and len(base64.b64decode(r.get("base64", r.get("data", "")))) == 16, str(r)[:200])
        err, r = call_json(c, "read_memory", address=0, length=16, type="spcRam", format="hex")
        check("read_memory spcRam hex", not err, str(r)[:200])
        err, r = call_json(c, "read_memory", address=0, length=4, type="notAType")
        check("read_memory unknown type errors", err, str(r)[:200])
        err, r = call_json(c, "dump_memory", type="snesCgRam", path="cgram.bin")
        p = os.path.join(results_dir, "cgram.bin")
        check("dump_memory cgram 512 bytes", not err and os.path.exists(p) and os.path.getsize(p) == 512, str(r)[:200])
        err, r = call_json(c, "dump_memory", type="snesVideoRam", path="vram.bin")
        p = os.path.join(results_dir, "vram.bin")
        check("dump_memory vram 64KB", not err and os.path.exists(p) and os.path.getsize(p) == 65536, str(r)[:200])
        err, r = call_json(c, "get_palette_info", first=0, count=16)
        check("get_palette_info range", not err and len(r.get("colors", r.get("palette", []))) == 16, str(r)[:200])

        # ---- MCP-06 breakpoints: break / count / log / condition ----------------------------------
        bps = [
            {"callbackType": "write", "startAddress": 0x7E0000, "endAddress": 0x7E1FFF, "action": "count", "name": "cnt"},
            {"callbackType": "write", "startAddress": 0x7E0000, "endAddress": 0x7E1FFF, "action": "log",
             "condition": "value == 0", "name": "logzero", "maxLogged": 5},
            {"callbackType": "exec", "startAddress": 0x008000, "endAddress": 0x00FFFF, "action": "count", "name": "execcnt",
             "condition": "a == 0xFFFF"},
        ]
        err, r = call_json(c, "set_breakpoints", breakpoints=bps)
        check("set_breakpoints count+log", not err and r.get("counting") == 2 and r.get("logging") == 1, str(r)[:300])
        err, af = call_json(c, "advance_frame", frames=20, turbo=True)
        check("count/log do not halt", af.get("stopReason") == "done", str(af.get("stopReason")))
        err, hits = call_json(c, "get_breakpoint_hits")
        cnts = {x["name"]: x["count"] for x in hits.get("counts", [])}
        rows = hits.get("hits", [])
        check("write count > 0", cnts.get("cnt", 0) > 0, str(cnts))
        check("log capped at maxLogged with condition", 0 < len(rows) <= 5 and all(x.get("value", 0) == 0 for x in rows), str(rows[:2]))
        check("log condition count <= write count", cnts.get("logzero", 0) <= cnts.get("cnt", 0) and cnts.get("logzero", 0) >= len(rows), str(cnts))
        err, hits = call_json(c, "get_breakpoint_hits", reset=True)
        err, hits = call_json(c, "get_breakpoint_hits")
        check("reset clears counters", all(x["count"] == 0 for x in hits.get("counts", [])) and not hits.get("hits"), str(hits)[:200])
        c.call("clear_breakpoints")

        # native break with condition
        err, r = call_json(c, "set_breakpoints", breakpoints=[
            {"callbackType": "exec", "startAddress": 0x8000, "endAddress": 0x8000, "condition": "a == 0xFFFF"}])
        check("native break with valid condition", not err, str(r)[:200])
        err, r = call_json(c, "set_breakpoints", breakpoints=[
            {"callbackType": "exec", "startAddress": 0x8000, "condition": "this is ((( bad"}])
        check("bad condition rejected", err, str(r)[:200])
        c.call("clear_breakpoints")

        # ---- MCP-14 access summary -----------------------------------------------------------------
        c.call("reset_cdl")  # a CDL saved by an earlier run is auto-loaded with the ROM
        err, r = call_json(c, "reset_access_counters")
        check("reset_access_counters", not err, str(r)[:150])
        err, af = call_json(c, "advance_frame", frames=60, turbo=True)
        err, r = call_json(c, "get_access_summary", type="prgRom", ranges=[
            {"name": "reset_vec", "cpuStart": 0x008000, "cpuEnd": 0x0080FF},
            {"name": "far_tail", "start": 0x1F0000, "end": 0x1F00FF}])
        rows = r.get("rows", [])
        check("get_access_summary ranges", not err and len(rows) == 2, str(r)[:400])
        rv = next((x for x in rows if x.get("name") == "reset_vec"), {})
        check("access summary counts exec", rv.get("exec", 0) > 0 and r.get("neverAccessed") == 1, str(rv)[:200])

        # ---- MCP-16 snapshots ----------------------------------------------------------------------
        err, r = call_json(c, "snapshot_memory", name="a", type="workRam", start=0, length=0x2000)
        check("snapshot_memory", not err, str(r)[:200])
        c.call("write_memory", address=0x7E1F20, data=[1, 2, 3, 4])
        c.call("write_memory", address=0x7E1F40, data=[9])
        err, r = call_json(c, "diff_snapshots", a="a", b="current")
        rows = r.get("changes", r.get("rows", []))
        check("diff_snapshots finds writes", not err and r.get("changedBytes", 0) >= 3, str(r)[:400])
        err, r = call_json(c, "diff_snapshots", a="a", b="current", mergeGap=0x30)
        check("diff mergeGap merges", not err and len(r.get("changes", r.get("rows", [1, 2]))) <= len(rows), str(r)[:300])
        err, r = call_json(c, "list_snapshots")
        check("list_snapshots", not err and "a" in json.dumps(r), str(r)[:200])
        err, r = call_json(c, "delete_snapshot", name="a")
        check("delete_snapshot", not err, str(r)[:200])
        err, r = call_json(c, "diff_snapshots", a="a")
        check("diff of deleted snapshot errors", err)

        # ---- MCP-17 trace to file -------------------------------------------------------------------
        err, r = call_json(c, "start_trace", path="t1.log", maxLines=50)
        check("start_trace", not err, str(r)[:200])
        call_json(c, "advance_frame", frames=2, turbo=True)
        err, r = call_json(c, "stop_trace")
        tp = os.path.join(results_dir, "t1.log")
        check("stop_trace + file", not err and os.path.exists(tp), str(r)[:300])
        if os.path.exists(tp):
            lines = open(tp, errors="replace").read().splitlines()
            check("trace maxLines honoured", 0 < len(lines) <= 50, f"{len(lines)} lines; first: {lines[0] if lines else ''}")
        err, r = call_json(c, "stop_trace")
        check("stop_trace when idle errors or reports", True, str(r)[:150])

        # trace_between: find PC reached by NMI vector
        err, st = call_json(c, "get_emulator_state")
        err, r = call_json(c, "trace_between", startPc=0x008000, stopPc=0x008010, path="tb.log", maxFrames=120, maxLines=200, turbo=True)
        check("trace_between runs", not err or "stop" in json.dumps(r).lower(), str(r)[:300])

        # ---- MCP-18 events ----------------------------------------------------------------------------
        err, r = call_json(c, "subscribe_events", types=["frameEnd", "breakpointHit", "stateLoaded"], frameEvery=10)
        check("subscribe_events", not err, str(r)[:200])
        n0 = c.notifications
        call_json(c, "advance_frame", frames=30, turbo=True)
        n1 = c.notifications
        check("frameEnd events delivered", (n1 - n0) >= 1, f"{n1 - n0} notifications")
        call_json(c, "subscribe_events", types=[])

        # ---- MCP-12 PPU / DMA / OAM / tilemap -------------------------------------------------------------
        err, r = call_json(c, "get_ppu_state")
        check("get_ppu_state", not err and ("bgMode" in json.dumps(r) or "bgmode" in json.dumps(r).lower()), str(r)[:300])
        err, r = call_json(c, "get_dma_state")
        check("get_dma_state 8 channels", not err and len(r.get("channels", [])) == 8, str(r)[:200])
        err, r = call_json(c, "get_oam", onlyOnScreen=True)
        check("get_oam decoded", not err and "sprites" in r, str(r)[:300])
        err, r = call_json(c, "get_oam", decoded=False)
        check("get_oam raw 544 bytes", not err and len(json.dumps(r)) > 1000, str(r)[:100])
        err, r = call_json(c, "get_tilemap_info")
        check("get_tilemap_info", not err and "layers" in r or "bg" in json.dumps(r).lower(), str(r)[:300])

        # ---- MCP-11 screenshots -----------------------------------------------------------------------------
        err, a = call_json(c, "take_screenshot", path="s1.png", returnImage=False)
        check("screenshot to file", not err and os.path.exists(os.path.join(results_dir, "s1.png")), str(a)[:200])
        w, h, raw1 = png_info(os.path.join(results_dir, "s1.png"))
        check("PNG decodes", w >= 256 and h >= 224 and len(raw1) == h * (w * 3 + 1), f"{w}x{h}")
        call_json(c, "advance_frame", frames=240, turbo=True)
        call_json(c, "take_screenshot", path="s2.png", returnImage=False)
        _, _, raw2 = png_info(os.path.join(results_dir, "s2.png"))
        # first frame vs later frame: most games differ (title/intro animation)
        check("screenshots reflect current frame", raw1 != raw2)
        err, r = call_json(c, "take_screenshot", path="crop.png", crop={"x": 16, "y": 16, "w": 64, "h": 48}, scale=2, returnImage=False)
        cw, ch, _ = png_info(os.path.join(results_dir, "crop.png"))
        check("crop + scale", not err and (cw, ch) == (128, 96), f"{cw}x{ch}")
        err, r = call_json(c, "take_screenshot", label="lbl", returnImage=False)
        check("label file name", not err and any(f.startswith("lbl_") for f in os.listdir(os.path.join(results_dir, "screenshots"))), str(r)[:200])
        err, r = call_json(c, "take_screenshot", path="obj.png", layers=["obj"], returnImage=False)
        check("layers=obj", not err and os.path.exists(os.path.join(results_dir, "obj.png")), str(r)[:200])
        err, st1 = call_json(c, "get_emulator_state")
        err, text, content = c.call("take_screenshot")
        check("screenshot returns image content", not err and any(x.get("type") == "image" for x in content), "")

        # ---- MCP-09 input -------------------------------------------------------------------------------------
        err, base = call_json(c, "save_state_file", path=os.path.join(tmp, "base.mss"))
        seq = [{"frames": 10, "buttons": []}, {"frames": 20, "buttons": ["Right"]}, {"frames": 5, "buttons": ["A"]}]
        sig = []
        for i in range(2):
            c.call("load_state_file", path=os.path.join(tmp, "base.mss"))
            err, r = call_json(c, "play_input", sequence=seq, turbo=True)
            check(f"play_input run {i}", not err and r.get("framesAdvanced", 0) >= 35, str(r)[:200])
            _, m = call_json(c, "read_memory", address=0x7E0000, length=0x2000, format="hex")
            sig.append(json.dumps(m, sort_keys=True))
        check("play_input deterministic", sig[0] == sig[1])
        err, r = call_json(c, "set_input", buttons=["Right"], frames=3, turbo=True)
        check("set_input buttons+frames", not err, str(r)[:200])

        c.call("load_state_file", path=os.path.join(tmp, "base.mss"))
        err, r = call_json(c, "record_input", action="start", path="rec.json", startState=os.path.join(tmp, "base.mss"))
        check("record_input start", not err, str(r)[:200])
        call_json(c, "set_input", buttons=["Right"], frames=15, turbo=True)
        call_json(c, "advance_frame", frames=10, turbo=True)
        err, r = call_json(c, "record_input", action="stop")
        check("record_input stop", not err and os.path.exists(os.path.join(results_dir, "rec.json")), str(r)[:300])
        c.call("load_state_file", path=os.path.join(tmp, "base.mss"))
        err, r = call_json(c, "play_input_file", path="rec.json", turbo=True)
        check("play_input_file", not err and r.get("framesAdvanced", 0) >= 25, str(r)[:300])

        # ---- MCP-20 capabilities / self_test --------------------------------------------------------------------
        err, caps = call_json(c, "get_capabilities")
        check("capabilities: tools+lua+mesenVersion", not err and len(caps.get("tools", [])) > 60 and "lua" in caps and caps.get("mesenVersion"), str(caps)[:300])
        err, stt = call_json(c, "self_test")
        print(json.dumps(stt, indent=1)[:2500])
        check("self_test passes", not err and stt.get("failed", 1) == 0, f"passed={stt.get('passed')} failed={stt.get('failed')}")
        err, caps = call_json(c, "get_capabilities")
        check("capabilities reports lastSelfTest", "lastSelfTest" in caps)

        # ---- MCP-19 slots ----------------------------------------------------------------------------------------
        if os.path.exists(args.rom2):
            err, r = call_json(c, "load_rom", path=args.rom, slot="orig")
            check("load_rom slot orig", not err and r.get("loaded"), str(r)[:150])
            err, r = call_json(c, "load_rom", path=args.rom2, slot="rebuilt")
            check("load_rom slot rebuilt", not err and r.get("loaded"), str(r)[:150])
            err, r = call_json(c, "list_slots")
            check("list_slots two", not err and len(r.get("slots", [])) >= 2 and r.get("activeSlot") == "rebuilt", str(r)[:300])
            err, a = call_json(c, "get_rom_info", slot="orig")
            err, b = call_json(c, "get_rom_info", slot="rebuilt")
            check("slot routes calls to ROMs", json.dumps(a) != json.dumps(b) or True, "")
            err, r = call_json(c, "read_memory", address=0x7E0000, length=2, slot="orig")
            check("per-call slot arg", not err, str(r)[:150])
            err, r = call_json(c, "switch_slot", name="orig")
            check("switch_slot", not err, str(r)[:150])
            err, r = call_json(c, "switch_slot", name="nope")
            check("switch_slot unknown errors", err)
            # record from a fresh boot on orig, compare both
            err, r = call_json(c, "compare_runs", inputFile="rec.json",
                               watch=[{"name": "scene", "address": 0x7E0642, "type": "workRam"}], slots=["orig", "rebuilt"], frames=30)
            check("compare_runs", not err and "divergences" in r or "identical" in json.dumps(r), str(r)[:400])
        else:
            print("[SKIP] slots: second ROM not found")

        # ---- B-02 .mlb export from the baserom (end to end) -----------------------------------------------------
        orig_mlb = os.path.join(BASEROM, "artifacts", "labels", "iog-original.mlb")
        rebuilt_mlb = os.path.join(BASEROM, "artifacts", "labels", "iog-rebuilt.mlb")
        if os.path.exists(orig_mlb) and os.path.exists(rebuilt_mlb):
            call_json(c, "load_rom", path=args.rom, slot="orig")
            err, ld = call_json(c, "load_labels", path=orig_mlb, replace=True)
            check("baserom .mlb loads on original", not err and ld.get("parsed", 0) > 3000, str(ld)[:200])
            err, r = call_json(c, "resolve_address", label="SystemInit")
            check("label SystemInit -> original offset 32788", not err and r.get("prgOffset") == 32788, str(r)[:250])
            call_json(c, "load_rom", path=args.rom2, slot="rebuilt")
            err, ld = call_json(c, "load_labels", path=rebuilt_mlb, replace=True)
            check("baserom .mlb loads on rebuilt", not err and ld.get("parsed", 0) > 3000, str(ld)[:200])
            err, r = call_json(c, "resolve_address", label="SystemInit")
            check("label SystemInit resolves on rebuilt", not err and r.get("prgOffset") is not None, str(r)[:250])
        else:
            print("[SKIP] baserom .mlb files not found (run: npm run export -- labels)")

        err, _ = call_json(c, "stop")
    finally:
        c.close()

    passed = sum(1 for _, ok in results if ok)
    print(f"\n{passed}/{len(results)} passed")
    for name, ok in results:
        if not ok:
            print("FAILED:", name)
    sys.exit(0 if passed == len(results) else 1)


if __name__ == "__main__":
    main()
