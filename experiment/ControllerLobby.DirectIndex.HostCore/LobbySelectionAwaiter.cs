using ControllerLobby.DirectIndex.Selection;

namespace ControllerLobby.DirectIndex.HostCore;

public static class LobbySelectionAwaiter
{
    // No idle deadline while controllers wake. Continue checking the sampler
    // and view so a dead sampler cannot leave an apparently live lobby waiting.
    public static async Task<SelectionSeal> Wait(Task<SelectionSeal> selection,Func<bool> canContinue,CancellationToken cancel)
    {
        while(!selection.IsCompleted)
        {
            cancel.ThrowIfCancellationRequested();
            if(/*guard:collect-wait-monitor*/!canContinue())throw new IOException("Lobby input/view no longer current");
            await Task.WhenAny(selection,Task.Delay(50,cancel)).ConfigureAwait(false);
        }
        return await selection.WaitAsync(cancel).ConfigureAwait(false);
    }
}
