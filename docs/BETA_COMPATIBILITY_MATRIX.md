# ControllerLobby Beta 1 — Compatibility Matrix

## 1. Supported Scope (Beta 1)

| Category | Supported in Beta 1 | Notes |
| :--- | :--- | :--- |
| **Operating System** | Windows 10 / Windows 11 (`win-x64`) | Requires standard `System32\XInput1_4.dll` with `XInputGetState` resolved directly to `System32\XInput1_4.dll`. |
| **Frontend** | LaunchBox / BigBox | Updates only selected `<ApplicationPath>` entries in `<LaunchBox>\Data\Emulators.xml` via exclusive-lock compare-and-swap (`<EmulatorDir>\ControllerLobby.BigBox\ControllerLobby.BigBox.exe`). |
| **Controllers** | Up to 4 XInput controllers (`XInput 0..3`) | Standard Windows XInput gamepads/arcade sticks (`XInput 0..3`). Players press **Start** to join and Player 1 presses **Start** a second time (then releases all selected buttons) to launch. |
| **Device Hiding (HidHide)** | **Not required / Not used** | Operates via per-launch emulator arguments/configuration and explicit existing-controls fallback without kernel drivers or HidHide. |
| **RetroArch** | `retroarch.exe` (`win-x64`) | Qualifies launch arguments and base configuration before applying per-launch player joypad index mapping; falls back to existing controls when custom arguments/configs are outside qualified mapping. |
| **MAME** | `mame.exe` / `mame64.exe` (0.279 `win-x64`) | Qualifies launch arguments and configuration before applying per-launch `-ctrlrpath` / `-ctrlr` XInput device mapping; falls back to existing controls when custom arguments/configs are outside qualified mapping. |

---

## 2. Qualification & Verification Boundary

- **Automated Verification (M Scope):** Manual installation package integrity, `System32\XInput1_4.dll` provider probing, final-location `ControllerLobby.BigBox.exe --verify-only`, `Data\Emulators.xml` compare-and-swap redirection and restoration, single-interruption `repair` generation recovery, safe stop + tested manual recovery when a `repair` rollback is itself interrupted, delete-locked management cleanup retry without system Python, and runtime argument/fallback unit checks are verified at M in isolated synthetic roots.
- **Hands-on testing:** Installation and live gameplay have been reported working on one Windows x64 LaunchBox/BigBox PC with MAME 0.279 and RetroArch, including MAME TMNT: Turtles in Time and RetroArch NES. Separate clean-PC (without preinstalled Python/.NET) and second-PC testing have not been completed; feedback from other configurations is welcome.

---

## 3. Explicitly Out of Scope in Beta 1

The following platforms, emulators, and features are **excluded** from the Beta 1 installer and runtime:

- **Standalone Emulators (Non-RetroArch / Non-MAME):** Standalone NES (Mesen/Nestopia/FCEUX), SNES (Snes9x/bsnes), Genesis, PS1 (DuckStation), PS2 (PCSX2), N64 (Project64), GameCube/Wii (Dolphin), Switch, etc.
  - *Note:* Console platforms launched through **RetroArch** use the RetroArch entry, subject to argument/configuration qualification and existing-controls fallback.
  - The Beta 1 installer ignores unrelated standalone emulator entries during automatic discovery and refuses them if explicitly passed via `--entries`.
- **PC / Windows Games & DOSBox:** Native Windows games, Steam/Epic shortcuts, and DOSBox entries are not wrapped by Beta 1.
- **DirectInput-Only / Raw HID Controllers without XInput:** Controllers must appear as standard Windows XInput slots (`0..3`).
- **More than 4 Simultaneous XInput Slots:** Standard Windows XInput supports slots `0..3`.

---

## 4. RetroArch Launch & Fallback Behavior

| Scenario | Beta 1 Behavior |
| :--- | :--- |
| **Lobby Join, Launch, and Cancel** | Press **Start** on any XInput controller (`0..3`) to claim a player slot. Player 1 presses **Start** a second time and releases all selected buttons to launch. Press **Esc** or click **Cancel** in the normal lobby window to cancel without launching RetroArch. |
| **Qualified Launch Mapping vs. Existing-Controls Fallback** | When launch arguments and RetroArch configuration pass qualification, ControllerLobby supplies a transient configuration mapping assigned XInput slots to player joypad indices. If custom command-line flags or overrides are outside the qualified mapping contract, ControllerLobby uses **existing-controls fallback** and launches RetroArch without overriding your working configuration (do not delete working per-core or per-game overrides). |
| **Emulator or OS Binary Updated** | Normal launches verify the pinned `retroarch.exe` and `System32\XInput1_4.dll` hashes. After updating RetroArch or Windows, run `<LaunchBox>\ControllerLobby.Setup\Upgrade or Repair.cmd` to re-validate, or run `Uninstall.cmd` to remove ControllerLobby. |

---

## 5. MAME 0.279 Launch & Fallback Behavior

| Scenario | Beta 1 Behavior |
| :--- | :--- |
| **Lobby Join, Launch, and Cancel** | Press **Start** on any XInput controller (`0..3`) to claim a player slot. Player 1 presses **Start** a second time and releases all selected buttons to launch. Press **Esc** or click **Cancel** in the normal lobby window to cancel without launching MAME. |
| **Qualified Launch Mapping vs. Existing-Controls Fallback** | When launch arguments and MAME configuration pass qualification, ControllerLobby generates a transient controller mapping (`-ctrlrpath` / `-ctrlr`) binding player slots to the selected XInput controllers while preserving your existing MAME `cfg` directory. If custom arguments or configuration cannot be safely mapped, ControllerLobby uses **existing-controls fallback** so your existing MAME setup remains intact. |
| **Emulator or OS Binary Updated** | Normal launches verify the pinned `mame.exe` and `System32\XInput1_4.dll` hashes. After updating MAME or Windows, run `<LaunchBox>\ControllerLobby.Setup\Upgrade or Repair.cmd` to re-validate, or run `Uninstall.cmd` to remove ControllerLobby. |

## Guided installer trial

An initial hands-on trial covered existing-install detection, Verify success and
closing Setup. Fresh install, Repair and Uninstall through the wizard remain
covered by synthetic checks rather than that hands-on trial. Other MAME versions
beyond 0.279 and specific controller translation software are not qualified here.
