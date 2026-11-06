# Pinned read-only XInput reader

Standalone component for the authorized DirectIndexExperiment. No production
caller or launch authority is introduced. Open requires a canonical absolute
library and expected SHA-256, a specific named GetState or ordinal100 export and
the expected function-provider module path/hash. It pins file contents until
unload, uses a restricted dependency search and refuses mismatch or missing export
without selecting another DLL/export. The host must independently establish the
correct expected pins; this reader cannot infer the emulator's backend/view.

Read queries all four actual indices without compaction and records raw API
results, packet, buttons, triggers, axes and monotonic QPC/frequency. Only success
and ERROR_DEVICE_NOT_CONNECTED (1167) are determinate; all other errors invalidate
the selection sample. Failed reads have their returned payload cleared. This is
a sequence of four calls, not an atomic hardware snapshot or durable identity.

One sampler owns the reader. UI and lifecycle consumers use published immutable
snapshots from that sampler; the host stops/joins it before Dispose. No concurrent
read/dispose contract is provided. The reader never calls SetState, changes device
policy, creates virtual outputs or falls back between API roles.

DirectNativeChecks builds harmless native ABI stand-ins and exercises named and
ordinal100 reads, all four slots, unknown errors, disconnected stale payload,
absolute/hash/provider/export pins, no fallback and file lifetime/disposal.
Seven deliberate guard controls must produce executable assertion failures before
the final baseline passes. Compiler failures are not counted as caught controls.
This is M/native stand-in proof only. Installed API/export/module provenance,
physical correlation, actual sampling cadence and H remain qualification work.
