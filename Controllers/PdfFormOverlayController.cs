using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI;

namespace PDF_simple_edit.Controllers;

/// <summary>Places editable hit targets over AcroForm widgets on the rendered PDF page.</summary>
public sealed class PdfFormOverlayController(
    Canvas canvas, TextBlock statusText, Func<PdfDocumentManager> getManager,
    Func<int> getPageIndex, Func<EditToolMode> getToolMode)
{
    private const double PdfToPixels = 96.0 / 72.0;
    private readonly PdfFormService _forms = new();
    private TextBox? _editor;
    private PdfFormInput? _editingInput;
    private PdfDocumentManager? _editingManager;

    public void Render()
    {
        CommitActiveEdit();
        canvas.Children.Clear();
        if (getToolMode() is not (EditToolMode.None or EditToolMode.Select)) return;
        byte[]? bytes = getManager().GetPdfBytes();
        if (bytes == null) return;

        try
        {
            foreach (PdfFormInput input in _forms.ReadPage(bytes, getPageIndex()))
            {
                var target = new Border
                {
                    Width = Math.Max(input.Bounds.Width * PdfToPixels, 12),
                    Height = Math.Max(input.Bounds.Height * PdfToPixels, 12),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderBrush = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(1)
                };
                Canvas.SetLeft(target, input.Bounds.X * PdfToPixels);
                Canvas.SetTop(target, input.Bounds.Y * PdfToPixels);
                ToolTipService.SetToolTip(target, input.Name);
                target.PointerEntered += (_, _) => target.BorderBrush =
                    new SolidColorBrush(Color.FromArgb(180, 0, 120, 212));
                target.PointerExited += (_, _) => target.BorderBrush =
                    new SolidColorBrush(Colors.Transparent);
                target.Tapped += (_, args) =>
                {
                    args.Handled = true;
                    if (input.Kind == PdfFormInputKind.Text)
                        BeginTextEdit(input);
                    else
                        Toggle(input);
                };
                canvas.Children.Add(target);
            }
        }
        catch (Exception ex)
        {
            statusText.Text = $"양식 필드를 읽을 수 없습니다: {ex.Message}";
        }
    }

    private void BeginTextEdit(PdfFormInput input)
    {
        CommitActiveEdit();
        var editor = new TextBox
        {
            Text = input.Value,
            Width = Math.Max(input.Bounds.Width * PdfToPixels, 48),
            Height = Math.Max(input.Bounds.Height * PdfToPixels, 24),
            FontSize = 13,
            AcceptsReturn = input.IsMultiline,
            TextWrapping = input.IsMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        if (input.MaxLength > 0) editor.MaxLength = input.MaxLength;
        Canvas.SetLeft(editor, input.Bounds.X * PdfToPixels);
        Canvas.SetTop(editor, input.Bounds.Y * PdfToPixels);
        editor.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                CancelActiveEdit();
            }
            else if (args.Key == VirtualKey.Enter && !input.IsMultiline)
            {
                args.Handled = true;
                CommitActiveEdit();
            }
        };
        editor.LostFocus += (_, _) =>
        {
            if (ReferenceEquals(_editor, editor)) CommitActiveEdit();
        };
        _editingInput = input;
        _editingManager = getManager();
        _editor = editor;
        canvas.Children.Add(editor);
        editor.Focus(FocusState.Programmatic);
        editor.SelectAll();
    }

    private void Toggle(PdfFormInput input)
    {
        CommitActiveEdit();
        try
        {
            string value = input.Value == input.OnValue ? "Off" : input.OnValue;
            _forms.SetValue(getManager(), input, value);
            statusText.Text = $"양식: {input.Name}";
        }
        catch (Exception ex) { statusText.Text = $"양식 입력 오류: {ex.Message}"; }
    }

    public Task FinishActiveEditAsync()
    {
        CommitActiveEdit();
        return Task.CompletedTask;
    }

    private void CommitActiveEdit()
    {
        if (_editor == null || _editingInput == null || _editingManager == null) return;
        TextBox editor = _editor;
        PdfFormInput input = _editingInput;
        PdfDocumentManager manager = _editingManager;
        string value = editor.Text;
        CancelActiveEdit();
        if (value == input.Value) return;
        try
        {
            _forms.SetValue(manager, input, value);
            statusText.Text = $"양식: {input.Name} · 저장하려면 Ctrl+S";
        }
        catch (Exception ex) { statusText.Text = $"양식 입력 오류: {ex.Message}"; }
    }

    private void CancelActiveEdit()
    {
        TextBox? editor = _editor;
        _editor = null;
        _editingInput = null;
        _editingManager = null;
        if (editor != null) canvas.Children.Remove(editor);
    }
}
