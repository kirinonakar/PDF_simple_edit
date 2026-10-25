using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PDF_simple_edit.Services;

public sealed class PdfSaveService
{
    private readonly PdfAnnotationDocumentService _annotationDocumentService;
    private readonly PdfFormService _formService = new();

    public PdfSaveService(PdfAnnotationDocumentService annotationDocumentService) =>
        _annotationDocumentService = annotationDocumentService;

    // Called on the UI thread: only font measurement touches WinUI. PDF parsing,
    // writing, encryption and signature detachment run on worker threads.
    private static List<PdfAnnotation> CaptureAnnotations(IReadOnlyList<PdfAnnotation> annotations)
    {
        var captured = annotations.Select(a => a.Clone()).ToList();
        foreach (var annotation in captured.Where(a => a.NativeText == null && !a.IsApplied &&
            !a.IsOriginalTextReplacement && a.Type is AnnotationType.Text or AnnotationType.FreeText))
        {
            annotation.LineHeight = AnnotationTextLayoutService.GetDisplayLineHeight(annotation, annotation.Content);
            annotation.BaselineOffset = AnnotationTextLayoutService.GetVisualBaselineOffset(annotation);
        }
        return captured;
    }

    public Task<byte[]> CreateAnnotatedBytesAsync(PdfDocumentManager manager, IReadOnlyList<PdfAnnotation> annotations)
    {
        byte[] source = manager.GetPdfBytes() ?? throw new InvalidOperationException("열린 PDF 문서가 없습니다.");
        var captured = CaptureAnnotations(annotations);
        return Task.Run(() => CreateAnnotatedBytes(manager, source, captured));
    }

    private byte[] CreateAnnotatedBytes(PdfDocumentManager manager, byte[] source, List<PdfAnnotation> annotations)
    {
        bool needsAnnotationWrite = annotations.Any(a => a.NativeText == null &&
            (!a.IsApplied && !a.IsOriginalTextReplacement || a.Type == AnnotationType.Signature));
        bool needsFormRepair = _formService.NeedsAppearanceRepair(source);
        if (!needsAnnotationWrite && !needsFormRepair) return source;
        return PdfDocumentManager.RewritePdfBytes(source, document =>
        {
            if (needsFormRepair) _formService.RepairAppearances(document);
            if (!needsAnnotationWrite) return;
            manager.RemoveSavedSignatureAnnotations(document);
            foreach (var annotation in annotations)
                _annotationDocumentService.Apply(manager, document, annotation, textMetricsPrepared: true);
        });
    }

    public async Task<bool> SaveAsync(PdfDocumentManager manager, List<PdfAnnotation> annotations, string filePath, bool isUserSave)
    {
        byte[] source = manager.GetPdfBytes() ?? throw new InvalidOperationException("열린 PDF 문서가 없습니다.");
        long version = manager.EditVersion;
        var protection = manager.Protection;
        var captured = CaptureAnnotations(annotations);
        var prepared = await Task.Run(async () =>
        {
            if (!isUserSave) return (Disk: CreateAnnotatedBytes(manager, source, captured), Editor: source);
            // Bake non-signature overlays into the editor once. Add signatures and
            // encryption together in the disk pass, keeping Ink out of the editor.
            byte[] editor = CreateAnnotatedBytes(manager, source, captured.Where(a => a.Type != AnnotationType.Signature).ToList());
            await RefreshSavedAnnotationsAsync(manager, editor, captured);
            var signatures = captured.Where(a => a.Type == AnnotationType.Signature).ToList();
            byte[] disk = signatures.Count == 0 ? PdfSecurityService.Encrypt(editor, protection)
                : PdfDocumentManager.RewritePdfBytes(editor, document =>
                {
                    manager.RemoveSavedSignatureAnnotations(document);
                    foreach (var signature in signatures)
                        _annotationDocumentService.Apply(manager, document, signature, textMetricsPrepared: true);
                }, protection.Enabled ? PdfSecurityService.CreateEncryptionProperties(protection) : null);
            return (Disk: disk, Editor: editor);
        });
        await WriteAtomicallyAsync(filePath, prepared.Disk);
        if (!isUserSave) return true;
        if (ReferenceEquals(source, manager.GetPdfBytes()) && version == manager.EditVersion)
        {
            for (int i = 0; i < annotations.Count; i++) CopySavedState(annotations[i], captured[i]);
            return manager.ReplacePdfBytesAfterSave(source, version, prepared.Editor, filePath);
        }
        // Edits made during a save stay modified, with their original byte/UI pair.
        return false;
    }

    private static void CopySavedState(PdfAnnotation target, PdfAnnotation saved)
    {
        if (saved.NativeText != null && !ReferenceEquals(target.NativeText, saved.NativeText))
            NativePdfTextService.UpdateAnnotation(target, saved.NativeText);
        target.IsApplied = saved.IsApplied;
        target.X = saved.X; target.Y = saved.Y; target.Width = saved.Width; target.Height = saved.Height;
        target.Rotation = saved.Rotation;
        target.IsOriginalImageReplacement = saved.IsOriginalImageReplacement;
        target.OriginalImageName = saved.OriginalImageName;
        target.OriginalPdfX = saved.OriginalPdfX; target.OriginalPdfY = saved.OriginalPdfY;
        target.GraphicCtm = saved.GraphicCtm;
        target.ContentStreamIndex = saved.ContentStreamIndex;
        target.ContentStreamObjectNumber = saved.ContentStreamObjectNumber;
        target.OperationIndex = saved.OperationIndex;
    }

    private static async Task RefreshSavedAnnotationsAsync(PdfDocumentManager manager, byte[] editedBytes, List<PdfAnnotation> annotations)
    {
        foreach (var pageGroup in annotations.Where(a => a.Type == AnnotationType.Image && !a.IsOriginalImageReplacement).GroupBy(a => a.PageIndex))
        {
            var contents = await new PdfPageContentExtractor().ExtractAsync(editedBytes, pageGroup.Key, manager.TextEditingMode);
            var used = new System.Collections.Generic.HashSet<PdfPageContent>();
            foreach (var annotation in pageGroup)
            {
                var box = new PdfTextBox(annotation.X, annotation.Y, annotation.Width, annotation.Height);
                var expected = PdfAffineTransform.Between(box, box, annotation.Rotation).Map(box);
                var match = contents.Where(c => c.Type == PageContentType.Image && !used.Contains(c))
                    .OrderBy(c => Math.Abs(c.X - expected.X) + Math.Abs(c.Y - expected.Y) +
                        Math.Abs(c.Width - expected.Width) + Math.Abs(c.Height - expected.Height)).FirstOrDefault();
                annotation.IsApplied = true;
                if (match == null) continue;
                used.Add(match);
                annotation.X = match.X; annotation.Y = match.Y; annotation.Width = match.Width; annotation.Height = match.Height;
                annotation.Rotation = 0; annotation.IsOriginalImageReplacement = true;
                annotation.OriginalImageName = match.ImageId; annotation.OriginalPdfX = match.OriginalPdfX; annotation.OriginalPdfY = match.OriginalPdfY;
                annotation.GraphicCtm = match.GraphicCtm;
                annotation.ContentStreamIndex = match.ContentStreamIndex; annotation.ContentStreamObjectNumber = match.ContentStreamObjectNumber;
                annotation.OperationIndex = match.OperationIndex;
            }
        }
        foreach (var annotation in annotations.Where(a => a.NativeText == null && !a.IsApplied &&
            !a.IsOriginalTextReplacement && a.Type is AnnotationType.Text or AnnotationType.FreeText && Math.Abs(a.Rotation) > .0001))
        {
            var textService = new NativePdfTextService();
            var box = new PdfTextBox(annotation.X, annotation.Y, annotation.Width, annotation.Height);
            var expected = PdfAffineTransform.Between(box, box, annotation.Rotation).Map(box);
            var blocks = NativePdfTextService.SelectRegion(textService.Extract(editedBytes, annotation.PageIndex),
                new(expected.X - 2, expected.Y - 2, expected.Width + 4, expected.Height + 4));
            if (blocks.Count == 0) continue;
            var selection = new NativePdfTextBlock
            {
                Text = annotation.Content,
                Runs = blocks.SelectMany(b => b.Runs).DistinctBy(r => r.Id).ToList(),
                Glyphs = blocks.SelectMany(b => b.Glyphs).ToList(),
                Lines = blocks.SelectMany(b => b.Lines).ToList(),
                Bounds = PdfTextBox.Union(blocks.Select(b => b.Bounds)),
                LineHeight = annotation.LineHeight
            };
            NativePdfTextService.UpdateAnnotation(annotation, selection);
            annotation.Rotation = 0;
        }
        foreach (PdfAnnotation annotation in annotations.Where(annotation => annotation.NativeText == null &&
            annotation.Type is AnnotationType.Text or AnnotationType.FreeText))
        {
            annotation.IsApplied = true;
        }
        foreach (PdfAnnotation annotation in annotations.Where(annotation => annotation.Type == AnnotationType.Signature))
            annotation.IsApplied = true;

        foreach (var annotation in annotations.Where(a => a.Type == AnnotationType.Highlight))
            annotation.IsApplied = true;
    }

    internal static Task WriteAtomicallyAsync(string filePath, byte[] contents) => PdfAtomicFile.WriteAsync(filePath, contents);
}
