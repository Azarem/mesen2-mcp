#!/usr/bin/env python3
"""End-to-end regression test for the Mesen2 C# MCP host using Illusion of Gaia.

Verifies, against a real MesenMcpHost process over newline-delimited JSON-RPC:
  1. stdout carries ONLY JSON (native std::cout output must not leak into the protocol)
  2. a controller is attached (set_input / Lua emu.setInput reach the game)
  3. a Lua script can bootstrap the player into Edward's Castle (scene $0A)
  4. input overrides move the player after bootstrap
  5. unknown tool arguments are rejected instead of silently ignored
  6. shutdown is clean (no AccessViolation, exit code 0)

usage: mcp_iog_e2e_test.py [--rom PATH] [--host DLL] [--shot OUT.png]
"""
import argparse
import base64
import json
import os
import re
import subprocess
import sys
import tempfile
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_HOST = os.path.join(HERE, "..", "McpHost", "bin", "Release", "net8.0", "MesenMcpHost.dll")
DEFAULT_ROM = r"C:\Games\SNES\Illusion of Gaia.smc"
BOOTSTRAP = os.path.join(HERE, "lua", "iog_bootstrap.lua")

WRAM_SCENE_CUR = 0x0644
WRAM_PLAYER_X = 0x09A2
WRAM_PLAYER_Y = 0x09A4


class Client:
    def __init__(self, host_dll, extra_args=()):
        env = dict(os.environ, MESEN_MCP_DEBUG="0")
        self.proc = subprocess.Popen(
            ["dotnet", host_dll, *extra_args], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, env=env)
        self.stderr_lines = []
        threading.Thread(target=self._drain_stderr, daemon=True).start()
        self.next_id = 0
        self.non_json_stdout = []
        self.notifications = 0

    def _drain_stderr(self):
        for line in self.proc.stderr:
            self.stderr_lines.append(line.decode("utf-8", "replace").rstrip())

    def request(self, method, params=None, timeout=60):
        self.next_id += 1
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        if params is not None:
            msg["params"] = params
        self.proc.stdin.write((json.dumps(msg) + "\n").encode())
        self.proc.stdin.flush()
        deadline = time.time() + timeout
        while time.time() < deadline:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError("host closed stdout")
            text = line.decode("utf-8", "replace").strip()
            if not text:
                continue
            try:
                payload = json.loads(text)
            except json.JSONDecodeError:
                self.non_json_stdout.append(text)
                continue
            if payload.get("id") == self.next_id:
                return payload
            self.notifications += 1
        raise TimeoutError(f"timeout waiting for {method}")

    def call(self, tool, **arguments):
        resp = self.request("tools/call", {"name": tool, "arguments": arguments})
        result = resp["result"]
        text = "\n".join(c.get("text", "") for c in result["content"] if c["type"] == "text")
        return result.get("isError", False), text, result["content"]

    def close(self):
        try:
            self.proc.stdin.close()
        except Exception:
            pass
        try:
            return self.proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            return None


def read_bytes(c, addr, length):
    err, text, _ = c.call("read_memory", address=addr, length=length, type="workRam")
    assert not err, text
    line = [l for l in text.splitlines() if re.match(r"^[0-9A-F]{4}: ", l)][0]
    hexpart = line[6:6 + 3 * 16]
    return bytes(int(b, 16) for b in hexpart.split()[:length])


def wram16(c, addr):
    b = read_bytes(c, addr, 2)
    return b[0] | (b[1] << 8)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rom", default=DEFAULT_ROM)
    ap.add_argument("--host", default=DEFAULT_HOST)
    ap.add_argument("--shot", default=os.path.join(tempfile.gettempdir(), "iog_edward_castle.png"))
    args = ap.parse_args()

    results = []

    def check(name, ok, detail=""):
        results.append((name, ok))
        print(f"[{'PASS' if ok else 'FAIL'}] {name}" + (f" - {detail}" if detail else ""))

    c = Client(os.path.abspath(args.host))
    try:
        init = c.request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                        "clientInfo": {"name": "e2e", "version": "1"}})
        check("initialize", "result" in init)
        c.proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        c.proc.stdin.flush()
        tools = c.request("tools/list")["result"]["tools"]
        check("tools/list", len(tools) > 30, f"{len(tools)} tools")

        err, text, _ = c.call("load_rom", path=args.rom)
        check("load_rom", not err, text)

        err, text, _ = c.call("pause")
        check("pause", not err, text)

        err, text, _ = c.call("read_memory", address=0, length=4, memoryType="workRam")
        check("unknown argument rejected", err and "Unknown argument 'memoryType'" in text, text)

        with open(BOOTSTRAP, "r", encoding="utf-8") as f:
            code = f.read()
        err, text, _ = c.call("load_script", name="iog_bootstrap", code=code)
        check("load_script", not err, text)

        err, text, _ = c.call("advance_frame", frames=420)
        check("advance_frame(420)", not err, text)

        err, log, _ = c.call("get_script_log")
        print("   script log:\n   " + log.replace("\n", "\n   "))
        check("bootstrap reached READY", "[bootstrap] READY" in log)
        check("banner dismissed (controller input reaches game)", "WARNING" not in log and "READY" in log)

        scene = wram16(c, WRAM_SCENE_CUR)
        x0, y0 = wram16(c, WRAM_PLAYER_X), wram16(c, WRAM_PLAYER_Y)
        check("scene == Edward's Castle ($0A)", scene == 0x0A, f"scene=${scene:04X}")
        check("spawn position", (x0, y0) == (0x60, 0x370), f"pos=(${x0:04X},${y0:04X})")

        err, text, _ = c.call("set_input", port=0, input={"right": True})
        c.call("advance_frame", frames=30)
        c.call("clear_input")
        c.call("advance_frame", frames=5)
        x1 = wram16(c, WRAM_PLAYER_X)
        check("set_input moves player right", x1 > x0, f"x ${x0:04X} -> ${x1:04X}")

        err, _, content = c.call("take_screenshot")
        img = next((i for i in content if i["type"] == "image"), None)
        if img:
            os.makedirs(os.path.dirname(os.path.abspath(args.shot)), exist_ok=True)
            with open(args.shot, "wb") as f:
                f.write(base64.b64decode(img["data"]))
            print(f"   screenshot: {os.path.abspath(args.shot)}")
        check("take_screenshot", img is not None)

        check("stdout contained only JSON", not c.non_json_stdout,
              "; ".join(c.non_json_stdout[:3]))
    finally:
        code_ = c.close()
        time.sleep(0.3)

    crashed = any("Fatal error" in l or "AccessViolation" in l for l in c.stderr_lines)
    check("clean shutdown (exit code 0, no crash)", code_ == 0 and not crashed,
          f"exit={code_}" + (" | " + " / ".join(c.stderr_lines[:3]) if crashed else ""))

    failed = [n for n, ok in results if not ok]
    print(f"\n{len(results) - len(failed)}/{len(results)} checks passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
