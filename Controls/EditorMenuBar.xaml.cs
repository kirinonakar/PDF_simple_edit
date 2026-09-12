using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PDF_simple_edit.Models;
using Windows.Foundation;

namespace PDF_simple_edit.Controls;

public enum EditorMenuCommand
{
    NewDocument,
    OpenFile,
    SaveFile,
    SaveAsFile,
    Print,
    CloseFile,
    Undo,
    Redo,
    Find,
    ZoomIn,
    ZoomOut,
    FitToPage,
    TogglePagePanel,
    SelectTool,
    AddTextTool,
    HighlightTool,
    SignatureTool,
    AddImageTool,
    ExtractAllImages,
    MergePdf,
    SplitPdf,
    DeletePage,
    Settings,
    About,
}

/// <summary>Owns menu presentation and emits commands for the editor shell.</summary>
public sealed partial class EditorMenuBar : UserControl
{
    public event Action<EditorMenuCommand>? CommandRequested;
    public event TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs>? ToolAcceleratorInvoked;

    public MenuFlyoutSubItem RecentFilesMenu => MenuRecentFiles;
    public bool ShowPagePanel => MenuShowPagePanel.IsChecked;

    public EditorMenuBar()
    {
        InitializeComponent();
        InitializeZoomAccelerators();
    }

    private void Command_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string command } &&
            Enum.TryParse(command, out EditorMenuCommand requested))
            CommandRequested?.Invoke(requested);
    }

    private void ToolAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        ToolAcceleratorInvoked?.Invoke(sender, args);

    public void UpdateDocumentState(bool hasDoc, bool canUndo, bool canRedo)
    {
        MenuSave.IsEnabled = hasDoc;
        MenuSaveAs.IsEnabled = hasDoc;
        MenuPrint.IsEnabled = hasDoc;
        MenuClose.IsEnabled = hasDoc;
        MenuFind.IsEnabled = hasDoc;
        MenuSelect.IsEnabled = hasDoc;
        MenuAddText.IsEnabled = hasDoc;
        MenuHighlight.IsEnabled = hasDoc;
        MenuSignature.IsEnabled = hasDoc;
        MenuAddImage.IsEnabled = hasDoc;
        MenuExtractImages.IsEnabled = hasDoc;
        MenuSplitPdf.IsEnabled = hasDoc;
        MenuDeletePage.IsEnabled = hasDoc;

        MenuUndo.IsEnabled = hasDoc && canUndo;
        MenuRedo.IsEnabled = hasDoc && canRedo;
    }

    public void SetToolMode(EditToolMode mode)
    {
        MenuSelect.IsChecked = mode == EditToolMode.Select;
        MenuAddText.IsChecked = mode == EditToolMode.AddText;
        MenuHighlight.IsChecked = mode == EditToolMode.Highlight;
        MenuSignature.IsChecked = mode == EditToolMode.Signature;
    }

    private void InitializeZoomAccelerators()
    {
        // 확대 (+ or =)
        var keysIn = new[] { Windows.System.VirtualKey.Add, (Windows.System.VirtualKey)187 };
        foreach (var key in keysIn)
        {
            var acc = new KeyboardAccelerator { Key = key };
            acc.Invoked += ToolAccelerator_Invoked;
            MenuZoomIn.KeyboardAccelerators.Add(acc);
        }

        // 축소 (-)
        var keysOut = new[] { Windows.System.VirtualKey.Subtract, (Windows.System.VirtualKey)189 };
        foreach (var key in keysOut)
        {
            var acc = new KeyboardAccelerator { Key = key };
            acc.Invoked += ToolAccelerator_Invoked;
            MenuZoomOut.KeyboardAccelerators.Add(acc);
        }
    }
}
