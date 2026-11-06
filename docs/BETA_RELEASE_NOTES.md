# ControllerLobby Beta 1 — Technical Release Notes

**Version:** `0.1.0-beta.1`
**Target Platform:** Windows 10 / 11 (`win-x64`)
**Supported Emulators:** RetroArch (`retroarch.exe`) and MAME 0.279 (`mame.exe` / `mame64.exe`) via LaunchBox / BigBox

---

These technical notes retain the qualification and recovery detail. For downloads,
installation and a short overview, start with the [README](../README.md).

The guided installer supports discovery, installation and existing-install
Verify/Repair/Uninstall. Its bundled-engine lifecycle has automated synthetic
coverage. An initial hands-on wizard trial covered existing-install detection,
Verify success and normal close. Fresh installation, Repair and Uninstall
through the wizard have synthetic coverage but have not been tested hands-on.
Separate clean-PC and second-PC tests have not been completed.

## Highlights

- **Pre-Launch XInput Controller Lobby for LaunchBox / BigBox:**
  - Press **Start** on any connected XInput controller (`XInput 0..3`) to claim Player 1–4 before your game starts.
  - Once ready, Player 1 presses **Start** a second time and releases all selected buttons to launch the game.
  - Cancel at any time from the normal lobby window by pressing **Esc** or clicking **Cancel**.
- **Qualified Mapping with Existing-Controls Fallback:**
  - Qualifies RetroArch and MAME launch arguments and configuration before applying per-launch controller slot mapping, and falls back to launching with your existing controls when custom arguments or configurations are outside the qualified mapping path.
- **No HidHide or Kernel Drivers Required:**
  - Works with standard Windows `System32\XInput1_4.dll` (verifying that `XInputGetState` resolves directly to `System32\XInput1_4.dll`) and up to 4 connected XInput controllers.
- **Emulator Discovery & Transactional Setup (`Schema: 2`):**
  - Discovers configured `RetroArch` and `MAME` entries in `<LaunchBox>\Data\Emulators.xml` (automatically ignoring dead/missing or out-of-root duplicate entries when a single live in-root entry exists).
  - Deploys the flat self-contained runtime payload and locally generated `normal.json` and `bigbox.json` manifests into `<EmulatorDir>\ControllerLobby.BigBox\`, runs `ControllerLobby.BigBox.exe --verify-only` at each deployed location, and updates only the selected `<ApplicationPath>` elements in `Data\Emulators.xml` via exclusive-lock compare-and-swap.
- **Self-Contained Management Copy (`<LaunchBox>\ControllerLobby.Setup\`):**
  - Bundles the official Windows x64 embeddable Python 3.13.1 runtime (`python-3.13.1-embed-amd64.zip`) and a verified backup copy of the payload inside `<LaunchBox>\ControllerLobby.Setup\`.
  - Verify, Repair, and Uninstall continue to work after deleting the downloaded package folder, even on machines without a system Python installation.
- **Crash-Resilient Repair & Uninstall Recovery:**
  - `Upgrade or Repair.cmd` persists durable generation preimages (`install-record.txt.genpre-<hash16>.bin`) and journals stage ownership before activating updated manifests, recovering automatically from single interrupted repair runs while refusing and preserving foreign edits and unknown-field stage files.
  - If a second interruption occurs during the rollback of an already-interrupted repair generation, the installer stops safely without data loss or installation corruption, preserves all user files, journal lines, and `.genpre` preimages in `<LaunchBox>\ControllerLobby.Setup\`, reports that automatic recovery is unavailable, and directs the user to the tested manual recovery procedure in `docs/BETA_SETUP_AND_RECOVERY.md`.
  - `Uninstall.cmd` restores only installer-owned `<ApplicationPath>` entries in `Data\Emulators.xml` (preserving unrelated `Emulators.xml` edits), works even after `retroarch.exe`, `mame.exe`, or `XInput1_4.dll` updates, and preserves a verified portable recovery runner under `%LOCALAPPDATA%\ControllerLobby\RecoveryRunner\<pin16>\` until management cleanup finishes.

---

## Qualification Scope & Known Limitations in Beta 1

1. **Testing coverage:** Automated checks cover package integrity, transactional install/verify/repair/remove recovery, manual recovery for interrupted rollback, and runtime argument/fallback verification in synthetic environments. Installation and live gameplay have been reported working on one Windows x64 LaunchBox/BigBox PC with MAME 0.279 and RetroArch. Separate clean-PC (without preinstalled Python/.NET) and second-PC hardware tests have not been completed; feedback from other configurations is welcome.
2. **Interrupted-Rollback Manual Recovery Boundary:** Ordinary `install`, `repair`, and `remove` interruptions recover automatically on the next run. If a second interruption occurs while rolling back an interrupted repair generation, automatic recovery is unavailable; the installer stops safely (exit code `3` on `repair`/`remove`, exit code `5` on `verify`) with all user files and preimages preserved, and recovery is completed via the step-by-step manual procedure in `docs/BETA_SETUP_AND_RECOVERY.md` (Section 8).
3. **RetroArch and MAME Only:** Standalone emulators (e.g., standalone Mesen, PCSX2, Dolphin, Project64) and native PC/DOSBox games are excluded from Beta 1.
4. **Up to 4 XInput Controllers:** Beta 1 reads standard Windows XInput slots `0..3`. DirectInput-only controllers without an XInput wrapper are not detected in the lobby.
5. **Local AppData Usage:** While deployed emulator wrappers and setup files reside under `<LaunchBox>`, the runtime stores local session/refusal state and logs under `%LOCALAPPDATA%\ControllerLobby\`, and setup uses `%LOCALAPPDATA%\ControllerLobby\RecoveryRunner\` and `%LOCALAPPDATA%\ControllerLobby\SetupHistory\` during uninstall recovery and record archiving.
6. **Unsigned Files & `PACKAGE_HASHES.json`:** Beta binaries and `.cmd` launchers are unsigned. `PACKAGE_HASHES.json` inventories the SHA-256 digest of every other file in the package (excluding `PACKAGE_HASHES.json` itself) for file-integrity verification, not publisher authentication. See `docs/BETA_SETUP_AND_RECOVERY.md` for Windows SmartScreen guidance.

---

## Integrity Verification

You can verify an installed deployment at any time by running:

```cmd
<LaunchBox>\ControllerLobby.Setup\Verify installation.cmd
```
or:
```cmd
<LaunchBox>\ControllerLobby.Setup\python\python.exe -I -B <LaunchBox>\ControllerLobby.Setup\bigbox_install.py verify --root "D:\LaunchBox"
```
