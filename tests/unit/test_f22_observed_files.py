"""Offline tests of the scoped F22 file comparison oracle; no NX involved."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "tools" / "v2" / "f22-compare-observed-files.py"
spec = importlib.util.spec_from_file_location("f22_file_comparison", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ObservedFilesTests(unittest.TestCase):
    def test_created_deleted_modified_same_size_and_unchanged(self):
        with tempfile.TemporaryDirectory() as tmp:
            before, after = Path(tmp) / "before", Path(tmp) / "after"
            before.mkdir(); after.mkdir()
            for root in before, after:
                (root / "子").mkdir()
                (root / "子" / "same.txt").write_bytes(b"same\x00bytes")
            (before / "changed").write_bytes(b"abc")
            (after / "changed").write_bytes(b"abd")
            (before / "deleted").write_bytes(b"")
            (after / "created").write_bytes(b"")
            result = module.compare(before, after)
            self.assertEqual(result["created"], ["created"])
            self.assertEqual(result["deleted"], ["deleted"])
            self.assertEqual(result["modified"], ["changed"])
            self.assertEqual(result["unchanged"], ["子/same.txt"])
            self.assertIn("outside paths", result["limitations"][0])

    def test_overlapping_or_missing_roots_fail(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            child = root / "child"
            child.mkdir()
            with self.assertRaises(ValueError):
                module.compare(root, child)
            with self.assertRaises(FileNotFoundError):
                module.compare(child, root / "missing")

    def test_symlink_fails_closed_when_available(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            before, after = root / "before", root / "after"
            before.mkdir(); after.mkdir()
            try:
                (after / "link").symlink_to(before, target_is_directory=True)
            except OSError as exc:
                self.skipTest(f"symlink creation unavailable: {exc}")
            with self.assertRaises(ValueError):
                module.compare(before, after)


if __name__ == "__main__":
    unittest.main()
