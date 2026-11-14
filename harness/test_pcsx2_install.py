"""PCSX2 through the real installer, on a synthetic LaunchBox root (M; no emulator, controller or live LaunchBox).

Usage: python -I -B harness/test_pcsx2_install.py <new empty output dir> <package dir> <official release dir> [<installer dir>]

<official release dir> is pcsx2-v2.7.182-windows-x64-Qt.7z extracted (its program files are copied into the synthetic
PCSX2 folder, never executed). With an installer dir (mutation controls), install, repair and uninstall run that
dir's bigbox_install.py instead of the package's commands.

Installs RetroArch, MAME and a portable PCSX2 with leftovers outside the release (an old resources file, zlib1.dll),
checks the launch copy is exactly the release inventory plus portable.ini and its manifest block, runs the installed
Verify and the host's --verify-only (and its refusal of a changed copy file), refuses Repair, plants what a mapped
session writes plus a crash file, uninstalls, and checks Emulators.xml is byte-identical and only the crash file
remains. A non-portable PCSX2, a program file that differs from the release, and another PCSX2 build are each refused
before anything is written (PCSX2 adapter contract section 2).
"""
import hashlib, json, os, shutil, subprocess, sys
from pathlib import Path
case, pkg, release = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve(), Path(sys.argv[3]).resolve()
alt = Path(sys.argv[4]).resolve() if len(sys.argv) > 4 else None
sys.path.insert(0, str(Path(__file__).resolve().parent))
import pcsx2_inventory as inventory
PIN = next(l for l in (pkg / "Install.cmd").read_text().splitlines() if "--package-sha256" in l).split("--package-sha256")[1].split()[0]
PY = str(pkg / "python" / "python.exe")
# cmd /s /c needs the outer quote pair when Install.cmd's package folder contains spaces.
def install_cmd(root): return ([PY, "-I", "-B", str(alt / "bigbox_install.py"), "install", "--receipt", str(pkg), "--root", str(root), "--package-sha256", PIN]
                           if alt else 'cmd.exe /d /s /c ""' + str(pkg / "Install.cmd") + '" "' + str(root) + '""')
if case.exists(): raise SystemExit("Output exists; choose a new directory")
cases, failed = 0, 0
def check(name, ok, detail=""):
    global cases, failed
    cases += 1; failed += 0 if ok else 1
    print(("PASS " if ok else "FAIL ") + name + ("" if ok else ": " + detail))
def sha(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest().upper()
ra, ma, pa = "aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa", "bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb", "cccccccc-3333-4333-8333-cccccccccccc"
def emu(i, t, p): return f"  <Emulator>\r\n    <ID>{i}</ID>\r\n    <Title>{t}</Title>\r\n    <ApplicationPath>{p}</ApplicationPath>\r\n  </Emulator>\r\n"
OWNER_INI = b"[UI]\r\nSettingsVersion = 1\r\n\r\n[Pad1]\r\nType = DualShock2\r\nCross = Keyboard/K\r\n"
def make_root(name, portable=True, change=None, other_build=False):
    root = case / name
    (root / "Data").mkdir(parents=True)
    xml = ('<?xml version="1.0" standalone="yes"?>\r\n<LaunchBox>\r\n' + emu(ra, "RetroArch", r"Emulators\RetroArch\retroarch.exe")
           + emu(ma, "MAME", r"Emulators\MAME\mame.exe") + emu(pa, "PCSX2", r"Emulators\PCSX2\pcsx2-qt.exe") + "</LaunchBox>").encode("utf-8")
    (root / "Data" / "Emulators.xml").write_bytes(xml)
    for f, e in (("RetroArch", "retroarch.exe"), ("MAME", "mame.exe")):
        (root / "Emulators" / f).mkdir(parents=True); (root / "Emulators" / f / e).write_bytes(b"synthetic emulator, never executed")
    d = root / "Emulators" / "PCSX2"
    shutil.copytree(release, d)  # the official program files, never executed
    (d / "inis").mkdir(); (d / "inis" / "PCSX2.ini").write_bytes(OWNER_INI)
    (d / "resources" / "icons" / "flags").mkdir(parents=True, exist_ok=True); (d / "resources" / "icons" / "flags" / "NTSC-OLD.svg").write_bytes(b"left over")
    (d / "zlib1.dll").write_bytes(b"left over"); (d / "memcards").mkdir(); (d / "memcards" / "Mcd001.ps2").write_bytes(b"a memory card")
    if portable: (d / "portable.ini").write_bytes(b"")
    if change: (d / change).write_bytes((d / change).read_bytes() + b"x")
    if other_build: (d / "pcsx2-qt.exe").write_bytes((d / "pcsx2-qt.exe").read_bytes() + b"another build")
    (root / "LaunchBox.exe").write_bytes(b"synthetic")
    return root, xml
la, tmp = case / "LocalAppData", case / "Temp"
env = None
def run(cmd, **kw): return subprocess.run(cmd, env=env, capture_output=True, timeout=900, **kw)
def out(r): return (r.stdout + r.stderr).decode(errors="replace")
def untouched(root): return not (root / "ControllerLobby.Setup").exists() and not any("ControllerLobby" in str(p) for p in (root / "Emulators").rglob("*"))

case.mkdir(); la.mkdir(); tmp.mkdir()
env = dict(os.environ, LOCALAPPDATA=str(la), TEMP=str(tmp), TMP=str(tmp), CONTROLLERLOBBY_NO_PAUSE="1")
for k in [k for k in env if k.startswith("SHIM_INSTALL_TEST")]: del env[k]
env["SHIM_INSTALL_TEST_FRONTEND"] = "closed"  # synthetic root: the running LaunchBox never reads it

check("release-is-the-pinned-build", sha(release / "pcsx2-qt.exe") == inventory.PIN, "the release dir is not PCSX2 2.7.182")
root, xml = make_root("Launch Box (x86)")
emu_dir = root / "Emulators" / "PCSX2"; copy = emu_dir / "ControllerLobby.BigBox" / "Pcsx2Copy"
before = {str(p.relative_to(emu_dir)): sha(p) for p in emu_dir.rglob("*") if p.is_file()}
r = run(install_cmd(root))
check("install-three-kinds", r.returncode == 0 and "installed" in out(r) and "3 entries" in out(r), out(r)[-600:])
want = set(inventory.FILES) | {"portable.ini"}
got = {str(p.relative_to(copy)) for p in copy.rglob("*") if p.is_file()} if copy.is_dir() else set()
check("copy-is-the-release-inventory-plus-marker", got == want, "difference %s" % sorted(got ^ want)[:10])
check("copy-files-equal-the-release", got == want and all(sha(copy / rel) == inventory.FILES[rel] for rel in inventory.FILES) and (copy / "portable.ini").read_bytes() == b"")
check("leftovers-and-data-not-copied", not (copy / "zlib1.dll").exists() and not (copy / "resources" / "icons" / "flags" / "NTSC-OLD.svg").exists()
      and not (copy / "updater.exe").exists() and not (copy / "utils").exists() and not (copy / "inis").exists() and not (copy / "memcards").exists())
normal = json.loads((emu_dir / "ControllerLobby.BigBox" / "normal.json").read_bytes()) if (emu_dir / "ControllerLobby.BigBox" / "normal.json").is_file() else {}
block = normal.get("Pcsx2") or {}
inv = {i["Path"]: i["Sha256"] for i in block.get("Inventory", [])}
check("manifest-block-pins-the-copy", normal.get("Kind") == "pcsx2" and Path(block.get("CopyRoot", "")) == copy and set(inv) == want
      and all(inv[k] == sha(copy / k) for k in want) and normal.get("DuckStation") is None and normal.get("ExecutableSha256", "").upper() == inventory.PIN, json.dumps(block)[:300])
text = (root / "Data" / "Emulators.xml").read_text(encoding="utf-8")
check("pcsx2-entry-redirected", "Emulators\\PCSX2\\ControllerLobby.BigBox\\ControllerLobby.BigBox.exe" in text)
setup = root / "ControllerLobby.Setup"
v = run('cmd.exe /d /s /c ""' + str(setup / "Verify installation.cmd") + '" "' + str(root) + '""', cwd=str(tmp))
check("installed-verify", v.returncode == 0 and "verified" in out(v), out(v)[-400:])
hostexe = emu_dir / "ControllerLobby.BigBox" / "ControllerLobby.BigBox.exe"
h = run([str(hostexe), "--verify-only"], cwd=str(hostexe.parent))
check("host-verify-only-opens-the-pinned-copy", h.returncode == 0, "exit %d %s" % (h.returncode, out(h)[-300:]))
target = copy / "qt.conf"; original = target.read_bytes(); target.write_bytes(original[:-1] + (b"X" if original[-1:] != b"X" else b"Y"))
h = run([str(hostexe), "--verify-only"], cwd=str(hostexe.parent))
check("host-refuses-a-same-length-changed-copy-file", h.returncode == 3, "exit %d" % h.returncode)
target.write_bytes(original)
rp = (run([PY, "-I", "-B", str(alt / "bigbox_install.py"), "repair", "--receipt", str(setup), "--record", str(setup / "install-record.txt"), "--root", str(root), "--package-sha256", PIN], cwd=str(tmp)) if alt
      else run('cmd.exe /d /s /c ""' + str(setup / "Upgrade or Repair.cmd") + '" "' + str(root) + '""', cwd=str(tmp)))
check("repair-refused-while-pcsx2-installed", rp.returncode != 0 and "Repair is not available while PCSX2 is installed" in out(rp), out(rp)[-400:])
# What a mapped session leaves in the copy, plus a crash file that must be kept.
for rel in ("inis/PCSX2.ini", "inis/PCSX2.ini.0123.tmp", "session/gamesettings/SLUS-20062_ABCD1234.ini", "session/debuggerlayouts/layout.json", "session/debuggersettings/s.json"):
    (copy / rel).parent.mkdir(parents=True, exist_ok=True); (copy / rel).write_bytes(b"session file")
(copy / "crash-2026-11-12-03-00-00.dmp").write_bytes(b"evidence")
u = (run([PY, "-I", "-B", str(alt / "bigbox_install.py"), "remove", "--receipt", str(setup), "--record", str(setup / "install-record.txt"), "--root", str(root), "--package-sha256", PIN], cwd=str(tmp)) if alt
     else run('cmd.exe /d /s /c ""' + str(setup / "Uninstall.cmd") + '""', cwd=str(tmp)))
check("uninstall", u.returncode == 0 and "removed" in out(u), out(u)[-600:])
check("emulators-xml-byte-identical", (root / "Data" / "Emulators.xml").read_bytes() == xml)
left = sorted(str(p.relative_to(root)) for p in (root / "Emulators").rglob("*") if p.is_file() and "ControllerLobby" in str(p))
check("only-the-crash-file-remains", left == ["Emulators\\PCSX2\\ControllerLobby.BigBox\\Pcsx2Copy\\crash-2026-11-12-03-00-00.dmp"], str(left))
after = {str(p.relative_to(emu_dir)): sha(p) for p in emu_dir.rglob("*") if p.is_file() and "ControllerLobby" not in str(p)}
check("owner-files-untouched", after == before, "changed: %s" % sorted(k for k in set(after) | set(before) if after.get(k) != before.get(k))[:10])
check("no-setup-folder-left", not setup.exists(), "ControllerLobby.Setup still present")

for name, kw, message in (("not-portable", {"portable": False}, "portable install"),
                          ("changed-release-file", {"change": "resources/GameIndex.yaml"}, "differs from the official 2.7.182 release"),
                          ("other-build", {"other_build": True}, "is not PCSX2 2.7.182")):
    proot, pxml = make_root("Refused " + name, **kw)
    r = run(install_cmd(proot))
    check("refused-before-writes-" + name, r.returncode != 0 and message in out(r) and (proot / "Data" / "Emulators.xml").read_bytes() == pxml and untouched(proot), out(r)[-400:])
print(f"Cases={cases} Passed={cases - failed} Failed={failed}; M real installer on a synthetic LaunchBox root with the official PCSX2 program files; no emulator, controller or live LaunchBox")
sys.exit(1 if failed else 0)
