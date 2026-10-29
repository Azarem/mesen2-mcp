#!/usr/bin/env python3
"""Regenerates the tool reference of README.md from the live `tools/list` of the MCP host.

    python tools/gen_api_docs.py [--host DLL] [--readme README.md]

The reference sits between the markers
    <!-- MCP-API:BEGIN -->  ...  <!-- MCP-API:END -->
Run it after every change to a tool schema or description, then commit the README.
"""
import argparse
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_iog_e2e_test import Client  # noqa: E402

BEGIN = "<!-- MCP-API:BEGIN -->"
END = "<!-- MCP-API:END -->"

CATEGORIES = [
    ("Session, ROM and slots", [
        "load_rom", "get_rom_info", "stop", "reset", "pause", "resume", "get_timing_info", "revert_rom_changes",
        "list_slots", "switch_slot", "compare_runs"]),
    ("Execution and stepping", [
        "advance_frame", "step_instruction", "step_scanline", "step_cpu_cycles", "rewind", "run_until"]),
    ("Memory", [
        "read_memory", "write_memory", "read_word", "write_word", "list_memory_types", "dump_memory",
        "snapshot_memory", "diff_snapshots", "list_snapshots", "delete_snapshot",
        "get_prg_rom_offset", "get_chr_rom_offset", "get_access_summary", "reset_access_counters"]),
    ("Labels and addresses", [
        "load_labels", "list_labels", "resolve_address", "resolve_addresses", "get_label_address"]),
    ("Debugger", [
        "get_emulator_state", "set_cpu_state", "set_breakpoints", "clear_breakpoints", "get_breakpoint_hits",
        "get_callstack", "get_disassembly", "get_execution_trace", "start_trace", "stop_trace", "trace_between",
        "get_profiler_data"]),
    ("Code/data log (CDL)", [
        "get_cdl_data", "get_cdl_statistics", "reset_cdl", "save_cdl", "load_cdl"]),
    ("Video and PPU", [
        "take_screenshot", "get_ppu_state", "get_dma_state", "get_oam", "get_tilemap_info", "get_tilemap_size",
        "get_palette_info"]),
    ("Input", [
        "get_input", "set_input", "clear_input", "play_input", "record_input", "play_input_file"]),
    ("Save states", [
        "save_state_file", "load_state_file", "list_states"]),
    ("Lua scripting and recipes", [
        "load_script", "run_script", "get_script_log", "get_script_result", "list_scripts", "unload_script",
        "list_recipes", "run_recipe"]),
    ("Server and events", [
        "get_capabilities", "self_test", "subscribe_events"]),
]


def cell(text):
    return re.sub(r"\s+", " ", str(text)).replace("|", "\\|").strip()


def type_of(schema):
    t = schema.get("type", "any")
    if isinstance(t, list):
        t = " \\| ".join(t)
    if t == "array":
        item = schema.get("items", {}).get("type")
        t = f"array<{item}>" if item else "array"
    if "enum" in schema:
        t += " (" + ", ".join(str(e) for e in schema["enum"]) + ")"
    return t


def render_tool(tool):
    lines = [f"#### `{tool['name']}`", "", tool.get("description", "").strip(), ""]
    schema = tool.get("inputSchema", {})
    props = {k: v for k, v in schema.get("properties", {}).items() if k != "slot"}
    required = set(schema.get("required", []))
    if props:
        lines += ["| Parameter | Type | Required | Description |", "|---|---|---|---|"]
        for name, p in props.items():
            lines.append(f"| `{name}` | {type_of(p)} | {'yes' if name in required else ''} | {cell(p.get('description', ''))} |")
    else:
        lines.append("_No parameters._")
    lines.append("")
    return "\n".join(lines)


def build(tools):
    by_name = {t["name"]: t for t in tools}
    listed = [n for _, names in CATEGORIES for n in names]
    missing = sorted(set(by_name) - set(listed))
    unknown = sorted(set(listed) - set(by_name))
    if missing or unknown:
        raise SystemExit(f"Category table out of date. Uncategorised tools: {missing}; unknown names: {unknown}")
    if len(listed) != len(set(listed)):
        raise SystemExit("A tool is listed in two categories.")

    out = [f"_{len(tools)} tools, generated from the server's `tools/list` by `tools/gen_api_docs.py`. "
           "Every tool also accepts the optional `slot` argument (see Conventions)._", "", "| Category | Tools |", "|---|---|"]
    for title, names in CATEGORIES:
        out.append(f"| [{title}](#{re.sub(r'[^a-z0-9 -]', '', title.lower()).replace(' ', '-')}) | " +
                   ", ".join(f"[`{n}`](#{n})" for n in names) + " |")
    out.append("")
    for title, names in CATEGORIES:
        out += [f"### {title}", ""]
        for n in names:
            out.append(render_tool(by_name[n]))
    return "\n".join(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="McpHost/bin/Debug/net8.0/MesenMcpHost.dll")
    ap.add_argument("--readme", default="README.md")
    args = ap.parse_args()

    c = Client(os.path.abspath(args.host))
    try:
        c.request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "gen", "version": "1"}})
        tools = c.request("tools/list", {})["result"]["tools"]
    finally:
        c.close()

    text = open(args.readme, encoding="utf-8", newline="").read()
    crlf = "\r\n" in text
    text = text.replace("\r\n", "\n")
    if BEGIN not in text or END not in text:
        raise SystemExit(f"{args.readme}: markers {BEGIN} / {END} not found")
    head, rest = text.split(BEGIN, 1)
    _, tail = rest.split(END, 1)
    text = head + BEGIN + "\n" + build(tools) + "\n" + END + tail
    if crlf:
        text = text.replace("\n", "\r\n")
    open(args.readme, "w", encoding="utf-8", newline="").write(text)
    print(f"{args.readme}: {len(tools)} tools written")


if __name__ == "__main__":
    main()
