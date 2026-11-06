using System;

namespace ControllerLobby.LobbyLink;

// One instance per child session. A marked foreground window is bound once; never silently retargeted.
// Matching endpoints cannot detect a switch away and back inside the interval and are not atomic proof.
public sealed class WindowObservation
{
    private IntPtr bound;
    public string Capture(int childPid, Func<ForegroundSample> foreground, Func<IntPtr, bool> matches,
        Func<IntPtr, string> snapshot)
    {
        string H(IntPtr h) => "0x" + h.ToString("X");
        string S(ForegroundSample f) => H(f.Hwnd) + ":" + f.OwnerPid;
        ForegroundSample before = default;
        try
        {
            before = foreground();
            if (before.Hwnd == IntPtr.Zero || before.OwnerPid != childPid || !matches(before.Hwnd)) // guard:eligible
                return "observation outcome=ineligible target=" + H(bound) + " before=" + S(before);
            if (bound != IntPtr.Zero && bound != before.Hwnd) // guard:bound
                return "observation outcome=target-changed target=" + H(bound) + " before=" + S(before);
            bound = before.Hwnd;
            string raw = snapshot(bound);
            bool valid = matches(bound); // guard:post-marker
            var after = foreground();
            bool same = after.Hwnd == bound && after.OwnerPid == childPid; // guard:endpoints
            return "observation outcome=" + (valid && same ? "matching-endpoints" : "changed-or-invalid") +
                " target=" + H(bound) + " before=" + S(before) + " after=" + S(after) + " " + raw;
        }
        catch (Exception)
        {
            // No exception text, class strings, titles or controller IDs cross this boundary.
            return "observation outcome=unknown target=" + H(bound) + " before=" + S(before);
        }
    }
}
