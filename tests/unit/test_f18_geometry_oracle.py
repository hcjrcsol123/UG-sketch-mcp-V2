"""F18 offline geometry oracle: independent hand-derived transform fixtures."""
import copy
import importlib.util
import json
import subprocess
import sys
import tempfile
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
PATH = ROOT / "tools" / "v2" / "f18-geometry-oracle.py"
SPEC = importlib.util.spec_from_file_location("f18_geometry_oracle", PATH)
ORACLE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ORACLE)

# The 2 x 1 x 3 box is deliberately asymmetric in its X/Y dimensions.
SEED = {
    "vertices_mm": [[2,3,1], [4,3,1], [2,4,1], [4,4,1],
                    [2,3,4], [4,3,4], [2,4,4], [4,4,4]],
    "edges": [[0,1], [2,3], [4,5], [6,7], [0,2], [1,3],
              [4,6], [5,7], [0,4], [1,5], [2,6], [3,7]],
    "faces": [[0,1,4,5], [2,3,6,7], [0,2,8,9],
              [1,3,10,11], [4,6,8,10], [5,7,9,11]],
}


def body(transform):
    return {**copy.deepcopy(SEED), "vertices_mm": [list(transform(*p)) for p in SEED["vertices_mm"]]}


def document(pattern, instances):
    return {"seed": copy.deepcopy(SEED), "pattern": pattern, "instances": instances}


class FeaturePatternOracleTests(unittest.TestCase):
    def test_negative_x_linear_unordered_instances(self):
        pattern = {"mode": "linear", "count1": 3, "direction1": [-1,0,0], "pitch1_mm": 5}
        instances = [body(lambda x,y,z: (x-10,y,z)), body(lambda x,y,z: (x,y,z)),
                     body(lambda x,y,z: (x-5,y,z))]
        self.assertEqual(ORACLE.verify(document(pattern, instances)), {"ok": True, "matches": [1,2,0]})

    def test_rectangular_negative_x_unequal_pitches_and_permuted_indexes(self):
        pattern = {"mode": "rectangular", "count1": 2, "count2": 3,
                   "direction1": [-1,0,0], "direction2": [0,1,0], "pitch1_mm": 7, "pitch2_mm": 4}
        instances = [body(lambda x,y,z, i=i,j=j: (x-7*i,y+4*j,z)) for i in range(2) for j in range(3)]
        instances.reverse()
        # Renumber vertices, edges, and faces while preserving topology.
        original = instances[2]
        permutation = [5,2,7,0,6,1,4,3]
        inverse = {old: new for new, old in enumerate(permutation)}
        edge_order = list(reversed(range(12)))
        edge_inverse = {old: new for new, old in enumerate(edge_order)}
        instances[2] = {"vertices_mm": [original["vertices_mm"][i] for i in permutation],
                        "edges": [[inverse[v] for v in original["edges"][i]] for i in edge_order],
                        "faces": [[edge_inverse[e] for e in face] for face in reversed(original["faces"])]}
        self.assertEqual(ORACLE.verify(document(pattern, instances)), {"ok": True, "matches": [5,4,3,2,1,0]})

    def test_nonorigin_axis_both_signed_angles(self):
        # World axis passes through (1,1,0); +/-90-degree transforms calculated explicitly.
        for sign in (1, -1):
            pattern = {"mode": "circular", "count1": 3, "axis_origin_mm": [1,1,0],
                       "axis_direction": [0,0,1], "angle_deg": 90*sign}
            turn = (lambda x,y,z: (2-y,x,z)) if sign == 1 else (lambda x,y,z: (y,2-x,z))
            instances = [body(lambda x,y,z: (x,y,z)), body(turn), body(lambda x,y,z: (2-x,2-y,z))]
            with self.subTest(sign=sign):
                self.assertTrue(ORACLE.verify(document(pattern, instances))["ok"])

    def test_nearly_unit_axis_is_normalized_and_close_instances_rejected(self):
        pattern = {"mode": "circular", "count1": 2, "axis_origin_mm": [1,1,0],
                   "axis_direction": [0,0,1.0000000005], "angle_deg": 90}
        rotated = body(lambda x,y,z: (2-y,x,z))
        self.assertTrue(ORACLE.verify(document(pattern, [copy.deepcopy(SEED), rotated]))["ok"])
        ambiguous = {"mode": "linear", "count1": 2, "direction1": [1,0,0], "pitch1_mm": 1e-8}
        with self.assertRaises(ValueError):
            ORACLE.verify(document(ambiguous, [copy.deepcopy(SEED), copy.deepcopy(SEED)]))

    def test_missing_duplicate_and_incorrect_position(self):
        pattern = {"mode": "linear", "count1": 3, "direction1": [-1,0,0], "pitch1_mm": 5}
        correct = [body(lambda x,y,z, i=i: (x-5*i,y,z)) for i in range(3)]
        self.assertFalse(ORACLE.verify(document(pattern, correct[:-1]))["ok"])
        self.assertFalse(ORACLE.verify(document(pattern, [correct[0], correct[1], correct[1]]))["ok"])
        wrong = copy.deepcopy(correct)
        wrong[2]["vertices_mm"][0][0] += 0.01
        self.assertFalse(ORACLE.verify(document(pattern, wrong))["ok"])

    def test_position_correct_but_wrong_orientation_is_rejected(self):
        pattern = {"mode": "circular", "count1": 2, "axis_origin_mm": [1,1,0],
                   "axis_direction": [0,0,1], "angle_deg": 90}
        # Center is rotated correctly to (-1.5, 3, 2.5), but body retains original X/Y orientation.
        bad = body(lambda x,y,z: (x-4.5,y-0.5,z))
        self.assertFalse(ORACLE.verify(document(pattern, [copy.deepcopy(SEED), bad]))["ok"])

    def test_wrong_edge_and_face_adjacency_are_rejected(self):
        pattern = {"mode": "linear", "count1": 2, "direction1": [1,0,0], "pitch1_mm": 8}
        shifted = body(lambda x,y,z: (x+8,y,z))
        # Relabel graph vertices but leave geometry fixed: same positions, wrong connectivity.
        permute = {0: 1, 1: 0, 2: 2, 3: 3, 4: 4, 5: 5, 6: 6, 7: 7}
        shifted["edges"] = [[permute[v] for v in edge] for edge in shifted["edges"]]
        self.assertFalse(ORACLE.verify(document(pattern, [copy.deepcopy(SEED), shifted]))["ok"])
        shifted = body(lambda x,y,z: (x+8,y,z))
        shifted["faces"][0][0] = 2
        with self.assertRaises(ValueError):
            ORACLE.verify(document(pattern, [copy.deepcopy(SEED), shifted]))

    def test_tolerance_and_malformed_input(self):
        pattern = {"mode": "linear", "count1": 2, "direction1": [1,0,0], "pitch1_mm": 8}
        shifted = body(lambda x,y,z: (x+8,y,z))
        shifted["vertices_mm"][0][0] += 5e-7
        self.assertTrue(ORACLE.verify(document(pattern, [copy.deepcopy(SEED), shifted]))["ok"])
        shifted["vertices_mm"][0][0] += 1e-4
        self.assertFalse(ORACLE.verify(document(pattern, [copy.deepcopy(SEED), shifted]))["ok"])
        for patch in ({"count1": True}, {"direction1": [0,0,0]}, {"pitch1_mm": float("nan")}, {"mode": "bad"}):
            with self.subTest(patch=patch), self.assertRaises(ValueError):
                ORACLE.verify(document({**pattern, **patch}, [copy.deepcopy(SEED), shifted]))
        bad_seed = copy.deepcopy(SEED)
        bad_seed["vertices_mm"][1] = [4, 3.2, 1]
        with self.assertRaises(ValueError):
            ORACLE.verify({**document(pattern, [copy.deepcopy(SEED), shifted]), "seed": bad_seed})

    def test_unrepresentable_transformed_box_is_rejected(self):
        pattern = {"mode": "linear", "count1": 2, "direction1": [1,0,0], "pitch1_mm": 1e18}
        with self.assertRaises(ValueError):
            ORACLE.verify(document(pattern, [copy.deepcopy(SEED), copy.deepcopy(SEED)]))

    def test_cli_json_contract(self):
        pattern = {"mode": "linear", "count1": 2, "direction1": [1,0,0], "pitch1_mm": 8}
        doc = document(pattern, [copy.deepcopy(SEED), body(lambda x,y,z: (x+8,y,z))])
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "case.json"
            path.write_text(json.dumps(doc), encoding="utf-8")
            proc = subprocess.run([sys.executable, str(PATH), str(path)], capture_output=True, text=True, check=False)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertEqual(json.loads(proc.stdout), {"ok": True, "matches": [0,1]})


if __name__ == "__main__":
    unittest.main()
