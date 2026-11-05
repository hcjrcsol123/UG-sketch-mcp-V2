"""Compare two quiescent, independent F22 observation-directory snapshots.

Reads file bytes directly; does not follow reparse points or claim to observe
side effects outside the snapshots. Output contains names and sizes, not bytes.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import stat


def _inventory(root: Path) -> dict[str, tuple[Path, int]]:
    if not root.is_dir() or root.is_symlink():
        raise ValueError(f"snapshot root must be a real directory: {root}")
    result: dict[str, tuple[Path, int]] = {}
    for directory, names, files in os.walk(root, followlinks=False, onerror=lambda e: (_ for _ in ()).throw(e)):
        for name in names + files:
            path = Path(directory) / name
            info = path.lstat()
            if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
                raise ValueError(f"snapshot contains a link or reparse point: {path}")
            if stat.S_ISDIR(info.st_mode):
                continue
            if not stat.S_ISREG(info.st_mode):
                raise ValueError(f"snapshot contains a non-regular file: {path}")
            result[path.relative_to(root).as_posix()] = (path, info.st_size)
    return result


def _same_bytes(left: Path, right: Path) -> bool:
    with left.open("rb") as a, right.open("rb") as b:
        while True:
            x, y = a.read(1024 * 1024), b.read(1024 * 1024)
            if x != y:
                return False
            if not x:
                return True


def compare(before: Path, after: Path) -> dict:
    before, after = Path(before), Path(after)
    # Reject a root reparse point before resolving it.
    for root in (before, after):
        info = root.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError(f"snapshot root is a link or reparse point: {root}")
    before, after = before.resolve(), after.resolve()
    if before == after or before in after.parents or after in before.parents:
        raise ValueError("snapshot roots must be separate and non-overlapping")
    old, new = _inventory(before), _inventory(after)
    created = sorted(new.keys() - old.keys())
    deleted = sorted(old.keys() - new.keys())
    modified, unchanged = [], []
    for name in sorted(old.keys() & new.keys()):
        if old[name][1] != new[name][1] or not _same_bytes(old[name][0], new[name][0]):
            modified.append(name)
        else:
            unchanged.append(name)
    return {
        "before_root": str(before), "after_root": str(after),
        "created": created, "deleted": deleted, "modified": modified, "unchanged": unchanged,
        "scope": "regular files in the two supplied quiescent snapshots only",
        "limitations": ["outside paths, empty directories and transient changes are not observed",
                        "snapshots and files must not change during comparison"],
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before", required=True, type=Path)
    parser.add_argument("--after", required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(compare(args.before, args.after), ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
