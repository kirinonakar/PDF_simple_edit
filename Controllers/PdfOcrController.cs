using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using Loc = PDF_simple_edit.Services.LocalizationService;

namespace PDF_simple_edit.Controllers;

public sealed class PdfOcrController(Func<PdfDocumentManager> getManager, Func<int> getCurrentPage,
    Func<XamlRoot> getRoot, Func<Task> finishEditing, TextBlock status, Func<string, string, Task> showError)
{
    private bool _running;

    public async Task RunAsync()
    {
        if (_running || !getManager().IsLoaded) return;
        _running = true;
        var manager = getManager();
        try
        {
            await finishEditing();
            if (!ReferenceEquals(manager, getManager()) || !manager.IsLoaded) return;
            long version = manager.EditVersion;
            var options = await new PdfOcrDialogService().ShowAsync(getRoot(), manager.PageCount, getCurrentPage());
            if (options == null || !ReferenceEquals(manager, getManager()) || manager.EditVersion != version) return;
            using var cancellation = new CancellationTokenSource();
            var text = new TextBlock { Text = Loc.Format("OcrProgress", 0, options.PageIndices.Count, options.PageIndices[0] + 1),
                TextWrapping = TextWrapping.Wrap };
            var bar = new ProgressBar { Minimum = 0, Maximum = options.PageIndices.Count, Value = 0 };
            var panel = new StackPanel { Spacing = 16, MinWidth = 300 };
            panel.Children.Add(text);
            panel.Children.Add(bar);
            var dialog = new ContentDialog { Title = Loc.Get("OcrTitle"), XamlRoot = getRoot(), Content = panel,
                CloseButtonText = Loc.Get("Cancel") };
            bool allowClose = false;
            dialog.Closing += (_, args) =>
            {
                if (allowClose) return;
                args.Cancel = true;
                cancellation.Cancel();
                text.Text = Loc.Get("OcrCancelling");
                dialog.CloseButtonText = Loc.Get("OcrCancelling");
            };
            var progress = new Progress<PdfOcrProgress>(value =>
            {
                if (cancellation.IsCancellationRequested) return;
                text.Text = Loc.Format("OcrProgress", value.Completed, value.Total, value.PageNumber);
                bar.Value = value.Completed;
            });
            var shown = dialog.ShowAsync();
            PdfOcrResult result;
            try { result = await manager.ApplyOcrAsync(options, progress, cancellation.Token); }
            finally { allowClose = true; dialog.Hide(); await shown; }
            status.Text = Loc.Format("OcrCompleted", result.ChangedPages.Count, result.Words, result.SkippedPages, result.EmptyPages);
        }
        catch (OperationCanceledException) { status.Text = Loc.Get("OcrCancelled"); }
        catch (Exception error)
        {
            status.Text = Loc.Get("OcrFailed");
            await showError(Loc.Get("OcrTitle"), error.Message);
        }
        finally { _running = false; }
    }
}
