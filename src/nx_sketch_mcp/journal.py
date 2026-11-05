"""Standalone, persistent supervisor for a single NX journal batch.

The coordination directory is shared with the GUI launcher.  The gate uses
CreateFileW with no sharing, and an active marker is a persistent stop sign.
This module deliberately has no marker reset or automatic recovery operation.
"""
from __future__ import annotations

import base64
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import json
import locale
import math
import os
from pathlib import Path
import re
import stat as stat_module
import subprocess
import sys
from typing import Any

RUN_JOURNAL = r"C:\0sf\ug\NXBIN\run_journal.exe"
MIN_TIMEOUT_SECONDS = 1.0
MAX_TIMEOUT_SECONDS = 86400.0
_MAX_OUTPUT_BYTES = 65536
_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}\Z", re.ASCII)
_RESERVED = {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}
_PROCESS_NAMES = {"ugraf.exe", "run_journal.exe", "m2jsonlhelper.exe"}


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _windows() -> None:
    if os.name != "nt":
        raise OSError("NX journal supervision is available only on Windows")


def _root() -> Path:
    _windows()
    # Match .NET Environment.GetFolderPath(UserProfile) without trusting an
    # environment override that could split the coordination domain.
    profile = ctypes.create_unicode_buffer(32768)
    shell = ctypes.WinDLL("shell32", use_last_error=True)
    shell.SHGetFolderPathW.argtypes = [wintypes.HWND, ctypes.c_int, wintypes.HANDLE,
                                      wintypes.DWORD, wintypes.LPWSTR]
    shell.SHGetFolderPathW.restype = ctypes.c_long
    if shell.SHGetFolderPathW(None, 40, None, 0, profile) != 0 or not Path(profile.value).is_absolute():
        raise OSError("Windows user profile directory is unavailable")
    return Path(profile.value) / ".codex" / "nx-sketch-mcp" / "journal-coordination"


def _id(value: Any) -> str:
    if not isinstance(value, str) or not _ID.fullmatch(value) or value.upper() in _RESERVED:
        raise ValueError("operation_id must be 1-64 ASCII letters, digits, underscores or hyphens, starting with a letter or digit, and not a reserved Windows name")
    return value


def _json_write(path: Path, obj: dict[str, Any]) -> None:
    temp = path.with_name(path.name + ".tmp")
    with temp.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(obj, stream, ensure_ascii=False, allow_nan=False, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temp, path)


def _read(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as stream:
        data = json.load(stream)
    if not isinstance(data, dict):
        raise ValueError("invalid operation record")
    return data


def _validate(request: Any) -> tuple[dict[str, Any], bytes]:
    if not isinstance(request, dict):
        raise ValueError("request must be an object")
    allowed = {"operation_id", "language", "script_path", "script_content", "args", "observation_root", "timeout_seconds"}
    if set(request) - allowed or not {"operation_id", "language", "args", "observation_root", "timeout_seconds"} <= set(request):
        raise ValueError("request has missing or unsupported fields")
    operation_id = _id(request["operation_id"])
    language = request["language"]
    if language not in ("python", "csharp"):
        raise ValueError("language must be python or csharp")
    if language == "csharp":
        raise ValueError("csharp journals are unavailable until execution is verified")
    if ("script_path" in request) == ("script_content" in request):
        raise ValueError("provide exactly one of script_path or script_content")
    source = None
    if "script_path" in request:
        value = request["script_path"]
        if not isinstance(value, str) or not value or not Path(value).is_absolute():
            raise ValueError("script_path must be an absolute existing file")
        source_path = Path(value).resolve(strict=True)
        if not source_path.is_file():
            raise ValueError("script_path must be an absolute existing file")
        source = str(source_path)
        script = source_path.read_bytes()
    else:
        if not isinstance(request["script_content"], str):
            raise ValueError("script_content must be a string")
        script = request["script_content"].encode("utf-8")
    args = request["args"]
    if not isinstance(args, list) or any(not isinstance(arg, str) or "\0" in arg for arg in args):
        raise ValueError("args must be an array of strings without NUL characters")
    value = request["observation_root"]
    if not isinstance(value, str) or not value or not Path(value).is_absolute():
        raise ValueError("observation_root must be an absolute existing directory")
    root = Path(value).resolve(strict=True)
    if not root.is_dir():
        raise ValueError("observation_root must be an absolute existing directory")
    timeout = request["timeout_seconds"]
    if isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or not math.isfinite(timeout) or not MIN_TIMEOUT_SECONDS <= timeout <= MAX_TIMEOUT_SECONDS:
        raise ValueError("timeout_seconds must be between 1 and 86400 seconds")
    return {"operation_id": operation_id, "language": language, "source_path": source,
            "args": args, "observation_root": str(root), "timeout_seconds": float(timeout)}, script


def submit(request: dict[str, Any]) -> dict[str, Any]:
    """Reserve an operation ID and start its detached supervisor; never replay an ID."""
    _windows()
    normalized, script = _validate(request)
    root = _root()
    operations = root / "operations"
    operations.mkdir(parents=True, exist_ok=True)
    directory = operations / normalized["operation_id"]
    try:
        directory.mkdir()
    except FileExistsError as exc:
        raise ValueError("operation_id is already reserved; replay is prohibited") from exc
    extension = ".py" if normalized["language"] == "python" else ".cs"
    frozen = directory / ("journal" + extension)
    frozen.write_bytes(script)
    normalized["frozen_script_path"] = str(frozen)
    _json_write(directory / "request.json", normalized)
    record = {"operation_id": normalized["operation_id"], "status": "STARTING", "created_at": _now(),
              "request": normalized, "session_observations": {"before": "unknown", "after": "unknown"}}
    _json_write(directory / "state.json", record)
    try:
        subprocess.Popen([sys.executable, str(Path(__file__).resolve()), "--supervise", normalized["operation_id"]],
                         stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         close_fds=True, creationflags=subprocess.CREATE_NO_WINDOW | subprocess.DETACHED_PROCESS)
    except OSError as exc:
        record.update(status="FAILED", finished_at=_now(), error="supervisor could not be started: " + str(exc), dispatched=False)
        _json_write(directory / "state.json", record)
        return record
    # Return our original record; reading here would race the supervisor's first update.
    return record


def get(operation_id: str) -> dict[str, Any]:
    """Read the durable operation state without changing or recovering it."""
    directory = _root() / "operations" / _id(operation_id)
    try:
        record = _read(directory / "state.json")
    except FileNotFoundError as exc:
        if directory.is_dir():
            return {"operation_id": operation_id, "status": "UNKNOWN", "error": "reserved operation has no complete state record", "state_is_last_recorded": True, "supervisor_health": "unknown"}
        raise ValueError("operation_id not found") from exc
    record["state_is_last_recorded"] = True
    record["supervisor_health"] = "unknown"
    for name in ("stdout", "stderr"):
        path = directory / (name + ".bin")
        if path.exists():
            with path.open("rb") as stream:
                data = stream.read(_MAX_OUTPUT_BYTES + 1)
            bounded = data[:_MAX_OUTPUT_BYTES]
            try:
                decoded = bounded.decode("utf-8", errors="strict")
                encoding = "utf-8"
            except UnicodeDecodeError:
                encoding = locale.getencoding()
                decoded = bounded.decode(encoding, errors="replace")
            record[name] = {"path": str(path), "text": decoded, "base64": base64.b64encode(bounded).decode("ascii"),
                            "truncated": len(data) > _MAX_OUTPUT_BYTES, "encoding": encoding,
                            "text_may_contain_replacement": "\ufffd" in decoded}
    file_report_path = directory / "observation-report.json"
    record["file_observations"] = {"status": "unknown", "path": str(file_report_path)}
    try:
        if file_report_path.is_file():
            if file_report_path.stat().st_size <= 262144:
                with file_report_path.open("r", encoding="utf-8") as stream:
                    record["file_observations"] = {"status": "available", "path": str(file_report_path), "data": json.load(stream), "truncated": False}
            else:
                record["file_observations"] = {"status": "available_on_disk", "path": str(file_report_path), "truncated": True}
    except (OSError, ValueError, UnicodeError):
        pass
    model_path = directory / "model-observation.json"
    record["model_observations"] = {"status": "unknown", "path": str(model_path)}
    try:
        if model_path.is_file():
            if model_path.stat().st_size <= 262144:
                with model_path.open("r", encoding="utf-8") as stream:
                    observation = json.load(stream)
                record["model_observations"] = {"status": "available", "path": str(model_path), "data": observation, "truncated": False}
            else:
                record["model_observations"] = {"status": "available_on_disk", "path": str(model_path), "truncated": True}
    except (OSError, ValueError, UnicodeError):
        pass
    return record


def _kernel():
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID,
                                   wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    return kernel


def _gate(root: Path):
    kernel = _kernel()
    handle = kernel.CreateFileW(str(root / "gate.lock"), 0xC0000000, 0, None, 4, 0x80, None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    return kernel, handle


def _processes() -> list[dict[str, Any]]:
    class PROCESSENTRY32W(ctypes.Structure):
        _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD), ("th32ProcessID", wintypes.DWORD),
                    ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", wintypes.DWORD),
                    ("cntThreads", wintypes.DWORD), ("th32ParentProcessID", wintypes.DWORD),
                    ("pcPriClassBase", wintypes.LONG), ("dwFlags", wintypes.DWORD), ("szExeFile", wintypes.WCHAR * 260)]
    kernel = _kernel()
    kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
    kernel.Process32FirstW.restype = wintypes.BOOL
    kernel.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
    kernel.Process32NextW.restype = wintypes.BOOL
    snapshot = kernel.CreateToolhelp32Snapshot(2, 0)
    if snapshot == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        item = PROCESSENTRY32W()
        item.dwSize = ctypes.sizeof(item)
        found = []
        ok = kernel.Process32FirstW(snapshot, ctypes.byref(item))
        while ok:
            if item.szExeFile.lower() in _PROCESS_NAMES:
                found.append({"name": item.szExeFile, "pid": item.th32ProcessID})
            ok = kernel.Process32NextW(snapshot, ctypes.byref(item))
        error = ctypes.get_last_error()
        if error != 18:  # ERROR_NO_MORE_FILES
            raise ctypes.WinError(error)
        return found
    finally:
        kernel.CloseHandle(snapshot)


def _snapshot(root: Path) -> dict[str, Any]:
    entries: dict[str, Any] = {}
    errors: list[str] = []
    def onerror(error: OSError) -> None:
        errors.append(str(error))
    for base, dirs, files in os.walk(root, onerror=onerror, followlinks=False):
        for name in dirs + files:
            path = Path(base) / name
            try:
                item = path.lstat()
                entries[str(path)] = {"size": item.st_size, "mtime_ns": item.st_mtime_ns,
                                      "mode": item.st_mode}
                if name in dirs and (path.is_symlink() or
                        getattr(item, "st_file_attributes", 0) & stat_module.FILE_ATTRIBUTE_REPARSE_POINT):
                    dirs.remove(name)
                    errors.append("not traversed (reparse point): " + str(path))
            except OSError as exc:
                if name in dirs:
                    dirs.remove(name)  # Unknown directory type must not be traversed.
                errors.append(str(exc))
    return {"entries": entries, "errors": errors}


def _diff(before: dict[str, Any], after: dict[str, Any]) -> dict[str, Any]:
    a, b = before["entries"], after["entries"]
    return {"created": sorted(b.keys() - a.keys()), "deleted": sorted(a.keys() - b.keys()),
            "modified": sorted(key for key in a.keys() & b.keys() if a[key] != b[key]),
            "before_errors": before["errors"], "after_errors": after["errors"],
            "limitations": "Metadata snapshots only (size, modification time, mode); no hashes; transient or metadata-preserving changes may be missed. Links and Windows reparse-point directories are not traversed; skipped paths and errors make coverage incomplete; concurrent changes are not isolated."}


def _completed_model_observation(path: Path) -> str | None:
    """Return the script outcome only for a complete wrapper observation."""
    try:
        observation = _read(path)
    except (OSError, ValueError, UnicodeError):
        return None
    outcome = observation.get("script_outcome")
    before, after, changes = (observation.get(key) for key in ("before", "after", "changes"))
    if outcome not in ("RETURNED", "RAISED"):
        return None
    if not all(isinstance(snapshot, dict) and snapshot.get("complete") is True
               and isinstance(snapshot.get("parts"), list)
               and isinstance(snapshot.get("errors"), list) and not snapshot["errors"]
               for snapshot in (before, after)):
        return None
    if not isinstance(changes, dict) or not all(isinstance(changes.get(key), list)
                                                  for key in ("added", "removed", "modified")):
        return None
    return outcome


def _supervise(operation_id: str) -> None:
    root = _root()
    directory = root / "operations" / _id(operation_id)
    state_path = directory / "state.json"
    # A durable one-shot claim prevents duplicate CLI invocations from ever
    # dispatching the same reserved ID, including after a supervisor crash.
    try:
        descriptor = os.open(directory / "supervisor.claim", os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    except FileExistsError:
        return
    with os.fdopen(descriptor, "w", encoding="utf-8") as claim:
        claim.write(_now())
        claim.flush()
        os.fsync(claim.fileno())
    record = _read(state_path)
    if record.get("status") != "STARTING":
        return
    request = _read(directory / "request.json")
    marker = root / "active.json"
    kernel = handle = None
    proc = None
    marked = False
    dispatched = False
    try:
        kernel, handle = _gate(root)
        if marker.exists():
            record.update(status="REJECTED", dispatched=False, error="journal coordination marker already exists", finished_at=_now())
            _json_write(state_path, record)
            return
        # Exclusive creation while holding the gate: crashes thereafter leave a persistent stop sign.
        with marker.open("x", encoding="utf-8") as stream:
            json.dump({"operation_id": operation_id, "created_at": _now()}, stream)
            stream.flush()
            os.fsync(stream.fileno())
        marked = True
        busy = _processes()
        if busy:
            record.update(status="REJECTED", dispatched=False, error="NX-related processes are present", processes=busy, finished_at=_now())
            _json_write(state_path, record)
            marker.unlink()
            marked = False
            return
        before = _snapshot(Path(request["observation_root"]))
        _json_write(directory / "observation-before.json", before)
        record.update(status="RUNNING", started_at=_now(), dispatched=False)
        _json_write(state_path, record)
        if request["language"] == "python":
            wrapper = Path(__file__).with_name("journal_wrapper.py")
            if not wrapper.is_file():
                raise FileNotFoundError("journal Python observation wrapper is unavailable")
            command = [RUN_JOURNAL, str(wrapper), "-args", request["frozen_script_path"],
                       str(directory / "model-observation.json"), *request["args"]]
        else:
            command = [RUN_JOURNAL, request["frozen_script_path"], "-args", *request["args"]]
        with (directory / "stdout.bin").open("wb") as out, (directory / "stderr.bin").open("wb") as err:
            proc = subprocess.Popen(command,
                                    stdin=subprocess.DEVNULL, stdout=out, stderr=err, close_fds=True,
                                    creationflags=subprocess.CREATE_NO_WINDOW)
            dispatched = True
            record.update(dispatched=True, runner_pid=proc.pid)
            _json_write(state_path, record)
            try:
                exit_code = proc.wait(timeout=request["timeout_seconds"])
            except subprocess.TimeoutExpired:
                record.update(status="UNKNOWN", error="runner exceeded timeout; outcome remains unknown", timed_out_at=_now())
                _json_write(state_path, record)
                proc.wait()  # Never kill or release the gate while the runner is alive.
                return  # Persistent UNKNOWN marker is never cleared automatically.
        after = _snapshot(Path(request["observation_root"]))
        _json_write(directory / "observation-after.json", after)
        report = _diff(before, after)
        report_path = directory / "observation-report.json"
        _json_write(report_path, report)
        outcome = _completed_model_observation(directory / "model-observation.json")
        if outcome is None or (exit_code == 0 and outcome != "RETURNED"):
            record.update(status="UNKNOWN", exit_code=exit_code, finished_at=_now(),
                          observation_report_path=str(report_path),
                          error="model observation is incomplete or contradicts runner exit; outcome remains unknown")
            _json_write(state_path, record)
            return  # Keep the persistent marker until explicit recovery.
        record.update(status="SUCCEEDED" if exit_code == 0 else "FAILED", exit_code=exit_code,
                      finished_at=_now(), observation_report_path=str(report_path))
        _json_write(state_path, record)
        marker.unlink()
        marked = False
    except Exception as exc:
        # After Popen succeeds, any loss of observation is conservatively UNKNOWN.
        record.update(status="UNKNOWN" if dispatched else "FAILED", error=f"{type(exc).__name__}: {exc}",
                      dispatched=dispatched, finished_at=_now())
        try:
            _json_write(state_path, record)
            if marked and not dispatched:
                marker.unlink()
                marked = False
        finally:
            if proc is not None and proc.poll() is None:
                proc.wait()
    finally:
        if kernel is not None and handle is not None:
            kernel.CloseHandle(handle)


def _main() -> int:
    if len(sys.argv) != 3 or sys.argv[1] != "--supervise":
        return 2
    try:
        _supervise(sys.argv[2])
    except Exception:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
