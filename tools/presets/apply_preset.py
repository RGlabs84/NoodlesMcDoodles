#!/usr/bin/env python3
"""Switch a NoodlesMcDoodles config between test stages.

  apply_preset.py <path/to/wubarrk.noodlesmcdoodles.cfg> <preset> [--dry-run]
  apply_preset.py <path/to/wubarrk.noodlesmcdoodles.cfg> --show
  apply_preset.py --list

Presets live in presets.json next to this script. The file is rewritten in place (same inode), so the mod's config
watcher picks the change up on a running server; a rename-replace such as `sed -i` would not be seen. Only the values
of listed keys change; comments, ordering and every other key are kept.
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
KEY_LINE = re.compile(r"^([A-Za-z0-9_.]+) = (.*)$")


def load_presets():
    with open(os.path.join(HERE, "presets.json")) as f:
        data = json.load(f)
    flat = {}
    for group in ("server", "client"):
        for name, preset in data[group].items():
            flat[name] = dict(preset, group=group)
    return flat


def resolve(presets, name, seen=()):
    if name not in presets:
        sys.exit(f"unknown preset '{name}' (try --list)")
    if name in seen:
        sys.exit(f"preset inheritance loop at '{name}'")
    preset = presets[name]
    values = {}
    if "inherits" in preset:
        values.update(resolve(presets, preset["inherits"], seen + (name,)))
    values.update(preset["set"])
    return values


def read_values(lines):
    section, found = None, {}
    for i, line in enumerate(lines):
        stripped = line.strip()
        if stripped.startswith("[") and stripped.endswith("]"):
            section = stripped[1:-1]
            continue
        m = KEY_LINE.match(stripped)
        if m and section is not None:
            found[f"{section}/{m.group(1)}"] = (i, m.group(2))
    return found


def main(argv):
    presets = load_presets()
    if len(argv) == 2 and argv[1] == "--list":
        for name, p in presets.items():
            print(f"{p['group']:6}  {name:15} {p['about']}")
        return
    if len(argv) < 3:
        sys.exit(__doc__)
    path, what = argv[1], argv[2]
    with open(path) as f:
        lines = f.read().split("\n")
    current = read_values(lines)

    if what == "--show":
        for key in sorted(k for k in current if k.endswith(".Active") or k.endswith("/Active") or k in (
                "Core/Enabled", "Transport/Q10_Enabled")):
            print(f"{key:48} {current[key][1]}")
        return

    wanted = resolve(presets, what)
    missing = [k for k in wanted if k not in current]
    if missing:
        sys.exit("keys not in this config (start the server/game once with the current DLL so it writes them): "
                 + ", ".join(missing))
    changes = []
    for key, value in wanted.items():
        i, old = current[key]
        if old != value:
            name = key.split("/", 1)[1]
            indent = lines[i][: len(lines[i]) - len(lines[i].lstrip())]
            lines[i] = f"{indent}{name} = {value}"
            changes.append((key, old, value))
    for key, old, value in changes:
        print(f"{key:48} {old} -> {value}")
    if not changes:
        print(f"already at preset '{what}'")
        return
    if "--dry-run" in argv:
        print("(dry run, nothing written)")
        return
    with open(path, "r+") as f:  # in place: keeps the inode so FileSystemWatcher sees a change
        f.seek(0)
        f.write("\n".join(lines))
        f.truncate()
    print(f"applied preset '{what}' to {path}")


if __name__ == "__main__":
    main(sys.argv)
