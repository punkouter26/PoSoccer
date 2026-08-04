#!/usr/bin/env python3
"""
Strip stale MonoBehaviour blocks referencing deleted scripts (Agent_DebugGizmos,
Sensor_Vision) from Unity scene YAML files.

For each scene file:
  1. Find every MonoBehaviour (!u!114) whose m_Script GUID matches a deleted script.
  2. Drop that MonoBehaviour block.
  3. Drop the corresponding entry from any GameObject's m_Component array.
  4. Drop the corresponding entry from any Transform's m_Children list.
  5. Rewrite the scene file in place.
"""
import re
import sys
from pathlib import Path

# Deleted scripts (guid -> script name) - sourced from git HEAD's .meta files
DELETED = {
    "36af2364527786e40b95967763b5758d": "Agent_DebugGizmos",
    "597b9ff63360fbd4bb0d20c4ee98e284": "Sensor_Vision",
}

MB_HEADER_RE = re.compile(r"^--- !u!114 &(\d+)\s*$")
GUID_RE = re.compile(r"m_Script: \{fileID: \d+, guid: ([0-9a-f]+), type: \d+\}")

def strip_scene(path: Path):
    text = path.read_text(encoding="utf-8")
    lines = text.splitlines(keepends=True)

    # First pass: identify MonoBehaviour blocks to delete.
    # Each block starts with '--- !u!114 &<fileID>' and ends at the next '--- ' header.
    deleted_ids = set()
    deleted_id_to_name = {}
    block_start = None
    block_file_id = None
    i = 0
    while i < len(lines):
        m = MB_HEADER_RE.match(lines[i].rstrip("\r\n"))
        if m:
            block_file_id = m.group(1)
            block_start = i
            # scan forward until next '---' line
            j = i + 1
            found_guid = None
            while j < len(lines) and not lines[j].startswith("---"):
                g = GUID_RE.search(lines[j])
                if g:
                    found_guid = g.group(1)
                    break
                j += 1
            if found_guid in DELETED:
                deleted_ids.add(block_file_id)
                deleted_id_to_name[block_file_id] = DELETED[found_guid]
                # advance i to end of this block
                while i < len(lines) and not (i > j and lines[i].startswith("---")):
                    i += 1
                # back up one: the next iteration will re-check the '---' line
                continue
        i += 1

    if not deleted_ids:
        return 0

    # Second pass: delete the MonoBehaviour blocks entirely.
    new_lines = []
    i = 0
    while i < len(lines):
        m = MB_HEADER_RE.match(lines[i].rstrip("\r\n"))
        if m and m.group(1) in deleted_ids:
            # skip until next '--- ' line (exclusive)
            i += 1
            while i < len(lines) and not lines[i].startswith("---"):
                i += 1
            continue
        new_lines.append(lines[i])
        i += 1
    lines = new_lines

    # Third pass: remove deleted fileIDs from any m_Component array (single-line
    # '  - {fileID: <id>, guid: 0, type: 0}' entries).
    pat_component = re.compile(r"^(\s*)-\s+\{fileID: (\d+), guid: \d+, type: \d+\}\s*$")
    lines = [
        l for l in lines
        if not (pat_component.match(l) and pat_component.match(l).group(2) in deleted_ids)
    ]

    # Fourth pass: remove deleted fileIDs from any Transform.m_Children list
    # (entries are also '  - {fileID: <id>, guid: 0, type: 0}' inside indented blocks).
    lines = [
        l for l in lines
        if not (pat_component.match(l) and pat_component.match(l).group(2) in deleted_ids)
    ]

    new_text = "".join(lines)
    path.write_text(new_text, encoding="utf-8")
    return len(deleted_ids)

def main():
    scenes = [Path(p) for p in sys.argv[1:]]
    total = 0
    for s in scenes:
        n = strip_scene(s)
        total += n
        print(f"  {s}: stripped {n} stale MonoBehaviour block(s)")
    print(f"Total stripped: {total}")
    return 0

if __name__ == "__main__":
    sys.exit(main())