"""Exercise the Schema 2 portable Beta 1 installer, XInput reader provider probe,
persistent ControllerLobby.Setup management copy, post-update uninstall, explicit
repair, junction/reparse confinement, and executable causal mutation controls in
isolated synthetic non-owner LaunchBox roots.
"""
from pathlib import Path
import ctypes, hashlib, json, os, shutil, subprocess, sys
from ctypes import wintypes

sys.dont_write_bytecode = True
repo = Path(__file__).resolve().parent.parent
out = Path(sys.argv[1]).resolve()
out.mkdir(parents=True, exist_ok=True)
mutation = sys.argv[2] if len(sys.argv) > 2 else None
python_embed_arg = sys.argv[3] if len(sys.argv) > 3 else os.environ.get("CONTROLLERLOBBY_PYTHON_EMBED")
if not python_embed_arg:
    raise SystemExit("Pass the official Python embeddable ZIP as argument 3 or CONTROLLERLOBBY_PYTHON_EMBED")
python_embed = Path(python_embed_arg).resolve()
runtime_arg = os.environ.get("CONTROLLERLOBBY_TEST_RUNTIME")
if not runtime_arg:
    raise SystemExit("CONTROLLERLOBBY_TEST_RUNTIME must name the runtime produced by tools/build.py")

RETRO_ID = "aaaa1111-2222-3333-4444-555566667777"
MAME_ID = "bbbb1111-2222-3333-4444-555566667777"
DOLPHIN_ID = "cccc1111-2222-3333-4444-555566667777"
RETRO_FOLDER = r"Emulators\RetroArch-Portable"
MAME_FOLDER = r"Emulators\MAME-0.279"
DOLPHIN_FOLDER = r"Emulators\Dolphin-x64"


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest().upper()


def tree(p: Path) -> dict[str, str]:
    return {str(f.relative_to(p)): sha(f) for f in sorted(p.rglob("*")) if f.is_file()}


def make_xml(entries_spec: list[tuple[str, str, str]]) -> bytes:
    body = "".join(
        f"  <Emulator>\r\n    <ID>{eid}</ID>\r\n    <Title>{title}</Title>\r\n    <ApplicationPath>{app_path}</ApplicationPath>\r\n  </Emulator>\r\n"
        for eid, title, app_path in entries_spec
    )
    return f"<LaunchBox>\r\n{body}</LaunchBox>\r\n".encode("utf-8")


# Build one shared base package directory using package_normal.py so individual fixtures can clone it rapidly.
BASE_BUILD = out / "_base_build"
BASE_PKG = out / "_base_package"


def ensure_base_package() -> Path:
    if BASE_PKG.exists():
        return BASE_PKG
    src_dir = BASE_BUILD / "source" / "harness"
    src_dir.mkdir(parents=True, exist_ok=True)
    for f in ("bigbox_install.py", "shim_install.py", "package_normal.py"):
        shutil.copyfile(repo / "harness" / f, src_dir / f)
    payload_dir = BASE_BUILD / "payload"
    payload_dir.mkdir(parents=True, exist_ok=True)
    (payload_dir / "ControllerLobby.BigBox.exe").write_bytes(b"#!verify-only-exit:0\r\nsynthetic-bigbox-exe")
    (payload_dir / "ControllerLobby.Core.dll").write_bytes(b"synthetic-core-dll-v1")
    cmd = [
        sys.executable,
        "-I",
        str(repo / "harness" / "package_normal.py"),
        "--repo",
        str(repo),
        "--build",
        str(BASE_BUILD),
        "--output",
        str(BASE_PKG),
        "--python-embed",
        str(python_embed),
    ]
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=60)
    if r.returncode != 0:
        raise RuntimeError(f"package_normal.py failed:\n{r.stdout}\n{r.stderr}")
    return BASE_PKG


def apply_mutation(pkg_dir: Path, mut: str) -> None:
    bi = pkg_dir / "bigbox_install.py"
    si = pkg_dir / "shim_install.py"
    bs = bi.read_text(encoding="utf-8")
    ss = si.read_text(encoding="utf-8")
    if mut == "verify-only-failure-before-redirection-rolls-back":
        assert "run_verify_only(exe_deployed)  # guard:verify-only-gate" in bs
        bs = bs.replace("run_verify_only(exe_deployed)  # guard:verify-only-gate", "pass")
    elif mut == "local-input-change-before-redirection-rolls-back":
        target = 'if tx.sha(r["ExecutablePath"])!=r["ExecutableSha256"]:  # guard:local-input-recheck'
        assert target in bs
        bs = bs.replace(target, "if False:")
    elif mut == "reader-probe-provider-mismatch-refused":
        target = 'if actual.casefold()!=path.casefold():raise tx.Refused("XInputGetState provider mismatch: "+actual)  # guard:reader-provider'
        assert target in bs
        bs = bs.replace(target, "pass")
    elif mut == "repair-refuses-tampered-payload-and-preserves-foreign-edits":
        t1 = "if fp.exists():  # guard:repair-foreign"
        assert t1 in bs
        bs = bs.replace(t1, "if False:")
    elif mut == "repair-real-process-exit-recovery":
        t1 = "if True:  # guard:repair-reconcile-interrupted"
        t2 = "if not dst_ok:  # guard:repair-reconcile-foreign"
        assert t1 in bs and t2 in bs
        bs = bs.replace(t1, "if False:").replace(t2, "if False:")
    elif mut == "cleanup-delete-lock-and-mid-cleanup-process-exit-retry":
        t1 = "if True:  # guard:preserve-recovery-runner"
        t2 = "if True:  # guard:precheck-delete-lock"
        assert t1 in bs and t2 in bs
        bs = bs.replace(t1, "if False:").replace(t2, "if False:")
    elif mut == "journal-validation-precedes-recovery-mutation":
        t1 = "if True:  # guard:validate-journal-before-reconcile"
        assert t1 in bs
        bs = bs.replace(t1, "if False:")
    elif mut == "canonical-extra-field-stage-rejected":
        t1 = "if True:  # guard:reject-unknown-manifest-fields"
        assert t1 in bs
        bs = bs.replace(t1, "if False:")
    elif mut == "reparse-and-junction-confinement":
        t_xml = 'if p.is_symlink() or (getattr(st, "st_file_attributes", 0) & 0x400):  # guard:reparse-xml'
        assert t_xml in ss
        ss = ss.replace(t_xml, "if False:").replace(
            'if not target_p.resolve().is_relative_to(root_resolved):',
            'if False:',
        )
        bs = bs.replace(
            'if getattr(os.lstat(ancestor),"st_file_attributes",0)&0x400 or getattr(ancestor.stat(),"st_file_attributes",0)&0x400:raise tx.Refused("Reparse package destination")',
            "if False: pass",
      ).replace(
            "if not p.resolve().is_relative_to(root):raise tx.Refused(\"Package path escapes root\")",
            "if False: pass",
        )
    elif mut == "payload-layout-and-equality-guards":
        t_flat = 'if any("/" in rel for rel in actual_payload) or any(p.is_dir() for p in payload_dir.iterdir()):  # guard:payload-flat'
        t_eq = "if actual_payload!=expected_payload:  # guard:payload-equality"
        assert t_flat in bs and t_eq in bs
        bs = bs.replace(t_flat, "if False:").replace(t_eq, "if False:")
    elif mut == "excluded-platforms-wrappers-and-ambiguous-entries-refused":
        t_live = "live_by_kind={k:[r for r in by_kind[k] if is_live_in_root(r)] for k in ALLOWED_KINDS}  # guard:ignore-dead-duplicate-entries"
        assert t_live in bs
        bs = bs.replace(t_live, "live_by_kind=by_kind")
    else:
        raise RuntimeError("Unknown mutation: " + mut)
    bi.write_text(bs, encoding="utf-8")
    si.write_text(ss, encoding="utf-8")
    # Re-pin distribution hashes in package.json and PACKAGE_HASHES.json so package integrity check allows the mutated script to execute.
    pkg_json = json.loads((pkg_dir / "package.json").read_bytes())
    for df in pkg_json["DistributionFiles"]:
        if df["Path"] in ("bigbox_install.py", "shim_install.py"):
            df["Sha256"] = sha(pkg_dir / df["Path"])
    (pkg_dir / "package.json").write_text(json.dumps(pkg_json, indent=2) + "\n", encoding="utf-8")
    hashes = {
        str(p.relative_to(pkg_dir)).replace("/", "\\"): sha(p)
        for p in sorted(pkg_dir.rglob("*"))
        if p.is_file() and p.name != "PACKAGE_HASHES.json"
    }
    (pkg_dir / "PACKAGE_HASHES.json").write_text(json.dumps(hashes, indent=2) + "\n", encoding="utf-8")


def fixture(name: str):
    base_pkg = ensure_base_package()
    short_id = "c_" + hashlib.sha256(name.encode("utf-8")).hexdigest()[:8]
    case = out / short_id
    if case.exists():
        shutil.rmtree(case)
    root = case / "LB"
    data = root / "Data"
    data.mkdir(parents=True)
    xml = make_xml([
        (RETRO_ID, "RetroArch Portable", RETRO_FOLDER + r"\retroarch.exe"),
        (MAME_ID, "MAME Arcade", MAME_FOLDER + r"\mame.exe"),
        (DOLPHIN_ID, "Dolphin Standalone", DOLPHIN_FOLDER + r"\Dolphin.exe"),
    ])
    (data / "Emulators.xml").write_bytes(xml)
    for folder, exe_name in (
        (RETRO_FOLDER, "retroarch.exe"),
        (MAME_FOLDER, "mame.exe"),
        (DOLPHIN_FOLDER, "Dolphin.exe"),
    ):
        d = root / folder
        d.mkdir(parents=True)
        (d / exe_name).write_bytes(f"binary-{exe_name}-v1".encode("utf-8"))

    package = case / "package"
    shutil.copytree(base_pkg, package)
    if mutation:
        apply_mutation(package, mutation)
    return case, root, package


def bundled_py(folder: Path) -> str:
    return str(folder / "python" / "python.exe")


def run_cli(
    case: Path,
    runner_dir: Path,
    root: Path,
    action: str,
    expected: int = 0,
    envextra: dict | None = None,
    entries: str | None = None,
    pin: str | None = None,
):
    env = dict(os.environ)
    env.update({
        "SHIM_INSTALL_TEST_FRONTEND": "closed",
        "SHIM_INSTALL_TEST_FAIL_AT": "",
        "SHIM_INSTALL_TEST_INJECT": "",
        "CONTROLLERLOBBY_NO_PAUSE": "1",
    })
    if envextra:
        env.update(envextra)
    runner_exec_dir = runner_dir
    if action == "remove" and runner_dir.resolve() == (root / "ControllerLobby.Setup").resolve():
        runner_exec_dir = case / f"_relocated_runner_{len(list(case.glob('*.log')))}"
        if runner_exec_dir.exists():
            shutil.rmtree(runner_exec_dir)
        shutil.copytree(runner_dir / "python", runner_exec_dir / "python")
        for s in ("bigbox_install.py", "shim_install.py"):
            shutil.copyfile(runner_dir / s, runner_exec_dir / s)
    cmd = [
        bundled_py(runner_exec_dir),
        "-I",
        "-B",
        str(runner_exec_dir / "bigbox_install.py"),
        action,
        "--receipt",
        str(runner_dir),
        "--root",
        str(root),
    ]
    if pin is not None:
        cmd.extend(["--package-sha256", pin])
    elif (runner_dir / "package.json").is_file():
        cmd.extend(["--package-sha256", sha(runner_dir / "package.json")])
    if entries is not None:
        cmd.extend(["--entries", entries])
    p = subprocess.run(cmd, env=env, capture_output=True, timeout=30)
    log = case / f"{action}-{len(list(case.glob('*.log')))}.log"
    log.write_bytes(p.stdout + p.stderr)
    assert p.returncode == expected, (
        action,
        p.returncode,
        expected,
        p.stdout.decode(errors="replace"),
        p.stderr.decode(errors="replace"),
    )
    return p


def case_package_builder_and_cmd_launchers(c: Path, r: Path, p: Path):
    # 1. Verify PACKAGE_HASHES.json covers all files and matches hashes.
    hashes = json.loads((p / "PACKAGE_HASHES.json").read_bytes())
    actual_files = {
        str(f.relative_to(p)).replace("/", "\\"): sha(f)
        for f in sorted(p.rglob("*"))
        if f.is_file() and f.name != "PACKAGE_HASHES.json"
    }
    assert hashes == actual_files, "PACKAGE_HASHES.json mismatch"
    assert (p / "LICENSE").is_file()
    assert (p / "notices/MAME_DEFAULTS_LICENSE.txt").is_file()
    assert (p / "python" / "python313._pth").read_bytes() == b"python313.zip\r\n.\r\n"

    # 2. Scan all text/json/cmd/md files in package for forbidden owner strings.
    forbidden_tokens = (
        "c:\\users\\developer",
        "goldeneye",
        "tmnt2",
        "mame-package-01",
    )
    for f in p.rglob("*"):
        if f.is_file() and f.suffix.lower() in (".json", ".cmd", ".md"):
            text_low = f.read_text(encoding="utf-8", errors="replace").lower()
            for tok in forbidden_tokens:
                assert tok not in text_low, f"Forbidden owner token {tok!r} found in {f}"

    # 3. Exercise .cmd launchers through cmd.exe /c using bundled Python.
    before = tree(r)
    env = dict(os.environ)
    env.update({
        "SHIM_INSTALL_TEST_FRONTEND": "closed",
        "SHIM_INSTALL_TEST_FAIL_AT": "",
        "SHIM_INSTALL_TEST_INJECT": "",
        "CONTROLLERLOBBY_NO_PAUSE": "1",
    })
    for cmd_name in ("Discover emulators.cmd", "Preview installation.cmd"):
        res = subprocess.run(f'cmd.exe /d /s /c ""{p / cmd_name}" "{r}""', env=env, capture_output=True, timeout=30)
        assert res.returncode == 0, (cmd_name, res.stdout.decode(errors="replace"), res.stderr.decode(errors="replace"))
    assert tree(r) == before

    res = subprocess.run(f'cmd.exe /d /s /c ""{p / "Install.cmd"}" "{r}""', env=env, capture_output=True, timeout=30)
    assert res.returncode == 0, res.stdout.decode(errors="replace") + res.stderr.decode(errors="replace")
    assert b"Finishing install, please wait..." in res.stdout

    # Verify generated normal.json and bigbox.json in both RetroArch and MAME folders.
    for folder, kind in ((RETRO_FOLDER, "retroarch"), (MAME_FOLDER, "mame")):
        cb_dir = r / folder / "ControllerLobby.BigBox"
        n_cfg = json.loads((cb_dir / "normal.json").read_bytes())
        b_cfg = json.loads((cb_dir / "bigbox.json").read_bytes())
        assert n_cfg["Kind"] == kind
        assert n_cfg["ReferenceArguments"] == []
        assert n_cfg["ReferenceBinaries"] == []
        assert n_cfg["ReferenceConfiguration"] is None
        assert n_cfg["RetroArch"] is None
        assert n_cfg["Reader"]["FunctionModulePath"].casefold() == n_cfg["Reader"]["LibraryPath"].casefold()
        assert b_cfg["ManifestSha256"] == sha(cb_dir / "normal.json")
    assert not (r / DOLPHIN_FOLDER / "ControllerLobby.BigBox").exists()

    for cmd_name in ("Verify installation.cmd", "Upgrade or Repair.cmd", "Uninstall.cmd"):
        res = subprocess.run(f'cmd.exe /d /s /c ""{p / cmd_name}" "{r}""', env=env, capture_output=True, timeout=30)
        assert res.returncode == 0, (cmd_name, res.stdout.decode(errors="replace"), res.stderr.decode(errors="replace"))
    assert tree(r) == before


def case_deleted_download_package_verify_repair_remove(c: Path, r: Path, p: Path):
    before = tree(r)
    run_cli(c, p, r, "install", 0)
    # Delete the downloaded package folder completely!
    shutil.rmtree(p)
    assert not p.exists()
    setup_dir = r / "ControllerLobby.Setup"
    assert setup_dir.is_dir()

    # Run verify and repair directly from ControllerLobby.Setup.
    run_cli(c, setup_dir, r, "verify", 0)
    run_cli(c, setup_dir, r, "repair", 0)

    # Run Uninstall.cmd from inside ControllerLobby.Setup (tests self-relocation to %TEMP% and full directory deletion).
    env = dict(os.environ)
    env.update({
        "SHIM_INSTALL_TEST_FRONTEND": "closed",
        "SHIM_INSTALL_TEST_FAIL_AT": "",
        "SHIM_INSTALL_TEST_INJECT": "",
        "CONTROLLERLOBBY_NO_PAUSE": "1",
    })
    res = subprocess.run(["cmd.exe", "/d", "/c", str(setup_dir / "Uninstall.cmd")], env=env, capture_output=True, timeout=30)
    assert res.returncode == 0, res.stdout.decode(errors="replace") + res.stderr.decode(errors="replace")
    assert not setup_dir.exists()
    assert tree(r) == before


def case_uninstall_after_emulator_and_os_updates(c: Path, r: Path, p: Path):
    run_cli(c, p, r, "install", 0)
    shutil.rmtree(p)
    setup_dir = r / "ControllerLobby.Setup"

    # Simulate updating both retroarch.exe and mame.exe after install.
    (r / RETRO_FOLDER / "retroarch.exe").write_bytes(b"retroarch-updated-v2.0")
    (r / MAME_FOLDER / "mame.exe").write_bytes(b"mame-updated-v0.280")

    # Verify must report that the emulator binary changed since install/repair (exit 5).
    v_res = run_cli(c, setup_dir, r, "verify", 5)
    assert b"MISMATCH emulator binary changed since installation" in v_res.stdout

    # Uninstall MUST succeed (exit 0) without requiring the old emulator hashes or XInput hashes!
    run_cli(c, setup_dir, r, "remove", 0)
    assert not setup_dir.exists()
    assert not (r / RETRO_FOLDER / "ControllerLobby.BigBox").exists()
    assert not (r / MAME_FOLDER / "ControllerLobby.BigBox").exists()
    assert (r / RETRO_FOLDER / "retroarch.exe").read_bytes() == b"retroarch-updated-v2.0"
    assert (r / MAME_FOLDER / "mame.exe").read_bytes() == b"mame-updated-v0.280"


def case_explicit_repair_revalidates_and_restores_missing_payload(c: Path, r: Path, p: Path):
    run_cli(c, p, r, "install", 0)
    shutil.rmtree(p)
    setup_dir = r / "ControllerLobby.Setup"

    # Update retroarch.exe and delete an installed immutable payload DLL.
    (r / RETRO_FOLDER / "retroarch.exe").write_bytes(b"retroarch-updated-v2.1")
    missing_dll = r / RETRO_FOLDER / "ControllerLobby.BigBox" / "ControllerLobby.Core.dll"
    missing_dll.unlink()
    assert not missing_dll.exists()

    # Verify fails before repair.
    run_cli(c, setup_dir, r, "verify", 5)

    # Repair restores the missing DLL from ControllerLobby.Setup\payload and updates normal.json/bigbox.json to Gen 2.
    rep = run_cli(c, setup_dir, r, "repair", 0)
    assert b"repaired generation 2" in rep.stdout
    assert missing_dll.is_file() and missing_dll.read_bytes() == b"synthetic-core-dll-v1"
    active = json.loads((setup_dir / "active-plan.json").read_bytes())
    assert active["Generation"] == 2

    # Verify now passes!
    run_cli(c, setup_dir, r, "verify", 0)

    # Remove cleanly uninstalls Generation 2 manifests and restores LaunchBox root.
    run_cli(c, setup_dir, r, "remove", 0)
    assert not setup_dir.exists()
    assert not (r / RETRO_FOLDER / "ControllerLobby.BigBox").exists()


def case_repair_refuses_tampered_payload_and_preserves_foreign_edits(c: Path, r: Path, p: Path):
    run_cli(c, p, r, "install", 0)
    setup_dir = r / "ControllerLobby.Setup"

    # 1. Modify deployed immutable payload file with foreign bytes -> repair must refuse and preserve bytes.
    dll = r / RETRO_FOLDER / "ControllerLobby.BigBox" / "ControllerLobby.Core.dll"
    orig_dll_bytes = dll.read_bytes()
    dll.write_bytes(b"foreign-user-modified-dll")
    run_cli(c, setup_dir, r, "repair", 3)
    assert dll.read_bytes() == b"foreign-user-modified-dll"
    dll.write_bytes(orig_dll_bytes)

    # 2. Modify deployed normal.json with foreign bytes -> repair must refuse and preserve bytes.
    normal_p = r / RETRO_FOLDER / "ControllerLobby.BigBox" / "normal.json"
    normal_p.write_bytes(b'{"foreign_user_edit": true}\n')
    run_cli(c, setup_dir, r, "repair", 3)
    assert normal_p.read_bytes() == b'{"foreign_user_edit": true}\n'


def case_repair_interruption_rollback(c: Path, r: Path, p: Path):
    run_cli(c, p, r, "install", 0)
    setup_dir = r / "ControllerLobby.Setup"
    normal_p = r / RETRO_FOLDER / "ControllerLobby.BigBox" / "normal.json"
    gen1_bytes = normal_p.read_bytes()
    (r / RETRO_FOLDER / "retroarch.exe").write_bytes(b"retroarch-v2-interrupted")

    for step in ("repair-after-staged", "repair-after-gen"):
        run_cli(c, setup_dir, r, "repair", 3, {"SHIM_INSTALL_TEST_FAIL_AT": step})
        assert normal_p.read_bytes() == gen1_bytes

    # Subsequent repair succeeds and remove cleans up completely.
    run_cli(c, setup_dir, r, "repair", 0)
    run_cli(c, setup_dir, r, "verify", 0)
    run_cli(c, setup_dir, r, "remove", 0)


def case_repair_real_process_exit_recovery(c: Path, r: Path, p: Path):
    before = tree(r)
    run_cli(c, p, r, "install", 0)
    setup_dir = r / "ControllerLobby.Setup"
    normal_p = r / RETRO_FOLDER / "ControllerLobby.BigBox" / "normal.json"
    pin = sha(p / "package.json")

    # Helper that runs repair in a real child process and terminates via os._exit(79) immediately after os.replace on normal.json (targeted process-exit probe).
    crash_wrapper = c / "crash_replace_runner.py"
    crash_wrapper.write_text(
        "from pathlib import Path\n"
        "import importlib.util, os, sys\n"
        "pkg, root, expected, target_name = sys.argv[1:5]\n"
        "spec = importlib.util.spec_from_file_location('inst', Path(pkg) / 'bigbox_install.py')\n"
        "mod = importlib.util.module_from_spec(spec); spec.loader.exec_module(mod)\n"
        "real_replace = os.replace\n"
        "def hook(src, dst, *a, **kw):\n"
        "    res = real_replace(src, dst, *a, **kw)\n"
        "    if Path(dst).name == target_name:\n"
        "        os._exit(79)\n"
        "    return res\n"
        "os.replace = hook\n"
        "sys.argv = [str(Path(pkg) / 'bigbox_install.py'), 'repair', '--receipt', pkg, '--root', root, '--package-sha256', expected]\n"
        "raise SystemExit(mod.main())\n",
        encoding="utf-8",
    )

    # 1. Real os._exit(79) during staging, after normal.json os.replace, after active-plan.json os.replace, and during GEN DONE.
    for idx, mode in enumerate(("stage", "normal-replace", "plan-replace", "gendone")):
        (r / RETRO_FOLDER / "retroarch.exe").write_bytes(f"retroarch-v2-exit-{idx}-{mode}".encode("utf-8"))
        if mode == "normal-replace":
            env = dict(os.environ)
            env.update({
                "SHIM_INSTALL_TEST_FRONTEND": "closed",
                "SHIM_INSTALL_TEST_FAIL_AT": "",
                "SHIM_INSTALL_TEST_INJECT": "",
                "CONTROLLERLOBBY_NO_PAUSE": "1",
            })
            cp = subprocess.run(
                [bundled_py(p), "-I", "-B", str(crash_wrapper), str(p), str(r), pin, "normal.json"],
                env=env,
                capture_output=True,
                timeout=30,
            )
            assert cp.returncode == 79, (mode, cp.returncode, cp.stdout, cp.stderr)
        else:
            inject_map = {
                "stage": "exit-repair-during-stage",
                "plan-replace": "exit-repair-after-plan-replace",
                "gendone": "exit-repair-during-gendone",
            }
            run_cli(c, p, r, "repair", 79, {"SHIM_INSTALL_TEST_INJECT": inject_map[mode]}, pin=pin)

        # Immediately after abrupt process exit, verify must report MISMATCH (exit 5).
        run_cli(c, p, r, "verify", 5, pin=pin)

        # If a foreign modification is made to normal.json while the generation is interrupted, repair must refuse (exit 3) and preserve it!
        crashed_normal_bytes = normal_p.read_bytes()
        normal_p.write_bytes(b'{"foreign_edit_during_interrupted_gen": true}\n')
        run_cli(c, p, r, "repair", 3, pin=pin)
        assert normal_p.read_bytes() == b'{"foreign_edit_during_interrupted_gen": true}\n'
        normal_p.write_bytes(crashed_normal_bytes)

        # Now run repair -> reconciles the interrupted generation (including owned .cl-*.part stages and preimages) and completes repair (exit 0).
        run_cli(c, p, r, "repair", 0, pin=pin)
        run_cli(c, p, r, "verify", 0, pin=pin)

    # 2. Also prove direct remove (without running repair first) right after normal.json os.replace os._exit(79) recovers and removes cleanly!
    (r / RETRO_FOLDER / "retroarch.exe").write_bytes(b"binary-retroarch.exe-v1")
    env = dict(os.environ)
    env.update({
        "SHIM_INSTALL_TEST_FRONTEND": "closed",
        "SHIM_INSTALL_TEST_FAIL_AT": "",
        "SHIM_INSTALL_TEST_INJECT": "",
        "CONTROLLERLOBBY_NO_PAUSE": "1",
    })
    cp2 = subprocess.run(
        [bundled_py(p), "-I", "-B", str(crash_wrapper), str(p), str(r), pin, "normal.json"],
        env=env,
        capture_output=True,
        timeout=30,
    )
    assert cp2.returncode == 79
    run_cli(c, p, r, "verify", 5, pin=pin)
    run_cli(c, p, r, "remove", 0, pin=pin)
    assert not normal_p.exists()
    assert not setup_dir.exists()
    assert tree(r) == before


def case_cleanup_delete_lock_and_mid_cleanup_process_exit_retry(c: Path, r: Path, p: Path):
    before = tree(r)
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    k32.CreateFileW.restype = wintypes.HANDLE
    k32.CloseHandle.argtypes = [wintypes.HANDLE]
    k32.CloseHandle.restype = wintypes.BOOL
    sys_buf = ctypes.create_unicode_buffer(32768)
    assert ctypes.windll.kernel32.GetSystemDirectoryW(sys_buf, len(sys_buf))
    sys32_dir = sys_buf.value

    required_mgmt = (
        "Uninstall.cmd",
        "Upgrade or Repair.cmd",
        "Verify installation.cmd",
        "python/python.exe",
        "bigbox_install.py",
        "shim_install.py",
        "package.json",
        "active-plan.json",
        "install-record.txt",
    )

    # 1. Readable but delete-locked active-plan.json (targeted process-exit probe) + second lock position (notices/THIRD_PARTY_NOTICES.md).
    for idx, locked_rel in enumerate(("active-plan.json", "notices/THIRD_PARTY_NOTICES.md")):
        pkg_copy = c / f"pkg_lock_{idx}"
        shutil.copytree(p, pkg_copy)
        pin = sha(pkg_copy / "package.json")
        local_app = c / f"appdata_{idx}"
        temp_dir = c / f"temp_{idx}"
        temp_dir.mkdir(parents=True, exist_ok=True)
        env_extra = {"LOCALAPPDATA": str(local_app), "TEMP": str(temp_dir), "TMP": str(temp_dir)}

        run_cli(c, pkg_copy, r, "install", 0, env_extra, pin=pin)
        # Delete the downloaded package so retry depends entirely on retained portable recovery assets!
        shutil.rmtree(pkg_copy)
        setup_dir = r / "ControllerLobby.Setup"

        h = k32.CreateFileW(str(setup_dir / locked_rel), 0x80000000, 1, None, 3, 0x80, None)
        assert h not in (None, 0, ctypes.c_void_p(-1).value)
        try:
            # Run remove while locked -> must exit 5 (RECOVERY INCOMPLETE) and keep all management files intact!
            run_cli(c, setup_dir, r, "remove", 5, env_extra, pin=pin)
            for req in required_mgmt:
                assert (setup_dir / req).exists(), f"Management file deleted despite lock on {locked_rel}: {req}"
        finally:
            k32.CloseHandle(h)

        # Retry via retained Uninstall.cmd with PATH restricted to System32 (modeling no system Python).
        retry_env = dict(os.environ)
        retry_env.update({
            "PATH": sys32_dir,
            "CONTROLLERLOBBY_NO_PAUSE": "1",
            "LOCALAPPDATA": str(local_app),
            "TEMP": str(temp_dir),
            "TMP": str(temp_dir),
            "SHIM_INSTALL_TEST_FRONTEND": "closed",
            "SHIM_INSTALL_TEST_FAIL_AT": "",
            "SHIM_INSTALL_TEST_INJECT": "",
        })
        u_res = subprocess.run([str(Path(sys32_dir) / "cmd.exe"), "/d", "/c", str(setup_dir / "Uninstall.cmd")], env=retry_env, capture_output=True, timeout=30)
        assert u_res.returncode == 0, (locked_rel, u_res.returncode, u_res.stdout.decode(errors="replace"), u_res.stderr.decode(errors="replace"))
        assert not setup_dir.exists()
        assert tree(r) == before

    # 2. Real os._exit(79) process death mid-cleanup AFTER ControllerLobby.Setup\python\python.exe is unlinked, retried with PATH=System32.
    pkg_exit = c / "pkg_exit_cleanup"
    shutil.copytree(p, pkg_exit)
    pin_exit = sha(pkg_exit / "package.json")
    local_app_exit = c / "appdata_exit"
    temp_exit = c / "temp_exit"
    temp_exit.mkdir(parents=True, exist_ok=True)
    env_exit = {"LOCALAPPDATA": str(local_app_exit), "TEMP": str(temp_exit), "TMP": str(temp_exit)}
    run_cli(c, pkg_exit, r, "install", 0, env_exit, pin=pin_exit)
    shutil.rmtree(pkg_exit)
    setup_dir = r / "ControllerLobby.Setup"

    run_cli(c, setup_dir, r, "remove", 79, {**env_exit, "SHIM_INSTALL_TEST_INJECT": "exit-cleanup-after-python"}, pin=pin_exit)
    assert not (setup_dir / "python" / "python.exe").exists(), "python.exe should have been unlinked before os._exit(79)"
    assert (setup_dir / "Uninstall.cmd").is_file(), "Uninstall.cmd must remain until cleanup finishes"

    retry_env = dict(os.environ)
    retry_env.update({
        "PATH": sys32_dir,
        "CONTROLLERLOBBY_NO_PAUSE": "1",
        "LOCALAPPDATA": str(local_app_exit),
        "TEMP": str(temp_exit),
        "TMP": str(temp_exit),
        "SHIM_INSTALL_TEST_FRONTEND": "closed",
        "SHIM_INSTALL_TEST_FAIL_AT": "",
        "SHIM_INSTALL_TEST_INJECT": "",
    })
    u_res2 = subprocess.run([str(Path(sys32_dir) / "cmd.exe"), "/d", "/c", str(setup_dir / "Uninstall.cmd")], env=retry_env, capture_output=True, timeout=30)
    assert u_res2.returncode == 0, (u_res2.returncode, u_res2.stdout.decode(errors="replace"), u_res2.stderr.decode(errors="replace"))
    assert not setup_dir.exists()
    assert tree(r) == before


def case_journal_validation_precedes_recovery_mutation(c: Path, r: Path, p: Path):
    before = tree(r)
    pin = sha(p / "package.json")
    run_cli(c, p, r, "install", 0, pin=pin)
    setup_dir = r / "ControllerLobby.Setup"
    rec_path = setup_dir / "install-record.txt"
    orig_rec = rec_path.read_text(encoding="utf-8")
    nonce = "0123456789abcdef0123456789abcdef"
    nonce2 = "fedcba9876543210fedcba9876543210"
    fake_new_sha = hashlib.sha256(b"planned-new\n").hexdigest().upper()

    # 1. Unplanned-target regression probe: unplanned outside-root owner.txt, owner.txt.cl-<nonce>.part, and owner.txt.cl-restore.tmp in both repair and remove.
    outside_dir = c / "outside_owner"
    outside_dir.mkdir(parents=True, exist_ok=True)
    assert not outside_dir.resolve().is_relative_to(r.resolve())
    owner_main = outside_dir / "owner.txt"
    owner_part = outside_dir / f"owner.txt.cl-{nonce}.part"
    owner_rest = outside_dir / "owner.txt.cl-restore.tmp"
    owner_main.write_bytes(b"owner-file\n")
    owner_part.write_bytes(b"owner-stage-shaped-file\n")
    owner_rest.write_bytes(b"owner-restore-shaped-file\n")
    expected_owner = {
        owner_main: sha(owner_main),
        owner_part: sha(owner_part),
        owner_rest: sha(owner_rest),
    }
    rec_path.write_text(
        orig_rec + f"GEN PENDING 2 {nonce} {expected_owner[owner_main]} {fake_new_sha} *{owner_main}\n",
        encoding="utf-8",
    )
    for act in ("repair", "remove"):
        run_cli(c, p, r, act, 3, pin=pin)
        for fp, exp_h in expected_owner.items():
            assert fp.is_file(), f"Unplanned file {fp.name} was deleted before journal validation during {act}!"
            assert sha(fp) == exp_h, f"Unplanned file {fp.name} was modified during {act}!"

    # 2. Unplanned in-root destination (Emulators\RetroArch-Portable\unplanned.txt) in both repair and remove.
    unplanned = r / RETRO_FOLDER / "unplanned.txt"
    unplanned_part = r / RETRO_FOLDER / f"unplanned.txt.cl-{nonce}.part"
    unplanned_rest = r / RETRO_FOLDER / "unplanned.txt.cl-restore.tmp"
    unplanned.write_bytes(b"unplanned-in-root\n")
    unplanned_part.write_bytes(b"unplanned-stage-bytes\n")
    unplanned_rest.write_bytes(b"unplanned-restore-bytes\n")
    exp_unplanned = {unplanned: sha(unplanned), unplanned_part: sha(unplanned_part), unplanned_rest: sha(unplanned_rest)}
    rec_path.write_text(
        orig_rec + f"GEN PENDING 2 {nonce} {exp_unplanned[unplanned]} {fake_new_sha} *{unplanned}\n",
        encoding="utf-8",
    )
    for act in ("repair", "remove"):
        run_cli(c, p, r, act, 3, pin=pin)
        for fp, exp_h in exp_unplanned.items():
            assert fp.is_file() and sha(fp) == exp_h, f"In-root unplanned file {fp.name} altered during {act}!"
    for fp in exp_unplanned:
        fp.unlink()

    # 3. Planned immutable payload destination (ControllerLobby.Core.dll) in GEN line in both repair and remove.
    dll = r / RETRO_FOLDER / "ControllerLobby.BigBox" / "ControllerLobby.Core.dll"
    dll_part = dll.with_name(f"ControllerLobby.Core.dll.cl-{nonce}.part")
    dll_rest = dll.with_name("ControllerLobby.Core.dll.cl-restore.tmp")
    dll_part.write_bytes(b"foreign-dll-stage\n")
    dll_rest.write_bytes(b"foreign-dll-restore\n")
    exp_dll = {dll: sha(dll), dll_part: sha(dll_part), dll_rest: sha(dll_rest)}
    rec_path.write_text(
        orig_rec + f"GEN PENDING 2 {nonce} {exp_dll[dll]} {fake_new_sha} *{dll}\n",
        encoding="utf-8",
    )
    for act in ("repair", "remove"):
        run_cli(c, p, r, act, 3, pin=pin)
        for fp, exp_h in exp_dll.items():
            assert fp.is_file() and sha(fp) == exp_h, f"Immutable payload stage/restore {fp.name} altered during {act}!"
    dll_part.unlink()
    dll_rest.unlink()

    # 4. Planned mutable destination (normal.json) with foreign .part (both mismatched hash AND forged matching hash) and .cl-restore.tmp in both repair and remove.
    normal_p = r / RETRO_FOLDER / "ControllerLobby.BigBox" / "normal.json"
    n_part = normal_p.with_name(f"normal.json.cl-{nonce}.part")
    n_rest = normal_p.with_name("normal.json.cl-restore.tmp")
    n_part.write_bytes(b"foreign-non-manifest-stage-bytes\n")
    n_rest.write_bytes(b"foreign-non-manifest-restore-bytes\n")
    exp_norm = {normal_p: sha(normal_p), n_part: sha(n_part), n_rest: sha(n_rest)}
    for candidate_new_sha in (fake_new_sha, exp_norm[n_part]):
        rec_path.write_text(
            orig_rec + f"GEN PENDING 2 {nonce} {exp_norm[normal_p]} {candidate_new_sha} *{normal_p}\n",
            encoding="utf-8",
        )
        for act in ("repair", "remove"):
            run_cli(c, p, r, act, 3, pin=pin)
            for fp, exp_h in exp_norm.items():
                assert fp.is_file() and sha(fp) == exp_h, f"Foreign stage/restore on normal.json ({fp.name}) altered during {act}!"

    # 4b. Canonical normal.json stage file with an unknown extra field (OwnerAnnotation) and matching new_sha in GEN PENDING 2 in both repair and remove.
    canon_extra_obj = json.loads(normal_p.read_bytes())
    canon_extra_obj["OwnerAnnotation"] = "foreign annotation; not produced by installer"
    n_part.write_bytes((json.dumps(canon_extra_obj, indent=2) + "\n").encode("utf-8"))
    exp_canon_extra = {normal_p: sha(normal_p), n_part: sha(n_part), n_rest: sha(n_rest)}
    rec_path.write_text(
        orig_rec + f"GEN PENDING 2 {nonce} {exp_canon_extra[normal_p]} {exp_canon_extra[n_part]} *{normal_p}\n",
        encoding="utf-8",
    )
    for act in ("repair", "remove"):
        run_cli(c, p, r, act, 3, pin=pin)
        for fp, exp_h in exp_canon_extra.items():
            assert fp.is_file() and sha(fp) == exp_h, f"Canonical extra-field stage/restore ({fp.name}) altered during {act}!"
    n_part.write_bytes(b"foreign-non-manifest-stage-bytes\n")

    # 5. Malformed and reordered GEN lines in both repair and remove.
    wrong_old = "F" * 64
    malformed_tails = [
        f"GEN STAGED 2 {nonce} {exp_norm[normal_p]} {fake_new_sha} *{normal_p}\n",
        f"GEN DONE 2 {fake_new_sha} *{normal_p}\n",
        f"GEN PENDING 2 {nonce} {wrong_old} {fake_new_sha} *{normal_p}\n",
        f"GEN PENDING 1 {nonce} {exp_norm[normal_p]} {fake_new_sha} *{normal_p}\n",
        (
            f"GEN PENDING 2 {nonce} {exp_norm[normal_p]} {fake_new_sha} *{normal_p}\n"
            f"GEN PENDING 3 {nonce2} {exp_norm[normal_p]} {fake_new_sha} *{normal_p}\n"
        ),
        f"GEN ABANDONED 2 {nonce} {exp_norm[normal_p]} *{normal_p}\n",
    ]
    for tail in malformed_tails:
        rec_path.write_text(orig_rec + tail, encoding="utf-8")
        for act in ("repair", "remove"):
            run_cli(c, p, r, act, 3, pin=pin)
            for fp, exp_h in exp_norm.items():
                assert fp.is_file() and sha(fp) == exp_h, f"File {fp.name} altered on malformed tail {tail!r} during {act}!"

    # 6. Directory reparse (NTFS junction) confinement on ControllerLobby.BigBox during open GEN PENDING 2 in both repair and remove.
    bb_dir = r / RETRO_FOLDER / "ControllerLobby.BigBox"
    outside_bb = c / "outside_bb_junction_target"
    shutil.move(str(bb_dir), str(outside_bb))
    subprocess.run(["cmd.exe", "/d", "/c", "mklink", "/J", str(bb_dir), str(outside_bb)], check=True, capture_output=True)
    try:
        rec_path.write_text(
            orig_rec + f"GEN PENDING 2 {nonce} {exp_norm[normal_p]} {fake_new_sha} *{normal_p}\n",
            encoding="utf-8",
        )
        before_outside_bb = tree(outside_bb)
        for act in ("repair", "remove"):
            run_cli(c, p, r, act, 3, pin=pin)
            assert tree(outside_bb) == before_outside_bb, f"Junction target modified during {act}!"
    finally:
        subprocess.run(["cmd.exe", "/d", "/c", "rmdir", str(bb_dir)], check=True, capture_output=True)
        shutil.move(str(outside_bb), str(bb_dir))

    # Clean up synthetic foreign stage/restore files and restore clean record -> verify repair, verify, and remove all succeed.
    n_part.unlink()
    n_rest.unlink()
    rec_path.write_text(orig_rec, encoding="utf-8")
    run_cli(c, p, r, "repair", 0, pin=pin)
    run_cli(c, p, r, "verify", 0, pin=pin)
    run_cli(c, p, r, "remove", 0, pin=pin)
    assert tree(r) == before


def case_verify_only_failure_before_redirection_rolls_back(c: Path, r: Path, p: Path):
    # Replace payload exe with one that fails --verify-only with exit 3, and update package hashes.
    exe = p / "payload" / "ControllerLobby.BigBox.exe"
    exe.write_bytes(b"#!verify-only-exit:3\r\nbroken-runtime-build")
    pkg_json = json.loads((p / "package.json").read_bytes())
    for pf in pkg_json["PayloadFiles"]:
        if pf["Name"] == "ControllerLobby.BigBox.exe":
            pf["Sha256"] = sha(exe)
    (p / "package.json").write_text(json.dumps(pkg_json, indent=2) + "\n", encoding="utf-8")
    hashes = {
        str(f.relative_to(p)).replace("/", "\\"): sha(f)
        for f in sorted(p.rglob("*"))
        if f.is_file() and f.name != "PACKAGE_HASHES.json"
    }
    (p / "PACKAGE_HASHES.json").write_text(json.dumps(hashes, indent=2) + "\n", encoding="utf-8")

    before = tree(r)
    run_cli(c, p, r, "install", 3)
    assert tree(r) == before


def case_local_input_change_before_redirection_rolls_back(c: Path, r: Path, p: Path):
    before_xml = (r / "Data" / "Emulators.xml").read_bytes()
    run_cli(c, p, r, "install", 3, {"SHIM_INSTALL_TEST_INJECT": "local-input-race"})
    assert (r / "Data" / "Emulators.xml").read_bytes() == before_xml
    assert not (r / RETRO_FOLDER / "ControllerLobby.BigBox").exists()
    assert not (r / MAME_FOLDER / "ControllerLobby.BigBox").exists()
    assert not (r / "ControllerLobby.Setup").exists()


def case_reader_probe_provider_mismatch_refused(c: Path, r: Path, p: Path):
    before = tree(r)
    run_cli(c, p, r, "install", 3, {"SHIM_INSTALL_TEST_INJECT": "reader-provider-mismatch"})
    assert tree(r) == before


def case_reparse_and_junction_confinement(c: Path, r: Path, p: Path):
    outside = c / "outside_target"
    outside.mkdir(parents=True)
    (outside / "retroarch.exe").write_bytes(b"outside-retroarch")

    # 1. Replace Emulators\RetroArch-Portable with an NTFS directory junction pointing outside LaunchBox root.
    retro_dir = r / RETRO_FOLDER
    shutil.rmtree(retro_dir)
    subprocess.run(["cmd.exe", "/d", "/c", "mklink", "/J", str(retro_dir), str(outside)], check=True, capture_output=True)
    try:
        run_cli(c, p, r, "discover", 3)
        run_cli(c, p, r, "preview", 3)
        run_cli(c, p, r, "install", 3)
    finally:
        subprocess.run(["cmd.exe", "/d", "/c", "rmdir", str(retro_dir)], check=True, capture_output=True)
    retro_dir.mkdir(parents=True)
    (retro_dir / "retroarch.exe").write_bytes(b"binary-retroarch.exe-v1")

    # 2. Replace LaunchBox\Data with an NTFS directory junction pointing outside LaunchBox root.
    outside_data = c / "outside_data"
    outside_data.mkdir(parents=True)
    shutil.copyfile(r / "Data" / "Emulators.xml", outside_data / "Emulators.xml")
    shutil.rmtree(r / "Data")
    subprocess.run(["cmd.exe", "/d", "/c", "mklink", "/J", str(r / "Data"), str(outside_data)], check=True, capture_output=True)
    try:
        run_cli(c, p, r, "discover", 3)
        run_cli(c, p, r, "preview", 3)
        run_cli(c, p, r, "install", 3)
    finally:
        subprocess.run(["cmd.exe", "/d", "/c", "rmdir", str(r / "Data")], check=True, capture_output=True)


def case_excluded_platforms_wrappers_and_ambiguous_entries_refused(c: Path, r: Path, p: Path):
    before = tree(r)
    # 1. Explicit selection of standalone Dolphin entry is refused in Beta 1.
    run_cli(c, p, r, "install", 3, entries=DOLPHIN_ID)
    assert tree(r) == before

    # 2. Duplicate/ambiguous RetroArch entries refused when --entries is omitted, but accepted when disambiguated via --entries.
    retro2_folder = r"Emulators\RetroArch-Second"
    (r / retro2_folder).mkdir(parents=True)
    (r / retro2_folder / "retroarch.exe").write_bytes(b"retroarch-second-instance")
    (r / "Data" / "Emulators.xml").write_bytes(
        make_xml([
            (RETRO_ID, "RetroArch 1", RETRO_FOLDER + r"\retroarch.exe"),
            ("dddd1111-2222-3333-4444-555566667777", "RetroArch 2", retro2_folder + r"\retroarch.exe"),
            (MAME_ID, "MAME Arcade", MAME_FOLDER + r"\mame.exe"),
        ])
    )
    run_cli(c, p, r, "install", 3)
    run_cli(c, p, r, "install", 0, entries=f"{RETRO_ID},{MAME_ID}")
    assert (r / RETRO_FOLDER / "ControllerLobby.BigBox" / "ControllerLobby.BigBox.exe").is_file()
    assert not (r / retro2_folder / "ControllerLobby.BigBox").exists()
    run_cli(c, r / "ControllerLobby.Setup", r, "remove", 0)

    # 3. Dead/missing or out-of-root duplicate entries (synthetic fixture UUIDs) are ignored automatically when a single live in-root entry exists.
    (r / "Data" / "Emulators.xml").write_bytes(
        make_xml([
            (RETRO_ID, "RetroArch 1", RETRO_FOLDER + r"\retroarch.exe"),
            (MAME_ID, "MAME 0.279", MAME_FOLDER + r"\mame.exe"),
            ("ffff1111-2222-3333-4444-555566667777", "Synthetic MAME External Dead", r"Z:\SyntheticExternal\Emulators\MAME\mame64.exe"),
            ("eeee1111-2222-3333-4444-555566667777", "Synthetic MAME InRoot Missing", r"Emulators\MissingMame\mame.exe"),
        ])
    )
    run_cli(c, p, r, "discover", 0)
    run_cli(c, p, r, "preview", 0)
    run_cli(c, p, r, "install", 0)
    assert (r / MAME_FOLDER / "ControllerLobby.BigBox" / "ControllerLobby.BigBox.exe").is_file()
    run_cli(c, r / "ControllerLobby.Setup", r, "remove", 0)

    # 4. Entry pointing to ControllerLobby.Shim.exe wrapper is refused on fresh install.
    (r / RETRO_FOLDER / "ControllerLobby.Shim.exe").write_bytes(b"old-shim")
    (r / "Data" / "Emulators.xml").write_bytes(
        make_xml([
            (RETRO_ID, "RetroArch Shimmed", RETRO_FOLDER + r"\ControllerLobby.Shim.exe"),
        ])
    )
    run_cli(c, p, r, "install", 3)


def case_payload_layout_and_equality_guards(c: Path, r: Path, p: Path):
    before = tree(r)
    # 1. Unlisted extra file inside payload/ is refused at installer intake.
    unlisted = p / "payload" / "unlisted_extra.dll"
    unlisted.write_bytes(b"unexpected-unlisted-file")
    try:
        run_cli(c, p, r, "install", 3)
        assert tree(r) == before
    finally:
        if unlisted.exists():
            unlisted.unlink()

    # 2. Nested satellite directory/file inside payload/ is refused at installer intake.
    nested_dir = p / "payload" / "cs"
    nested_dir.mkdir(parents=True)
    (nested_dir / "PresentationFramework.resources.dll").write_bytes(b"nested-satellite-resource")
    try:
        run_cli(c, p, r, "install", 3)
        assert tree(r) == before
    finally:
        if nested_dir.exists():
            shutil.rmtree(nested_dir)

    # A nested satellite fixture is refused by the builder as well as the installer.
    nested_build = c / "nested-build"
    nested_payload = nested_build / "payload" / "cs"
    nested_payload.mkdir(parents=True)
    (nested_payload / "PresentationFramework.resources.dll").write_bytes(b"synthetic-satellite")
    res = subprocess.run([
        sys.executable, "-I", "-B", str(repo / "harness/package_normal.py"),
        "--repo", str(repo), "--build", str(nested_build),
        "--output", str(c / "nested-rejected"), "--python-embed", str(python_embed),
    ], capture_output=True, text=True, timeout=30)
    assert res.returncode != 0 and "Nested payload entries refused" in res.stdout + res.stderr


def case_built_runtime_apphost_intake_and_roundtrip(c: Path, r: Path, p: Path):
    cand02 = Path(runtime_arg).resolve()
    expected_inventory_sha = sha(cand02 / "runtime-payload-hashes.json")
    expected_source_sha = sha(cand02 / "payload/source-manifest.json")
    expected_count = len(json.loads((cand02 / "runtime-payload-hashes.json").read_bytes()))
    cand02_pkg = c / "cand02_package"
    cmd = [
        sys.executable,
        "-I",
        "-B",
        str(repo / "harness" / "package_normal.py"),
        "--repo",
        str(repo),
        "--build",
        str(cand02),
        "--output",
        str(cand02_pkg),
        "--python-embed",
        str(python_embed),
        "--expected-inventory-sha256",
        expected_inventory_sha,
        "--expected-source-manifest-sha256",
        expected_source_sha,
    ]
    res = subprocess.run(cmd, capture_output=True, text=True, timeout=60)
    assert res.returncode == 0, res.stdout + "\n" + res.stderr

    pkg_data = json.loads((cand02_pkg / "package.json").read_bytes())
    assert len(pkg_data["PayloadFiles"]) == expected_count
    assert pkg_data["RuntimeProvenance"]["InventorySha256"] == expected_inventory_sha
    assert pkg_data["RuntimeProvenance"]["SourceManifestSha256"] == expected_source_sha

    before = tree(r)
    # Install using the real 255-file self-contained candidate-02 payload (runs actual ControllerLobby.BigBox.exe --verify-only at both final locations).
    run_cli(c, cand02_pkg, r, "install", 0)
    shutil.rmtree(cand02_pkg)
    setup_dir = r / "ControllerLobby.Setup"

    for folder in (RETRO_FOLDER, MAME_FOLDER):
        n_cfg = json.loads((r / folder / "ControllerLobby.BigBox" / "normal.json").read_bytes())
        assert len(n_cfg["Payload"]) == expected_count
        assert any(
            item["Path"] == "source-manifest.json"
            and item["Sha256"] == expected_source_sha
            for item in n_cfg["Payload"]
        )

    # Tamper with deployed PresentationFramework.dll and source-manifest.json -> verify fails (exit 5); restore -> verify passes (exit 0).
    for rel_name in ("PresentationFramework.dll", "source-manifest.json"):
        target_f = r / MAME_FOLDER / "ControllerLobby.BigBox" / rel_name
        orig_b = target_f.read_bytes()
        target_f.write_bytes(orig_b + b"\ntampered\n")
        run_cli(c, setup_dir, r, "verify", 5)
        target_f.write_bytes(orig_b)
        run_cli(c, setup_dir, r, "verify", 0)

    # Delete deployed PresentationFramework.dll and update retroarch.exe -> repair restores DLL from ControllerLobby.Setup\payload and re-verifies real apphost!
    (r / RETRO_FOLDER / "ControllerLobby.BigBox" / "PresentationFramework.dll").unlink()
    orig_retro_bytes = (r / RETRO_FOLDER / "retroarch.exe").read_bytes()
    (r / RETRO_FOLDER / "retroarch.exe").write_bytes(b"retroarch-updated-after-cand02-install")
    run_cli(c, setup_dir, r, "repair", 0)
    run_cli(c, setup_dir, r, "verify", 0)

    # Restore original retroarch.exe bytes and run Uninstall.cmd -> verify LaunchBox root matches pre-install tree byte-for-byte.
    (r / RETRO_FOLDER / "retroarch.exe").write_bytes(orig_retro_bytes)
    env = dict(os.environ)
    env.update({
        "SHIM_INSTALL_TEST_FRONTEND": "closed",
        "SHIM_INSTALL_TEST_FAIL_AT": "",
        "SHIM_INSTALL_TEST_INJECT": "",
        "CONTROLLERLOBBY_NO_PAUSE": "1",
    })
    u_res = subprocess.run(["cmd.exe", "/d", "/c", str(setup_dir / "Uninstall.cmd")], env=env, capture_output=True, timeout=30)
    assert u_res.returncode == 0, u_res.stdout.decode(errors="replace") + u_res.stderr.decode(errors="replace")
    assert tree(r) == before


def case_canonical_extra_field_stage_rejected(c: Path, r: Path, p: Path):
    before = tree(r)
    pin = sha(p / "package.json")
    run_cli(c, p, r, "install", 0, pin=pin)
    setup_dir = r / "ControllerLobby.Setup"
    rec_path = setup_dir / "install-record.txt"
    orig_rec = rec_path.read_text(encoding="utf-8")
    nonce = "0123456789abcdef0123456789abcdef"

    normal_p = r / RETRO_FOLDER / "ControllerLobby.BigBox" / "normal.json"
    bigbox_p = r / RETRO_FOLDER / "ControllerLobby.BigBox" / "bigbox.json"

    for target_p, label in ((normal_p, "normal.json"), (bigbox_p, "bigbox.json")):
        part_p = target_p.with_name(f"{target_p.name}.cl-{nonce}.part")
        rest_p = target_p.with_name(f"{target_p.name}.cl-restore.tmp")
        extra_obj = json.loads(target_p.read_bytes())
        extra_obj["OwnerAnnotation"] = "foreign annotation; not produced by installer"
        part_p.write_bytes((json.dumps(extra_obj, indent=2) + "\n").encode("utf-8"))
        rest_p.write_bytes(b"foreign-restore-bytes\n")
        exp_target_sha = sha(target_p)
        exp_part_sha = sha(part_p)
        exp_rest_sha = sha(rest_p)

        for act in ("repair", "remove"):
            rec_path.write_text(
                orig_rec + f"GEN PENDING 2 {nonce} {exp_target_sha} {exp_part_sha} *{target_p}\n",
                encoding="utf-8",
            )
            env = dict(os.environ)
            env.update({
                "SHIM_INSTALL_TEST_FRONTEND": "closed",
                "SHIM_INSTALL_TEST_FAIL_AT": "",
                "SHIM_INSTALL_TEST_INJECT": "",
                "CONTROLLERLOBBY_NO_PAUSE": "1",
            })
            cmd = [
                bundled_py(p),
                "-I",
                "-B",
                str(p / "bigbox_install.py"),
                act,
                "--receipt",
                str(p),
                "--root",
                str(r),
                "--package-sha256",
                pin,
            ]
            res = subprocess.run(cmd, env=env, capture_output=True, timeout=30)
            assert part_p.is_file() and sha(part_p) == exp_part_sha, (
                f"Canonical {label} stage file with unknown OwnerAnnotation field was deleted or altered during {act}!"
            )
            assert rest_p.is_file() and sha(rest_p) == exp_rest_sha, (
                f"Restore file next to {label} was deleted or altered during {act}!"
            )
            assert target_p.is_file() and sha(target_p) == exp_target_sha, (
                f"Deployed {label} was altered during {act}!"
            )
            assert res.returncode == 3, (act, label, res.returncode, res.stdout.decode(errors="replace"), res.stderr.decode(errors="replace"))

        part_p.unlink()
        rest_p.unlink()

    rec_path.write_text(orig_rec, encoding="utf-8")
    run_cli(c, p, r, "repair", 0, pin=pin)
    run_cli(c, p, r, "verify", 0, pin=pin)
    run_cli(c, p, r, "remove", 0, pin=pin)
    assert tree(r) == before


def case_interrupted_rollback_safe_stop_and_manual_recovery(c: Path, r: Path, p: Path):
    import re as _re
    base_root_template = c / "_clean_lb_template"
    shutil.copytree(r, base_root_template)
    pin = sha(p / "package.json")

    wrapper_script = c / "rollback_interruption_wrapper.py"
    wrapper_script.write_text(
        "from pathlib import Path\n"
        "import importlib.util, os, sys\n"
        "sys.dont_write_bytecode = True\n"
        "pkg_dir, lb_root, pin, phase, mode = sys.argv[1:6]\n"
        "spec = importlib.util.spec_from_file_location('inst', Path(pkg_dir) / 'bigbox_install.py')\n"
        "mod = importlib.util.module_from_spec(spec); spec.loader.exec_module(mod)\n"
        "orig_replace = os.replace\n"
        "orig_append = mod.tx.append\n"
        "if phase == 'forward-normal-replace':\n"
        "    def hooked_replace(src, dst, *a, **kw):\n"
        "        res = orig_replace(src, dst, *a, **kw)\n"
        "        if os.path.basename(str(dst)).lower() == 'normal.json':\n"
        "            os._exit(79)\n"
        "        return res\n"
        "    os.replace = hooked_replace\n"
        "elif phase == 'rollback-interrupt':\n"
        "    if mode == 'before-rollback-replace':\n"
        "        def hooked_replace(src, dst, *a, **kw):\n"
        "            if os.path.basename(str(dst)).lower() == 'normal.json':\n"
        "                os._exit(78)\n"
        "            return orig_replace(src, dst, *a, **kw)\n"
        "        os.replace = hooked_replace\n"
        "    elif mode in ('partial-rollback-of-activated-plan', 'after-rollback-abandoned'):\n"
        "        def hooked_append(record, line):\n"
        "            res = orig_append(record, line)\n"
        "            if str(line).startswith('GEN ABANDONED '):\n"
        "                os._exit(78)\n"
        "            return res\n"
        "        mod.tx.append = hooked_append\n"
        "sys.argv = [os.path.join(pkg_dir, 'bigbox_install.py'), 'repair', '--receipt', pkg_dir, '--root', lb_root, '--package-sha256', pin]\n"
        "raise SystemExit(mod.main())\n",
        encoding="utf-8",
    )

    env = dict(os.environ)
    env.update({
        "SHIM_INSTALL_TEST_FRONTEND": "closed",
        "SHIM_INSTALL_TEST_FAIL_AT": "",
        "SHIM_INSTALL_TEST_INJECT": "",
        "CONTROLLERLOBBY_NO_PAUSE": "1",
    })

    for mode in ("before-rollback-replace", "partial-rollback-of-activated-plan", "after-rollback-abandoned"):
        sub_r = c / f"LB_{mode}"
        shutil.copytree(base_root_template, sub_r)

        # Place unrelated user files inside the emulator directory (including a .part-named user file), inside Data, and outside the LaunchBox root.
        user_emu_note = sub_r / RETRO_FOLDER / "user_custom_notes.txt"
        user_emu_part = sub_r / RETRO_FOLDER / "user_custom.cl-ffffffffffffffffffffffffffffffff.part"
        user_data_xml = sub_r / "Data" / "Platforms.xml"
        outside_user = c / f"outside_{mode}.txt"
        user_emu_note.write_bytes(f"user-notes-{mode}\n".encode("utf-8"))
        user_emu_part.write_bytes(f"unrelated-user-part-{mode}\n".encode("utf-8"))
        user_data_xml.write_bytes(f"<LaunchBox><Platform>{mode}</Platform></LaunchBox>\n".encode("utf-8"))
        outside_user.write_bytes(f"outside-user-{mode}\n".encode("utf-8"))
        before_sub = tree(sub_r)
        exp_outside_sha = sha(outside_user)

        run_cli(c, p, sub_r, "install", 0, pin=pin)
        setup_dir = sub_r / "ControllerLobby.Setup"
        rec_path = setup_dir / "install-record.txt"

        # Update retroarch.exe so repair generates new normal.json and bigbox.json hashes (Generation 2).
        updated_retro_v2 = f"retroarch-v2-{mode}".encode("utf-8")
        (sub_r / RETRO_FOLDER / "retroarch.exe").write_bytes(updated_retro_v2)
        expected_after_remove = dict(before_sub)
        expected_after_remove[str((sub_r / RETRO_FOLDER / "retroarch.exe").relative_to(sub_r))] = hashlib.sha256(updated_retro_v2).hexdigest().upper()

        # 1. Trigger first interruption (forward repair exit 79).
        if mode == "partial-rollback-of-activated-plan":
            run_cli(c, p, sub_r, "repair", 79, {"SHIM_INSTALL_TEST_INJECT": "exit-repair-after-plan-replace"}, pin=pin)
        else:
            r1 = subprocess.run(
                [bundled_py(p), "-I", "-B", str(wrapper_script), str(p), str(sub_r), pin, "forward-normal-replace", mode],
                env=env,
                capture_output=True,
                timeout=30,
            )
            assert r1.returncode == 79, (mode, r1.returncode, r1.stdout, r1.stderr)

        # 2. Trigger second interruption during rollback (exit 78).
        r2 = subprocess.run(
            [bundled_py(p), "-I", "-B", str(wrapper_script), str(p), str(sub_r), pin, "rollback-interrupt", mode],
            env=env,
            capture_output=True,
            timeout=30,
        )
        assert r2.returncode == 78, (mode, r2.returncode, r2.stdout, r2.stderr)

        # 3. Verify safe stop: verify exits 5, repair and remove exit 3 with explicit manual-recovery diagnostic and zero file mutations.
        snapshot_interrupted_rollback = tree(sub_r)
        v_res = run_cli(c, p, sub_r, "verify", 5, pin=pin)
        v_out = (v_res.stdout + v_res.stderr).decode(errors="replace")
        assert "Rollback of generation 2 was interrupted; automatic recovery is unavailable" in v_out, (mode, v_out)
        assert "docs/BETA_SETUP_AND_RECOVERY.md" in v_out, (mode, v_out)
        assert tree(sub_r) == snapshot_interrupted_rollback

        for act in ("repair", "remove"):
            a_res = run_cli(c, p, sub_r, act, 3, pin=pin)
            a_out = (a_res.stdout + a_res.stderr).decode(errors="replace")
            assert "Rollback of generation 2 was interrupted; automatic recovery is unavailable" in a_out, (mode, act, a_out)
            assert "docs/BETA_SETUP_AND_RECOVERY.md" in a_out, (mode, act, a_out)
            assert tree(sub_r) == snapshot_interrupted_rollback, f"{act} mutated files during interrupted rollback in {mode}!"

        # 4. Execute the documented step-by-step Manual Recovery Procedure from docs/BETA_SETUP_AND_RECOVERY.md (Section 8, Steps 1-4).
        rec_lines = rec_path.read_text(encoding="utf-8").splitlines(keepends=True)
        pending_gen2 = []
        for ln in rec_lines:
            m = _re.fullmatch(r"GEN PENDING 2 ([0-9a-f]{32}) ([0-9A-F]{64}) ([0-9A-F]{64}) \*(.+)", ln.rstrip("\r\n"))
            if m:
                pending_gen2.append(m.groups())
        assert len(pending_gen2) == 5, (mode, len(pending_gen2))

        for nonce_val, old_sha_val, new_sha_val, target_str in pending_gen2:
            target_p = Path(target_str)
            genpre_p = setup_dir / f"install-record.txt.genpre-{old_sha_val[:16]}.bin"
            assert genpre_p.is_file() and sha(genpre_p) == old_sha_val, f"Missing or invalid genpre for {target_p}"
            shutil.copyfile(genpre_p, target_p)
            assert sha(target_p) == old_sha_val
            part_cand = Path(f"{target_str}.cl-{nonce_val}.part")
            if part_cand.is_file() and sha(part_cand) in (old_sha_val, new_sha_val):
                part_cand.unlink()

        cleaned_lines = [
            ln for ln in rec_lines
            if not _re.fullmatch(r"GEN (PENDING|STAGED|DONE|ABANDONED) 2 .*", ln.rstrip("\r\n"))
        ]
        rec_path.write_text("".join(cleaned_lines), encoding="utf-8")

        # 5. Execute documented Step 5 with the updated retroarch.exe kept in place:
        #    - Verify reports expected external emulator binary hash mismatch (exit 5) against restored Generation 1 manifests.
        #    - Repair revalidates the updated emulator binary to Generation 2 (exit 0).
        #    - Verify now reports verified (exit 0).
        #    - Remove succeeds (exit 0) and preserves the updated emulator binary and all unrelated user files.
        v_after_manual = run_cli(c, p, sub_r, "verify", 5, pin=pin)
        v_after_manual_out = (v_after_manual.stdout + v_after_manual.stderr).decode(errors="replace")
        assert "MISMATCH emulator binary changed since installation" in v_after_manual_out, (mode, v_after_manual_out)
        run_cli(c, p, sub_r, "repair", 0, pin=pin)
        run_cli(c, p, sub_r, "verify", 0, pin=pin)
        run_cli(c, p, sub_r, "remove", 0, pin=pin)
        assert tree(sub_r) == expected_after_remove
        assert (sub_r / RETRO_FOLDER / "retroarch.exe").read_bytes() == updated_retro_v2
        assert user_emu_note.is_file() and user_emu_part.is_file() and user_data_xml.is_file()
        assert outside_user.is_file() and sha(outside_user) == exp_outside_sha


cases = [
    ("package-builder-and-cmd-launchers", case_package_builder_and_cmd_launchers),
    ("deleted-download-package-verify-repair-remove", case_deleted_download_package_verify_repair_remove),
    ("uninstall-after-emulator-and-os-updates", case_uninstall_after_emulator_and_os_updates),
    ("explicit-repair-revalidates-and-restores-missing-payload", case_explicit_repair_revalidates_and_restores_missing_payload),
    ("repair-refuses-tampered-payload-and-preserves-foreign-edits", case_repair_refuses_tampered_payload_and_preserves_foreign_edits),
    ("repair-interruption-rollback", case_repair_interruption_rollback),
    ("repair-real-process-exit-recovery", case_repair_real_process_exit_recovery),
    ("cleanup-delete-lock-and-mid-cleanup-process-exit-retry", case_cleanup_delete_lock_and_mid_cleanup_process_exit_retry),
    ("journal-validation-precedes-recovery-mutation", case_journal_validation_precedes_recovery_mutation),
    ("canonical-extra-field-stage-rejected", case_canonical_extra_field_stage_rejected),
    ("interrupted-rollback-safe-stop-and-manual-recovery", case_interrupted_rollback_safe_stop_and_manual_recovery),
    ("verify-only-failure-before-redirection-rolls-back", case_verify_only_failure_before_redirection_rolls_back),
    ("local-input-change-before-redirection-rolls-back", case_local_input_change_before_redirection_rolls_back),
    ("reader-probe-provider-mismatch-refused", case_reader_probe_provider_mismatch_refused),
    ("reparse-and-junction-confinement", case_reparse_and_junction_confinement),
    ("excluded-platforms-wrappers-and-ambiguous-entries-refused", case_excluded_platforms_wrappers_and_ambiguous_entries_refused),
    ("payload-layout-and-equality-guards", case_payload_layout_and_equality_guards),
    ("built-runtime-apphost-intake-and-roundtrip", case_built_runtime_apphost_intake_and_roundtrip),
]

if mutation:
    cases = [(n, fn) for n, fn in cases if n == mutation]
    assert cases, f"No test case matching mutation {mutation}"

for name, fn in cases:
    try:
        fn(*fixture(name))
        print("PASS " + name, flush=True)
    except Exception as e:
        print("FAIL " + name + ": " + str(e), flush=True)
        raise

print(f"{len(cases)}/{len(cases)} portable Beta 1 installer checks passed; no live LaunchBox writes")
