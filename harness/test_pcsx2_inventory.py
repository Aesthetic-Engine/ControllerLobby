"""The PCSX2 launch-copy allow-list exists twice: harness/pcsx2_inventory.py for Setup and Pcsx2Inventory.cs for the
host. This check requires them to be equal, and the pinned exe to be the inventory's pcsx2-qt.exe (M)."""
import re, sys
from pathlib import Path
root = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(root / "harness"))
import pcsx2_inventory as inv
cs = (root / "adapters/pcsx2/ControllerLobby.Pcsx2/Pcsx2Inventory.cs").read_text(encoding="utf-8")
host = dict(re.findall(r'\[@"([^"]+)"\] = "([0-9A-F]{64})"', cs))
session = (root / "adapters/pcsx2/ControllerLobby.Pcsx2/Pcsx2Session.cs").read_text(encoding="utf-8")
pin = re.search(r'PinnedSha256 = "([0-9A-F]{64})"', session).group(1)
checks = [("same-files", host == inv.FILES, f"{len(host)} host entries, {len(inv.FILES)} setup entries"),
          ("exe-is-pin", inv.FILES.get("pcsx2-qt.exe") == inv.PIN == pin, "pin mismatch"),
          ("no-updater-or-utils", not any(k.lower() == "updater.exe" or k.lower().startswith("utils\\") for k in inv.FILES), "updater or utils listed"),
          ("nonempty", len(inv.FILES) > 200, str(len(inv.FILES)))]
for name, ok, why in checks: print(("PASS " if ok else "FAIL ") + name + ("" if ok else ": " + why))
failed = sum(1 for _, ok, _ in checks if not ok)
print(f"Cases={len(checks)} Passed={len(checks) - failed} Failed={failed}; M source comparison")
sys.exit(1 if failed else 0)
