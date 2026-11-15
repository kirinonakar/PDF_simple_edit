using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PDF_simple_edit.Controllers;

internal static class InlineTextFormatting
{
    public static void ConnectFlyouts(RichEditBox editor, Func<Task> finish, Action? formatChanged = null)
    {
        // Own the flyouts for this editor so their focus events can be tracked
        // without attaching session handlers to shared XAML resources.
        var selection = new TextCommandBarFlyout { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft };
        var context = new TextCommandBarFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        editor.SelectionFlyout = selection;
        editor.ContextFlyout = context;

        async Task FinishIfUnfocusedAsync()
        {
            if (editor.IsLoaded && editor.FocusState == FocusState.Unfocused && !selection.IsOpen && !context.IsOpen)
                await finish();
        }

        void CheckAfterFocusReturns() =>
            editor.DispatcherQueue.TryEnqueue(async () => await FinishIfUnfocusedAsync());

        editor.LostFocus += (_, _) => CheckAfterFocusReturns();
        selection.Closed += (_, _) => CheckAfterFocusReturns();
        context.Closed += (_, _) => CheckAfterFocusReturns();

        if (formatChanged == null) return;
        var observedButtons = new HashSet<AppBarToggleButton>();
        void ObserveCommands(TextCommandBarFlyout flyout)
        {
            foreach (var button in flyout.PrimaryCommands.OfType<AppBarToggleButton>())
            {
                if (!observedButtons.Add(button)) continue;
                // Read the document after the built-in command applies its format.
                button.Click += (_, _) => editor.DispatcherQueue.TryEnqueue(() =>
                {
                    if (editor.IsLoaded) formatChanged();
                });
            }
        }
        selection.Opened += (_, _) => ObserveCommands(selection);
        context.Opened += (_, _) => ObserveCommands(context);
    }

    public static void Apply(RichEditTextDocument document, string text, IReadOnlyList<PdfTextStyleRange> styles)
    {
        int offset = 0, position = 0;
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            int start = position;
            foreach (var (part, style) in PdfTextStyleRange.SplitLine(line, offset, styles))
            {
                if (style.IsBold != null || style.IsItalic != null || style.IsUnderline != null)
                    SetFormat(document.GetRange(start, start + part.Length).CharacterFormat, style);
                start += part.Length;
            }
            offset += line.Length;
            position += line.Length + 1;
        }
    }

    public static void SetFormat(ITextCharacterFormat format, PdfTextStyleRange style)
    {
        if (style.IsBold is bool bold) format.Bold = bold ? FormatEffect.On : FormatEffect.Off;
        if (style.IsItalic is bool italic) format.Italic = italic ? FormatEffect.On : FormatEffect.Off;
        if (style.IsUnderline is bool underline) format.Underline = underline ? UnderlineType.Single : UnderlineType.None;
    }

    public static PdfTextStyleRange[] Read(RichEditTextDocument document, string text, PdfTextStyleRange defaults = default)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var result = new List<PdfTextStyleRange>();
        var range = document.GetRange(0, 0);
        int position = 0, offset = 0;
        while (position < text.Length)
        {
            range.SetRange(position, position);
            range.MoveEnd(TextRangeUnit.CharacterFormat, 1);
            int end = Math.Clamp(range.EndPosition, position + 1, text.Length);
            range.SetRange(position, end);
            int count = PdfTextStyleRange.CountCharacters(text.AsSpan(position, end - position));
            if (count > 0)
            {
                var format = range.CharacterFormat;
                bool bold = format.Bold == FormatEffect.On;
                bool italic = format.Italic == FormatEffect.On;
                bool underline = format.Underline is not (UnderlineType.None or UnderlineType.Undefined);
                var style = new PdfTextStyleRange(offset, count,
                    bold == defaults.IsBold ? null : bold,
                    italic == defaults.IsItalic ? null : italic,
                    underline == defaults.IsUnderline ? null : underline);
                if (style.IsBold != null || style.IsItalic != null || style.IsUnderline != null)
                {
                    if (result.Count > 0 && result[^1].End == offset &&
                        result[^1] with { Start = offset, Length = count } == style)
                        result[^1] = result[^1] with { Length = result[^1].Length + count };
                    else
                        result.Add(style);
                }
                offset += count;
            }
            position = end;
        }
        return result.ToArray();
    }
}
