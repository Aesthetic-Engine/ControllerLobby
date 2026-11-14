# ControllerLobby 1.0 — Technical Release Notes

**Version:** `1.0.0`
**Target Platform:** Windows 10 / 11 (`win-x64`)
**Supported Emulators:** RetroArch (`retroarch.exe`), MAME 0.279 (`mame.exe` / `mame64.exe`), DuckStation (PlayStation, `0.1-9903-g8f0c9dd17`), PCSX2 2.7.182 (PlayStation 2, portable) and Xemu 0.8.135 (Xbox) via LaunchBox / BigBox

---

These technical notes retain the qualification and recovery detail. For downloads,
installation and a short overview, start with the [README](../README.md). For the changes since Beta 1, see the [changelog](../CHANGELOG.md).

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
  - Cancel at any time by holding **Back** on any controller for one second, pressing **Esc** or clicking **Cancel**.
  - Hold **Y** on any controller for one second, or click **Y - Bypass ControllerLobby**, to launch without applying join order.
- **Qualified Mapping with Existing-Controls Fallback:**
  - Qualifies each supported emulator's launch arguments and configuration before applying per-launch controller slot mapping, and falls back to launching with your existing controls when custom arguments or configurations are outside the qualified mapping path.
- **No HidHide or Kernel Drivers Required:**
  - Works with standard Windows `System32\XInput1_4.dll` (verifying that `XInputGetState` resolves directly to `System32\XInput1_4.dll`) and up to 4 connected XInput controllers.
- **Emulator Discovery & Transactional Setup (`Schema: 2`):**
  - Discovers configured RetroArch, MAME, DuckStation, PCSX2 and Xemu entries in `<LaunchBox>\Data\Emulators.xml` (automatically ignoring dead/missing or out-of-root duplicate entries when a single live in-root entry exists).
  - Deploys the flat self-contained runtime payload and locally generated `normal.json` and `bigbox.json` manifests into `<EmulatorDir>\ControllerLobby.BigBox\`, runs `ControllerLobby.BigBox.exe --verify-only` at each deployed location, and updates only the selected `<ApplicationPath>` elements in `Data\Emulators.xml` via exclusive-lock compare-and-swap.
- **Self-Contained Management Copy (`<LaunchBox>\ControllerLobby.Setup\`):**
  - Bundles the official Windows x64 embeddable Python 3.13.1 runtime (`python-3.13.1-embed-amd64.zip`) and a verified backup copy of the payload inside `<LaunchBox>\ControllerLobby.Setup\`.
  - Verify, Repair, and Uninstall continue to work after deleting the downloaded package folder, even on machines without a system Python installation.
- **Crash-Resilient Repair & Uninstall Recovery:**
  - `Upgrade or Repair.cmd` persists durable generation preimages (`install-record.txt.genpre-<hash16>.bin`) and journals stage ownership before activating updated manifests, recovering automatically from single interrupted repair runs while refusing and preserving foreign edits and unknown-field stage files.
  - If a second interruption occurs during the rollback of an already-interrupted repair generation, the installer stops safely without data loss or installation corruption, preserves all user files, journal lines, and `.genpre` preimages in `<LaunchBox>\ControllerLobby.Setup\`, reports that automatic recovery is unavailable, and directs the user to the tested manual recovery procedure in `docs/SETUP_AND_RECOVERY.md`.
  - `Uninstall.cmd` restores only installer-owned `<ApplicationPath>` entries in `Data\Emulators.xml` (preserving unrelated `Emulators.xml` edits), works even after `retroarch.exe`, `mame.exe`, or `XInput1_4.dll` updates, and preserves a verified portable recovery runner under `%LOCALAPPDATA%\ControllerLobby\RecoveryRunner\<pin16>\` until management cleanup finishes.

---

## Qualification Scope & Known Limitations in 1.0

1. **Testing coverage:** Automated checks cover package integrity, transactional install/verify/repair/remove recovery, manual recovery for interrupted rollback, and runtime argument/fallback verification in synthetic environments. Installation and live gameplay have been reported working on one Windows x64 LaunchBox/BigBox PC with MAME 0.279 and RetroArch, and, on the same PC, a two-player DuckStation game, a four-player PCSX2 multitap game and a one-player Xemu game through the lobby. Xemu with two or more controllers, and save continuity between lobby and normal sessions, have automated synthetic coverage only. Separate clean-PC (without preinstalled Python/.NET) and second-PC hardware tests have not been completed; feedback from other configurations is welcome.
2. **Interrupted-Rollback Manual Recovery Boundary:** Ordinary `install`, `repair`, and `remove` interruptions recover automatically on the next run. If a second interruption occurs while rolling back an interrupted repair generation, automatic recovery is unavailable; the installer stops safely (exit code `3` on `repair`/`remove`, exit code `5` on `verify`) with all user files and preimages preserved, and recovery is completed via the step-by-step manual procedure in `docs/SETUP_AND_RECOVERY.md` (Section 8).
3. **Five Supported Emulators:** RetroArch, MAME, DuckStation, PCSX2 and Xemu. Other standalone emulators (e.g., standalone Mesen, Dolphin, Project64, PPSSPP) and native PC/DOSBox games are not supported. Version identity differs: Setup refuses any PCSX2 release other than 2.7.182; Xemu mapping is pinned to 0.8.135 at launch and another build starts exactly as LaunchBox would; DuckStation has no version gate (its mappings were qualified against `0.1-9903-g8f0c9dd17`) and Setup records the files it finds at install. For all three, files that change after installation give existing controls and a notice.
   - **DuckStation and PCSX2 launch copies:** Setup copies their program files into `<EmulatorDir>\ControllerLobby.BigBox\`; a mapped launch runs that copy on session settings built from yours, so a setting changed inside the emulator during a mapped session is not kept. With existing controls they run your own program and settings. Saves and memory cards stay in your folders.
   - **Repair is refused while DuckStation or PCSX2 is installed.** `Upgrade or Repair.cmd` and Setup's Repair do not rebuild their launch copies yet; after updating either emulator, run `Uninstall.cmd`, then install again. Setup's Upgrade to a new ControllerLobby version removes the installed version and installs the new one, so the refusal does not apply to it.
   - **PCSX2 players:** one or two use ports 1 and 2; three or four use a multitap on port 1 (slots 1A–1D in join order), turned on for that session only.
   - **Xemu:** each joined controller's SDL GUID must be unique among all connected controllers, or the game starts with existing controls. Existing controls use an unchanged session copy of your settings when they qualify, and otherwise start Xemu as LaunchBox would.
4. **Up to 4 XInput Controllers:** ControllerLobby reads standard Windows XInput slots `0..3`. DirectInput-only controllers without an XInput wrapper are not detected in the lobby.
5. **Local AppData Usage:** While deployed emulator wrappers and setup files reside under `<LaunchBox>`, the runtime stores local session/refusal state and logs under `%LOCALAPPDATA%\ControllerLobby\`, and setup uses `%LOCALAPPDATA%\ControllerLobby\RecoveryRunner\` and `%LOCALAPPDATA%\ControllerLobby\SetupHistory\` during uninstall recovery and record archiving.
6. **Unsigned Files & `PACKAGE_HASHES.json`:** ControllerLobby binaries and `.cmd` launchers are unsigned. `PACKAGE_HASHES.json` inventories the SHA-256 digest of every other file in the package (excluding `PACKAGE_HASHES.json` itself) for file-integrity verification, not publisher authentication. See `docs/SETUP_AND_RECOVERY.md` for Windows SmartScreen guidance.
7. **LaunchBox AutoHotkey Exit Scripts:** LaunchBox AutoHotkey exit scripts that force-close `{{{StartupEXE}}}` will force-close ControllerLobby and the game, with the interrupted lobby session recovered on the next launch. This bypasses the emulator’s normal shutdown, so unsaved progress or unfinished save and settings writes may be lost.

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
