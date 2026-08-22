using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDF_simple_edit.Services;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace PDF_simple_edit.Controllers;

public sealed class RecentFilesController
{
    private readonly RecentFilesService _recentFilesService;
    private readonly MenuFlyoutSubItem _menu;
    private readonly Func<StorageFile, Task> _openFileAsync;
    private readonly Func<string, string, Task> _showErrorAsync;

    public RecentFilesController(
        RecentFilesService recentFilesService,
        MenuFlyoutSubItem menu,
        Func<StorageFile, Task> openFileAsync,
        Func<string, string, Task> showErrorAsync)
    {
        _recentFilesService = recentFilesService;
        _menu = menu;
        _openFileAsync = openFileAsync;
        _showErrorAsync = showErrorAsync;
    }

    public void Load()
    {
        _recentFilesService.Load();
        RefreshMenu();
    }

    public void Add(string filePath)
    {
        _recentFilesService.Add(filePath);
        RefreshMenu();
    }

    private void RefreshMenu()
    {
        _menu.Items.Clear();
        if (_recentFilesService.Files.Count == 0)
        {
            _menu.Items.Add(new MenuFlyoutItem
            {
                Text = "최근 파일 없음",
                IsEnabled = false
            });
            return;
        }

        foreach (string filePath in _recentFilesService.Files)
        {
            var item = new MenuFlyoutItem
            {
                Text = Path.GetFileName(filePath),
                Tag = filePath
            };
            ToolTipService.SetToolTip(item, filePath);
            item.Click += RecentFileItem_Click;
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new MenuFlyoutSeparator());
        var clearItem = new MenuFlyoutItem { Text = "최근 파일 목록 지우기" };
        clearItem.Click += (_, _) =>
        {
            _recentFilesService.Clear();
            RefreshMenu();
        };
        _menu.Items.Add(clearItem);
    }

    private async void RecentFileItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string filePath })
            return;

        if (!File.Exists(filePath))
        {
            await _showErrorAsync("오류", "파일을 찾을 수 없습니다.");
            Remove(filePath);
            return;
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
            await _openFileAsync(file);
        }
        catch (Exception ex)
        {
            await _showErrorAsync("오류", $"파일을 여는 중 오류가 발생했습니다: {ex.Message}");
            Remove(filePath);
        }
    }

    private void Remove(string filePath)
    {
        _recentFilesService.Remove(filePath);
        RefreshMenu();
    }
}
