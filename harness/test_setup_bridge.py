"""Setup discovery reports install state left behind without an active installation (M; synthetic LaunchBox root).

Usage: python -I -B harness/test_setup_bridge.py <new empty output dir>
"""
import json,subprocess,sys
from pathlib import Path
bridge=Path(__file__).resolve().with_name("setup_bridge.py")
case=Path(sys.argv[1]).resolve()
if case.exists():raise SystemExit("Output exists; choose a new directory")
root=case/"LaunchBox";(root/"Data").mkdir(parents=True);(root/"LaunchBox.exe").write_bytes(b"synthetic")
(root/"Data"/"Emulators.xml").write_bytes(b'<?xml version="1.0" standalone="yes"?>\r\n<LaunchBox>\r\n  <Emulator>\r\n    <ID>bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb</ID>\r\n    <Title>MAME</Title>\r\n    <ApplicationPath>Emulators\\MAME\\mame.exe</ApplicationPath>\r\n  </Emulator>\r\n</LaunchBox>')
setup=root/"ControllerLobby.Setup"
def discover():
    r=subprocess.run([sys.executable,"-I","-B",str(bridge),str(root)],capture_output=True,timeout=60)
    if r.returncode:raise SystemExit("bridge failed: "+(r.stdout+r.stderr).decode(errors="replace"))
    return json.loads(r.stdout)
failed=0
def need(ok,name):
    global failed
    print(("PASS " if ok else "FAIL ")+name);failed+=not ok
d=discover();need(not d["Installed"] and d.get("RecoveryPending") is False,"clean folder reports no leftover state")
setup.mkdir();d=discover();need(d.get("RecoveryPending") is False,"empty ControllerLobby.Setup folder reports no leftover state")
(setup/"install-record.txt").write_text("# FAILED: fixture; rolling back\n# rolled back: RECOVERY INCOMPLETE: fixture\n",encoding="utf-8")
d=discover();need(not d["Installed"] and d.get("RecoveryPending") is True,"kept record without an active plan reports leftover state")
(setup/"install-record.txt").unlink();(setup/"payload").mkdir();(setup/"payload"/"left.dll").write_bytes(b"x")
d=discover();need(d.get("RecoveryPending") is True,"kept files without an active plan report leftover state")
(setup/"active-plan.json").write_text(json.dumps({"PackageSha256":"A"*64,"Entries":[{"Id":"bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb"}]}),encoding="utf-8")
d=discover();need(d["Installed"] and "RecoveryPending" not in d,"active installation is reported as installed")
print(f"{5-failed}/5 bridge checks; M/synthetic root, no live LaunchBox")
sys.exit(1 if failed else 0)
