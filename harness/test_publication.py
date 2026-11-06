"""Publication safety checks using synthetic inputs only."""
from pathlib import Path
import subprocess
import sys
import importlib.util
import hashlib
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent.parent


class PublicationChecks(unittest.TestCase):
    def source_fixture(self, check):
        spec = importlib.util.spec_from_file_location("packager", ROOT / "harness/package_normal.py")
        packager = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(packager)
        with tempfile.TemporaryDirectory(prefix="publication-fixture-") as directory:
            repo = Path(directory).resolve() / "source"
            repo.mkdir()
            inputs = {}
            for name in ("harness/bigbox_install.py", "harness/shim_install.py", "harness/package_normal.py",
                         "tools/build.py", "LICENSE", "README.md"):
                file = repo / name
                file.parent.mkdir(parents=True, exist_ok=True)
                file.write_bytes(b"synthetic original")
                inputs[name] = hashlib.sha256(file.read_bytes()).hexdigest().upper()
            manifest = {"schema": 2, "source_tree": inputs}
            packager.verify_source_tree(repo, manifest)
            check(packager, repo, manifest)

    def test_current_installer_must_match_bound_source(self):
        def check(packager, repo, manifest):
            (repo / "harness/bigbox_install.py").write_bytes(b"changed installer")
            with self.assertRaisesRegex(ValueError, "Build/source mismatch"):
                packager.verify_source_tree(repo, manifest)
        self.source_fixture(check)

    def test_bound_source_requires_installer_and_build_inputs(self):
        def check(packager, repo, manifest):
            del manifest["source_tree"]["tools/build.py"]
            with self.assertRaisesRegex(ValueError, "Curated source manifest is incomplete"):
                packager.verify_source_tree(repo, manifest)
        self.source_fixture(check)

    def test_bound_source_cannot_read_outside_tree(self):
        def check(packager, repo, manifest):
            outside = repo.parent / "outside.py"
            outside.write_bytes(b"synthetic outside file")
            manifest["source_tree"]["../outside.py"] = hashlib.sha256(outside.read_bytes()).hexdigest().upper()
            with self.assertRaisesRegex(ValueError, "Invalid source manifest path"):
                packager.verify_source_tree(repo, manifest)
        self.source_fixture(check)

    def test_legacy_cli_requires_explicit_root(self):
        result = subprocess.run(
            [sys.executable, "-I", "-B", str(ROOT / "harness/shim_install.py"), "preview"],
            capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("--root is required", result.stdout)


if __name__ == "__main__":
    unittest.main()
