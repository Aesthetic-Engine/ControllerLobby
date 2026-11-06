# ControllerLobby

ControllerLobby makes local multiplayer easier in LaunchBox and BigBox. 

1. Select a Retroarch or MAME title from your Launchbox/Bigbox library
2. Connect usb controllers and pair wireless/bluetooth controllers you want to use
3. Press **Start** on each controller you want to use. Players will be joined the moment they press Start and the first controller will be Player 1.
4. When everyone is ready, Player 1 presses **Start** again to launch.

This is **Beta 1**. Bugs, feedback and feature requests are welcome!

## Compatibility

- Windows 10 / 11, 64-bit.
- An existing LaunchBox or BigBox installation with MAME or RetroArch configured.
- Up to four controllers that Windows exposes through **XInput**, such as Xbox-compatible gamepads.
- Emulator executables must be in subfolders inside your LaunchBox folder, with relative paths in the LaunchBox emulator entries (for example, `Emulators\MAME\mame.exe`). External emulator folders are not supported by Beta 1 setup.
- Includes controller mappings designed for **MAME 0.279**. Other MAME versions should work but have not been qualified for this beta.

Standalone emulators other than MAME/RetroArch, native PC games and DOSBox are planned for 1.0 but
outside this beta. ControllerLobby does not require HidHide or a virtual-controller
driver. No additional runtime software is needed; LaunchBox/BigBox and your
emulator must already be installed.

## Install

Download **ControllerLobby.Setup.exe** from the
[official GitHub release](https://github.com/Aesthetic-Engine/ControllerLobby/releases).

1. Close LaunchBox, BigBox and your emulators.
2. Open **ControllerLobby.Setup.exe**.
3. Click **Browse** and select the folder where LaunchBox is installed.
4. Confirm the MAME/RetroArch entries and click **Install**. Existing installations offer **Verify**, **Repair** and **Uninstall**.
5. Wait for Setup to report success, then open LaunchBox or BigBox and launch a game using a selected emulator entry.

Alternatively, download the **Manual installation ZIP**
(`ControllerLobby-v0.1.0-beta.1-win-x64.zip`), choose **Extract All**, then open
**Install.cmd** in the extracted folder.

The installer is not digitally signed, so Windows may display a SmartScreen
warning. Obtain downloads only from the official release above. Use its
**SHA256SUMS.txt** to check file integrity; checksums do not authenticate the
publisher. See [download verification](docs/BETA_SETUP_AND_RECOVERY.md#2-download-verification-and-windows-smartscreen).
Do not disable Windows security protections to install ControllerLobby.

## Questions

**Will it overwrite my controller settings?**

Mapped launches use separate session configuration rather than rewriting your
original emulator controller configuration. Game saves use their normal
locations. With existing-controls fallback, the emulator uses its normal
configuration and normal save behavior. Setup changes the launch paths of your
selected LaunchBox emulator entries; Uninstall restores the paths it owns and
preserves unrelated edits. If conflicting changes prevent safe restoration,
it stops with recovery information rather than overwriting them.

**Can I use Xbox, PlayStation or Switch controllers?**

The requirement is XInput support in Windows.
Xbox-compatible XInput gamepads fit that requirement. PlayStation and Switch
controllers are not supported natively as DirectInput-only devices; they must
already appear as XInput devices. Specific adapters or translation software
may allow Playstation and Switch controllers to work with ControllerLobby but they have not been qualified by this beta.

**Does the lobby appear for single-player games?**

Yes. It appears for all Retroarch and MAME games in your library, not just multiplayer games. This allows for players to choose their arcade stick for some games and a controller with analog sticks for others. For solo play, join with one controller and
press Start again when ready.

**Can I skip assignment or turn it off?**

Choose **Use existing controls** in the lobby to launch without applying lobby
join order. **Cancel** or **Esc** cancels the launch; it is not a skip button.
To remove the integration, close LaunchBox/BigBox and emulators, then use
**Uninstall** in Setup. Beta 1 does not provide a per-game disable switch.

**What if my game cannot use lobby mapping?**

The lobby displays a notice and proceeds using the game's existing controls,
without applying join order, provided the launch safety checks pass. You can
also choose **Use existing controls** yourself. A damaged or changed installation
can refuse to launch and require Verify/Repair; fallback does not bypass those checks.

**Do I need to recreate my LaunchBox setup?**

For the supported folder layout above, no. Setup discovers existing MAME/RetroArch emulator entries. Select the entries
you want to use; you do not need to recreate your games or emulator definitions.
Ambiguous entries require an explicit choice. See the setup guide for supported
folder layouts and entry selection.

## Help and beta feedback

Use [GitHub Issues](https://github.com/Aesthetic-Engine/ControllerLobby/issues/new)
for bugs, feedback and feature requests. For installation, maintenance or removal,
see the [setup and recovery guide](docs/BETA_SETUP_AND_RECOVERY.md).
After installation, maintenance shortcuts are also available in
`ControllerLobby.Setup` inside your LaunchBox folder.

Automated installation and maintenance checks and initial hands-on testing have
passed. Broader PC and controller coverage remains open for beta feedback.
The [technical release notes](docs/BETA_RELEASE_NOTES.md) and
[compatibility matrix](docs/BETA_COMPATIBILITY_MATRIX.md) describe the tested
scope and known limitations.

## Source layout and licences

The build starts at `integration/ControllerLobby.BigBox`. Its project references
and linked files include shared code under `experiment/`, `plugin/`, `helper/`,
`shim/`, `transaction/` and `adapters/`. Trial-named shared components remain
because the normal host uses them. Unreferenced historical experiments, local
receipts, development probes and private Git history are excluded from this
curated source repository. Legacy gate IDs retained in shared code are synthetic;
normal setup discovers real entries dynamically.

Original code is under [MIT](LICENSE). The derived MAME defaults retain their
[BSD-3-Clause notice](docs/MAME_DEFAULTS_LICENSE.txt). Bundled Python, .NET and
their dependencies retain their own [notices](docs/THIRD_PARTY_NOTICES.md).

## Contributing

Pull requests are welcome! For larger changes, please open an
[issue](https://github.com/Aesthetic-Engine/ControllerLobby/issues) first so we can
discuss the approach.

<details>
<summary>Build from source (contributors)</summary>

Install Git, Python 3.13, and .NET SDK **10.0.403**. Obtain the official
[Python 3.13.1 embeddable x64 ZIP](https://www.python.org/ftp/python/3.13.1/python-3.13.1-embed-amd64.zip).
The builder requires its SHA-256 to equal
`7B7923FF0183A8B8FCA90F6047184B419B108CB437F75FC1C002F9D2F8BCEC16`.

From a clean checkout, run:

```powershell
python -I -B tools/build.py --python-embed C:/Downloads/python-3.13.1-embed-amd64.zip --output dist/beta1
```

Use a new output directory for each run. `--dotnet` can select an explicit SDK
host. The SDK may download its pinned runtime/targeting packs during restore.
The build creates a source snapshot, a flat self-contained `runtime/payload`,
source and runtime inventories, and `package/` with launchers and licence notices.
`payload/source-manifest.json` binds the source commit and **all** curated source
files, including the installer and build tools. Local build logs remain outside
the package. This binds inputs; byte-for-byte reproducibility is not claimed.

Run focused synthetic verification (requires MinGW-w64 GCC for native stand-ins):

```powershell
python -I -B tools/check.py --build dist/beta1 --python-embed C:/Downloads/python-3.13.1-embed-amd64.zip --output artifacts/check-01 --cc C:/msys64/mingw64/bin/gcc.exe
```

Tests create synthetic LaunchBox roots and harmless child programs. They do not
read a live Emulators.xml, launch an emulator, or use physical controllers.
The WPF checks require a Windows desktop session. Outputs and receipts must not
be added to the public source tree.

</details>
