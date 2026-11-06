# ControllerLobby.LobbyLink (I1 plugin half, layer M)

This library contains no LaunchBox types. It provides the legacy gate,
verified payload binding, child-process ownership and result validation shared
by the launcher components. The normal Beta host discovers emulator IDs rather
than using the retained synthetic legacy game IDs.

- **`I1Gate` (section 2):** the lobby opens only for SMB3 on RetroArch and mk on MAME (pinned game and emulator ids).
  Another game on a claimed emulator is `BuiltInOnly`; an unclaimed emulator is `LeaveAlone`.
- **`PayloadPin` (4.1):**
  - Every file of the lobby folder is pinned by SHA256, and the folder must hold exactly those files.
  - Verification opens each file with read sharing only, and keeps the files locked against writes and deletion until
    the lobby has ended.
  - `LinkSession` starts the child only if it is a bare file name in that verified set (`payload-exe-not-pinned`
    otherwise).
- **`LobbyProcess` (4.2, 4.3, 4.6, 4.8):**
  - Starts `--session <guid> --game-title <text> --platform <text>`, quoted by Windows rules.
  - The child is created suspended inside a kill-on-close job, and job membership is verified before it runs.
  - It is created with `CREATE_NO_WINDOW`, so a console-subsystem lobby gets a hidden console of its own.
  - It inherits only the stdout pipe, with no stdin and no stderr, and gets the foreground grant.
  - Stdout is read as raw bytes. The first 16 KiB are kept; the rest is drained.
  - The output counts only if the reader finished, reached end of file and saw no read error (`OutputComplete`). The
    exit code counts only from a process proven to have ended.
  - No idle timeout. Guards and bounds, all placeholders:
    - `no-window` after 15 s;
    - `hung` after a further 10 s once Windows reports the window hung;
    - `no-exit-after-result` 3 s after the first LF;
    - `no-exit-after-window-closed` 3 s after a shown window has gone.
  - The job is terminated on every terminal path.
- **`LobbyResult` (4.2, 4.4, 4.7):**
  - Launches only on exactly one LF-terminated UTF-8 record (no BOM), followed by complete output and exit code 0.
  - The record needs the exact property set, version 1, outcome `confirmed`, and the session as canonical lowercase "D"
    text. Uppercase, braced, N, P and padded forms are refused.
  - It needs valid seats. Everything else cancels.
  - `Describe()` writes the validated fields as one JSON object, so a lobby-supplied reason cannot add a log line.

Checks: `harness/I1Checks/Test-I1Link.ps1 -Dotnet <dotnet.exe> -ReceiptDir <new folder>`. All runs are against
`harness/I1Checks/FakeLobby` or the checks program itself.
- **45 cases:**
  - Pure: the gate; framing, schema and seat validation; canonical session forms; incomplete reads; log escaping; the
    argument round trip; the payload pin, lock and executable binding.
  - Runs: responsive-idle; stderr flood; confirmed-but-no-exit; a window gone with no exit; an injected read fault
    after a valid record; a console-subsystem child; the plugin process killed while the lobby runs.
- **Fifteen compiling mutants**, each of which must finish the suite and fail its named guard:
  - session not checked, or checked loosely;
  - read completion ignored;
  - raw reason in the log;
  - window-gone bound removed;
  - console window allowed;
  - executable not bound to the payload;
  - missing output launches;
  - child outside the job;
  - no kill-on-close;
  - hang guard removed;
  - post-result bound removed;
  - game gate removed;
  - payload check reduced to the exe;
  - no-window guard removed.

Not covered here (H/L, attended): BigBox, foreground and focus, timing, a console window under a GUI parent, and
whether BigBox reacts behind the lobby.

## r3 focus handoff (brief section 9)

- **Seam:** `IWindowSystem` (`WindowSystem.cs`) wraps the grant, the foreground sample (handle, owner PID, class; never
  titles) and `SetForegroundWindow`. Each failing call's Win32 error is read inside the seam, before any logging.
- **Logs:** the caller PID and thread with the entry sample, then one sample per poll, logged when it changes.
- **Parent activation:** one `SetForegroundWindow` on the child's visible window, and only if this process owns the
  foreground at that moment. It is never repeated.
- **Focus guard:** `no-focus` if the child never owns the foreground within `FocusAcquireSeconds` (placeholder 5 s) of
  its window becoming visible. Once the child has had the foreground, nothing ends it for focus reasons.
- **Checks:** 50 cases.
  - Five focus cases against a scripted seam with a real lobby process: grant failure with its error, delayed success,
    another application owning the foreground, the guard ending the job, and no reactivation after a user switch.
  - Three new red controls: focus guard removed, activation unconditional, activation repeating.
  - The full control sweep runs before an install, not on every change . Receipt
    `receipts/i1-lobbylink/model-04` is partial by design; see its PARTIAL.txt.

## r3 z-order diagnostic (brief section 12)

- `ZOrderDiagnostic.Snapshot` (read-only), with the real reads in `RealWindowGraph`:
  - the lobby's native window state;
  - a `WindowFromPoint` hit-test candidate at the lobby's centre (child and root);
  - a bounded snapshot of the windows above the lobby that belong to BigBox or the lobby, or overlap it, disabled
    ones kept (64 windows and 20 ms at most; repeat, truncation, unreached lobby and failed reads marked).
- Whitelisted keys only, and never titles.
- `LobbyProcess` samples it while the lobby owns the foreground, at most every 500 ms, and logs it on change.
- **Diagnostic validation:**
  - A window with an unknown PID or rectangle is kept as a candidate.
  - The adapter re-checks liveness and owner PID after its reads, reporting `unstable(changed-during-read)` if either
    changed.
  - The budget is checked between calls and before the walk is declared complete. An overrun is reported; 20 ms is not
    a hard limit.
  - A hit test that finds no window says so explicitly.
- **Checks:** twelve pure cases against a scripted graph and scripted native reads.
  - Eight targeted controls are red, run with `-PureOnly`: unknown dropped, no revalidation, early budget removed,
    completion unchecked, root not resolved, repeat undetected, disabled dropped, node bound removed.
  - `Test-I1Link.ps1 -Only <names> [-PureOnly]` runs selected controls. `--pure-only` skips the cases that start lobby
    processes.
