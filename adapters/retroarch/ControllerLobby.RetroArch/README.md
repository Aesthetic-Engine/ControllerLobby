# Standalone RetroArch mapping/config analyzer

Scope: Windows path semantics,
.NET 10, no third-party packages. This module is not called by the production shim.

`MappingAnalyzer.Analyze` accepts selected seats and an observer declaration for
the same session/helper/topology/observation epoch. Each pad output must have one
matching current-generation xinput/base-zero index with observer/receipt references.
These are internal model declarations, not authenticated wire messages. Matching
them proves consistency only, not physical identity, freshness, isolation or routing.
The caller must eventually supply genuine authenticated helper/observer evidence.

`Candidate` means append-config text can be generated. `CanLaunch` is always false;
there is no Supported/Ready or qualification-success API. Q-A/Q-O/Q-B/Q-D, real shim
preparation/command-line qualification and helper/release/recovery/completion remain
open. PlayGame Q-C does not apply to the shim path (ROUTING-SESSION section 3 step 5).
The candidate does not attach itself to the shim command line or start an emulator.

The text sets `input_max_users` to the contiguous joined count, exactly one
`input_playerN_joypad_index` per joined player, and both save-on-exit keys false.
The P1=index2/P2=index0 fixture checks the generated configuration against eight-user
defaults. This is an M model of active ports; core-facing Q-D must still measure
that RetroArch actually leaves the remaining ports neutral. Q-B must establish the
emulator-visible backend mapping; an xinput driver string is not that proof.

The analyzer resolves core, content-directory and game `.cfg` and `.rmp` paths from
the caller-provided core identity/content and base config. It records raw file hashes
and explicit missing-file stamps for those candidate inputs. It does not discover
the actual core name from a DLL filename, infer archive-member names, process command
line overrides, or promise a complete effective runtime chain. Preparation and
qualification must establish those inputs. Paths within the RetroArch root, plain
relative paths and `:/` or `:\` application-relative directories are supported;
external, environment-expanded, parent-traversing or unresolved locations refuse.

Parsing is deliberately conservative: UTF-8 (optional BOM), lowercase assignment
keys, simple double-quoted or unquoted values, `#` comments, no include evaluation,
no quoted escape interpretation and no duplicate keys. Ambiguous syntax refuses.
The [Libretro config API](https://buildbot.libretro.com/doxygen/a04769_source.html)
documents includes and config precedence; this subset does not claim full parser or
emulator-chain equivalence. Unknown ordinary keys are retained only for analysis.
Every override/remap is checked conservatively even if automatic loading is disabled.
Protected keys in those files and nonidentity joined-port remaps refuse. Corrective
session-copy overrides are a separate future adapter revision, not implemented here.

Native seats remain in the returned selection and produce Unverified pending native
profile/capability work. Analyzer failures feed future static qualification or runtime
FixIt; this module creates no new Cancel-only or keyboard-out-of-product UI policy.

`DiskConfigReader` only reads, bounds file size and rejects reparse ancestors and
invalid UTF-8. `CandidateFile.WriteNew` writes one fixed-name file in a new session
directory outside the declared owner root, with matching result/root and exclusive
file creation. Existing paths are not overwritten; failures retain partial artifacts.
Whole-chain snapshots are not atomic: future integration must revalidate the complete
input key before Ready and invalidate stale confirmation. No files are cleaned up
automatically by this module. No emulator settings or device policy is changed.

Checks link the actual analyzer/file code. They use in-memory observations and owned
scratch configs only. Native controller input, RetroArch execution, archive handling,
frontend callbacks, supported/Ready qualification and end-to-end launch are unmeasured.


ExistingEndpointMapping is a separate candidate-only entry for the approved direct-index experiment. It uses explicit existing slot indices and a visibility-receipt reference, never helper/output declarations. Routed validation is retained; both paths share config inspection and reject unresolved override redirects of config/remap/core-options roots. See experiment/ControllerLobby.DirectIndex.Preparation/README.md for session copies, option preservation, exact launch-spec generation and remaining qualification gates. A receipt reference alone is not endpoint authentication or launch permission.
