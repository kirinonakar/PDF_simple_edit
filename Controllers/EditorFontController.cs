using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System.Globalization;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using PDF_simple_edit.Controls;

namespace PDF_simple_edit.Controllers;

/// <summary>Synchronizes font controls with selection and applies toolbar edits.</summary>
public sealed class EditorFontController
{
    private readonly EditorToolbar _toolbar;
    private readonly AnnotationCanvasController _annotationCanvasController;
    private readonly AnnotationEditController _annotationEditController;
    private readonly TextFontSettings _fontSettings;
    private readonly TextBlock TxtStatus;
    private readonly Func<PdfDocumentManager> _getManager;
    private readonly Func<List<PdfAnnotation>> _getAnnotations;
    private readonly Action _saveSettings;
    private bool _isSyncingFontControls;

    public EditorFontController(
        EditorToolbar toolbar,
        AnnotationCanvasController canvasController,
        AnnotationEditController editController,
        TextFontSettings fontSettings,
        TextBlock statusText,
        Func<PdfDocumentManager> getManager,
        Func<List<PdfAnnotation>> getAnnotations,
        Action saveSettings)
    {
        _toolbar = toolbar;
        _annotationCanvasController = canvasController;
        _annotationEditController = editController;
        _fontSettings = fontSettings;
        TxtStatus = statusText;
        _getManager = getManager;
        _getAnnotations = getAnnotations;
        _saveSettings = saveSettings;
        AddInstalledNotoFonts();
        EditorColorService.PopulatePalette(ColorPalette);
        _annotationCanvasController.PrimarySelectionChanged += SyncFontControlsWithSelection;
        _toolbar.TextEditingModeComboBox.SelectionChanged += TextEditingMode_Changed;
        CmbFontFamily.SelectionChanged += FontFamily_Changed;
        CmbFontSize.SelectionChanged += FontSize_Changed;
        BtnBold.Click += FontBold_Click;
        BtnItalic.Click += FontItalic_Click;
        ColorPalette.SelectionChanged += FontColor_Changed;
    }

    private ComboBox CmbFontFamily => _toolbar.FontFamilyComboBox;
    private ComboBox CmbFontSize => _toolbar.FontSizeComboBox;
    private ToggleButton BtnBold => _toolbar.BoldButton;
    private ToggleButton BtnItalic => _toolbar.ItalicButton;
    private Border FontColorIndicator => _toolbar.FontColorIndicatorElement;
    private GridView ColorPalette => _toolbar.ColorPaletteGrid;

    private void AddInstalledNotoFonts()
    {
        var existingFamilies = CmbFontFamily.Items
            .OfType<ComboBoxItem>()
            .Select(item => item.Content?.ToString())
            .Where(family => !string.IsNullOrWhiteSpace(family))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string family in InstalledFontService.GetInstalledNotoFamilies())
        {
            if (existingFamilies.Add(family))
                CmbFontFamily.Items.Add(new ComboBoxItem { Content = family });
        }
    }

    public void ApplySettings()
    {
        if (!_changingTextMode)
            _toolbar.TextEditingModeComboBox.SelectedIndex = (int)_fontSettings.TextEditingMode;
        _getManager().TextEditingMode = _fontSettings.TextEditingMode;
        ApplyFontValuesToControls(
            _fontSettings.FontFamily,
            _fontSettings.FontSize,
            _fontSettings.IsBold,
            _fontSettings.IsItalic,
            _fontSettings.Color);
    }

    private bool _changingTextMode;
    private async void TextEditingMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_changingTextMode) return;
        var mode = (TextEditingMode)_toolbar.TextEditingModeComboBox.SelectedIndex;
        if (!Enum.IsDefined(mode)) return;
        _toolbar.TextEditingModeDescription.Text = mode == TextEditingMode.PreserveOriginal
            ? "원본 글꼴로 문단 편집 · 드래그 범위 선택 · Alt+드래그 이동"
            : "글꼴·크기 변경 가능 · 원본 텍스트를 교체하여 편집";
        if (_fontSettings.TextEditingMode == mode) return;
        _changingTextMode = true;
        _toolbar.TextEditingModeComboBox.IsEnabled = false;
        try
        {
            if (_annotationCanvasController != null)
                await _annotationCanvasController.FinishActiveInlineEditAsync();
            _fontSettings.TextEditingMode = mode;
            _getManager().TextEditingMode = mode;
            // Selection handles describe one extraction mode. Pending rewritten
            // annotations are edits and must survive a mode change.
            _getAnnotations().RemoveAll(a => a.IsOriginalTextReplacement);
            _annotationCanvasController?.ClearSelection();
            _annotationCanvasController?.Render();
            _saveSettings();
            TxtStatus.Text = mode == TextEditingMode.PreserveOriginal
                ? "원본 보존 방식으로 전환했습니다. 텍스트를 다시 선택해 주세요."
                : "대체 방식으로 전환했습니다. 텍스트를 다시 선택해 주세요.";
        }
        catch (Exception error) { TxtStatus.Text = error.Message; }
        finally
        {
            _changingTextMode = false;
            _toolbar.TextEditingModeComboBox.SelectedIndex = (int)_fontSettings.TextEditingMode;
            _toolbar.TextEditingModeComboBox.IsEnabled = true;
        }
    }

    private void SyncFontControlsWithSelection(PdfAnnotation? annotation)
    {
        if (annotation?.Type is AnnotationType.Text or AnnotationType.FreeText)
        {
            ApplyFontValuesToControls(
                annotation.FontFamily,
                annotation.FontSize,
                annotation.IsBold,
                annotation.IsItalic,
                annotation.Color);
            return;
        }

        ApplySettings();
    }

    private void ApplyFontValuesToControls(
        string fontFamily,
        double fontSize,
        bool isBold,
        bool isItalic,
        string color)
    {
        _isSyncingFontControls = true;
        try
        {
            if (CmbFontFamily != null && !string.IsNullOrWhiteSpace(fontFamily))
            {
                ComboBoxItem? fontItem = CmbFontFamily.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(
                        item.Content?.ToString(),
                        fontFamily,
                        StringComparison.OrdinalIgnoreCase));
                if (fontItem == null)
                {
                    fontItem = new ComboBoxItem { Content = fontFamily };
                    CmbFontFamily.Items.Add(fontItem);
                }

                if (!ReferenceEquals(CmbFontFamily.SelectedItem, fontItem))
                    CmbFontFamily.SelectedItem = fontItem;
            }

            if (CmbFontSize != null && double.IsFinite(fontSize) && fontSize > 0)
            {
                string sizeText = fontSize.ToString("0.##", CultureInfo.InvariantCulture);
                ComboBoxItem? sizeItem = CmbFontSize.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => double.TryParse(
                            item.Content?.ToString(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double itemSize) &&
                        Math.Abs(itemSize - fontSize) < 0.005);
                if (sizeItem == null)
                {
                    sizeItem = new ComboBoxItem { Content = sizeText };
                    CmbFontSize.Items.Add(sizeItem);
                }

                if (!ReferenceEquals(CmbFontSize.SelectedItem, sizeItem))
                    CmbFontSize.SelectedItem = sizeItem;
            }

            if (BtnBold != null)
                BtnBold.IsChecked = isBold;
            if (BtnItalic != null)
                BtnItalic.IsChecked = isItalic;
            if (FontColorIndicator != null)
                FontColorIndicator.Background = new SolidColorBrush(EditorColorService.Parse(color));
        }
        finally
        {
            _isSyncingFontControls = false;
        }
    }

    private async void FontFamily_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingFontControls)
            return;

        if (CmbFontFamily.SelectedItem is ComboBoxItem item)
        {
            string font = item.Content?.ToString() ?? "맑은 고딕";
            await _annotationEditController.ApplyFontFamilyAsync(font);
            _saveSettings();
        }
    }

    private async void FontSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingFontControls)
            return;

        if (CmbFontSize.SelectedItem is ComboBoxItem item &&
            double.TryParse(
                item.Content?.ToString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double sizeVal))
        {
            await _annotationEditController.ApplyFontSizeAsync(sizeVal);
            _saveSettings();
        }
    }

    private async void FontBold_Click(object sender, RoutedEventArgs e)
    {
        await _annotationEditController.ApplyBoldAsync(BtnBold.IsChecked == true);
        _saveSettings();
    }

    private async void FontItalic_Click(object sender, RoutedEventArgs e)
    {
        await _annotationEditController.ApplyItalicAsync(BtnItalic.IsChecked == true);
        _saveSettings();
    }

    private async void FontColor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ColorPalette.SelectedItem is Border border && border.Tag is string color)
        {
            FontColorIndicator.Background = new SolidColorBrush(EditorColorService.Parse(color));
            await _annotationEditController.ApplyColorAsync(color);
            _saveSettings();
        }
    }
}
