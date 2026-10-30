#!/usr/bin/env python3
import json
import os
import subprocess
import sys
import threading
import time


def _read_headers(stream):
    headers = {}
    while True:
        line = stream.readline()
        if not line:
            return None
        if line in (b"\r\n", b"\n"):
            return headers
        key, _, value = line.decode("ascii", errors="replace").partition(":")
        headers[key.strip().lower()] = value.strip()


def read_message(stream):
    headers = _read_headers(stream)
    if headers is None:
        return None
    length = int(headers.get("content-length", "0"))
    if length <= 0:
        return None
    payload = stream.read(length)
    if not payload:
        return None
    return json.loads(payload.decode("utf-8"))


def send_message(stream, payload):
    body = json.dumps(payload, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    header = f"Content-Length: {len(body)}\r\n\r\n".encode("ascii")
    stream.write(header)
    stream.write(body)
    stream.flush()


def assert_result(response, expected_id):
    if response is None:
        raise RuntimeError("No response received from MCP server.")
    if response.get("id") != expected_id:
        raise RuntimeError(f"Unexpected response id. expected={expected_id}, got={response.get('id')}")
    if "error" in response:
        raise RuntimeError(f"MCP error: {response['error']}")
    if "result" not in response:
        raise RuntimeError(f"Missing result in response: {response}")
    return response["result"]


def read_response_for_id(stream, expected_id, timeout_s=10):
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        response = read_message(stream)
        if response is None:
            continue
        if "id" not in response:
            # Notification; ignore and keep waiting.
            continue
        return assert_result(response, expected_id)
    raise RuntimeError(f"Timed out waiting for response id={expected_id}")


def main():
    if len(sys.argv) < 2:
        print("usage: mcp_smoke_test.py <command> [args...]", file=sys.stderr)
        return 2

    command = sys.argv[1:]
    if "--mcp-server" not in command:
        command = command + ["--mcp-server"]

    proc = subprocess.Popen(
        command,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )

    stderr_lines = []

    def _read_stderr():
        assert proc.stderr is not None
        for line in proc.stderr:
            stderr_lines.append(line.decode("utf-8", errors="replace"))

    stderr_thread = threading.Thread(target=_read_stderr, daemon=True)
    stderr_thread.start()

    try:
        assert proc.stdin is not None
        assert proc.stdout is not None

        send_message(
            proc.stdin,
            {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "initialize",
                "params": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {},
                    "clientInfo": {"name": "ci-smoke", "version": "1.0"},
                },
            },
        )
        init_result = read_response_for_id(proc.stdout, 1)
        if "capabilities" not in init_result:
            raise RuntimeError("initialize response is missing capabilities")

        # The host version comes from the VERSION file at the repo root; a release must report it.
        version_file = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "VERSION")
        if os.path.isfile(version_file):
            with open(version_file, encoding="utf-8") as f:
                expected_version = f.read().strip()
            reported_version = (init_result.get("serverInfo") or {}).get("version")
            if reported_version != expected_version:
                raise RuntimeError(
                    f"serverInfo.version is {reported_version!r}, expected {expected_version!r} (VERSION file)")

        send_message(
            proc.stdin,
            {
                "jsonrpc": "2.0",
                "method": "notifications/initialized",
                "params": {},
            },
        )

        send_message(
            proc.stdin,
            {
                "jsonrpc": "2.0",
                "id": 2,
                "method": "tools/list",
                "params": {},
            },
        )
        tools_result = read_response_for_id(proc.stdout, 2)
        tools = tools_result.get("tools", [])
        names = {tool.get("name") for tool in tools}
        required = {
            "load_rom", "get_rom_info", "advance_frame", "read_memory", "write_memory",
            "set_breakpoints", "get_disassembly", "take_screenshot", "run_script",
            "get_capabilities", "self_test", "stop",
        }
        missing = sorted(required - names)
        if missing:
            raise RuntimeError(f"tools/list missing required tools: {missing}")

        send_message(
            proc.stdin,
            {
                "jsonrpc": "2.0",
                "id": 3,
                "method": "tools/call",
                "params": {"name": "stop", "arguments": {"exitCode": 0}},
            },
        )
        _ = read_response_for_id(proc.stdout, 3)

        proc.wait(timeout=15)
        if proc.returncode not in (0, None):
            raise RuntimeError(f"MCP server exited with code {proc.returncode}")

        print("MCP smoke test passed.")
        return 0
    except Exception as exc:
        try:
            proc.kill()
        except Exception:
            pass
        print(f"MCP smoke test failed: {exc}", file=sys.stderr)
        if stderr_lines:
            print("--- mesen stderr ---", file=sys.stderr)
            print("".join(stderr_lines), file=sys.stderr)
        return 1
    finally:
        try:
            if proc.poll() is None:
                proc.kill()
        except Exception:
            pass
        stderr_thread.join(timeout=1)
        time.sleep(0.05)


if __name__ == "__main__":
    raise SystemExit(main())
