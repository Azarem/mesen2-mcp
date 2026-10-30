#!/usr/bin/env python3
"""Checks that a published MCP host folder is a complete, standalone release.

Usage:
    python tools/verify_release.py <dist-folder> <rid> [--deps-zip <Dependencies.zip>]

<rid> is the .NET runtime identifier the folder was published for (win-x64, linux-x64,
linux-arm64, osx-x64, osx-arm64). The check fails when a file the host needs at run time is
missing: the managed host, the Mesen assembly, the native emulation core, the Skia/HarfBuzz
native libraries and the Lua kit. With --deps-zip it also checks that the Dependencies.zip
embedded into Mesen.dll holds the native libraries (the host unpacks them on first start).
"""
import argparse
import json
import os
import sys
import zipfile


def native_names(rid):
    if rid.startswith("win-"):
        ext = ".dll"
    elif rid.startswith("osx-"):
        ext = ".dylib"
    elif rid.startswith("linux-"):
        ext = ".so"
    else:
        raise SystemExit(f"unknown runtime identifier: {rid}")
    return [f"MesenCore{ext}", f"libSkiaSharp{ext}", f"libHarfBuzzSharp{ext}"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dist")
    ap.add_argument("rid")
    ap.add_argument("--deps-zip")
    args = ap.parse_args()

    natives = native_names(args.rid)
    required = [
        "MesenMcpHost.dll",
        "MesenMcpHost.deps.json",
        "MesenMcpHost.runtimeconfig.json",
        "Mesen.dll",
        os.path.join("lua", "lib", "mesen_kit.lua"),
        os.path.join("lua", "lib", "iog_kit.lua"),
        os.path.join("lua", "lib", "recipes", "warp.lua"),
        os.path.join("lua", "lib", "recipes", "census_pc.lua"),
        os.path.join("lua", "lib", "recipes", "exec_coverage.lua"),
    ] + natives

    problems = []
    for rel in required:
        path = os.path.join(args.dist, rel)
        if not os.path.isfile(path) or os.path.getsize(path) == 0:
            problems.append(f"missing or empty: {rel}")

    runtimeconfig = os.path.join(args.dist, "MesenMcpHost.runtimeconfig.json")
    if os.path.isfile(runtimeconfig):
        with open(runtimeconfig, encoding="utf-8") as f:
            options = json.load(f).get("runtimeOptions", {})
        framework = options.get("framework", {})
        if framework.get("name") != "Microsoft.NETCore.App" or not str(framework.get("version", "")).startswith("8."):
            problems.append(f"runtimeconfig does not target Microsoft.NETCore.App 8.x: {framework}")
        if "includedFrameworks" in options:
            problems.append("runtimeconfig is self-contained; the release must be framework-dependent")

    if args.deps_zip:
        if not os.path.isfile(args.deps_zip):
            problems.append(f"missing: {args.deps_zip}")
        else:
            with zipfile.ZipFile(args.deps_zip) as z:
                names = {n.replace("\\", "/") for n in z.namelist()}
                for native in natives:
                    if native not in names:
                        problems.append(f"{os.path.basename(args.deps_zip)} does not contain {native}")

    if problems:
        print("Release verification failed:", file=sys.stderr)
        for p in problems:
            print(f"  - {p}", file=sys.stderr)
        return 1

    print(f"Release folder OK ({args.rid}): {len(required)} required files present.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
