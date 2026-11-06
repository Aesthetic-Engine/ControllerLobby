namespace ControllerLobby.Lobby;

// HWND_NOTOPMOST can leave an inactive window above ordinary windows. After the WPF property is cleared,
// lower only this window without activation, movement, resizing or changes to its owner's z-order.
// Native z-order and the literal Chrome case still need an attended check.
public static class SessionWindowRelease
{
    public const uint Flags = 0x0213; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER
    public static bool Apply(nint own, Action clearTopmost, Func<nint, nint, uint, bool> position)
    {
        clearTopmost();
        if (own == 0) return true; // no native window exists to lower
        return position(own, (nint)1, Flags); // guard:lower-inactive
    }
}
