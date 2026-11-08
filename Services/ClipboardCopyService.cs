using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace PDF_simple_edit.Services;

/// <summary>Publishes clipboard data while tolerating short-lived clipboard locks.</summary>
public static class ClipboardCopyService
{
    private static readonly SemaphoreSlim CopyGate = new(1, 1);
    private const int MaxAttempts = 6;

    // Call from the UI thread. Awaiting the delays preserves its synchronization
    // context and lets the window keep processing input while the clipboard is busy.
    public static async Task SetContentAsync(DataPackage content)
    {
        await CopyGate.WaitAsync();
        try
        {
            await RetryAsync(() => Clipboard.SetContent(content));

            // SetContent has already published the data. Flush only makes it
            // available after this app exits; its failure must not report a failed
            // copy or republish the package over a newer clipboard entry.
            try
            {
                await RetryAsync(Clipboard.Flush);
            }
            catch (Exception error)
            {
                Debug.WriteLine($"Clipboard persistence error (content was copied): {error}");
            }
        }
        finally
        {
            CopyGate.Release();
        }
    }

    private static async Task RetryAsync(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception error) when (attempt < MaxAttempts && IsTemporaryFailure(error))
            {
                await Task.Delay(50 * attempt);
            }
        }
    }

    private static bool IsTemporaryFailure(Exception error) => error.HResult is
        unchecked((int)0x800401D0) or // CLIPBRD_E_CANT_OPEN
        unchecked((int)0x800401D1) or // CLIPBRD_E_CANT_EMPTY
        unchecked((int)0x800401D2) or // CLIPBRD_E_CANT_SET
        unchecked((int)0x800401D4) or // CLIPBRD_E_CANT_CLOSE
        unchecked((int)0x80070005);   // E_ACCESSDENIED (clipboard temporarily unavailable)
}
