"""Compare a frozen F20 candidate or installed mapping by bytes, without hashes."""
import argparse
import datetime
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path)
    parser.add_argument("--install-root", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if bool(args.candidate) == bool(args.install_root):
        parser.error("Choose exactly one comparison mode")
    output = args.output.resolve()
    if output.exists():
        raise FileExistsError("Refusing to overwrite evidence")
    rows = []
    if args.candidate:
        candidate = args.candidate.resolve(strict=True)
        manifest = json.loads((candidate / "manifest.json").read_text(encoding="utf-8-sig"))
        pairs = [(ROOT / row["source"], candidate / row["source"]) for row in manifest["files"]]
        label = manifest["candidate"]
    else:
        install = args.install_root.resolve(strict=True)
        app = install / "app"
        record = json.loads((ROOT / "workspace/build/current/M2/installed-m7.json").read_text(encoding="utf-8-sig"))
        pairs = []
        for item in record["files"]:
            target = Path(item["file"]).resolve(strict=True)
            if target == install / "README.md":
                source = ROOT / "docs/installed-README.md"
            else:
                relative = target.relative_to(app)
                source = (ROOT / "workspace/build/current/M2/M2Startup.dll"
                          if relative.as_posix() == "nx-profile/startup/M2Startup.dll" else ROOT / relative)
            pairs.append((source, target))
        label = "installed " + record["version"]
    for source, target in pairs:
        a, b = source.read_bytes(), target.read_bytes()
        rows.append({"source": str(source), "target": str(target), "source_bytes": len(a),
                     "target_bytes": len(b), "direct_bytes_equal": a == b})
    report = {"scope": label, "comparison": "direct byte equality; no hashes",
              "captured_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "files": rows, "passed": all(row["direct_bytes_equal"] for row in rows)}
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2)
        stream.write("\n")
    print(json.dumps({"output": str(output), "files": len(rows), "passed": report["passed"]}))
    if not report["passed"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
