using Microsoft.UI.Input;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI.Core;

namespace PDF_simple_edit.Services;

public static class KeyboardStateService
{
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyLeftControl = 0xA2;
    private const int VirtualKeyRightControl = 0xA3;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    public static bool IsControlDown()
    {
        try
        {
            if ((GetAsyncKeyState(VirtualKeyControl) & 0x8000) != 0 ||
                (GetAsyncKeyState(VirtualKeyLeftControl) & 0x8000) != 0 ||
                (GetAsyncKeyState(VirtualKeyRightControl) & 0x8000) != 0)
                return true;
        }
        catch
        {
        }

        return new[] { VirtualKey.Control, VirtualKey.LeftControl, VirtualKey.RightControl }
            .Any(key => InputKeyboardSource.GetKeyStateForCurrentThread(key)
                .HasFlag(CoreVirtualKeyStates.Down));
    }

    public static bool IsControlKey(VirtualKey key) =>
        key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl;

    public static bool IsShiftDown() => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
        .HasFlag(CoreVirtualKeyStates.Down);
}
