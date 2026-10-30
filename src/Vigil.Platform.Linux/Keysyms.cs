namespace Vigil.Platform.Linux;

/// <summary>X11 keysym values, which the X11 backend and the portal's NotifyKeyboardKeysym both use.</summary>
internal static class Keysyms
{
    /// <summary>
    /// keysymdef.h: <c>#define XK_F15 0xffcc</c>
    /// https://gitlab.freedesktop.org/xorg/proto/xorgproto/-/blob/master/include/X11/keysymdef.h
    /// </summary>
    internal const int F15 = 0xffcc;
}
