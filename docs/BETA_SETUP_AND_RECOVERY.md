# ControllerLobby Beta 1 — Setup, Repair, and Uninstall Guide

ControllerLobby Beta 1 adds pre-launch XInput controller assignment for **LaunchBox / BigBox** on **Windows 10 / 11 (x64)** for **MAME 0.279** and **RetroArch** using up to **4 connected XInput controllers**.

> **Deployment & Local State Scope:** Beta 1 does **not** require HidHide or kernel drivers. All deployed emulator wrapper files and manual installation tools are written inside your selected `<LaunchBox>` root (`<EmulatorDir>\ControllerLobby.BigBox\` and `<LaunchBox>\ControllerLobby.Setup\`). Outside `<LaunchBox>`, ControllerLobby uses `%LOCALAPPDATA%\ControllerLobby\` for runtime session/refusal state and logs, temporary uninstall recovery-runner staging (`%LOCALAPPDATA%\ControllerLobby\RecoveryRunner\<pin16>\`), and post-uninstall record archiving (`%LOCALAPPDATA%\ControllerLobby\SetupHistory\<id>\`).

---

## 1. Prerequisites

- **Operating System:** Windows 10 or Windows 11 (64-bit / `win-x64`) with standard system `System32\XInput1_4.dll`.
- **Frontend:** LaunchBox / BigBox installed in a local directory (for example `D:\LaunchBox`).
- **Emulators Configured in LaunchBox (`Data\Emulators.xml`):**
  - **RetroArch** (`retroarch.exe`)
  - **MAME 0.279** (`mame.exe` or `mame64.exe`)
- **Folder layout:** Emulator executables must be in subfolders inside the selected LaunchBox folder, and emulator entries must use relative paths such as `Emulators\MAME\mame.exe`. External emulator folders, absolute paths and parent-directory traversal are not supported by Beta 1 setup. Selected entries must use distinct emulator folders.
- **Close LaunchBox, BigBox, RetroArch, and MAME** before running setup, repair, or uninstall.

---

## 2. Download verification and Windows SmartScreen

The installer is not digitally signed, so Windows may display a SmartScreen
warning. Download only from the
[official ControllerLobby GitHub release](https://github.com/Aesthetic-Engine/ControllerLobby/releases).
Download `SHA256SUMS.txt` from that same release and compare the file's SHA-256
with its entry. For example, in PowerShell from your download folder:

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath .\ControllerLobby.Setup.exe
```

The complete hash must match the entry for that filename. Use the same command
with the ZIP filename to check a ZIP download. If it does not match, do not run
the file; obtain a fresh copy from the official release. If the origin is
uncertain or Windows reports a threat, stop and investigate rather than
bypassing the warning. Do not disable Defender, SmartScreen or other Windows
security protections to install ControllerLobby.

Checksums verify integrity against published values; they do not authenticate
the publisher or guarantee safety. The extracted package also includes
`PACKAGE_HASHES.json`, an inventory of its files used by setup's integrity checks.

---

## 3. Installing ControllerLobby Beta 1

Download the guided installer from the official release linked above. Open
`ControllerLobby.Setup.exe`, click **Browse**, select your LaunchBox folder,
confirm the available MAME/RetroArch entries, and click **Install**. Multiple
available entries of the same kind require an explicit selection. Setup checks
the proposed changes before installation. If an operation fails, open **Details**
for its recovery information; a failure does not mean installation completed.
Selecting an existing installation offers Verify, Repair and Uninstall. Wait for
Setup to report success before reopening LaunchBox/BigBox or your emulators.

The wizard retains its validated bundled package outside LaunchBox at
`%LOCALAPPDATA%\ControllerLobby\SetupCache\<bundleSHA256>\`. It refuses a cache
with changed, missing, extra or linked files. If cache validation fails, close
Setup and remove only the identified hash-named cache folder, then reopen Setup
to extract a fresh copy. Do not remove `RecoveryRunner`, `SetupHistory`, or your
installation's management folder as a cache fix.

The manual installation ZIP uses the root `.cmd` launchers (which run the bundled
isolated Python 3.13.1 runtime inside `python\`) or Command Prompt / PowerShell.

### Option A: Double-Click `.cmd` Launchers
1. Extract the package to any temporary folder.
2. Double-click **`Discover emulators.cmd`** (or **`Preview installation.cmd`**) and enter the path to your LaunchBox root folder when prompted (for example, `D:\LaunchBox`).
   - `Discover emulators.cmd` outputs JSON containing `"Selected"`, `"AllEntries"`, and `"Reader"` (`System32\XInput1_4.dll` provider check).
3. Double-click **`Install.cmd`** and enter your LaunchBox root folder.
   - The installer verifies all package hashes, probes `System32\XInput1_4.dll` (including export provider resolution for `XInputGetState`), deploys the runtime payload and locally generated `normal.json` and `bigbox.json` manifests into `<EmulatorDir>\ControllerLobby.BigBox\`, runs `ControllerLobby.BigBox.exe --verify-only` at each final deployed location, persists a self-contained management copy under `<LaunchBox>\ControllerLobby.Setup\`, and updates only the selected `<ApplicationPath>` entries in `Data\Emulators.xml` via exclusive-lock compare-and-swap.

### Option B: Command Line (Explicit Entry Selection)
If your `Data\Emulators.xml` contains multiple RetroArch or MAME entries, automatic discovery refuses to guess and asks you to pass the target entry GUIDs or unambiguous titles via `--entries`:

```cmd
python\python.exe -I -B bigbox_install.py discover --root "D:\LaunchBox"
python\python.exe -I -B bigbox_install.py preview  --root "D:\LaunchBox" --entries "RetroArch,MAME"
python\python.exe -I -B bigbox_install.py install  --root "D:\LaunchBox" --entries "RetroArch,MAME"
```

---

## 4. Lobby Controls During Game Launch

When you launch a configured RetroArch or MAME game from LaunchBox or BigBox:
- **Join Lobby:** Press **Start** on any connected XInput controller (`XInput 0..3`) to claim the next player slot (Player 1..4).
- **Launch Game:** Once at least Player 1 has joined, Player 1 presses **Start** a second time and then **releases all selected buttons** to start the emulator.
- **Cancel Launch:** Press **Esc** on the keyboard or click the **Cancel** control in the normal lobby window to abort the launch and return to LaunchBox / BigBox.
- **Qualified Mapping & Existing-Controls Fallback:** ControllerLobby inspects the launch arguments and configuration before applying per-launch controller slot mapping. When arguments or custom configurations are outside the qualified mapping path, ControllerLobby falls back to launching the emulator with your existing controls rather than forcing an incompatible override.

---

## 5. Self-Contained Management Copy (`<LaunchBox>\ControllerLobby.Setup\`)

During installation, ControllerLobby writes a complete management copy inside:

```text
<LaunchBox>\ControllerLobby.Setup\
```

This folder contains:
- `python\` — Bundled Windows x64 embeddable Python 3.13.1 runtime (`python313._pth` locked to isolated execution)
- `payload\` — Verified backup copy of the flat runtime payload (`255` files)
- `bigbox_install.py` and `shim_install.py` — Transactional installer, repair, and recovery scripts
- `package.json` and `active-plan.json` — Pinned package metadata, active entry plan, and generation number (`Generation`)
- `install-record.txt` — Transactional journal of staged/installed files, repair generations (`GEN PENDING` / `GEN STAGED` / `GEN DONE` / `GEN ABANDONED`), and `Data\Emulators.xml` compare-and-swap writes
- `install-record.txt.preimage-<hash16>.xml` — Pre-image snapshots recorded around `Data\Emulators.xml` compare-and-swap writes
- `install-record.txt.genpre-<hash16>.bin` — Durable pre-image snapshots recorded before `repair` activates updated `normal.json`, `bigbox.json`, or `active-plan.json` generations
- `Verify installation.cmd`, `Upgrade or Repair.cmd`, and `Uninstall.cmd`

**You can delete the downloaded package folder after installing.** Verification, repair, and uninstall run directly from `<LaunchBox>\ControllerLobby.Setup\` without requiring a system Python installation.

---

## 6. Verifying or Repairing After Emulator / Windows Updates

ControllerLobby pins the SHA-256 hash of your configured `retroarch.exe` / `mame.exe` and `System32\XInput1_4.dll` inside each entry's `<EmulatorDir>\ControllerLobby.BigBox\normal.json` manifest and `<LaunchBox>\ControllerLobby.Setup\active-plan.json`.

### What happens when you update RetroArch, MAME, or Windows?
- `Verify installation.cmd` (exit code `5` on mismatch) and normal launches detect when an emulator binary or `System32\XInput1_4.dll` has changed since installation or last repair.
- **Uninstalling NEVER requires the old emulator or OS binary hashes to match.** You can always run `Uninstall.cmd` after updating RetroArch, MAME, or Windows.

### Running Repair (`Upgrade or Repair.cmd`)
To re-validate updated `retroarch.exe`, `mame.exe`, or `System32\XInput1_4.dll` binaries (or restore a missing file in `<EmulatorDir>\ControllerLobby.BigBox\`):
1. Close LaunchBox, BigBox, RetroArch, and MAME.
2. Run **`<LaunchBox>\ControllerLobby.Setup\Upgrade or Repair.cmd`** (or `python\python.exe -I -B bigbox_install.py repair --root "D:\LaunchBox"`).
3. `repair` reconciles any single interrupted repair generation using durable `install-record.txt.genpre-<hash16>.bin` preimages, verifies `ControllerLobby.Setup\payload\`, restores any missing immutable payload file, re-hashes your emulator and `XInput1_4.dll` binaries, stages and activates the new `normal.json` and `bigbox.json` generation, re-runs `ControllerLobby.BigBox.exe --verify-only`, and increments `Generation` in `active-plan.json`.
   - **Foreign-Edit & Stage Protection:** If any deployed file in `<EmulatorDir>\ControllerLobby.BigBox\` was modified with unknown/foreign bytes, an unowned file was placed in `<EmulatorDir>\ControllerLobby.BigBox\`, or a `.cl-<nonce>.part` stage file contains unexpected or non-schema fields, `repair` refuses (exit code `3`) before modifying or deleting any file and preserves the unexpected file untouched.
   - **Interrupted-Rollback Boundary:** If a repair generation was interrupted and a *second* interruption occurs while rolling back that generation, automatic recovery stops safely (exit code `3` on `repair`/`remove`, exit code `5` on `verify`) with all user files, journal entries, and `.genpre` preimages preserved. Follow **Section 8** below to complete rollback manually.

---

## 7. Uninstalling (`Uninstall.cmd`) and Recovery Behavior

To remove ControllerLobby and restore your original `<ApplicationPath>` entries in `Data\Emulators.xml`:
1. Close LaunchBox, BigBox, RetroArch, and MAME.
2. Run **`<LaunchBox>\ControllerLobby.Setup\Uninstall.cmd`** (or `python\python.exe -I -B bigbox_install.py remove --root "D:\LaunchBox"`).
3. The uninstaller:
   - Reconciles any single interrupted repair generation state using durable journal and preimage evidence (or stops safely with exit code `3` if a prior rollback was itself interrupted; see **Section 8**).
   - Restores **only** the installer-owned `<ApplicationPath>` fields in `Data\Emulators.xml` via exclusive-lock compare-and-swap (`cas`), preserving any unrelated edits you made elsewhere in `Data\Emulators.xml`.
   - Verifies that every wrapped entry in `Data\Emulators.xml` has returned to its original `<ApplicationPath>` before deleting deployed files.
   - Deletes only installer-owned files in `<EmulatorDir>\ControllerLobby.BigBox\` whose SHA-256 hashes match the recorded installation/generation hashes.
   - Stages a portable recovery runner under `%LOCALAPPDATA%\ControllerLobby\RecoveryRunner\<pin16>\`, verifies delete-shareability across `<LaunchBox>\ControllerLobby.Setup\`, archives `install-record.txt` and preimage files to `%LOCALAPPDATA%\ControllerLobby\SetupHistory\<id>\`, and removes `<LaunchBox>\ControllerLobby.Setup\` and the staged recovery runner.

### Unrelated vs. Conflicting Edits During Uninstall
- **Unrelated `Data\Emulators.xml` edits (Preserved, Exit Code `0`):** Adding other emulators or changing unrelated fields in `Data\Emulators.xml` does **not** block uninstallation. Compare-and-swap restores only the wrapped `<ApplicationPath>` elements and keeps your other XML changes intact.
- **Conflicting `<ApplicationPath>` edits, modified deployed files, or locked management files (Kept, Exit Code `5` / `RECOVERY INCOMPLETE`):**
  - If you changed a wrapped entry's `<ApplicationPath>` in `Data\Emulators.xml` to a different path so compare-and-swap sees a conflict, `Uninstall.cmd` stops before deleting deployed files, logs `RECOVERY INCOMPLETE`, and exits with code **`5`**.
  - If a file inside `<EmulatorDir>\ControllerLobby.BigBox\` or `<LaunchBox>\ControllerLobby.Setup\` was modified with foreign bytes, `Uninstall.cmd` restores `Data\Emulators.xml`, keeps the modified file along with `<LaunchBox>\ControllerLobby.Setup\install-record.txt` and `install-record.txt.preimage-<hash16>.xml`, prints `RECOVERY INCOMPLETE`, and exits with code **`5`**.
  - If a management file in `<LaunchBox>\ControllerLobby.Setup\` is held open by another process without delete-sharing, `Uninstall.cmd` preserves the portable recovery runner and management files, logs `RECOVERY INCOMPLETE` (exit code **`5`**), and can be retried via `<LaunchBox>\ControllerLobby.Setup\Uninstall.cmd` once the handle is closed—even if no system Python is installed.

---

## 8. Manual Recovery Procedure for an Interrupted Rollback

While ordinary `install`, `repair`, and `remove` interruptions recover automatically on the next run, **automatic recovery is unavailable if a second interruption occurs during the rollback of an already-interrupted repair generation** (for example, if the process terminates after writing a `.genpre` rollback stage file before replacing the target, or after recording partial `GEN ABANDONED` lines before all targets in that generation are restored).

When this boundary is detected:
- `Verify installation.cmd` exits with code **`5`** and `Upgrade or Repair.cmd` / `Uninstall.cmd` stop safely with exit code **`3`** before modifying or deleting any file.
- No installation corruption or user-data loss occurs: all user files, unrelated/foreign files, `install-record.txt`, and `install-record.txt.genpre-<hash16>.bin` preimages remain intact inside `<LaunchBox>\ControllerLobby.Setup\`.
- The diagnostic message identifies the interrupted generation number `<g>`:
  `Rollback of generation <g> was interrupted; automatic recovery is unavailable. All user files, journal, and preimages are preserved in ControllerLobby.Setup; follow the manual recovery procedure in docs/BETA_SETUP_AND_RECOVERY.md.`

### Step-by-Step Manual Rollback Procedure

1. **Close Frontends and Emulators:** Ensure `LaunchBox.exe`, `BigBox.exe`, `retroarch.exe`, and `mame.exe` are closed.
2. **Inspect `install-record.txt`:** Open `<LaunchBox>\ControllerLobby.Setup\install-record.txt` and locate the trailing `GEN PENDING <g> <nonce> <old_sha> <new_sha> *<target_path>` lines for the interrupted generation `<g>`.
3. **Restore Each Target from Its `.genpre` Preimage and Remove Its Owned `.part` Stage:**
   For each `GEN PENDING <g> <nonce> <old_sha> <new_sha> *<target_path>` line in generation `<g>`:
   - Locate `<LaunchBox>\ControllerLobby.Setup\install-record.txt.genpre-<old_sha[:16]>.bin` (where `<old_sha[:16]>` is the first 16 uppercase hex characters of `<old_sha>`) and verify that its SHA-256 digest equals `<old_sha>`.
   - Copy `install-record.txt.genpre-<old_sha[:16]>.bin` over `<target_path>` so that `<target_path>` is restored to `<old_sha>`.
   - If the stage file `<target_path>.cl-<nonce>.part` exists and its SHA-256 digest equals `<old_sha>` or `<new_sha>`, delete `<target_path>.cl-<nonce>.part`. Do **not** delete any other file or any `.part` file whose SHA-256 digest does not match `<old_sha>` or `<new_sha>`.
4. **Remove the Incomplete Generation Lines from `install-record.txt`:**
   - Delete the trailing `GEN PENDING <g> ...`, `GEN STAGED <g> ...`, `GEN DONE <g> ...`, and `GEN ABANDONED <g> ...` lines for generation `<g>` from the end of `<LaunchBox>\ControllerLobby.Setup\install-record.txt`, preserving all earlier lines in `install-record.txt` unchanged.
5. **Revalidate the Restored Installation:**
   - Run `<LaunchBox>\ControllerLobby.Setup\Verify installation.cmd` (or `python\python.exe -I -B bigbox_install.py verify --root "<LaunchBox>"`). The restored manifests describe the previously accepted emulator and Windows/XInput binaries. If those binaries have changed, verification may report a hash mismatch (exit code **`5`**) even though the manual rollback steps completed correctly.
   - Keep your intended emulator/Windows updates. If verification reports only those expected external-binary changes, run `<LaunchBox>\ControllerLobby.Setup\Upgrade or Repair.cmd` (`repair`) to revalidate them, then run `<LaunchBox>\ControllerLobby.Setup\Verify installation.cmd` (`verify`) again and require `verified` with exit code **`0`**. Do not overwrite or downgrade emulator/Windows binaries just to satisfy an old manifest hash.
   - For an unexpected file change, unowned file, or another mismatch, preserve the file and recovery evidence and inspect the reported condition before continuing. An existing foreign-file refusal remains a refusal; do not delete unknown files merely to make `repair` pass. `Uninstall.cmd` (`remove`) remains an option when it can safely undo owned changes without matching obsolete external binary hashes.
