"""DuckStation through the real installer, on a synthetic LaunchBox root (M; no emulator, controller or live LaunchBox).

Usage: python -I -B harness/test_duckstation_install.py <new empty output dir> <package dir (a build's package/ folder)> [<installer dir>]

With an installer dir (mutation controls), install and uninstall run that dir's bigbox_install.py instead of the
package's Install.cmd and the installed Uninstall.cmd.

Installs RetroArch, MAME and DuckStation entries with the package's Install.cmd, checks the launch copy (an allow-list
of program files plus portable.txt) and its manifest block, runs the installed Verify, refuses Repair, plants the files
a mapped session writes plus a crash file, uninstalls with the installed Uninstall.cmd and checks that Emulators.xml is
byte-identical and only the crash file remains. A DuckStation in portable mode is refused before anything is written.
"""
import json, os, shutil, subprocess, sys, hashlib
from pathlib import Path
case, pkg = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
alt = Path(sys.argv[3]).resolve() if len(sys.argv) > 3 else None
PIN = next(l for l in (pkg / "Install.cmd").read_text().splitlines() if "--package-sha256" in l).split("--package-sha256")[1].split()[0]
# cmd /s /c needs the outer quote pair when Install.cmd's package folder contains spaces.
def install_cmd(root): return ([str(pkg / "python" / "python.exe"), "-I", "-B", str(alt / "bigbox_install.py"), "install", "--receipt", str(pkg), "--root", str(root), "--package-sha256", PIN]
                             if alt else 'cmd.exe /d /s /c ""' + str(pkg / "Install.cmd") + '" "' + str(root) + '""')
if case.exists(): raise SystemExit("Output exists; choose a new directory")
cases, failed = 0, 0
def check(name, ok, detail=""):
    global cases, failed
    cases += 1; failed += 0 if ok else 1
    print(("PASS " if ok else "FAIL ") + name + ("" if ok else ": " + detail))
def sha(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest().upper()
ra, ma, da, xa = "aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa", "bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb", "dddddddd-4444-4444-8444-dddddddddddd", "eeeeeeee-5555-4555-8555-eeeeeeeeeeee"
EXE = "duckstation-qt-x64-ReleaseLTCG.exe"
def emu(i, t, p): return f"  <Emulator>\r\n    <ID>{i}</ID>\r\n    <Title>{t}</Title>\r\n    <ApplicationPath>{p}</ApplicationPath>\r\n  </Emulator>\r\n"
def make_root(name, portable=False):
    root = case / name
    (root / "Data").mkdir(parents=True)
    xml = ('<?xml version="1.0" standalone="yes"?>\r\n<LaunchBox>\r\n' + emu(ra, "RetroArch", r"Emulators\RetroArch\retroarch.exe")
           + emu(ma, "MAME", r"Emulators\MAME\mame.exe") + emu(da, "DuckStation", "Emulators\\DuckStation\\" + EXE) + emu(xa, "Xemu", r"Emulators\Xemu\xemu.exe") + "</LaunchBox>").encode("utf-8")
    (root / "Data" / "Emulators.xml").write_bytes(xml)
    for f, e in (("RetroArch", "retroarch.exe"), ("MAME", "mame.exe"), ("Xemu", "xemu.exe")):
        (root / "Emulators" / f).mkdir(parents=True); (root / "Emulators" / f / e).write_bytes(b"synthetic emulator, never executed")
    d = root / "Emulators" / "DuckStation"
    for rel, body in ((EXE, b"synthetic duckstation, never executed"), ("Qt6Core.dll", b"lib A"), ("SDL3.dll", b"lib B"), ("qt.conf", b"[Paths]"),
                      ("LICENSE.txt", b"licence"), ("QtPlugins/platforms/qwindows.dll", b"plugin"), ("resources/gamedb.yaml", b"db"),
                      ("translations/duckstation-qt_de.qm", b"de"), ("updater.exe", b"never copied"), ("notes/readme.txt", b"never copied")):
        (d / rel).parent.mkdir(parents=True, exist_ok=True); (d / rel).write_bytes(body)
    if portable: (d / "portable.txt").write_bytes(b"")
    (root / "LaunchBox.exe").write_bytes(b"synthetic")
    return root, xml
la, tmp = case / "LocalAppData", case / "Temp"
env = None
def run(cmd, **kw): return subprocess.run(cmd, env=env, capture_output=True, timeout=600, **kw)
def out(r): return (r.stdout + r.stderr).decode(errors="replace")

case.mkdir(); la.mkdir(); tmp.mkdir()
env = dict(os.environ, LOCALAPPDATA=str(la), TEMP=str(tmp), TMP=str(tmp), CONTROLLERLOBBY_NO_PAUSE="1")
for k in [k for k in env if k.startswith("SHIM_INSTALL_TEST")]: del env[k]
env["SHIM_INSTALL_TEST_FRONTEND"] = "closed"  # synthetic root: the running LaunchBox never reads it

root, xml = make_root("Launch Box (x86)")
r = run(install_cmd(root))
check("install-four-kinds", r.returncode == 0 and "installed" in out(r) and "4 entries" in out(r), out(r)[-600:])
xn = root / "Emulators" / "Xemu" / "ControllerLobby.BigBox" / "normal.json"
xemu_manifest = json.loads(xn.read_bytes()) if xn.is_file() else {}
check("xemu-entry-installed-without-a-copy", xemu_manifest.get("Kind") == "xemu" and "DuckStation" not in xemu_manifest
      and not (root / "Emulators" / "Xemu" / "ControllerLobby.BigBox" / "DuckStationCopy").exists(), json.dumps(xemu_manifest)[:300])
check("xemu-host-has-sdl3", (root / "Emulators" / "Xemu" / "ControllerLobby.BigBox" / "SDL3.dll").is_file())
emu_dir = root / "Emulators" / "DuckStation"; copy = emu_dir / "ControllerLobby.BigBox" / "DuckStationCopy"
want = {EXE, "Qt6Core.dll", "SDL3.dll", "qt.conf", "LICENSE.txt", "QtPlugins\\platforms\\qwindows.dll", "resources\\gamedb.yaml", "translations\\duckstation-qt_de.qm", "portable.txt"}
got = {str(p.relative_to(copy)) for p in copy.rglob("*") if p.is_file()} if copy.is_dir() else set()
check("copy-is-the-allow-list-plus-marker", got == want, "copy holds %s" % sorted(got ^ want))
check("copy-matches-program-files", all(sha(copy / rel) == sha(emu_dir / rel) for rel in want - {"portable.txt"}) if got == want else False)
normal = json.loads((emu_dir / "ControllerLobby.BigBox" / "normal.json").read_bytes()) if (emu_dir / "ControllerLobby.BigBox" / "normal.json").is_file() else {}
block = normal.get("DuckStation") or {}
inv = {i["Path"]: i["Sha256"] for i in block.get("Inventory", [])}
check("manifest-block-pins-the-copy", normal.get("Kind") == "duckstation" and Path(block.get("CopyRoot", "")) == copy and set(inv) == want
      and all(inv[k] == sha(copy / k) for k in want), json.dumps(block)[:400])
other = json.loads((root / "Emulators" / "MAME" / "ControllerLobby.BigBox" / "normal.json").read_bytes()) if (root / "Emulators" / "MAME" / "ControllerLobby.BigBox" / "normal.json").is_file() else {"DuckStation": 1}
check("other-kinds-have-no-block", "DuckStation" not in other)
text = (root / "Data" / "Emulators.xml").read_text(encoding="utf-8")
check("duckstation-entry-redirected", "Emulators\\DuckStation\\ControllerLobby.BigBox\\ControllerLobby.BigBox.exe" in text)
setup = root / "ControllerLobby.Setup"
v = run('cmd.exe /d /s /c ""' + str(setup / "Verify installation.cmd") + '" "' + str(root) + '""', cwd=str(tmp))
check("installed-verify", v.returncode == 0 and "verified" in out(v), out(v)[-400:])
host = run([str(emu_dir / "ControllerLobby.BigBox" / "ControllerLobby.BigBox.exe"), "--verify-only"], cwd=str(emu_dir / "ControllerLobby.BigBox"))
check("host-verify-only-opens-the-pinned-copy", host.returncode == 0, "exit %d" % host.returncode)
(copy / "QtPlugins" / "platforms" / "qwindows.dll").write_bytes(b"plugix")  # same length, changed
host = run([str(emu_dir / "ControllerLobby.BigBox" / "ControllerLobby.BigBox.exe"), "--verify-only"], cwd=str(emu_dir / "ControllerLobby.BigBox"))
check("host-refuses-a-same-length-changed-copy-file", host.returncode == 3, "exit %d" % host.returncode)
(copy / "QtPlugins" / "platforms" / "qwindows.dll").write_bytes(b"plugin")
rp = run('cmd.exe /d /s /c ""' + str(setup / "Upgrade or Repair.cmd") + '" "' + str(root) + '""', cwd=str(tmp))
check("repair-refused-while-duckstation-installed", rp.returncode != 0 and "Repair is not available while DuckStation is installed" in out(rp), out(rp)[-400:])
# What a mapped session and DuckStation leave in the copy, plus a crash file that must be kept.
for rel in ("settings.ini", "playtime.dat", "duckstation.log", "settings.ini.0123.tmp", "session/gamesettings/SLUS-00001.ini", "session/inputprofiles/p.ini"):
    (copy / rel).parent.mkdir(parents=True, exist_ok=True); (copy / rel).write_bytes(b"session file")
(copy / "crash-2026-11-10-21-00-00-000.dmp").write_bytes(b"evidence")
u = run([str(pkg / "python" / "python.exe"), "-I", "-B", str(alt / "bigbox_install.py"), "remove", "--receipt", str(setup), "--record", str(setup / "install-record.txt"), "--root", str(root), "--package-sha256", PIN], cwd=str(tmp)) if alt else run('cmd.exe /d /s /c ""' + str(setup / "Uninstall.cmd") + '""', cwd=str(tmp))
check("uninstall", u.returncode == 0 and "removed" in out(u), out(u)[-600:])
check("emulators-xml-byte-identical", (root / "Data" / "Emulators.xml").read_bytes() == xml)
left = sorted(str(p.relative_to(root)) for p in (root / "Emulators").rglob("*") if p.is_file() and "ControllerLobby" in str(p))
check("only-the-crash-file-remains", left == ["Emulators\\DuckStation\\ControllerLobby.BigBox\\DuckStationCopy\\crash-2026-11-10-21-00-00-000.dmp"], str(left))
check("program-files-untouched", all(sha(emu_dir / rel) == sha(emu_dir / rel) for rel in want - {"portable.txt"}) and (emu_dir / "updater.exe").read_bytes() == b"never copied")
check("no-setup-folder-left", not setup.exists(), "ControllerLobby.Setup still present")

proot, pxml = make_root("Portable Box", portable=True)
r = run(install_cmd(proot))
check("portable-duckstation-refused-before-writes", r.returncode != 0 and "portable mode" in out(r) and (proot / "Data" / "Emulators.xml").read_bytes() == pxml
      and not (proot / "ControllerLobby.Setup").exists() and not any("ControllerLobby" in str(p) for p in (proot / "Emulators").rglob("*")), out(r)[-400:])
print(f"Cases={cases} Passed={cases - failed} Failed={failed}; M real installer on a synthetic LaunchBox root; no emulator, controller or live LaunchBox")
sys.exit(1 if failed else 0)
