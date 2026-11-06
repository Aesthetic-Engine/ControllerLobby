# ControllerLobby Beta 1 — Bug Report & Feedback Template

When reporting an issue with ControllerLobby Beta 1, please copy and fill out the template below.

> **Privacy Reminder:** Before attaching logs or `Data\Emulators.xml` excerpts, redact personal Windows usernames or private file paths if desired. Never attach copyrighted ROMs or BIOS files.

---

## Environment

- **ControllerLobby Package Version / Commit:** `0.1.0-beta.1`
- **Windows Version & Build** (run `winver`): `Windows 10 / 11 Build ...`
- **LaunchBox / BigBox Version:** `...`
- **Emulator Used:** `[ ] RetroArch` / `[ ] MAME` (version: `...`)
- **Connected Controllers (Model & Connection Type — USB / Bluetooth / 2.4GHz Dongle):**
  1. `...`
  2. `...`
  3. `...`
  4. `...`

---

## Issue Category

- `[ ]` Setup / Discovery (`Discover emulators.cmd` / `Install.cmd`)
- `[ ]` Repair / Uninstall (`Upgrade or Repair.cmd` / `Uninstall.cmd`)
- `[ ]` Controller Lobby UI / Controller Detection
- `[ ]` Player Slot Mapping in Game (RetroArch / MAME)
- `[ ]` Return to BigBox / LaunchBox After Exiting Game

---

## Steps to Reproduce

1. 
2. 
3. 

**Expected Behavior:**  
...

**Actual Behavior:**  
...

---

## Diagnostic Output

Please run the following command from `<LaunchBox>\ControllerLobby.Setup\` (or your extracted package folder) and paste the JSON output:

```cmd
python\python.exe -I bigbox_install.py verify --root "<YourLaunchBoxRoot>"
```

```json
Paste output here
```

