using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using WinRT.Interop;

namespace PDF_simple_edit.Controllers;

public sealed class WindowSettingsController
{
    private readonly Window _window;
    private readonly EditorSettingsService _settingsService;
    private readonly TextFontSettings _fontSettings;
    private readonly Action _applyFontSettings;
    private bool _isInitializing = true;
    private bool _isRestoring;

    public WindowSettingsController(
        Window window,
        EditorSettingsService settingsService,
        TextFontSettings fontSettings,
        Action applyFontSettings)
    {
        _window = window;
        _settingsService = settingsService;
        _fontSettings = fontSettings;
        _applyFontSettings = applyFontSettings;
    }

    public void CompleteInitialization() => _isInitializing = false;

    public void Save()
    {
        if (_isInitializing || _isRestoring)
            return;

        AppWindow? appWindow = GetAppWindow();
        bool isMaximized = appWindow?.Presenter is OverlappedPresenter presenter &&
                           presenter.State == OverlappedPresenterState.Maximized;
        WindowPlacement? placement = appWindow != null && !isMaximized
            ? new WindowPlacement(
                appWindow.Position.X,
                appWindow.Position.Y,
                appWindow.Size.Width,
                appWindow.Size.Height)
            : null;
        _settingsService.Save(placement, _fontSettings);
    }

    public void Load()
    {
        _isRestoring = true;
        try
        {
            WindowPlacement? placement = _settingsService.Load(_fontSettings);
            AppWindow? appWindow = GetAppWindow();
            if (appWindow != null && placement != null)
            {
                appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    placement.X,
                    placement.Y,
                    placement.Width,
                    placement.Height));
            }
            else
            {
                appWindow?.Resize(new Windows.Graphics.SizeInt32(1400, 900));
            }
            _applyFontSettings();
        }
        finally
        {
            _isRestoring = false;
        }
    }

    private AppWindow? GetAppWindow()
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(_window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }
}
