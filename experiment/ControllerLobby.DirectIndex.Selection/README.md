# Direct-index selection candidate

Shared selection logic retained as a runtime dependency of the normal Beta host.

This standalone, single-threaded model has no production caller. It uses existing
XInput indices 0-3 without compaction, virtual outputs, hiding or routing. The
explicit experiment policy is required; Disabled and unknown enum values refuse.
It does not change the shim's EscOnly/Routed policies, prepare a target, write
RetroArch configs or grant launch permission.

Supply coherent four-slot snapshots from a future qualified endpoint reader.
First call Observe until all eligible Start buttons are released, then each
distinct fresh Start edge assigns the next contiguous player. Simultaneous Start
edges, a new edge while another Start remains held, or selecting a used endpoint
latches failure. Once selection is complete, BeginConfirmation freezes the player
list and starts a bounded monotonic release deadline (positive, at most 30 seconds).
The integration chooses the bound explicitly; it is not a measured product value.

While awaiting release, poll TryConfirm with fresh snapshots. It checks the entire
connection mask, readable/valid shape and deadline, then requires every selected
endpoint's button mask to be zero. A mask change observed by Observe while waiting
also latches failure. Deadline equality expires. Cancellation and uncertainty are
terminal. The returned one-shot read-only SelectionSeal is a candidate snapshot,
not Supported/Ready. Future launch integration must revalidate release, endpoint
eligibility and topology before resume and continue monitoring during play.

The model does not run a timer or native reader: deadlines are checked when polled.
The caller must poll and honor failure; stopping polling cannot authorize launch.
An independent monitor is required for preparation/gameplay. Unselected button
presses do not block the release barrier; their emulator hotkey effects remain an
explicit experiment limitation. This component cannot distinguish one physical
press mirrored across endpoints from simultaneous presses, or identify replacement
devices between samples. Actual selector/emulator visibility and unambiguous
physical endpoint qualification remain H gates.

Config, lifecycle, native job/lock/recovery and package behavior retain their
separate qualification requirements. This standalone selector is not an
end-to-end launch qualification.

Checks use synthetic snapshots and an injected monotonic clock only. They cover
the literal index2/index0 case, a four-player permutation, release/edge arbitration,
uncertainty, cancellation, terminal behavior and timeout boundaries. Deliberate
guard mutations must produce failed assertions, not merely compiler errors. No
controller, emulator, owner config or paid service is accessed.
