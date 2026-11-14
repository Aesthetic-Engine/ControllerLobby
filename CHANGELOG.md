# Changelog

## 1.0.0 — changes since Beta 1 (0.1.0-beta.1)

### Controller controls

- Hold **Back / View for one second** to cancel a launch while players are joining.
- Hold **Y for one second** to skip player assignment and launch with the game's
  existing controls.
- Both actions work from any connected XInput controller, including one that has
  not joined. Release a button held when the lobby opens or the controller connects,
  then press and hold it again. These shortcuts apply during player selection.
- The lobby shows the shortcuts and prompts you to keep holding the button.
  Keyboard Esc and the on-screen Cancel and **Y - Bypass ControllerLobby** buttons remain available.

### Launch recovery and emulator updates

- Normal play can recover an interrupted session on the next launch after obtaining
  the exclusive session lock. It archives the old session marker instead of leaving
  future launches blocked by that marker. An active session still prevents a second one.
- After a detected change to the configured MAME/RetroArch executable or Windows
  XInput library, games can launch with their existing controls. The lobby is skipped
  and a notice explains how to run Upgrade or Repair to restore player assignment.
- Changed or damaged ControllerLobby files still stop the launch. Early refusals
  now display a visible recovery message directing you to Verify, rather than exiting
  silently. The notice closes automatically after 10 seconds or can be dismissed.
- Other normal-play launch errors display a message and close after 15 seconds;
  Esc or Close can dismiss the finished error window.

### Setup, upgrades and recovery

- New Setup detects an earlier installed package and offers **Upgrade**. It uses
  that version's own uninstaller, then installs the new package with the same emulator choices.
- **Uninstall**, choose different emulators, and **Install** again in the same Setup window.
- Verify and Repair are scoped to the installed package version; a different
  detected version is offered Upgrade instead.
- Installed-uninstaller launch quoting handles folder names containing spaces,
  brackets and ampersands.
- Failed installs now recheck what remains. A recorded partial installation offers
  maintenance; an unconfirmed rollback blocks a fresh install instead of claiming
  that LaunchBox has safely returned to direct emulator launches.
- Checking an emulator box cannot bypass that recovery block. Folder refreshes keep
  an unconfirmed block until discovery explicitly reports recovery, and reopening
  Setup detects leftover installation files even without an active installation record.

### Diagnostics and documentation

- Normal-play controller logs omit unchanged samples to reduce repetitive output.
- Normal play uses a dedicated Sessions folder and prunes older session logs,
  including the legacy Beta 1 location. Interrupted-session markers and refusal
  reports also have retention limits.
- Updated usage and recovery guidance covers gamepad shortcuts, emulator/Windows
  updates, upgrades, and reinstalling with different emulator choices.

### New emulators

- **DuckStation (PlayStation)**, mappings qualified against 0.1-9903-g8f0c9dd17 (Setup
  does not check the version), with its settings in Documents. Setup copies the program files into a launch copy; lobby
  launches run it on session settings built from yours.
- **PCSX2 2.7.182 (PlayStation 2)**, portable install (Setup refuses another release), with PCSX2's standard XInput
  layout. One or two players use ports 1 and 2; three or four use a multitap on port 1
  (slots 1A to 1D in join order), turned on for that session only. Also a launch copy.
- **Xemu 0.8.135 (Xbox)**, up to four players in join order through a session
  settings file; another Xemu build starts exactly as LaunchBox would. Each joined
  controller's SDL GUID must be unique among all connected controllers, or the game
  starts with existing controls.
- After a supported emulator's files change, its games start with existing controls
  and a notice. A setting changed inside the emulator during a mapped lobby session is
  not kept; see the README for the existing-controls cases. Repair (`Upgrade or Repair.cmd`, or Repair
  in Setup) is refused while DuckStation or PCSX2 is installed; uninstall, then install
  again. Setup's Upgrade to a new version removes the old version and installs the new
  one, so the refusal does not apply to it.

### Compatibility and qualification

- Scope is **RetroArch, MAME, DuckStation, PCSX2 and Xemu through LaunchBox/BigBox on
  Windows x64**, with up to four XInput controllers. Other standalone emulators,
  native PC games, DOSBox and native DirectInput are not supported.
- MAME mappings remain based on **0.279**. There is no MAME version-number gate;
  other versions are not thereby qualified.
- Automated synthetic checks cover the controller logic, window behavior, recovery
  guards, and an upgrade from the packaged Beta 1 to 1.0 followed by verify and uninstall.
- Hands-on testing on one LaunchBox/BigBox PC: after upgrading from Beta 1, holding Back
  and holding Y on an Xbox 360 controller in slot 2, before anyone joined, worked as described.
- Clean-PC and second-PC testing have not been completed; feedback from other
  configurations is welcome.
