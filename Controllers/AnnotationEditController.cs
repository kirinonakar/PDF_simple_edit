using Microsoft.UI.Xaml.Controls;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PDF_simple_edit.Controllers;

/// <summary>
/// Executes commands against the current annotation selection. Canvas input is
/// intentionally kept in <see cref="AnnotationCanvasController"/>.
/// </summary>
public sealed class AnnotationEditController
{
    private readonly AnnotationCanvasController _canvasController;
    private readonly AnnotationAlignmentService _alignmentService;
    private readonly TextFontSettings _fontSettings;
    private readonly TextBlock _statusText;
    private readonly Func<PdfDocumentManager> _getManager;
    private readonly Func<List<PdfAnnotation>> _getAnnotations;
    private readonly Func<int> _getCurrentPageIndex;
    private readonly Func<Task> _renderCurrentPageAsync;

    public AnnotationEditController(
        AnnotationCanvasController canvasController,
        AnnotationAlignmentService alignmentService,
        TextFontSettings fontSettings,
        TextBlock statusText,
        Func<PdfDocumentManager> getManager,
        Func<List<PdfAnnotation>> getAnnotations,
        Func<int> getCurrentPageIndex,
        Func<Task> renderCurrentPageAsync)
    {
        _canvasController = canvasController;
        _alignmentService = alignmentService;
        _fontSettings = fontSettings;
        _statusText = statusText;
        _getManager = getManager;
        _getAnnotations = getAnnotations;
        _getCurrentPageIndex = getCurrentPageIndex;
        _renderCurrentPageAsync = renderCurrentPageAsync;
    }

    public async Task<bool> DeleteSelectionAsync()
    {
        List<PdfAnnotation> targets = _canvasController.SelectedAnnotations.Count > 0
            ? _canvasController.SelectedAnnotations.ToList()
            : _canvasController.PrimarySelection != null
                ? new List<PdfAnnotation> { _canvasController.PrimarySelection }
                : new List<PdfAnnotation>();
        if (targets.Count == 0)
            return false;

        List<PdfAnnotation> originalTextTargets = targets
            .Where(annotation => annotation.IsOriginalTextReplacement)
            .ToList();
        List<PdfAnnotation> appliedTextTargets = targets
            .Where(annotation => annotation.NativeText == null && annotation.IsApplied &&
                annotation.Type is AnnotationType.Text or AnnotationType.FreeText)
            .ToList();
        List<PdfAnnotation> originalImageTargets = targets
            .Where(annotation => annotation.IsOriginalImageReplacement)
            .ToList();

        if (originalTextTargets.Count > 0 && originalImageTargets.Count > 0)
        {
            _statusText.Text = "원본 텍스트와 이미지는 한 번에 함께 삭제할 수 없습니다.";
            return false;
        }

        PdfDocumentManager manager = _getManager();
        int pageIndex = _getCurrentPageIndex();
        if (originalTextTargets.Count > 0 &&
            !await manager.RemoveOriginalTextAnnotationsAsync(pageIndex, originalTextTargets))
        {
            _statusText.Text = "배경을 보존하면서 삭제할 수 없는 PDF 텍스트입니다.";
            return false;
        }

        foreach (PdfAnnotation annotation in appliedTextTargets)
        {
            bool removed = await manager.RemoveAppliedTextAnnotationAsync(
                pageIndex,
                annotation,
                annotation.X,
                annotation.Y,
                annotation.Width,
                annotation.Height);
            if (!removed)
            {
                _statusText.Text = "저장된 텍스트를 안전하게 삭제할 수 없습니다.";
                return false;
            }
        }

        if (originalImageTargets.Count > 0 &&
            !await manager.RemoveOriginalImageAnnotationsAsync(pageIndex, originalImageTargets))
        {
            _statusText.Text = "이 PDF의 이미지를 안전하게 삭제할 수 없습니다.";
            return false;
        }

        foreach (PdfAnnotation annotation in targets)
            _getAnnotations().Remove(annotation);

        if (targets.Any(a => a.NativeText != null))
            _getAnnotations().RemoveAll(a => a.PageIndex == pageIndex && a.NativeText != null);

        _canvasController.ClearSelection();
        manager.MarkModified();
        if (originalTextTargets.Count > 0 || appliedTextTargets.Count > 0 ||
            originalImageTargets.Count > 0)
        {
            await _renderCurrentPageAsync();
        }
        _canvasController.Render();
        _statusText.Text = targets.Count > 1
            ? $"{targets.Count}개 객체 삭제됨"
            : targets[0].Type == AnnotationType.Image
                ? "이미지가 삭제되었습니다."
                : "텍스트가 삭제되었습니다.";
        return true;
    }

    public async Task ApplyFontFamilyAsync(string fontFamily)
    {
        _fontSettings.FontFamily = fontFamily;
        PdfAnnotation? annotation = GetSelectedTextAnnotation();
        if (annotation == null || !await PrepareTextReplacementAsync(annotation))
            return;

        annotation.FontFamily = fontFamily;
        annotation.OriginalFontObjectNumber = -1;
        foreach (PdfTextFragment fragment in annotation.TextFragments)
        {
            fragment.FontFamily = fontFamily;
            fragment.OriginalFontObjectNumber = -1;
        }
        if (annotation.TextFragments.Count < 2)
            ResizeTextAnnotation(annotation);
        MarkChanged(annotation);
    }

    public async Task ApplyFontSizeAsync(double fontSize)
    {
        _fontSettings.FontSize = fontSize;
        PdfAnnotation? annotation = GetSelectedTextAnnotation();
        if (annotation == null || !await PrepareTextReplacementAsync(annotation))
            return;

        annotation.FontSize = fontSize;
        ResizeTextAnnotation(annotation);
        MarkChanged(annotation);
    }

    public async Task ApplyBoldAsync(bool isBold)
    {
        _fontSettings.IsBold = isBold;
        PdfAnnotation? annotation = GetSelectedTextAnnotation();
        if (annotation == null || !await PrepareTextReplacementAsync(annotation))
            return;

        annotation.IsBold = isBold;
        annotation.FontWeight = isBold ? 700 : 400;
        annotation.OriginalFontObjectNumber = -1;
        ResizeTextAnnotation(annotation);
        MarkChanged(annotation);
    }

    public async Task ApplyItalicAsync(bool isItalic)
    {
        _fontSettings.IsItalic = isItalic;
        PdfAnnotation? annotation = GetSelectedTextAnnotation();
        if (annotation == null || !await PrepareTextReplacementAsync(annotation))
            return;

        annotation.IsItalic = isItalic;
        annotation.OriginalFontObjectNumber = -1;
        ResizeTextAnnotation(annotation);
        MarkChanged(annotation);
    }

    public async Task ApplyColorAsync(string color)
    {
        _fontSettings.Color = color;
        PdfAnnotation? annotation = _canvasController.PrimarySelection;
        if (annotation == null)
            return;

        if (annotation.Type is AnnotationType.Text or AnnotationType.FreeText &&
            !await PrepareTextReplacementAsync(annotation))
        {
            return;
        }

        annotation.Color = color;
        if (annotation.Type is AnnotationType.Text or AnnotationType.FreeText)
            annotation.IsApplied = false;
        _getManager().MarkModified();
        _canvasController.Render();
    }

    public async Task AlignSelectionAsync(AnnotationAlignment alignment)
    {
        List<PdfAnnotation> selection = _canvasController.SelectedAnnotations;
        if (selection.Any(a => a.NativeText != null))
        {
            if (selection.Count < 2) return;
            if (selection.Any(a => a.NativeText == null))
            { _statusText.Text = "원본 텍스트끼리 선택하여 정렬해 주세요."; return; }
            var aligned = selection.Select(a => a.Clone()).ToList();
            _alignmentService.Align(aligned, alignment);
            try
            {
                await _getManager().ApplyNativeTextEditsAsync(_getCurrentPageIndex(), aligned.Select(a =>
                    new NativePdfTextEdit(a.NativeText!, a.NativeText!.Text, a.X - a.NativeText.Bounds.X, a.Y - a.NativeText.Bounds.Y)).ToList());
                _getAnnotations().RemoveAll(a => a.PageIndex == _getCurrentPageIndex() && a.NativeText != null);
                _canvasController.ClearSelection(); await _renderCurrentPageAsync();
                _canvasController.Render(); _statusText.Text = "원본 텍스트가 정렬되었습니다.";
            }
            catch (Exception error) { _statusText.Text = error.Message; }
            return;
        }
        if (selection.Count < 2 || !await RemoveOriginalTextForSelectionAsync(selection))
            return;

        _alignmentService.Align(selection, alignment);
        _getManager().MarkModified();
        _canvasController.Render();
    }

    private PdfAnnotation? GetSelectedTextAnnotation()
    {
        PdfAnnotation? annotation = _canvasController.PrimarySelection;
        return annotation?.Type is AnnotationType.Text or AnnotationType.FreeText
            ? annotation
            : null;
    }

    private async Task<bool> PrepareTextReplacementAsync(PdfAnnotation annotation)
    {
        if (annotation.NativeText != null)
        {
            _statusText.Text = "텍스트 편집 구역 선택됨 (더블클릭: 편집, Alt+드래그: 이동)";
            return false;
        }
        PdfDocumentManager manager = _getManager();
        int pageIndex = _getCurrentPageIndex();
        if (annotation.IsApplied)
        {
            bool removed = await manager.RemoveAppliedTextAnnotationAsync(
                pageIndex,
                annotation,
                annotation.X,
                annotation.Y,
                annotation.Width,
                annotation.Height);
            if (!removed)
            {
                _statusText.Text = "저장된 텍스트를 안전하게 수정할 수 없습니다.";
                return false;
            }

            annotation.IsApplied = false;
            annotation.IsOriginalTextReplacement = false;
            await _renderCurrentPageAsync();
            return true;
        }

        if (!annotation.IsOriginalTextReplacement)
            return true;

        bool originalRemoved = await manager.RemoveOriginalTextAnnotationsAsync(
            pageIndex, new[] { annotation });
        if (!originalRemoved)
        {
            _statusText.Text = "배경을 보존하면서 수정할 수 없는 PDF 텍스트입니다.";
            return false;
        }

        annotation.IsOriginalTextReplacement = false;
        annotation.IsApplied = false;
        await _renderCurrentPageAsync();
        return true;
    }

    private async Task<bool> RemoveOriginalTextForSelectionAsync(
        IReadOnlyCollection<PdfAnnotation> selection)
    {
        List<PdfAnnotation> targets = selection
            .Where(annotation => annotation.IsOriginalTextReplacement)
            .ToList();
        if (targets.Count == 0)
            return true;

        bool success = await _getManager().RemoveOriginalTextAnnotationsAsync(
            _getCurrentPageIndex(), targets);
        if (!success)
        {
            _statusText.Text = "배경을 보존하면서 이동할 수 없는 PDF 텍스트입니다.";
            return false;
        }

        foreach (PdfAnnotation annotation in targets)
            annotation.IsOriginalTextReplacement = false;
        await _renderCurrentPageAsync();
        return true;
    }

    private static void ResizeTextAnnotation(PdfAnnotation annotation)
    {
        (double width, double height) = AnnotationTextLayoutService.MeasureBounds(
            annotation.Content,
            annotation.FontFamily,
            annotation.FontSize,
            annotation.IsBold,
            annotation.IsItalic);
        annotation.Width = width;
        annotation.Height = height;
    }

    private void MarkChanged(PdfAnnotation annotation)
    {
        annotation.IsApplied = false;
        _getManager().MarkModified();
        _canvasController.Render();
    }
}
