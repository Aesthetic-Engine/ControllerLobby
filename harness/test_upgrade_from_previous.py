"""Upgrade check against a previously released package (M with real release artifacts; synthetic LaunchBox root).

Usage: python -I -B harness/test_upgrade_from_previous.py <extracted previous release ZIP> <new empty output dir> [<new package dir>]

With a new package directory (a build's package/ folder), the replacement is then installed with its own
Install.cmd, verified with the installed Verify installation.cmd and removed with the installed Uninstall.cmd;
Emulators.xml must be byte-identical and no ControllerLobby file may remain at the end.

Installs the previous release into a synthetic root with its own Install.cmd, reads it with this
branch's setup_bridge.py (Setup's discovery), then removes it with the installed copy's own
Uninstall.cmd the way Setup's Upgrade does (cmd /d /s /c ""path"", CONTROLLERLOBBY_NO_PAUSE=1, cwd TEMP).
LOCALAPPDATA and TEMP point inside the output directory. No emulator, controller or live LaunchBox.
"""
import json,os,shutil,subprocess,sys,hashlib
from pathlib import Path
beta=Path(sys.argv[1]).resolve();branch=Path(__file__).resolve().parent.parent
case=Path(sys.argv[2]).resolve()
if case.exists():raise SystemExit("Output exists; choose a new directory")
root=case/"Launch Box (x86)";  # spaces and brackets, as in Program Files (x86)
(root/"Data").mkdir(parents=True)
ra,ma="aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa","bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb"
def emu(i,t,p):return f"  <Emulator>\r\n    <ID>{i}</ID>\r\n    <Title>{t}</Title>\r\n    <ApplicationPath>{p}</ApplicationPath>\r\n  </Emulator>\r\n"
xml=('<?xml version="1.0" standalone="yes"?>\r\n<LaunchBox>\r\n'+emu(ra,"RetroArch",r"Emulators\RetroArch\retroarch.exe")+emu(ma,"MAME",r"Emulators\MAME\mame.exe")+"</LaunchBox>").encode("utf-8")
(root/"Data"/"Emulators.xml").write_bytes(xml)
for f,e in (("RetroArch","retroarch.exe"),("MAME","mame.exe")):(root/"Emulators"/f).mkdir(parents=True);(root/"Emulators"/f/e).write_bytes(b"synthetic emulator, never executed")
(root/"LaunchBox.exe").write_bytes(b"synthetic")
la,tmp=case/"LocalAppData",case/"Temp";la.mkdir();tmp.mkdir()
env=dict(os.environ,LOCALAPPDATA=str(la),TEMP=str(tmp),TMP=str(tmp),CONTROLLERLOBBY_NO_PAUSE="1")
for k in [k for k in env if k.startswith("SHIM_INSTALL_TEST")]:del env[k]
env["SHIM_INSTALL_TEST_FRONTEND"]="closed"  # synthetic root: the running LaunchBox never reads it
def show(label,r):print(f"[{label}] rc={r.returncode}");print("   "+(r.stdout+r.stderr).decode(errors="replace").strip().replace("\n","\n   ")[-900:])
r=subprocess.run(["cmd.exe","/d","/c",str(beta/"Install.cmd"),str(root)],env=env,capture_output=True,timeout=300);show("beta1 install",r)
if r.returncode:sys.exit(1)
plan=json.loads((root/"ControllerLobby.Setup"/"active-plan.json").read_bytes())
b=subprocess.run([str(beta/"python"/"python.exe"),"-I","-B",str(branch/"harness"/"setup_bridge.py"),str(root)],env=env,capture_output=True,timeout=60);show("v1 bridge on beta1 install",b)
d=json.loads(b.stdout)
ok_bridge=d["Installed"] and d["InstalledPackagePin"]==plan["PackageSha256"].upper() and sorted(d["InstalledEntryIds"])==sorted([ra,ma])
print("bridge reports pin and both entry ids:",ok_bridge)
u=subprocess.run('cmd.exe /d /s /c ""'+str(root/"ControllerLobby.Setup"/"Uninstall.cmd")+'""',env=env,cwd=str(tmp),capture_output=True,timeout=300);show("installed Uninstall.cmd (v1 upgrade path)",u)
restored=(root/"Data"/"Emulators.xml").read_bytes()==xml
leftover=[str(p.relative_to(root)) for p in root.rglob("*") if "ControllerLobby" in str(p)]
print("removal rc0:",u.returncode==0,"| Emulators.xml byte-identical:",restored,"| ControllerLobby files left:",leftover)
b2=subprocess.run([str(beta/"python"/"python.exe"),"-I","-B",str(branch/"harness"/"setup_bridge.py"),str(root)],env=env,capture_output=True,timeout=60)
d2=json.loads(b2.stdout);print("rediscovery after removal: Installed=",d2["Installed"],"eligible=",sorted(e["Id"] for e in d2["Entries"] if e["Eligible"])==sorted([ra,ma]))
removed_ok=ok_bridge and u.returncode==0 and restored and not leftover and not d2["Installed"]
if len(sys.argv)<4 or not removed_ok:sys.exit(0 if removed_ok else 1)
new=Path(sys.argv[3]).resolve();setup=root/"ControllerLobby.Setup"
def cmd(path):return subprocess.run('cmd.exe /d /s /c ""'+str(path)+'" "'+str(root)+'""',env=env,cwd=str(tmp),capture_output=True,timeout=300)
i=subprocess.run(["cmd.exe","/d","/c",str(new/"Install.cmd"),str(root)],env=env,capture_output=True,timeout=300);show("new package install",i)
if i.returncode:sys.exit(1)
plan2=json.loads((setup/"active-plan.json").read_bytes())
b3=subprocess.run([str(new/"python"/"python.exe"),"-I","-B",str(new/"setup_bridge.py"),str(root)],env=env,capture_output=True,timeout=60);d3=json.loads(b3.stdout)
ok_new=d3["Installed"] and d3["InstalledPackagePin"]==plan2["PackageSha256"].upper()!=plan["PackageSha256"].upper() and sorted(d3["InstalledEntryIds"])==sorted([ra,ma])
print("new install reports a different pin and both entry ids:",ok_new)
v=cmd(setup/"Verify installation.cmd");show("installed Verify installation.cmd (new package)",v)
u2=cmd(setup/"Uninstall.cmd");show("installed Uninstall.cmd (new package)",u2)
restored2=(root/"Data"/"Emulators.xml").read_bytes()==xml
leftover2=[str(p.relative_to(root)) for p in root.rglob("*") if "ControllerLobby" in str(p)]
print("new removal rc0:",u2.returncode==0,"| Emulators.xml byte-identical:",restored2,"| ControllerLobby files left:",leftover2)
sys.exit(0 if (ok_new and v.returncode==0 and u2.returncode==0 and restored2 and not leftover2) else 1)
