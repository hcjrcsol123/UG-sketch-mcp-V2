"""Run F01/F02 prepare -> execute -> verify through the local MCP stdio API.

The request file contains model identifiers and geometric inputs, never a bridge token.
The server reads its normal installation configuration; this client does not open it.

Request shape:
{
  "ownership_id": "...",
  "binding_epoch": 1,
  "revision_id": "...",
  "steps": [
    {"tool": "nx_prepare_datum_plane", "arguments": {
      "origin_mm": [10, 20, 30], "normal": [1, 1, 1], "x_axis": [1, 0, 0]}},
    {"tool": "nx_prepare_sketch", "arguments": {
      "support_datum_plane_id": "$latest.datum_plane_id", "x_axis": [1, 0, 0],
      "geometries": []}}
  ],
  "save_copy": {"target_path": "C:\\...\\result.prt"}  // optional
}

Each step accepts only nx_prepare_datum_plane or nx_prepare_sketch. The helper discovers
the tools first and sends separate MCP calls for prepare, execute, and verify. It never
takes ownership, launches NX, or retries an unknown model-write outcome.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import uuid
from typing import Any


PROTOCOL_VERSION = "2025-11-25"
MAX_LINE_BYTES = 1_048_576
PREPARE_TOOLS = {"nx_prepare_datum_plane", "nx_prepare_sketch"}
TERMINAL_STATES = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}


class McpClientError(RuntimeError):
    pass


class McpStdioClient:
    def __init__(self, process: subprocess.Popen[bytes], timeout_seconds: float,
                 max_line_bytes: int = MAX_LINE_BYTES) -> None:
        if isinstance(max_line_bytes, bool) or not isinstance(max_line_bytes, int) or max_line_bytes < 1:
            raise ValueError("max_line_bytes must be a positive integer")
        self.process = process
        self.timeout_seconds = timeout_seconds
        self.max_line_bytes = max_line_bytes
        self.last_response: dict[str, Any] | None = None
        self._next_id = 1
        self._messages: queue.Queue[bytes | None] = queue.Queue()
        self._stderr_tail: bytearray = bytearray()
        self._reader = threading.Thread(target=self._read_stdout, daemon=True, name="f01-f02-mcp-reader")
        self._stderr_reader = threading.Thread(target=self._read_stderr, daemon=True, name="f01-f02-mcp-stderr")
        self._reader.start()
        self._stderr_reader.start()

    def _read_stdout(self) -> None:
        stream = self.process.stdout
        if stream is None:
            self._messages.put(None)
            return
        try:
            while True:
                line = stream.readline(self.max_line_bytes + 1)
                if not line:
                    self._messages.put(None)
                    return
                if len(line) > self.max_line_bytes:
                    self._messages.put(b"__MCP_LINE_TOO_LARGE__")
                    return
                if not line.endswith(b"\n"):
                    self._messages.put(b"__MCP_LINE_UNTERMINATED__")
                    return
                self._messages.put(line)
        except OSError:
            self._messages.put(None)

    def _read_stderr(self) -> None:
        stream = self.process.stderr
        if stream is None:
            return
        try:
            while True:
                chunk = stream.read(1024)
                if not chunk:
                    return
                self._stderr_tail.extend(chunk)
                if len(self._stderr_tail) > 8192:
                    del self._stderr_tail[:-8192]
        except OSError:
            return

    def request(self, method: str, params: dict[str, Any] | None = None) -> dict[str, Any]:
        self.last_response = None
        if self.process.poll() is not None:
            raise McpClientError("MCP server exited before completing the request")
        request_id = self._next_id
        self._next_id += 1
        envelope = {"jsonrpc": "2.0", "id": request_id, "method": method}
        if params is not None:
            envelope["params"] = params
        raw = json.dumps(envelope, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8") + b"\n"
        if self.process.stdin is None:
            raise McpClientError("MCP server stdin is unavailable")
        try:
            self.process.stdin.write(raw)
            self.process.stdin.flush()
        except OSError as exc:
            raise McpClientError("MCP server stopped accepting requests") from exc
        try:
            response_line = self._messages.get(timeout=self.timeout_seconds)
        except queue.Empty as exc:
            raise McpClientError(f"MCP request {method} timed out") from exc
        if response_line is None:
            raise McpClientError("MCP server closed stdout before responding")
        if response_line == b"__MCP_LINE_TOO_LARGE__":
            raise McpClientError(
                "MCP server returned an oversized JSON-RPC line "
                f"(max_line_bytes={self.max_line_bytes}, observed_at_least_bytes={self.max_line_bytes + 1})"
            )
        if response_line == b"__MCP_LINE_UNTERMINATED__":
            raise McpClientError(
                "MCP server returned an unterminated JSON-RPC line "
                f"(max_line_bytes={self.max_line_bytes})"
            )
        try:
            response = json.loads(response_line.decode("utf-8", errors="strict"))
        except (UnicodeError, ValueError) as exc:
            raise McpClientError("MCP server returned invalid JSON-RPC") from exc
        if not isinstance(response, dict) or response.get("id") != request_id:
            raise McpClientError("MCP response id did not match the request")
        self.last_response = response
        if "error" in response:
            error = response["error"]
            message = error.get("message") if isinstance(error, dict) else "invalid JSON-RPC error"
            raise McpClientError(f"MCP {method} failed: {message}")
        result = response.get("result")
        if not isinstance(result, dict):
            raise McpClientError(f"MCP {method} returned no result object")
        return result

    def notify(self, method: str, params: dict[str, Any] | None = None) -> None:
        if self.process.stdin is None:
            raise McpClientError("MCP server stdin is unavailable")
        envelope = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            envelope["params"] = params
        raw = json.dumps(envelope, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8") + b"\n"
        self.process.stdin.write(raw)
        self.process.stdin.flush()

    def close(self) -> None:
        if self.process.stdin is not None:
            try:
                self.process.stdin.close()
            except OSError:
                pass
        try:
            self.process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            # This only terminates the MCP child started by this client.
            self.process.terminate()
            try:
                self.process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=2)


def _tool_payload(client: McpStdioClient, name: str, arguments: dict[str, Any]) -> dict[str, Any]:
    response = client.request("tools/call", {"name": name, "arguments": arguments})
    if response.get("isError") is True:
        content = response.get("structuredContent")
        error = content.get("error") if isinstance(content, dict) else None
        if isinstance(error, dict):
            raise McpClientError(f"{name} failed: {error.get('code')}: {error.get('message')}")
        raise McpClientError(f"{name} returned a tool error")
    structured = response.get("structuredContent")
    if isinstance(structured, dict):
        return structured
    content = response.get("content")
    if isinstance(content, list):
        for item in content:
            if isinstance(item, dict) and isinstance(item.get("text"), str):
                try:
                    parsed = json.loads(item["text"])
                except ValueError:
                    continue
                if isinstance(parsed, dict):
                    return parsed
    raise McpClientError(f"{name} returned no structured result")


def _wait_operation(client: McpStdioClient, payload: dict[str, Any], timeout_seconds: float,
                    poll_interval_seconds: float) -> dict[str, Any]:
    operation_id = payload.get("operation_id")
    if not isinstance(operation_id, str) or not operation_id:
        raise McpClientError("operation response omitted operation_id")
    deadline = time.monotonic() + timeout_seconds
    while payload.get("state") not in TERMINAL_STATES:
        if time.monotonic() >= deadline:
            raise McpClientError(f"operation {operation_id} did not reach a terminal state before timeout")
        time.sleep(poll_interval_seconds)
        payload = _tool_payload(client, "nx_get_operation", {"operation_id": operation_id})
    if payload.get("state") != "SUCCEEDED":
        error = payload.get("error")
        raise McpClientError(f"operation {operation_id} ended in {payload.get('state')}: {error}")
    return payload


def _latest_value(latest: dict[str, Any], value: Any) -> Any:
    if isinstance(value, str) and value.startswith("$latest."):
        key = value[len("$latest."):]
        if key not in latest:
            raise McpClientError(f"request references unavailable value {value}")
        return latest[key]
    if isinstance(value, list):
        return [_latest_value(latest, item) for item in value]
    if isinstance(value, dict):
        return {key: _latest_value(latest, item) for key, item in value.items()}
    return value


def _operation_summary(payload: dict[str, Any]) -> dict[str, Any]:
    result = payload.get("result")
    if not isinstance(result, dict):
        result = {}
    summary = {key: value for key, value in result.items() if key in {
        "plan_id", "kind", "mutation_state", "revision_id", "datum_plane_id", "sketch_id",
        "feature_id", "verified", "checks", "datum_plane", "sketch", "geometry_readback",
        "target_path", "saved_path", "reopened", "saved", "roundtrip_verified",
        "new_ownership_id", "new_binding_epoch",
    }}
    summary["operation_id"] = payload.get("operation_id")
    summary["state"] = payload.get("state")
    return summary


def _run_step(client: McpStdioClient, step: dict[str, Any], *, ownership_id: str,
              binding_epoch: int, revision_id: str, latest: dict[str, Any], timeout_seconds: float,
              poll_interval_seconds: float) -> tuple[dict[str, Any], str]:
    tool = step.get("tool")
    raw_arguments = step.get("arguments", {})
    if tool not in PREPARE_TOOLS or not isinstance(raw_arguments, dict):
        raise McpClientError("each step requires nx_prepare_datum_plane or nx_prepare_sketch and an arguments object")
    arguments = _latest_value(latest, raw_arguments)
    arguments.update({
        "operation_id": "f01f02-prepare-" + uuid.uuid4().hex,
        "ownership_id": ownership_id,
        "binding_epoch": binding_epoch,
        "revision_id": revision_id,
    })
    prepared = _wait_operation(client, _tool_payload(client, tool, arguments), timeout_seconds, poll_interval_seconds)
    prepare_result = prepared.get("result")
    plan = prepare_result.get("plan") if isinstance(prepare_result, dict) else None
    if not isinstance(plan, dict) or not isinstance(plan.get("plan_id"), str):
        raise McpClientError(f"{tool} succeeded without returning plan.plan_id")
    plan_id = plan["plan_id"]

    execute_id = "f01f02-execute-" + uuid.uuid4().hex
    executed = _wait_operation(client, _tool_payload(client, "nx_execute_plan", {
        "operation_id": execute_id,
        "ownership_id": ownership_id,
        "binding_epoch": binding_epoch,
        "plan_id": plan_id,
    }), timeout_seconds, poll_interval_seconds)

    verify_id = "f01f02-verify-" + uuid.uuid4().hex
    verified = _wait_operation(client, _tool_payload(client, "nx_verify_part", {
        "operation_id": verify_id,
        "ownership_id": ownership_id,
        "binding_epoch": binding_epoch,
        "execute_operation_id": execute_id,
    }), timeout_seconds, poll_interval_seconds)
    verify_result = verified.get("result")
    if not isinstance(verify_result, dict) or verify_result.get("verified") is not True:
        raise McpClientError(f"operation {verify_id} completed without verified=true")

    execute_result = executed.get("result")
    if not isinstance(execute_result, dict):
        execute_result = {}
    if tool == "nx_prepare_datum_plane":
        datum_id = execute_result.get("datum_plane_id") or verify_result.get("datum_plane_id")
        if not isinstance(datum_id, str) or not datum_id:
            raise McpClientError("verified DatumPlane omitted datum_plane_id")
        latest["datum_plane_id"] = datum_id
    else:
        sketch_id = execute_result.get("sketch_id") or verify_result.get("sketch_id")
        if isinstance(sketch_id, str):
            latest["sketch_id"] = sketch_id
    new_revision = verify_result.get("revision_id") or execute_result.get("revision_id") or revision_id
    latest["revision_id"] = new_revision
    return ({
        "tool": tool,
        "prepare_operation_id": prepared["operation_id"],
        "plan_id": plan_id,
        "execute_operation_id": execute_id,
        "verify_operation_id": verify_id,
        "prepare": _operation_summary(prepared),
        "execute": _operation_summary(executed),
        "verify": _operation_summary(verified),
    }, new_revision)


def _default_install_root() -> Path:
    codex_home = os.environ.get("CODEX_HOME")
    root = Path(codex_home) if codex_home else Path.home() / ".codex"
    return root / "nx-sketch-mcp"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", required=True, type=Path, help="UTF-8 JSON request file")
    parser.add_argument("--config", type=Path, help="MCP config path; defaults to the local installation config")
    parser.add_argument("--source-root", type=Path, help="Python source root; defaults to installed app/src when present")
    parser.add_argument("--python", default=sys.executable, help="Python executable for the MCP server")
    parser.add_argument("--timeout-seconds", type=float, default=60.0)
    parser.add_argument("--poll-interval-seconds", type=float, default=0.2)
    parser.add_argument("--output", type=Path, help="optional path to save the result JSON")
    args = parser.parse_args(argv)
    if not (1.0 <= args.timeout_seconds <= 600.0) or not (0.05 <= args.poll_interval_seconds <= 5.0):
        parser.error("timeouts must be within the documented bounds")
    request_path = args.request.resolve(strict=True)
    try:
        request = json.loads(request_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, ValueError) as exc:
        parser.error(f"request file must be readable UTF-8 JSON ({type(exc).__name__})")
    if not isinstance(request, dict):
        parser.error("request file must contain a JSON object")
    ownership_id = request.get("ownership_id")
    binding_epoch = request.get("binding_epoch")
    revision_id = request.get("revision_id")
    steps = request.get("steps")
    if not isinstance(ownership_id, str) or not ownership_id.strip():
        parser.error("request ownership_id must be a non-empty string")
    if isinstance(binding_epoch, bool) or not isinstance(binding_epoch, int) or binding_epoch < 1:
        parser.error("request binding_epoch must be a positive integer")
    if not isinstance(revision_id, str) or not revision_id:
        parser.error("request revision_id must be a non-empty string")
    if not isinstance(steps, list) or not steps:
        parser.error("request steps must contain at least one prepare operation")

    install_root = _default_install_root()
    config_path = args.config.resolve() if args.config else install_root / "mcp-config.json"
    installed_source = install_root / "app" / "src"
    source_root = args.source_root.resolve() if args.source_root else (
        installed_source if installed_source.is_dir() else Path(__file__).resolve().parents[2] / "src"
    )
    if not (source_root / "nx_sketch_mcp").is_dir():
        parser.error("source-root must contain the nx_sketch_mcp package")

    env = os.environ.copy()
    env["PYTHONUTF8"] = "1"
    env["PYTHONDONTWRITEBYTECODE"] = "1"
    existing_pythonpath = env.get("PYTHONPATH", "")
    env["PYTHONPATH"] = str(source_root) + (os.pathsep + existing_pythonpath if existing_pythonpath else "")
    command = [args.python, "-B", "-m", "nx_sketch_mcp", "--config", str(config_path)]
    try:
        process = subprocess.Popen(command, cwd=str(source_root.parent), env=env,
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   bufsize=0)
    except OSError as exc:
        parser.error(f"could not start the local MCP stdio process ({type(exc).__name__})")
    client = McpStdioClient(process, args.timeout_seconds)
    try:
        initialized = client.request("initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "nx-sketch-mcp-f01-f02-client", "version": "1"},
        })
        server_info = initialized.get("serverInfo")
        if not isinstance(server_info, dict):
            raise McpClientError("MCP initialize omitted serverInfo")
        client.notify("notifications/initialized")
        listing = client.request("tools/list")
        tools = listing.get("tools")
        if not isinstance(tools, list):
            raise McpClientError("MCP tools/list omitted tools")
        tool_names = {item.get("name") for item in tools if isinstance(item, dict)}
        requested_tools = {step.get("tool") for step in steps if isinstance(step, dict)}
        missing = requested_tools - tool_names
        if missing:
            raise McpClientError("MCP tool discovery omitted: " + ", ".join(sorted(str(item) for item in missing)))

        latest: dict[str, Any] = {"revision_id": revision_id}
        step_results: list[dict[str, Any]] = []
        current_revision = revision_id
        for step in steps:
            if not isinstance(step, dict):
                raise McpClientError("every steps entry must be an object")
            result, current_revision = _run_step(
                client, step, ownership_id=ownership_id, binding_epoch=binding_epoch,
                revision_id=current_revision, latest=latest, timeout_seconds=args.timeout_seconds,
                poll_interval_seconds=args.poll_interval_seconds,
            )
            step_results.append(result)

        save_result = None
        save_request = request.get("save_copy")
        if save_request is not None:
            if not isinstance(save_request, dict) or set(save_request) != {"target_path"}:
                raise McpClientError("save_copy must contain only target_path")
            if "nx_save_copy" not in tool_names:
                raise McpClientError("MCP tool discovery omitted nx_save_copy")
            save_payload = _tool_payload(client, "nx_save_copy", {
                "operation_id": "f01f02-save-" + uuid.uuid4().hex,
                "ownership_id": ownership_id,
                "binding_epoch": binding_epoch,
                "target_path": save_request["target_path"],
                "reopen": True,
            })
            save_result = _wait_operation(client, save_payload, args.timeout_seconds, args.poll_interval_seconds)
            save_result = _operation_summary(save_result)

        output = {
            "server_version": server_info.get("version"),
            "discovered_tool_count": len(tool_names),
            "discovered_f01_f02_tools": sorted(name for name in tool_names if name in PREPARE_TOOLS),
            "steps": step_results,
            "latest_ids": {key: latest[key] for key in ("datum_plane_id", "sketch_id", "revision_id") if key in latest},
            "save_copy": save_result,
        }
        serialized = json.dumps(output, ensure_ascii=False, allow_nan=False, indent=2)
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(serialized + "\n", encoding="utf-8")
        sys.stdout.write(serialized + "\n")
        return 0
    except McpClientError as exc:
        sys.stderr.write(f"F01/F02 MCP client failed: {exc}\n")
        return 1
    finally:
        client.close()


if __name__ == "__main__":
    raise SystemExit(main())
