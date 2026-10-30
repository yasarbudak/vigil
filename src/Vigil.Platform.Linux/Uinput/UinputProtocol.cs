using System.Runtime.InteropServices;

namespace Vigil.Platform.Linux.Uinput;

/// <summary>
/// The ioctl request numbers of the generic Linux layout (include/uapi/asm-generic/ioctl.h):
/// <c>(dir &lt;&lt; 30) | (size &lt;&lt; 16) | (type &lt;&lt; 8) | nr</c>, with 8 bits for nr and type, 14 for the size and 2 for the
/// direction. x86 and arm64 use it unchanged, and they are the two Linux targets; powerpc, mips, sparc, alpha and parisc
/// override the field widths and are not supported. https://github.com/torvalds/linux/blob/master/include/uapi/asm-generic/ioctl.h
/// </summary>
internal static class IoctlNumber
{
    private const int TypeShift = 8;
    private const int SizeShift = 16;
    private const int DirectionShift = 30;
    private const uint None = 0;
    private const uint Write = 1;

    /// <summary><c>_IO(type, nr)</c>.</summary>
    public static uint NoArgument(char type, int nr) => Encode(None, type, nr, 0);

    /// <summary><c>_IOW(type, nr, argtype)</c> with an argument of <paramref name="size"/> bytes.</summary>
    public static uint WriteArgument(char type, int nr, int size) => Encode(Write, type, nr, size);

    private static uint Encode(uint direction, char type, int nr, int size) =>
        (direction << DirectionShift) | ((uint)size << SizeShift) | ((uint)type << TypeShift) | (uint)nr;
}

/// <summary>input_id (include/uapi/linux/input.h): __u16 bustype, vendor, product, version.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct InputId
{
    public ushort BusType;
    public ushort Vendor;
    public ushort Product;
    public ushort Version;
}

/// <summary>
/// uinput_setup (include/uapi/linux/uinput.h): struct input_id id; char name[UINPUT_MAX_NAME_SIZE]; __u32 ff_effects_max.
/// UINPUT_MAX_NAME_SIZE is 80, so the structure is 8 + 80 + 4 = 92 bytes.
/// https://github.com/torvalds/linux/blob/master/include/uapi/linux/uinput.h
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct UinputSetup
{
    public InputId Id;
    public fixed byte Name[UinputProtocol.MaxNameSize];
    public uint FfEffectsMax;
}

/// <summary>
/// input_event on a 64-bit kernel (include/uapi/linux/input.h): two 8-byte time fields (__kernel_ulong_t __sec, __usec), then
/// __u16 type, __u16 code, __s32 value: 24 bytes. The time is ignored on write (Documentation/input/uinput.rst: "timestamp
/// values below are ignored"). Linux-x64 and linux-arm64 are the only targets, so the 32-bit layout is not handled.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct InputEvent
{
    public long Seconds;
    public long Microseconds;
    public ushort Type;
    public ushort Code;
    public int Value;
}

/// <summary>
/// uinput constants and the event sequences. Every number is from the kernel's uapi headers
/// (https://github.com/torvalds/linux/tree/master/include/uapi/linux: uinput.h, input.h, input-event-codes.h), and the use
/// follows Documentation/input/uinput.rst: enable the event types and codes, UI_DEV_SETUP, UI_DEV_CREATE, write events, a
/// SYN_REPORT after each group, UI_DEV_DESTROY.
/// </summary>
internal static class UinputProtocol
{
    /// <summary>uinput.h: <c>#define UINPUT_MAX_NAME_SIZE 80</c></summary>
    internal const int MaxNameSize = 80;

    /// <summary>uinput.h: <c>#define UINPUT_IOCTL_BASE 'U'</c></summary>
    internal const char IoctlBase = 'U';

    internal static readonly uint DevCreate = IoctlNumber.NoArgument(IoctlBase, 1);
    internal static readonly uint DevDestroy = IoctlNumber.NoArgument(IoctlBase, 2);
    internal static readonly uint DevSetup = IoctlNumber.WriteArgument(IoctlBase, 3, 92);
    internal static readonly uint SetEvBit = IoctlNumber.WriteArgument(IoctlBase, 100, sizeof(int));
    internal static readonly uint SetKeyBit = IoctlNumber.WriteArgument(IoctlBase, 101, sizeof(int));
    internal static readonly uint SetRelBit = IoctlNumber.WriteArgument(IoctlBase, 102, sizeof(int));

    // input-event-codes.h
    internal const ushort EvSyn = 0x00;
    internal const ushort EvKey = 0x01;
    internal const ushort EvRel = 0x02;
    internal const ushort SynReport = 0;
    internal const ushort RelX = 0x00;
    internal const ushort RelY = 0x01;
    internal const ushort BtnLeft = 0x110;
    internal const ushort KeyF15 = 185;

    /// <summary>input.h: <c>#define BUS_VIRTUAL 0x06</c></summary>
    internal const ushort BusVirtual = 0x06;

    /// <summary>Documentation/input/uinput.rst: "/dev/uinput (or /dev/input/uinput)".</summary>
    internal static readonly string[] DevicePaths = ["/dev/uinput", "/dev/input/uinput"];

    internal const string DeviceName = "Vigil virtual input";

    internal const int EventSize = 24;

    /// <summary>
    /// Out and back, each followed by SYN_REPORT. A value of 0 would be dropped by the input core
    /// (drivers/input/input.c, input_get_disposition, case EV_REL: <c>&amp;&amp; value</c>), so a zero move cannot be the event.
    /// </summary>
    internal static byte[] MouseNudge() => Serialize(
        (EvRel, RelX, 1), (EvSyn, SynReport, 0),
        (EvRel, RelX, -1), (EvSyn, SynReport, 0));

    /// <summary>
    /// Press and release of F15. The release alone would be dropped: input_get_disposition passes an EV_KEY event only when
    /// it changes the key's state (<c>!!test_bit(code, dev-&gt;key) != !!value</c>).
    /// </summary>
    internal static byte[] F15Tap() => Serialize(
        (EvKey, KeyF15, 1), (EvSyn, SynReport, 0),
        (EvKey, KeyF15, 0), (EvSyn, SynReport, 0));

    internal static byte[] Serialize(params (ushort Type, ushort Code, int Value)[] events)
    {
        var bytes = new byte[events.Length * EventSize];
        for (var i = 0; i < events.Length; i++)
        {
            var e = new InputEvent { Type = events[i].Type, Code = events[i].Code, Value = events[i].Value };
            MemoryMarshal.Write(bytes.AsSpan(i * EventSize, EventSize), in e);
        }

        return bytes;
    }
}
