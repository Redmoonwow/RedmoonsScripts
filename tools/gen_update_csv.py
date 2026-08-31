#!/usr/bin/env python3
"""Generate Splatoon script update.csv for this repository.

A Python port of PunishXIV/Splatoon's ScriptUpdateFileGenerator (Program.cs).
Scans a scripts directory for *.cs files, extracts (namespace, class, version)
with the same regexes the official generator uses, and writes one line per
script:

    {namespace}@{Class},{version},{raw github url}

Usage: gen_update_csv.py <scriptsDir> <outputPath>
"""
import os
import re
import sys
from urllib.parse import quote

RAW_BASE = "https://github.com/Redmoonwow/RedmoonsScripts/raw/main"

NAMESPACE_RE = re.compile(r"namespace[\s]+([a-z0-9_\.]+)", re.IGNORECASE)
CLASS_RE = re.compile(r"([a-z0-9_\.]+)\s*:\s*SplatoonScript", re.IGNORECASE)
VERSION_RE = re.compile(r"override.+Metadata.+Metadata.+new\D+([0-9]+)")

# Never scan build output - it contains generated .cs files.
SKIP_DIRS = {"bin", "obj", ".git", ".vs"}


def extract(code):
    ns = NAMESPACE_RE.search(code)
    cls = CLASS_RE.search(code)
    ver = VERSION_RE.search(code)
    if not (ns and cls and ver):
        return None
    return ns.group(1), cls.group(1), int(ver.group(1))


def main():
    if len(sys.argv) != 3:
        print("Input and output destinations must be defined")
        return 0

    scripts_dir, output_path = sys.argv[1], sys.argv[2]
    repo_root = os.path.dirname(os.path.abspath(scripts_dir)) or "."
    lines = []

    for root, dirs, files in os.walk(scripts_dir):
        dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS)
        for fname in sorted(files):
            if not fname.endswith(".cs"):
                continue
            path = os.path.join(root, fname)
            try:
                with open(path, encoding="utf-8-sig") as f:
                    code = f.read()
            except OSError as e:
                print(e)
                continue
            parsed = extract(code)
            rel = os.path.relpath(path, repo_root).replace(os.sep, "/")
            print(f"Processing file {path} ({rel})")
            if parsed is None:
                print("  skipped: namespace/class/version not found")
                continue
            ns, cls, ver = parsed
            print(f"  Namespace: {ns}, Class: {cls}, Version: {ver}")
            url = f"{RAW_BASE}/{quote(rel)}"
            line = f"{ns}@{cls},{ver},{url}"
            print(f"  {line}")
            lines.append(line)

    with open(output_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))
    print(f"Wrote {len(lines)} entries to {output_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
