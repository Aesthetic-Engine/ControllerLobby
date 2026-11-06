using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControllerLobby.Lobby;

public enum SessionUiStage { Show, AppConstruction, WindowConstruction, Dispatcher, AppRun, AppRunReturn, TerminalClose, WindowClosing, WindowClosed, MarkerDisposal, TopmostChange }
public enum SessionExceptionCategory { InvalidOperation, ObjectDisposed, Argument, Io, Win32, Com, Other }
public enum SessionTerminalState { None, Cancel, Confirmed, Error }
public sealed record SessionUiFault(int Version, SessionUiStage Stage, SessionExceptionCategory Category, SessionTerminalState Terminal);

// Diagnostics never carry exception text/type names, titles, input or device identifiers.
// Capture precedes rethrow. The first origin survives outer wrappers and failed diagnostic writes.
public sealed class SessionUiFaults(Func<SessionRecord?> terminal, Action<SessionUiFault>? sink = null)
{
    private SessionUiFault? first;
    public SessionUiFault? First => Volatile.Read(ref first);
    public void Capture(SessionUiStage stage, Exception error)
    {
        var state = terminal()?.Outcome switch { "confirmed" => SessionTerminalState.Confirmed, "cancel" => SessionTerminalState.Cancel, "error" => SessionTerminalState.Error, _ => SessionTerminalState.None };
        var category = error switch { ObjectDisposedException => SessionExceptionCategory.ObjectDisposed, InvalidOperationException => SessionExceptionCategory.InvalidOperation, ArgumentException => SessionExceptionCategory.Argument, IOException => SessionExceptionCategory.Io, Win32Exception => SessionExceptionCategory.Win32, COMException => SessionExceptionCategory.Com, _ => SessionExceptionCategory.Other };
        var record = new SessionUiFault(1, stage, category, state);
        if (Interlocked.CompareExchange(ref first, record, null) is not null) return; // guard:first-ui-fault
        try { sink?.Invoke(record); }
        catch (Exception) { } // guard:best-effort-ui-fault; no retry or recursive logging
    }
    public void Guard(SessionUiStage stage, Action action)
    { try { action(); } catch (Exception e) { Capture(stage, e); throw; } }
    public T Guard<T>(SessionUiStage stage, Func<T> action)
    { try { return action(); } catch (Exception e) { Capture(stage, e); throw; } }
}

public static class SessionUiFaultFile
{
    public const int MaxBytes = 256;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
    public static byte[] Encode(SessionUiFault record)
    {
        if (record.Version != 1 || !Enum.IsDefined(record.Stage) || !Enum.IsDefined(record.Category) || !Enum.IsDefined(record.Terminal)) throw new InvalidOperationException("Invalid diagnostic enum.");
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Json) + "\n");
        if (bytes.Length > MaxBytes) throw new InvalidOperationException("Diagnostic bound exceeded.");
        return bytes;
    }
    // The optional stream factory is an isolated failure seam. Production never truncates/retries.
    public static void Write(string path, SessionUiFault record, Func<string, Stream>? openForTest = null)
    {
        byte[] bytes = Encode(record);
        using var output = openForTest is null ? new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read) : openForTest(path); // guard:create-new-ui-fault
        output.Write(bytes); output.Flush();
    }
}
