"""Offline controls for F16 installed harness; no NX calls."""
import importlib.util
from pathlib import Path
import unittest

PATH = Path(__file__).resolve().parents[1] / "nx/v2/smoke-f16-mcp.py"
SPEC = importlib.util.spec_from_file_location("f16_smoke", PATH)
smoke = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(smoke)


class F16SmokeControls(unittest.TestCase):
    def test_resolves_current_session_full_jid_and_reversed_endpoints(self):
        a = "EDGE * 2 * 3 {(40,0,0)(40,0,5)(40,0,10) BLOCK(1)}"
        b = "EDGE * 4 * 6 {(60,12,10)(60,12,5)(60,12,0) BLOCK(1)}"
        body = {"edge_ids": [a, b]}
        self.assertEqual(smoke.fixture_edge(body, [40, 0, 0], [40, 0, 10]), a)
        self.assertEqual(smoke.fixture_edge(body, [60, 12, 0], [60, 12, 10]), b)
        with self.assertRaises(AssertionError):
            smoke.fixture_edge({"edge_ids": [a, a]}, [40, 0, 0], [40, 0, 10])
        with self.assertRaises(AssertionError):
            smoke.fixture_edge({"edge_ids": ["EDGE * 2 * 3"]}, [40, 0, 0], [40, 0, 10])

    def test_schema_rejects_nonunique_edges_or_wrong_fields(self):
        props = {x: {"type": "string"} for x in smoke.FIELDS}
        props["edge_ids"] = {"type": "array", "minItems": 1, "maxItems": 32, "uniqueItems": True}
        props["radius_mm"] = {"type": "number"}
        schema = {"type": "object", "additionalProperties": False, "properties": props, "required": list(smoke.FIELDS)}
        tools = [{"name": "nx_prepare_edge_blend", "inputSchema": schema}]
        smoke.schema_contract(tools)
        props["edge_ids"]["uniqueItems"] = False
        with self.assertRaises(AssertionError):
            smoke.schema_contract(tools)
        with self.assertRaises(AssertionError):
            smoke.schema_contract([])


if __name__ == "__main__":
    unittest.main()
