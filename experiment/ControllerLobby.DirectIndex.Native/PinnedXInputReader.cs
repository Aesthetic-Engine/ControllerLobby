using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ControllerLobby.DirectIndex.Selection;

namespace ControllerLobby.DirectIndex.Native;

public enum StateExport { NamedGetState, Ordinal100 }
public sealed record ReaderBinding(string LibraryPath, string LibrarySha256, StateExport Export,
    string FunctionModulePath, string FunctionModuleSha256);
public sealed record SlotRead(int Index, uint Result, uint Packet, ushort Buttons, byte LeftTrigger,
    byte RightTrigger, short LeftX, short LeftY, short RightX, short RightY);
public sealed record EndpointRead(long Qpc, long QpcFrequency, IReadOnlyList<SlotRead> Slots)
{
    public EndpointSample Sample
    {
        get
        {
            bool shape = Slots.Count == 4 && Slots.Select(s => s.Index).SequenceEqual(new[] { 0, 1, 2, 3 });
            bool readable = shape && Slots.All(s => s.Result is 0 or 1167);
            int mask = shape ? Slots.Aggregate(0, (m, s) => m | (s.Result == 0 ? 1 << s.Index : 0)) : 0;
            return new(mask, Array.AsReadOnly(shape ? Slots.Select(s => s.Result == 0 ? s.Buttons : (ushort)0).ToArray() : new ushort[4]), readable);
        }
    }
}

// Read only: no SetState, rumble, driver settings or endpoint creation. Exactly
// the requested absolute DLL and export are used; there is no loader fallback.
public sealed class PinnedXInputReader : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Pad
    { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LeftX, LeftY, RightX, RightY; }
    [StructLayout(LayoutKind.Sequential)] private struct State { public uint Packet; public Pad Pad; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GetState(uint index, out State state);
    private readonly List<FileStream> pins = [];
    private nint library;
    private GetState? getState;
    public ReaderBinding Binding { get; private set; } = null!;
    private PinnedXInputReader() { }

    public static PinnedXInputReader Open(ReaderBinding expected)
    {
        var reader = new PinnedXInputReader();
        try
        {
            if (/*guard:reader-export-kind*/ !Enum.IsDefined(expected.Export)) throw new ArgumentException("Unrecognized state export");
            string path = reader.Pin(expected.LibraryPath, expected.LibrarySha256);
            string functionPath = reader.Pin(expected.FunctionModulePath, expected.FunctionModuleSha256);
            reader.library = LoadLibraryExW(path, 0, 0x00000100 | 0x00000800); // DLL_LOAD_DIR | SYSTEM32
            if (reader.library == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Pinned XInput DLL could not load");
            nint address = expected.Export == StateExport.Ordinal100 ? GetProcAddressOrdinal(reader.library, (nint)100) : GetProcAddress(reader.library, "XInputGetState");
            if (address == 0) throw new InvalidOperationException("Requested state export is absent; no fallback");
            if (!GetModuleHandleExW(0x00000004 | 0x00000002, address, out nint provider)) throw new Win32Exception(Marshal.GetLastWin32Error(), "State export provider is unreadable");
            var actualPath = new StringBuilder(32768);
            uint length = GetModuleFileNameW(provider, actualPath, actualPath.Capacity);
            if (/*guard:reader-provider*/ (length == 0 || length >= actualPath.Capacity - 1 || !Path.GetFullPath(actualPath.ToString()).Equals(functionPath, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("State export provider differs from pinned module");
            reader.getState = Marshal.GetDelegateForFunctionPointer<GetState>(address);
            reader.Binding = expected with { LibraryPath = path, FunctionModulePath = functionPath };
            return reader;
        }
        catch { reader.Dispose(); throw; }
    }

    private string Pin(string input, string hash)
    {
        if (/*guard:reader-pin-shape*/ (!Path.IsPathFullyQualified(input) || !System.Text.RegularExpressions.Regex.IsMatch(hash, "^[A-Fa-f0-9]{64}$")))
            throw new ArgumentException("Absolute module path and SHA-256 required");
        string path = Path.GetFullPath(input);
        for (string? ancestor = path; ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
            if (File.GetAttributes(ancestor).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Reparse module path");
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        pins.Add(file); // retain read-only, no-write/no-delete sharing until unload
        if (/*guard:reader-pin-hash*/ !Convert.ToHexString(SHA256.HashData(file)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Pinned module hash mismatch");
        return path;
    }

    public EndpointRead Read()
    {
        if (getState == null) throw new ObjectDisposedException(nameof(PinnedXInputReader));
        var slots = new SlotRead[4];
        for (uint index = 0; /*guard:all-four-slots*/ index < 4; index++)
        {
            uint result = getState(index, out State state);
            if (/*guard:clear-failed-state*/ result != 0) state = default;
            slots[index] = new((int)index, result, state.Packet, state.Pad.Buttons, state.Pad.LeftTrigger,
                state.Pad.RightTrigger, state.Pad.LeftX, state.Pad.LeftY, state.Pad.RightX, state.Pad.RightY);
        }
        return new(Stopwatch.GetTimestamp(), Stopwatch.Frequency, Array.AsReadOnly(slots));
    }

    public void Dispose()
    {
        getState = null;
        bool failed = library != 0 && !FreeLibrary(library);
        library = 0;
        foreach (var pin in pins) pin.Dispose();
        pins.Clear();
        if (failed) throw new Win32Exception(Marshal.GetLastWin32Error(), "Pinned library unload failed");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryExW(string path, nint file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", ExactSpelling = true, SetLastError = true)] private static extern nint GetProcAddressOrdinal(nint module, nint ordinal);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetModuleHandleExW(uint flags, nint address, out nint module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetModuleFileNameW(nint module, StringBuilder path, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeLibrary(nint module);
}
