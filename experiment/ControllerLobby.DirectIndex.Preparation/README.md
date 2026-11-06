# Existing-endpoint RetroArch preparation

Uses the shared selection
assembly and a dedicated ExistingEndpointMap; never manufactures helper generations,
owned outputs or routed acknowledgements. The existing routed analyzer keeps its
selection/observation validation and now shares config inspection/emission. Both
paths additionally reject override redirects through rgui_config_directory,
input_remapping_directory or core_options_path (review finding 20638).

Prepare consumes a typed experiment request. Extra arguments are restricted to
the existing -f and --verbose flags. Unknown or duplicate options refuse; no host
tail is parsed, silently dropped or normalized by this module. The future manifest/
shim caller must validate its complete argument input before constructing a request.
The application, core, content and working directory are explicit absolute paths.
The core display name is supplied independently of the DLL filename; actual loaded
core identity/compatibility remains a qualification requirement.

The actual filesystem implementation reads source configs, rejects uncertain,
conflicting or unsupported chains, and captures source hashes plus positively
missing optional files. It includes core/content/game cfg, remap and opt paths,
plus the configured/default global core-options file. Source binary identities
for executable/core/content are streamed with a 512 MiB per-file experiment bound.
Reparse/uncertain paths and missing required files refuse.

Only a new session directory outside the owner root is written. It contains a copy
of the inspected applicable chain, a copied base whose known config/remap/options
locations point at session copies, and an append file with the joined player count,
literal selected indices and both save-on-exit keys false. Core option contents and
ordinary settings remain literal; no user source file is changed or deleted.
Incomplete artifacts are retained on failure. Existing session paths are never
reused. Cleanup requires ownership verification by the later session supervisor.

Preserved rechecks every captured source state (including absent inputs), binary
hash/size and created config artifacts. It is detection, not an atomic snapshot or
a permission to resume. The caller must repeat checks immediately before resume,
monitor eligibility/release/topology, and check sources after all completion paths.
Reparse races, actual runtime path/implicit-config semantics and all writable core
surfaces require their own qualification. The candidate does not assert those
properties solely because file generation succeeded.

The output is an immutable launch specification with exact argument tokens, a
Windows-quoted complete command line and original application working directory.
The checks verify token transport with native CommandLineToArgvW without launching
anything. Candidate/CanLaunch=false is mandatory: no target factory, Supported,
Ready, native visibility, built-in preparation equivalence or trial permit is
issued. The lifecycle still needs the real eligible caller and native services.

Checks use owned synthetic configs/binaries and the actual accepted selector;
no controller or emulator runs. They include the literal index2/index0 collision,
four-player permutation, source/artifact changes, optional-file appearance,
concurrent-copy uncertainty, caller argument snapshotting, source read-only paths,
overrides/remaps/options, redirect precedence and command quoting/bounds. The shared
analyzer's existing 63 checks remain neighbors. Unsupported redirect chains need
an automated corrective adapter revision in future; the user is not instructed to
redo emulator player bindings. MAME retains its generated session-profile ruling.
