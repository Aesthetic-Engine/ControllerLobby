# ControllerLobby 1.0 — Compatibility Matrix

## 1. Supported Scope (1.0)

| Category | Supported in 1.0 | Notes |
| :--- | :--- | :--- |
| **Operating System** | Windows 10 / Windows 11 (`win-x64`) | Requires standard `System32\XInput1_4.dll` with `XInputGetState` resolved directly to `System32\XInput1_4.dll`. |
| **Frontend** | LaunchBox / BigBox | Updates only selected `<ApplicationPath>` entries in `<LaunchBox>\Data\Emulators.xml` via exclusive-lock compare-and-swap (`<EmulatorDir>\ControllerLobby.BigBox\ControllerLobby.BigBox.exe`). |
| **Controllers** | Up to 4 XInput controllers (`XInput 0..3`) | Standard Windows XInput gamepads/arcade sticks (`XInput 0..3`). Players press **Start** to join and Player 1 presses **Start** a second time (then releases all selected buttons) to launch. |
| **Device Hiding (HidHide)** | **Not required / Not used** | Operates via per-launch emulator arguments/configuration and explicit existing-controls fallback without kernel drivers or HidHide. |
| **RetroArch** | `retroarch.exe` (`win-x64`) | Qualifies launch arguments and base configuration before applying per-launch player joypad index mapping; falls back to existing controls when custom arguments/configs are outside qualified mapping. |
| **MAME** | `mame.exe` / `mame64.exe` (0.279 `win-x64`) | Qualifies launch arguments and configuration before applying per-launch `-ctrlrpath` / `-ctrlr` XInput device mapping; falls back to existing controls when custom arguments/configs are outside qualified mapping. |
| **DuckStation (PlayStation)** | `duckstation-qt-x64-ReleaseLTCG.exe`, mappings qualified against `0.1-9903-g8f0c9dd17` (no version gate: Setup records the files it finds at install), with its settings in `Documents\DuckStation` | Setup copies DuckStation's program files into `<EmulatorDir>\ControllerLobby.BigBox\DuckStationCopy` (no updater, no settings file). A mapped launch runs that copy with a session `settings.ini` built from yours; memory cards and saves stay in your folders. Players map to the controller ports your settings enable (one or two). Portable-mode DuckStation (settings in the program folder) and multitap are refused. |
| **PCSX2 (PlayStation 2)** | `pcsx2-qt.exe` 2.7.182 exactly, portable install (`portable.ini` beside the exe) | Setup copies the 2.7.182 release files into `<EmulatorDir>\ControllerLobby.BigBox\Pcsx2Copy`. A mapped launch runs that copy with a session `PCSX2.ini` built from yours, using PCSX2's standard XInput layout; memory cards, save states and BIOS stay in your folders. One or two players use ports 1 and 2. Three or four players use a multitap on port 1 (slots 1A–1D, in join order), turned on for that session only. Your own multitap setting, input profiles, per-game controller settings and USB devices give existing controls. Another PCSX2 version, or a non-portable install, is refused. |
| **Xemu (Xbox)** | Setup accepts any `xemu.exe`; join-order mapping only for 0.8.135 (commit `6318bb1`), pinned at launch | A mapped launch passes a session `xemu.toml` (`-config_path`) built from yours, with ports 1–4 bound in join order. Each joined controller's SDL GUID must be unique among all connected controllers; a joined controller that cannot be told apart gives existing controls. Another Xemu build starts exactly as LaunchBox would. |

---

## 2. Qualification & Verification Boundary

- **Automated Verification (M Scope):** Manual installation package integrity, `System32\XInput1_4.dll` provider probing, final-location `ControllerLobby.BigBox.exe --verify-only`, `Data\Emulators.xml` compare-and-swap redirection and restoration, single-interruption `repair` generation recovery, safe stop + tested manual recovery when a `repair` rollback is itself interrupted, delete-locked management cleanup retry without system Python, and runtime argument/fallback unit checks are verified at M in isolated synthetic roots.
- **Hands-on testing:** Installation and live gameplay have been reported working on one Windows x64 LaunchBox/BigBox PC with MAME 0.279 and RetroArch, including MAME TMNT: Turtles in Time and RetroArch NES. On the same PC: a two-player DuckStation game (Battle Arena Toshinden) and a four-player PCSX2 game through the port 1 multitap (WWE SmackDown, SLUS-20787) and a one-player Xemu game (Burnout 2) have been reported working through the lobby. Xemu with two or more controllers, and saving in a lobby session then loading in a normal session (or the reverse), have automated synthetic coverage only. Separate clean-PC (without preinstalled Python/.NET) and second-PC testing have not been completed; feedback from other configurations is welcome.

---

## 3. Explicitly Out of Scope in 1.0

The following platforms, emulators, and features are **excluded** from the 1.0 installer and runtime:

- **Other Standalone Emulators:** Standalone NES (Mesen/Nestopia/FCEUX), SNES (Snes9x/bsnes), Genesis, N64 (Project64), GameCube/Wii (Dolphin), PSP (PPSSPP), Switch, etc. Only RetroArch, MAME, DuckStation, PCSX2 and Xemu entries are supported.
  - *Note:* Console platforms launched through **RetroArch** use the RetroArch entry, subject to argument/configuration qualification and existing-controls fallback.
  - The installer ignores unrelated standalone emulator entries during automatic discovery and refuses them if explicitly passed via `--entries`.
- **PC / Windows Games & DOSBox:** Native Windows games, Steam/Epic shortcuts, and DOSBox entries are not wrapped by ControllerLobby.
- **DirectInput-Only / Raw HID Controllers without XInput:** Controllers must appear as standard Windows XInput slots (`0..3`).
- **More than 4 Simultaneous XInput Slots:** Standard Windows XInput supports slots `0..3`.

---

## 4. RetroArch Launch & Fallback Behavior

| Scenario | 1.0 Behavior |
| :--- | :--- |
| **Lobby Join, Launch, and Cancel** | Press **Start** on any XInput controller (`0..3`) to claim a player slot. Player 1 presses **Start** a second time and releases all selected buttons to launch. Hold **Back** on any controller for one second, or use **Esc** or **Cancel**, to cancel without launching RetroArch; hold **Y** for one second or click **Y - Bypass ControllerLobby** to launch unmapped. |
| **Qualified Launch Mapping vs. Existing-Controls Fallback** | When launch arguments and RetroArch configuration pass qualification, ControllerLobby supplies a transient configuration mapping assigned XInput slots to player joypad indices. If custom command-line flags or overrides are outside the qualified mapping contract, ControllerLobby uses **existing-controls fallback** and launches RetroArch without overriding your working configuration (do not delete working per-core or per-game overrides). |
| **Emulator or OS Binary Updated** | Normal launches verify the pinned `retroarch.exe` and `System32\XInput1_4.dll` hashes. After an update, the game starts with its existing controls and a notice. After updating RetroArch or Windows, run `<LaunchBox>\ControllerLobby.Setup\Upgrade or Repair.cmd` to re-validate, or run `Uninstall.cmd` to remove ControllerLobby. |

---

## 5. MAME 0.279 Launch & Fallback Behavior

| Scenario | 1.0 Behavior |
| :--- | :--- |
| **Lobby Join, Launch, and Cancel** | Press **Start** on any XInput controller (`0..3`) to claim a player slot. Player 1 presses **Start** a second time and releases all selected buttons to launch. Hold **Back** on any controller for one second, or use **Esc** or **Cancel**, to cancel without launching MAME; hold **Y** for one second or click **Y - Bypass ControllerLobby** to launch unmapped. |
| **Qualified Launch Mapping vs. Existing-Controls Fallback** | When launch arguments and MAME configuration pass qualification, ControllerLobby generates a transient controller mapping (`-ctrlrpath` / `-ctrlr`) binding player slots to the selected XInput controllers while preserving your existing MAME `cfg` directory. If custom arguments or configuration cannot be safely mapped, ControllerLobby uses **existing-controls fallback** so your existing MAME setup remains intact. |
| **Emulator or OS Binary Updated** | Normal launches verify the pinned `mame.exe` and `System32\XInput1_4.dll` hashes. After an update, the game starts with its existing controls and a notice. After updating MAME or Windows, run `<LaunchBox>\ControllerLobby.Setup\Upgrade or Repair.cmd` to re-validate, or run `Uninstall.cmd` to remove ControllerLobby. |

## 6. DuckStation, PCSX2 and Xemu Launch & Fallback Behavior

| Scenario | 1.0 Behavior |
| :--- | :--- |
| **Lobby Join, Launch, and Cancel** | The same as RetroArch and MAME. |
| **Settings changed during a lobby session** | A mapped launch runs on session settings, so a setting you change inside the emulator during that session (including a per-game one) is not kept; your own settings files are only read. With existing controls, DuckStation and PCSX2 run your own program and settings, and changes are kept as usual. Xemu with existing controls uses an unchanged session copy of your settings when your settings and the launch arguments qualify (changes not kept); otherwise (another Xemu build, unqualified settings or arguments, or a failed read) it starts as LaunchBox would, and Xemu writes your own `xemu.toml` as usual. |
| **Emulator or OS Binary Updated** | After an update to the emulator or `System32\XInput1_4.dll`, the game starts with its existing controls and a notice. **Repair (`Upgrade or Repair.cmd`, or Repair in Setup) is refused while DuckStation or PCSX2 is installed** (it does not rebuild their launch copies yet): run `Uninstall.cmd`, then install again. Setup's **Upgrade** to a new ControllerLobby version removes the installed version and installs the new one, so the refusal does not apply to it. Xemu has no launch copy, so Repair is not refused for it; Repair with Xemu installed has not been tested. |
| **Emulator already running** | A DuckStation, PCSX2 or Xemu lobby launch is refused while that emulator is already running. |

## Guided installer trial

An initial hands-on trial covered existing-install detection, Verify success and
closing Setup. Fresh install, Repair and Uninstall through the wizard remain
covered by synthetic checks rather than that hands-on trial. Other MAME versions
beyond 0.279 and specific controller translation software are not qualified here.
