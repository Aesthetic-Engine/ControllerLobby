"""Build an immutable portable Beta 1 installer package (Schema 2); does not install or launch games.
Intakes the accepted flat self-contained win-x64 runtime payload (e.g. beta-runtime-candidate-02)
with recursive file-set/hash equality and reparse/nested-entry refusal.
"""
from pathlib import Path
import argparse, hashlib, json, os, shutil, stat, subprocess, zipfile

# Official Python 3.13.1 embeddable amd64 zip SHA-256:
OFFICIAL_PYTHON_EMBED_ZIP_SHA256 = "7B7923FF0183A8B8FCA90F6047184B419B108CB437F75FC1C002F9D2F8BCEC16"
LOCKED_PTH_BYTES = b"python313.zip\r\n.\r\n"
FORBIDDEN_PAYLOAD_MANIFESTS = {"normal.json", "bigbox.json", "trial.json"}


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest().upper()


def verify_source_tree(repo: Path, manifest: dict) -> None:
    """New curated manifests bind the installer as well as compiled inputs."""
    if manifest.get("schema") != 2:
        return  # Historical runtime manifests retain their original inventory contract.
    inputs = manifest.get("source_tree", {})
    required = {"harness/bigbox_install.py", "harness/shim_install.py", "harness/package_normal.py",
                "tools/build.py", "LICENSE", "README.md"}
    if not required.issubset(inputs):
        raise ValueError("Curated source manifest is incomplete")
    for name, expected in inputs.items():
        rel = Path(name)
        source = (repo / rel).resolve()
        if rel.is_absolute() or ".." in rel.parts or ":" in name or not source.is_relative_to(repo):
            raise ValueError("Invalid source manifest path")
        if not source.is_file() or sha(source) != expected:  # guard:source-tree-binding
            raise ValueError("Build/source mismatch: " + name)


def recursive_inventory(root: Path) -> dict[str, str]:
    if root.is_symlink() or (root.lstat().st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT):
        raise ValueError("Payload reparse point refused")  # guard:intake-reparse
    records: dict[str, str] = {}
    for directory, dirs, files in os.walk(root, followlinks=False):
        for name in dirs + files:
            path = Path(directory) / name
            if path.is_symlink() or (path.lstat().st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT):  # guard:intake-reparse
                raise ValueError("Payload reparse point refused: " + str(path))
        for name in files:
            path = Path(directory) / name
            records[path.relative_to(root).as_posix()] = sha(path)
    return dict(sorted(records.items()))


def require_flat_payload(root: Path, expected: dict[str, str] | None = None) -> dict[str, str]:
    actual = recursive_inventory(root)
    if any("/" in rel for rel in actual) or any(p.is_dir() for p in root.iterdir()):  # guard:intake-flat
        raise ValueError("Nested payload entries refused: flat runtime required")
    for forbidden in FORBIDDEN_PAYLOAD_MANIFESTS:
        if forbidden in actual:
            raise ValueError(f"Portable payload must not contain pre-generated {forbidden}")
    if any(rel.lower().endswith(".pdb") for rel in actual):
        raise ValueError("Debug symbols (.pdb) refused in runtime payload")
    if expected is not None and actual != expected:  # guard:intake-equality
        raise ValueError("Payload file-set/hash equality failed")
    return actual


def package_cmd_bytes(action: str, pin: str) -> bytes:
    lines = [
        "@echo off",
        "setlocal",
        'set "LB_ROOT=%~1"',
        'if "%LB_ROOT%"=="" set "LB_ROOT=%CONTROLLERLOBBY_LAUNCHBOX_ROOT%"',
        'if "%LB_ROOT%"=="" set /p "LB_ROOT=Enter LaunchBox root folder (e.g. D:\\LaunchBox): "',
        'if "%LB_ROOT%"=="" (',
        "  echo Error: LaunchBox root folder is required.",
        "  exit /b 3",
        ")",
    ]
    if action == "discover":
        lines.append(
            '"%~dp0python\\python.exe" -I -B "%~dp0bigbox_install.py" discover --receipt "%~dp0." --root "%LB_ROOT%"'
        )
    else:
        lines.append(
            '"%~dp0python\\python.exe" -I -B "%~dp0bigbox_install.py" '
            + action
            + ' --receipt "%~dp0." --root "%LB_ROOT%" --package-sha256 '
            + pin
        )
    lines.extend([
        'set "result=%errorlevel%"',
        'if "%CONTROLLERLOBBY_NO_PAUSE%"=="" pause',
        "exit /b %result%",
    ])
    return ("\r\n".join(lines) + "\r\n").encode("utf-8")


def populate_python_runtime(python_src: Path, dest_python: Path) -> None:
    if python_src.is_file() and python_src.suffix.lower() == ".zip":
        actual_zip_sha = sha(python_src)
        if actual_zip_sha != OFFICIAL_PYTHON_EMBED_ZIP_SHA256:
            raise RuntimeError(
                f"Embeddable Python archive SHA-256 mismatch: expected {OFFICIAL_PYTHON_EMBED_ZIP_SHA256}, got {actual_zip_sha}"
            )
        dest_python.mkdir(parents=True)
        with zipfile.ZipFile(python_src, "r") as zf:
            zf.extractall(dest_python)
    elif python_src.is_dir():
        shutil.copytree(python_src, dest_python)
    else:
        raise RuntimeError(f"Missing --python-embed zip or directory: {python_src}")

    for req in ("python.exe", "python313.dll", "python313.zip", "LICENSE.txt"):
        if not (dest_python / req).is_file():
            raise RuntimeError(f"Bundled Python runtime missing required file: {req}")

    # Lock python313._pth so user site-packages and PYTHONPATH cannot leak into isolated execution.
    (dest_python / "python313._pth").write_bytes(LOCKED_PTH_BYTES)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", required=True)
    ap.add_argument("--build", required=True, help="Runtime candidate directory (containing payload/ and runtime-payload-hashes.json) or synthetic build dir")
    ap.add_argument("--output", required=True)
    ap.add_argument(
        "--python-embed",
        default=os.environ.get("CONTROLLERLOBBY_PYTHON_EMBED", ""),
        help="Path to python-3.13.1-embed-amd64.zip or extracted directory",
    )
    ap.add_argument("--runtime-hashes", default="", help="Optional path to runtime-payload-hashes.json")
    ap.add_argument("--expected-inventory-sha256", default="", help="Optional expected SHA-256 of runtime-payload-hashes.json")
    ap.add_argument("--expected-source-manifest-sha256", default="", help="Optional expected SHA-256 of payload/source-manifest.json")
    # Retained as optional no-op flags for legacy caller compatibility; portable packages never read owner paths.
    ap.add_argument("--references", required=False, default="")
    ap.add_argument("--launchbox", required=False, default="")
    a = ap.parse_args()

    repo = Path(a.repo).resolve()
    build = Path(a.build).resolve()
    out = Path(a.output).resolve()
    if not a.python_embed:
        raise RuntimeError("Pass --python-embed <path to python-3.13.1-embed-amd64.zip or dir>")
    python_src = Path(a.python_embed).resolve()

    if out.exists():
        raise RuntimeError("Output already exists; use a new immutable package directory")

    src_payload = build / "payload" if (build / "payload").exists() else build
    hashes_file = Path(a.runtime_hashes).resolve() if a.runtime_hashes else (build / "runtime-payload-hashes.json")

    expected_inventory: dict[str, str] | None = None
    inventory_sha256 = ""
    if hashes_file.is_file():
        inventory_sha256 = sha(hashes_file)
        if a.expected_inventory_sha256 and inventory_sha256 != a.expected_inventory_sha256.upper():
            raise RuntimeError(
                f"runtime-payload-hashes.json SHA-256 mismatch: expected {a.expected_inventory_sha256.upper()}, got {inventory_sha256}"
            )
        raw_inv = json.loads(hashes_file.read_bytes())
        expected_inventory = {str(k): str(v).upper() for k, v in sorted(raw_inv.items())}
        require_flat_payload(src_payload, expected_inventory)
        if "source-manifest.json" not in expected_inventory:
            raise RuntimeError("Accepted runtime payload must include pinned source-manifest.json")
        sm_sha = sha(src_payload / "source-manifest.json")
        if a.expected_source_manifest_sha256 and sm_sha != a.expected_source_manifest_sha256.upper():
            raise RuntimeError(
                f"source-manifest.json SHA-256 mismatch: expected {a.expected_source_manifest_sha256.upper()}, got {sm_sha}"
            )
        source_manifest = json.loads((src_payload / "source-manifest.json").read_bytes())
        verify_source_tree(repo, source_manifest)
        commit = source_manifest.get("base_commit", "")
        bound_count = len(expected_inventory)
    else:
        # Synthetic build directory mode (validates build/source against repo and synthesizes source-manifest.json if absent).
        require_flat_payload(src_payload)
        inputs = {}
        if (build / "source").is_dir():
            for p in sorted((build / "source").rglob("*")):
                rel = p.relative_to(build / "source")
                if not p.is_file() or any(x in ("bin", "obj", "__pycache__") for x in rel.parts):
                    continue
                current = repo / rel
                if not current.is_file() or sha(current) != sha(p):
                    raise RuntimeError("Build/source mismatch: " + str(rel))
                inputs[str(rel)] = sha(p)
        if not inputs and not (src_payload / "source-manifest.json").is_file():
            raise RuntimeError("Build snapshot missing")
        commit = subprocess.check_output(["git", "-C", str(repo), "rev-parse", "HEAD"], text=True).strip()
        bound_count = len(inputs)

    out.mkdir(parents=True)

    # 1. Copy runtime payload and preserve accepted source-manifest.json byte-for-byte.
    dest_payload = out / "payload"
    shutil.copytree(src_payload, dest_payload)
    if expected_inventory is None and not (dest_payload / "source-manifest.json").is_file():
        source_manifest = {
            "Commit": commit,
            "Candidate": True,
            "Scope": "portable beta-1 installer package; MAME 0.279 and RetroArch only",
            "Files": inputs,
        }
        (dest_payload / "source-manifest.json").write_text(
            json.dumps(source_manifest, indent=2) + "\n", encoding="utf-8"
        )

    verified_payload_map = require_flat_payload(dest_payload, expected_inventory)
    if "ControllerLobby.BigBox.exe" not in verified_payload_map or "source-manifest.json" not in verified_payload_map:
        raise RuntimeError("Payload missing ControllerLobby.BigBox.exe or source-manifest.json")

    # 2. Bundle official Windows x64 embeddable Python 3.13.1 runtime.
    dest_python = out / "python"
    populate_python_runtime(python_src, dest_python)

    # 3. Copy installer transaction scripts.
    for script in ("bigbox_install.py", "shim_install.py", "setup_bridge.py"):
        shutil.copyfile(repo / "harness" / script, out / script)

    # 4. Copy Beta 1 documentation and third-party license notices.
    dest_docs = out / "docs"
    dest_docs.mkdir()
    for doc_name in (
        "BETA_SETUP_AND_RECOVERY.md",
        "BETA_COMPATIBILITY_MATRIX.md",
        "BETA_RELEASE_NOTES.md",
        "BETA_ISSUE_TEMPLATE.md",
        "THIRD_PARTY_NOTICES.md",
        "MAME_DEFAULTS_LICENSE.txt",
    ):
        src_doc = repo / "docs" / doc_name
        if src_doc.is_file():
            shutil.copyfile(src_doc, dest_docs / doc_name)

    for name in ("LICENSE", "README.md"):
        shutil.copyfile(repo / name, out / name)

    dest_notices = out / "notices"
    dest_notices.mkdir()
    shutil.copyfile(dest_python / "LICENSE.txt", dest_notices / "PYTHON_LICENSE.txt")
    for notice_name in ("THIRD_PARTY_NOTICES.md", "DOTNET_LICENSE.txt", "DOTNET_THIRD_PARTY_NOTICES.txt", "MAME_DEFAULTS_LICENSE.txt"):
        if (repo / "docs" / notice_name).is_file():
            shutil.copyfile(repo / "docs" / notice_name, dest_notices / notice_name)

    # 5. Enumerate PayloadFiles and DistributionFiles for Schema 2 package.json.
    payload_files = [
        {
            "Source": "payload\\" + name,
            "Name": name,
            "Sha256": file_sha,
        }
        for name, file_sha in verified_payload_map.items()
    ]

    dist_files = []
    for p in sorted(out.rglob("*")):
        if not p.is_file():
            continue
        rel = p.relative_to(out)
        if rel.parts[0] == "payload":
            continue
        dist_files.append({
            "Path": str(rel).replace("/", "\\"),
            "Sha256": sha(p),
        })

    pkg_obj = {
        "Schema": 2,
        "PackageVersion": "0.1.0-beta.1",
        "Architecture": "win-x64",
        "AllowedKinds": ["retroarch", "mame"],
        "RuntimeProvenance": {
            "Layout": "flat-self-contained-win-x64",
            "FileCount": len(payload_files),
            "InventorySha256": inventory_sha256,
            "SourceManifestSha256": verified_payload_map["source-manifest.json"],
        },
        "PythonRuntime": {
            "Version": "3.13.1",
            "Distribution": "python-3.13.1-embed-amd64.zip",
            "ArchiveSha256": OFFICIAL_PYTHON_EMBED_ZIP_SHA256,
            "RelativeExecutable": "python\\python.exe",
        },
        "PayloadFiles": payload_files,
        "DistributionFiles": dist_files,
    }
    (out / "package.json").write_text(json.dumps(pkg_obj, indent=2) + "\n", encoding="utf-8")
    pin = sha(out / "package.json")

    # 6. Write portable .cmd launchers (no hardcoded LaunchBox root or system Python path).
    for title, action in (
        ("Discover emulators", "discover"),
        ("Preview installation", "preview"),
        ("Install", "install"),
        ("Verify installation", "verify"),
        ("Upgrade or Repair", "repair"),
        ("Uninstall", "remove"),
    ):
        (out / f"{title}.cmd").write_bytes(package_cmd_bytes(action, pin))

    # 7. Write PACKAGE_HASHES.json pinning every file in the distribution.
    hashes = {
        str(p.relative_to(out)).replace("/", "\\"): sha(p)
        for p in sorted(out.rglob("*"))
        if p.is_file() and p.name != "PACKAGE_HASHES.json"
    }
    (out / "PACKAGE_HASHES.json").write_text(json.dumps(hashes, indent=2) + "\n", encoding="utf-8")

    print(out)
    print("PACKAGE_SHA256=" + pin)
    print("SOURCE_COMMIT=" + commit)
    print("Bound build inputs=" + str(bound_count))


if __name__ == "__main__":
    main()
