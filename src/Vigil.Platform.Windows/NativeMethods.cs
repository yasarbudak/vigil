using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Vigil.Platform.Windows;

/// <summary>
/// LASTINPUTINFO (winuser.h): UINT cbSize, DWORD dwTime.
/// https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-lastinputinfo
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LastInputInfo
{
    public uint Size;
    public uint Time;
}

/// <summary>
/// SYSTEM_POWER_STATUS (winbase.h): BYTE ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
/// DWORD BatteryLifeTime, BatteryFullLifeTime.
/// https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-system_power_status
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SystemPowerStatus
{
    public byte AcLineStatus;
    public byte BatteryFlag;
    public byte BatteryLifePercent;
    public byte SystemStatusFlag;
    public uint BatteryLifeTime;
    public uint BatteryFullLifeTime;
}

/// <summary>
/// MOUSEINPUT (winuser.h): LONG dx, LONG dy, DWORD mouseData, DWORD dwFlags, DWORD time, ULONG_PTR dwExtraInfo.
/// https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MouseInput
{
    public int Dx;
    public int Dy;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

/// <summary>
/// KEYBDINPUT (winuser.h): WORD wVk, WORD wScan, DWORD dwFlags, DWORD time, ULONG_PTR dwExtraInfo.
/// https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardInput
{
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

/// <summary>The anonymous union inside INPUT. HARDWAREINPUT is smaller than MOUSEINPUT and never used, so it is left out.</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct InputUnion
{
    [FieldOffset(0)]
    public MouseInput Mouse;

    [FieldOffset(0)]
    public KeyboardInput Keyboard;
}

/// <summary>
/// INPUT (winuser.h): DWORD type, then a union of MOUSEINPUT, KEYBDINPUT and HARDWAREINPUT. The union holds a
/// pointer-sized member, so it is aligned to the pointer size: the struct is 40 bytes on 64-bit Windows (what
/// SendInput's cbSize must be, "If cbSize is not the size of an INPUT structure, the function fails") and 28 on 32-bit.
/// https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-input
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Input
{
    public uint Type;
    public InputUnion Data;

    /// <summary>A relative mouse move of zero pixels: no displacement, whatever the pointer speed or acceleration.</summary>
    public static Input MouseMoveWithoutDisplacement() => new()
    {
        Type = Win32Constants.InputMouse,
        Data = new InputUnion { Mouse = new MouseInput { Flags = Win32Constants.MouseEventMove } },
    };

    /// <summary>The release of a key that is not held down.</summary>
    public static Input KeyUp(ushort virtualKey) => new()
    {
        Type = Win32Constants.InputKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = Win32Constants.KeyEventKeyUp } },
    };
}

/// <summary>
/// The first fields of WTSINFOEXW and its WTSINFOEX_LEVEL1_W, which is as far as Vigil reads. WTSINFOEXW is a DWORD
/// Level followed by a union (WTSINFOEX_LEVEL_W) whose only member is WTSINFOEX_LEVEL1_W; that structure starts with
/// ULONG SessionId, WTS_CONNECTSTATE_CLASS SessionState, LONG SessionFlags. The union is placed at offset 8 because
/// WTSINFOEX_LEVEL1_W contains LARGE_INTEGER members, which need 8-byte alignment. The offsets follow from the
/// documented field order and types and the C alignment rules; the docs do not state them. The rest of the structure
/// (names, times, counters) is never read, so it is not declared.
/// https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ns-wtsapi32-wtsinfoexw
/// https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ns-wtsapi32-wtsinfoex_level1_w
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = Size)]
internal struct WtsInfoExHead
{
    /// <summary>Bytes that must have been returned before every field below can be read.</summary>
    public const int Size = 20;

    [FieldOffset(0)]
    public uint Level;

    [FieldOffset(8)]
    public uint SessionId;

    [FieldOffset(12)]
    public int SessionState;

    [FieldOffset(16)]
    public uint SessionFlags;
}

/// <summary>
/// Constants of the Win32 headers, checked against the Microsoft Learn page named in the adjacent comment. They live
/// apart from the imports so the pure code that uses them (input structures, classifiers) is not platform-attributed.
/// </summary>
internal static class Win32Constants
{
    // winuser.h INPUT: type INPUT_MOUSE = 0, INPUT_KEYBOARD = 1
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-input
    internal const uint InputMouse = 0;
    internal const uint InputKeyboard = 1;

    // winuser.h MOUSEINPUT dwFlags: MOUSEEVENTF_MOVE = 0x0001 ("Movement occurred"); MOUSEEVENTF_ABSOLUTE (0x8000) is not set,
    // so dx and dy are relative. https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput
    internal const uint MouseEventMove = 0x0001;

    // winuser.h KEYBDINPUT dwFlags: KEYEVENTF_KEYUP = 0x0002 ("the key is being released")
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput
    internal const uint KeyEventKeyUp = 0x0002;

    // Virtual-key codes: VK_F15 = 0x7E
    // https://learn.microsoft.com/en-us/windows/win32/inputdev/virtual-key-codes
    internal const ushort VirtualKeyF15 = 0x7E;

    // wtsapi32.h WTS_CURRENT_SERVER_HANDLE is ((HANDLE)NULL) and WTS_CURRENT_SESSION is ((DWORD)-1). The Learn pages
    // name the constants but not their values; the values are those of the MinGW-w64 and Wine headers (wtsapi32.h).
    internal const nint WtsCurrentServerHandle = 0;
    internal const uint WtsCurrentSession = 0xFFFFFFFF;

    // wtsapi32.h WTS_INFO_CLASS: an enumeration without explicit values, so WTSSessionInfoEx is its 26th member, 25
    // (WTSInitialProgram = 0 ... WTSSessionInfo = 24, WTSSessionInfoEx = 25).
    // https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ne-wtsapi32-wts_info_class
    internal const int WtsSessionInfoEx = 25;
}

/// <summary>
/// Every import below was checked against the Microsoft Learn page named in the adjacent comment.
/// Only the idle counter, posting and two session facts are used; no hooks, no raw input, no window or key reading.
/// </summary>
/// <remarks>
/// The assembly-level <see cref="DefaultDllImportSearchPathsAttribute"/> above restricts the lookup of the three DLLs to
/// System32, so a same-named DLL next to the executable or in the working directory is never loaded
/// (https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.defaultdllimportsearchpathsattribute).
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    // Kernel32.dll, User32.dll and Wtsapi32.dll are the "Library" rows of the requirements tables of the pages below.
    private const string Kernel32 = "kernel32.dll";
    private const string User32 = "user32.dll";
    private const string Wtsapi32 = "wtsapi32.dll";

    // sysinfoapi.h: DWORD GetTickCount(void). Milliseconds since system start, wraps after 49.7 days. This is the tick
    // count LASTINPUTINFO.dwTime is expressed in ("see GetTickCount"), so both wrap the same way.
    // https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-gettickcount
    [LibraryImport(Kernel32)]
    internal static partial uint GetTickCount();

    // winuser.h: BOOL GetLastInputInfo(PLASTINPUTINFO plii). Zero on failure; GetLastError has the reason.
    // Session-specific: it reports input only for the session that called it.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo
    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLastInputInfo(ref LastInputInfo info);

    // winuser.h: UINT SendInput(UINT cInputs, LPINPUT pInputs, int cbSize). Returns the number of events inserted; zero
    // means the input was blocked, and "neither GetLastError nor the return value will indicate" a block by UIPI.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput
    [LibraryImport(User32, SetLastError = true)]
    internal static unsafe partial uint SendInput(uint inputCount, Input* inputs, int inputSize);

    // wtsapi32.h: BOOL WTSQuerySessionInformationW(HANDLE hServer, DWORD SessionId, WTS_INFO_CLASS, LPWSTR *ppBuffer,
    // DWORD *pBytesReturned). The buffer is allocated by the system and must be freed with WTSFreeMemory. For
    // WTSSessionInfoEx it holds a WTSINFOEXW, not a string, so it is received as a plain pointer.
    // https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsquerysessioninformationw
    [LibraryImport(Wtsapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQuerySessionInformationW(
        nint server, uint sessionId, int infoClass, out nint buffer, out uint bytesReturned);

    // wtsapi32.h: VOID WTSFreeMemory(PVOID pMemory)
    // https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsfreememory
    [LibraryImport(Wtsapi32)]
    internal static partial void WTSFreeMemory(nint memory);

    // winbase.h: BOOL GetSystemPowerStatus(LPSYSTEM_POWER_STATUS). Zero on failure; GetLastError has the reason.
    // https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getsystempowerstatus
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}
