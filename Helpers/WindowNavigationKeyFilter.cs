using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.System;

namespace PDF_simple_edit.Helpers;

/// <summary>Consumes page navigation keys before WinUI menus receive them.</summary>
internal sealed class WindowNavigationKeyFilter : IDisposable
{
    private readonly IntPtr _window;
    private readonly Func<VirtualKey, bool> _tryHandle;
    private readonly HookProcedure _procedure;
    private readonly HashSet<VirtualKey> _consumedKeys = new();
    private IntPtr _hook;

    public WindowNavigationKeyFilter(IntPtr window, Func<VirtualKey, bool> tryHandle)
    {
        _window = window;
        _tryHandle = tryHandle;
        _procedure = Filter;
        // Thread-local WH_KEYBOARD: only this window's UI thread, never a global hook.
        _hook = SetWindowsHookEx(2, _procedure, IntPtr.Zero, GetCurrentThreadId());
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr Filter(int code, IntPtr keyCode, IntPtr keyData)
    {
        if (code == 0)
        {
            var key = (VirtualKey)keyCode.ToInt32();
            if (key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown)
            {
                bool released = (keyData.ToInt64() & 0x80000000L) != 0;
                if (released)
                {
                    if (_consumedKeys.Remove(key)) return new IntPtr(1);
                }
                else if (GetAncestor(GetFocus(), 2) == _window)
                {
                    try
                    {
                        if (_tryHandle(key))
                        {
                            _consumedKeys.Add(key);
                            return new IntPtr(1);
                        }
                    }
                    catch (Exception error)
                    {
                        // Never let a managed exception escape a native hook callback.
                        System.Diagnostics.Debug.WriteLine($"Navigation key filter: {error}");
                    }
                }
            }
        }
        return CallNextHookEx(_hook, code, keyCode, keyData);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _consumedKeys.Clear();
    }

    private delegate IntPtr HookProcedure(int code, IntPtr keyCode, IntPtr keyData);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, HookProcedure procedure, IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr keyCode, IntPtr keyData);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
