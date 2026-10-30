using System.Runtime.CompilerServices;
using Vigil.Platform.Windows;

namespace Vigil.Tests;

/// <summary>
/// The structures handed to Win32 must have exactly the layout the headers define, or SendInput fails (cbSize) and
/// the session query is read at the wrong offsets. Pure layout checks: nothing here calls Win32 and they run on every OS.
/// </summary>
public class WindowsNativeLayoutTests
{
    [Fact]
    public void INPUT_has_the_size_SendInput_demands()
    {
        // DWORD type, then a union that holds a pointer-sized member (ULONG_PTR dwExtraInfo): 40 bytes on 64-bit
        // Windows (4 + 4 padding + 32), 28 on 32-bit (4 + 24).
        var expected = nint.Size == 8 ? 40 : 28;

        Assert.Equal(expected, Unsafe.SizeOf<Input>());
    }

    [Fact]
    public void The_input_union_members_start_at_the_same_offset_and_the_larger_one_decides_the_size()
    {
        var mouse = Unsafe.SizeOf<MouseInput>();
        var keyboard = Unsafe.SizeOf<KeyboardInput>();

        Assert.Equal(nint.Size == 8 ? 32 : 24, mouse); // 5 x 4 bytes, padding, ULONG_PTR
        Assert.Equal(nint.Size == 8 ? 24 : 16, keyboard); // 2 x 2 + 2 x 4 bytes, padding, ULONG_PTR
        Assert.Equal(mouse, Unsafe.SizeOf<InputUnion>());
    }

    [Fact]
    public void LASTINPUTINFO_and_SYSTEM_POWER_STATUS_have_their_header_sizes()
    {
        Assert.Equal(8, Unsafe.SizeOf<LastInputInfo>()); // UINT + DWORD
        Assert.Equal(12, Unsafe.SizeOf<SystemPowerStatus>()); // 4 BYTE + 2 DWORD
    }

    [Fact]
    public void The_mouse_nudge_is_a_relative_move_of_zero_pixels()
    {
        var input = Input.MouseMoveWithoutDisplacement();

        Assert.Equal(0u, input.Type); // INPUT_MOUSE
        Assert.Equal(0x0001u, input.Data.Mouse.Flags); // MOUSEEVENTF_MOVE only: no ABSOLUTE, no button, no wheel
        Assert.Equal(0, input.Data.Mouse.Dx);
        Assert.Equal(0, input.Data.Mouse.Dy);
        Assert.Equal(0u, input.Data.Mouse.MouseData);
        Assert.Equal(0u, input.Data.Mouse.Time); // 0: the system supplies the time stamp
        Assert.Equal(nuint.Zero, input.Data.Mouse.ExtraInfo);
    }

    [Fact]
    public void The_F15_event_is_only_a_key_up()
    {
        var input = Input.KeyUp(Win32Constants.VirtualKeyF15);

        Assert.Equal(1u, input.Type); // INPUT_KEYBOARD
        Assert.Equal(0x7E, input.Data.Keyboard.VirtualKey); // VK_F15
        Assert.Equal(0x0002u, input.Data.Keyboard.Flags); // KEYEVENTF_KEYUP only: no key down, no unicode, no scan code
        Assert.Equal(0, input.Data.Keyboard.ScanCode);
        Assert.Equal(0u, input.Data.Keyboard.Time);
    }

    [Fact]
    public void The_session_query_constants_match_the_documentation()
    {
        Assert.Equal(25, Win32Constants.WtsSessionInfoEx); // 26th member of WTS_INFO_CLASS, which has no explicit values
        Assert.Equal(0xFFFFFFFFu, Win32Constants.WtsCurrentSession); // (DWORD)-1
        Assert.True(Win32Constants.WtsCurrentServerHandle == nint.Zero); // (HANDLE)NULL
    }

    [InlineArray(33)]
    private struct Chars33
    {
        private char _element;
    }

    [InlineArray(21)]
    private struct Chars21
    {
        private char _element;
    }

    [InlineArray(18)]
    private struct Chars18
    {
        private char _element;
    }

#pragma warning disable CS0649 // The structures below are only measured, never filled.

    /// <summary>WTSINFOEX_LEVEL1_W with every field the documentation lists, array lengths from wtsapi32.h (WINSTATIONNAME_LENGTH 32, USERNAME_LENGTH 20, DOMAIN_LENGTH 17, each plus one).</summary>
    private struct Level1
    {
        public uint SessionId;
        public int SessionState;
        public int SessionFlags;
        public Chars33 WinStationName;
        public Chars21 UserName;
        public Chars18 DomainName;
        public long LogonTime;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long CurrentTime;
        public uint IncomingBytes;
        public uint OutgoingBytes;
        public uint IncomingFrames;
        public uint OutgoingFrames;
        public uint IncomingCompressedBytes;
        public uint OutgoingCompressedBytes;
    }

    /// <summary>WTSINFOEXW: DWORD Level, then the union (here only its one member).</summary>
    private struct InfoEx
    {
        public uint Level;
        public Level1 Data;
    }

#pragma warning restore CS0649

    private static int OffsetOf<TStruct, TField>(ref TStruct origin, ref TField field) =>
        (int)Unsafe.ByteOffset(ref Unsafe.As<TStruct, byte>(ref origin), ref Unsafe.As<TField, byte>(ref field));

    [Fact]
    public void The_declared_head_of_WTSINFOEXW_sits_where_the_full_structure_puts_its_fields()
    {
        // The managed layout rules of a 64-bit process match the C rules the header is compiled with: a long is
        // aligned to 8 bytes, so the union starts at offset 8, and the three leading fields of the level 1 structure follow.
        if (nint.Size != 8)
        {
            return;
        }

        var full = default(InfoEx);
        Assert.Equal(0, OffsetOf(ref full, ref full.Level));
        Assert.Equal(8, OffsetOf(ref full, ref full.Data.SessionId));
        Assert.Equal(12, OffsetOf(ref full, ref full.Data.SessionState));
        Assert.Equal(16, OffsetOf(ref full, ref full.Data.SessionFlags));
        Assert.Equal(WtsInfoExHead.Size, 16 + sizeof(int)); // The head ends where SessionFlags ends.

        // Fill the declared head and read it back at those offsets.
        var head = new WtsInfoExHead { Level = 1, SessionId = 2, SessionState = 3, SessionFlags = 4 };
        ref var bytes = ref Unsafe.As<WtsInfoExHead, byte>(ref head);
        Assert.Equal(WtsInfoExHead.Size, Unsafe.SizeOf<WtsInfoExHead>());
        Assert.Equal(1u, Unsafe.ReadUnaligned<uint>(ref bytes));
        Assert.Equal(2u, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bytes, 8)));
        Assert.Equal(3, Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref bytes, 12)));
        Assert.Equal(4u, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bytes, 16)));
    }
}
