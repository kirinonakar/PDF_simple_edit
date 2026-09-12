using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.PdfCleanup;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PDF_simple_edit.Helpers
{
    public class PdfDocumentManager
    {
        private byte[]? _pdfBytes;
        private string? _filePath;
        private bool _isModified;
        private readonly object _docLock = new();
        private readonly Dictionary<int, List<PdfPageContent>> _contentCache = new();
        private TextEditingMode _textEditingMode = TextEditingMode.PreserveOriginal;
        public TextEditingMode TextEditingMode
        {
            get => _textEditingMode;
            set { lock (_docLock) { if (_textEditingMode != value) { _textEditingMode = value; _contentCache.Clear(); } } }
        }

        private readonly Stack<(byte[] Bytes, object? UIState)> _undoStack = new();
        private readonly Stack<(byte[] Bytes, object? UIState)> _redoStack = new();
        private readonly PdfContentWriter _contentWriter = new();
        private readonly PdfPageContentExtractor _contentExtractor = new();
        private readonly PdfContentStreamEditor _contentStreamEditor = new();
        private readonly PdfAnnotationTargetResolver _annotationTargetResolver = new();
        private readonly PdfFileOperationService _fileOperationService = new();
        private readonly PdfSecurityService _securityService = new();
        private string? _openPassword;
        private bool _sourceEncrypted;
        private bool _passwordRequiredToOpen;
        private int _sourcePermissions = -1;
        private const int MaxUndoSteps = 30;

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        public string? FilePath => _filePath;
        public void SetFilePath(string path) => _filePath = path;

        public bool IsPasswordProtected => _sourceEncrypted;
        public string? SavePassword => _openPassword;
        
        public bool IsModified => _isModified;
        public void MarkModified(bool modified = true)
        {
            _isModified = modified;
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public int PageCount
        {
            get
            {
                if (_pdfBytes == null) return 0;
                try
                {
                    using var reader = new PdfReader(new MemoryStream(_pdfBytes));
                    using var doc = new PdfDocument(reader);
                    return doc.GetNumberOfPages();
                }
                catch { return 0; }
            }
        }

        public bool IsLoaded => _pdfBytes != null;
        public byte[]? GetPdfBytes() => _pdfBytes;

        // One native inline session is one undo entry. Previews are detached byte
        // snapshots and never modify document state or pollute undo history.
        public void CommitNativeTextEdit(byte[] expectedBytes, byte[] editedBytes)
        {
            lock (_docLock)
            {
                if (!ReferenceEquals(_pdfBytes, expectedBytes))
                    throw new InvalidOperationException("편집 중 문서가 변경되었습니다. 텍스트를 다시 선택해 주세요.");
                if (ReferenceEquals(expectedBytes, editedBytes) || expectedBytes.AsSpan().SequenceEqual(editedBytes)) return;
                _undoStack.Push((_pdfBytes!, GetUIStateFunc?.Invoke()));
                if (_undoStack.Count > MaxUndoSteps)
                {
                    var keep = _undoStack.Take(MaxUndoSteps).Reverse().ToArray();
                    _undoStack.Clear(); foreach (var item in keep) _undoStack.Push(item);
                }
                _redoStack.Clear(); _pdfBytes = editedBytes; _isModified = true; _contentCache.Clear();
            }
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task ApplyNativeTextEditsAsync(int pageIndex, IReadOnlyList<NativePdfTextEdit> edits)
        {
            byte[] snapshot = _pdfBytes ?? throw new InvalidOperationException("열린 PDF 문서가 없습니다.");
            var result = await Task.Run(() => new NativePdfTextService().EditMany(snapshot, pageIndex, edits));
            CommitNativeTextEdit(snapshot, result.Bytes);
        }

        public event EventHandler? DocumentChanged;
        public event EventHandler? PageStructureChanged;
        public event EventHandler? ModifiedStateChanged;
        public event EventHandler<object?>? UndoRedoPerformed;

        public Func<object?>? GetUIStateFunc { get; set; }

        public PdfDocumentManager()
        {
        }

        public async Task<bool> OpenAsync(string filePath) =>
            await OpenWithPasswordAsync(filePath, null) == PdfOpenStatus.Success;

        public async Task<PdfOpenStatus> OpenWithPasswordAsync(string filePath, string? password)
        {
            return await Task.Run(() =>
            {
                lock (_docLock)
                {
                    try
                    {
                        if (!File.Exists(filePath)) return PdfOpenStatus.Failed;

                        PdfOpenResult result = _securityService.Open(File.ReadAllBytes(filePath), password);
                        if (!result.IsSuccess || result.Bytes == null)
                            return result.Status;

                        _pdfBytes = result.Bytes;
                        _filePath = filePath;
                        _isModified = false;
                        _contentCache.Clear();
                        _undoStack.Clear();
                        _redoStack.Clear();
                        _openPassword = result.Password;
                        _sourceEncrypted = result.WasEncrypted;
                        _passwordRequiredToOpen = result.PasswordRequiredToOpen;
                        _sourcePermissions = result.Permissions;

                        DocumentChanged?.Invoke(this, EventArgs.Empty);
                        PageStructureChanged?.Invoke(this, EventArgs.Empty);
                        ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                        return PdfOpenStatus.Success;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error opening PDF: {ex.Message}");
                        return PdfOpenStatus.Failed;
                    }
                }
            });
        }

        public async Task<bool> SaveAsync()
        {
            if (_pdfBytes == null || string.IsNullOrEmpty(_filePath))
                return false;
            return await SaveAsAsync(_filePath, true);
        }

        public async Task<bool> SaveAsAsync(string filePath, bool isUserSave = true)
        {
            if (_pdfBytes == null) return false;

            return await Task.Run(() =>
            {
                lock (_docLock)
                {
                    try
                    {
                        // 메모리의 문서는 항상 평문이므로, 사용자 저장 시에만 보호
                        // 설정을 적용한 바이트를 디스크에 기록한다.
                        File.WriteAllBytes(filePath, isUserSave ? ProtectBytesForSave(_pdfBytes) : _pdfBytes);
                        
                        if (isUserSave)
                        {
                            _filePath = filePath;
                            _isModified = false;
                            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                        }
                        return true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error saving PDF: {ex.Message}");
                        return false;
                    }
                }
            });
        }

        /// <summary>
        /// 사용자 저장(디스크 기록)용 바이트를 만든다. 암호로 보호된 문서는 원래의
        /// 열기 암호와 권한 설정을 유지한 채 AES-256으로 다시 암호화하며, 메모리의
        /// 문서는 계속 평문으로 유지된다.
        /// </summary>
        public byte[] ProtectBytesForSave(byte[] content)
        {
            if (!_sourceEncrypted)
                return content;

            return PdfSecurityService.Encrypt(
                content,
                _passwordRequiredToOpen ? _openPassword : null,
                _openPassword,
                _sourcePermissions);
        }

        /// <summary>
        /// 병합/나누기/페이지 내보내기처럼 원본 문서에서 파생된 파일을 새로 쓸 때
        /// 사용할 보호 설정을 만든다. 보호되지 않은 문서면 null을 반환한다.
        /// </summary>
        public WriterProperties? CreateProtectedWriterProperties()
        {
            if (!_sourceEncrypted)
                return null;

            return PdfSecurityService.CreateEncryptionProperties(
                _passwordRequiredToOpen ? _openPassword : null,
                _openPassword,
                _sourcePermissions);
        }

        public void ApplyBatchEdit(Action<PdfDocument> editAction)
        {
            ApplyEdit(editAction);
        }

        private void ApplyEdit(Action<PdfDocument> editAction)
        {
            if (_pdfBytes == null) return;

            lock (_docLock)
            {
                // Save current state to undo stack before mutation
                _undoStack.Push(((byte[])_pdfBytes.Clone(), GetUIStateFunc?.Invoke()));
                if (_undoStack.Count > MaxUndoSteps)
                {
                    // Remove oldest (inefficient with Stack, but infrequent)
                    var list = _undoStack.ToList();
                    _undoStack.Clear();
                    for (int i = Math.Min(list.Count - 1, MaxUndoSteps - 1); i >= 0; i--)
                        _undoStack.Push(list[i]);
                }
                _redoStack.Clear();

                try
                {
                    using (var msInput = new MemoryStream(_pdfBytes))
                    using (var msOutput = new MemoryStream())
                    {
                        var reader = new PdfReader(msInput);
                        
                        // iText 9 에러 방지: WriterProperties 설정 (BouncyCastle 어댑터가 로드된 상태여야 함)
                        WriterProperties props = new WriterProperties();
                        var writer = new PdfWriter(msOutput, props);
                        
                        // Stamping 모드 사용하여 불필요한 객체 복제 및 직렬화 에러 방지
                        using (var doc = new PdfDocument(reader, writer, new StampingProperties()))
                        {
                            editAction(doc);
                        }
                        
                        byte[] resultBytes = msOutput.ToArray();
                        if (resultBytes.Length > 0)
                        {
                            _pdfBytes = resultBytes;
                            _isModified = true;
                            _contentCache.Clear(); 
                            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                            DocumentChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 에러 메시지에 더 자세한 정보 포함
                    throw new Exception($"PDF 편집 중 에러 발생: {ex.Message}\n타입: {ex.GetType().Name}\n스택: {ex.StackTrace}");
                }
            }
        }

        public byte[] CreatePdfBytesWithEdits(Action<PdfDocument> editAction)
        {
            ArgumentNullException.ThrowIfNull(editAction);
            if (_pdfBytes == null)
                throw new InvalidOperationException("열린 PDF 문서가 없습니다.");

            lock (_docLock)
            {
                using var msInput = new MemoryStream(_pdfBytes);
                using var msOutput = new MemoryStream();

                using (var reader = new PdfReader(msInput))
                using (var writer = new PdfWriter(msOutput))
                using (var doc = new PdfDocument(reader, writer, new StampingProperties()))
                {
                    editAction(doc);
                }

                byte[] result = msOutput.ToArray();
                if (result.Length == 0)
                    throw new InvalidOperationException("편집된 PDF 데이터가 생성되지 않았습니다.");

                return result;
            }
        }

        public byte[]? GetPdfBytesWithEdits(Action<PdfDocument> editAction)
        {
            try
            {
                return CreatePdfBytesWithEdits(editAction);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting PDF bytes with edits: {ex.Message}");
                return null;
            }
        }

        public void ReplacePdfBytesAfterSave(byte[] pdfBytes, string filePath)
        {
            ArgumentNullException.ThrowIfNull(pdfBytes);
            lock (_docLock)
            {
                _pdfBytes = (byte[])pdfBytes.Clone();
                _filePath = filePath;
                _isModified = false;
                _contentCache.Clear();
                _undoStack.Clear();
                _redoStack.Clear();
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                DocumentChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public List<PdfAnnotation> LoadSavedSignatures()
        {
            var signatures = new List<PdfAnnotation>();
            if (_pdfBytes == null)
                return signatures;

            lock (_docLock)
            {
                try
                {
                    using var input = new MemoryStream(_pdfBytes);
                    using var reader = new PdfReader(input);
                    using var document = new PdfDocument(reader);
                    for (int pageIndex = 0; pageIndex < document.GetNumberOfPages(); pageIndex++)
                    {
                        PdfPage page = document.GetPage(pageIndex + 1);
                        foreach (iText.Kernel.Pdf.Annot.PdfAnnotation pdfAnnotation in page.GetAnnotations())
                        {
                            if (!IsSavedSignatureAnnotation(pdfAnnotation))
                                continue;

                            string? contents = pdfAnnotation.GetContents()?.ToUnicodeString();
                            if (PdfSignatureMetadata.TryDeserialize(contents, pageIndex, out PdfAnnotation? signature) &&
                                signature != null)
                            {
                                signatures.Add(signature);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Saved signature load error: {ex.Message}");
                }
            }

            if (signatures.Count > 0)
            {
                try
                {
                    DetachSavedSignatureAnnotationsForEditing();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Saved signature detach error: {ex.Message}");
                }
            }

            return signatures;
        }

        public void RemoveSavedSignatureAnnotations(PdfDocument document)
        {
            for (int pageIndex = 0; pageIndex < document.GetNumberOfPages(); pageIndex++)
            {
                PdfPage page = document.GetPage(pageIndex + 1);
                foreach (iText.Kernel.Pdf.Annot.PdfAnnotation annotation in page.GetAnnotations().ToList())
                {
                    if (IsSavedSignatureAnnotation(annotation))
                        page.RemoveAnnotation(annotation);
                }
            }
        }

        /// <summary>
        /// Removes the application's saved Ink annotations from the in-memory
        /// document while keeping the editor annotations as the UI source of
        /// truth. The file on disk is not changed and the document remains
        /// unmodified, so the annotations are written back exactly once on
        /// the next save.
        /// </summary>
        public void DetachSavedSignatureAnnotationsForEditing()
        {
            if (_pdfBytes == null)
                return;

            byte[] detachedBytes = CreatePdfBytesWithEdits(RemoveSavedSignatureAnnotations);
            lock (_docLock)
            {
                _pdfBytes = detachedBytes;
                _contentCache.Clear();
            }
        }

        private static bool IsSavedSignatureAnnotation(
            iText.Kernel.Pdf.Annot.PdfAnnotation annotation)
        {
            if (!PdfName.Ink.Equals(annotation.GetSubtype()))
                return false;

            string? contents = annotation.GetContents()?.ToUnicodeString();
            return contents?.StartsWith(PdfSignatureMetadata.Prefix, StringComparison.Ordinal) == true;
        }

        public void AddText(int pageIndex, double x, double y, string text,
            string fontFamily, double fontSize, Color color,
            bool isBold = false, bool isItalic = false)
        {
            ApplyEdit(document => _contentWriter.AddText(
                document, pageIndex, x, y, text, fontFamily, fontSize, color, isBold, isItalic));
        }

        public void AddTextInternal(PdfDocument doc, int pageIndex, double x, double y, string text,
            string fontFamily, double fontSize, Color color,
            bool isBold = false, bool isItalic = false, double lineHeight = 0,
            double baselineOffset = 0, int originalFontObjectNumber = -1,
            IReadOnlyList<int>? originalFontObjectNumbersByLine = null,
            IReadOnlyList<double>? lineXOffsets = null,
            IReadOnlyList<double>? lineBaselineOffsets = null,
            int characterSpacing = 0,
            int fontWeight = 400,
            IReadOnlyList<string>? originalLines = null)
        {
            _contentWriter.AddText(
                doc,
                pageIndex,
                x,
                y,
                text,
                fontFamily,
                fontSize,
                color,
                isBold,
                isItalic,
                lineHeight,
                baselineOffset,
                originalFontObjectNumber,
                originalFontObjectNumbersByLine,
                lineXOffsets,
                lineBaselineOffsets,
                characterSpacing,
                fontWeight,
                originalLines);
        }

        public void AddHighlight(
            int pageIndex,
            double x,
            double y,
            double width,
            double height,
            Color? color = null,
            float opacity = 0.5f)
        {
            ApplyEdit(document =>
                _contentWriter.AddHighlight(document, pageIndex, x, y, width, height, color, opacity));
        }

        public void AddHighlightInternal(
            PdfDocument doc,
            int pageIndex,
            double x,
            double y,
            double width,
            double height,
            Color? color = null,
            float opacity = 0.5f)
        {
            _contentWriter.AddHighlight(doc, pageIndex, x, y, width, height, color, opacity);
        }

        public void AddSignatureInternal(
            PdfDocument doc,
            int pageIndex,
            IReadOnlyList<PdfPathPoint> points,
            Color color,
            double lineWidth,
            string? metadata = null)
        {
            _contentWriter.AddSignature(doc, pageIndex, points, color, lineWidth, metadata);
        }

        public void AddImage(
            int pageIndex,
            string imagePath,
            double x,
            double y,
            double width,
            double height)
        {
            ApplyEdit(document =>
                _contentWriter.AddImage(document, pageIndex, imagePath, x, y, width, height));
        }

        public void AddImageInternal(
            PdfDocument doc,
            int pageIndex,
            string imagePath,
            double x,
            double y,
            double width,
            double height)
        {
            _contentWriter.AddImage(doc, pageIndex, imagePath, x, y, width, height);
        }
        public void DeletePage(int pageIndex)
        {
            ApplyEdit(doc => DeletePageInternal(doc, pageIndex));
            PageStructureChanged?.Invoke(this, EventArgs.Empty);
        }

        public void DeletePages(IEnumerable<int> pageIndices)
        {
            List<int> indices = pageIndices
                .Distinct()
                .Where(index => index >= 0 && index < PageCount)
                .OrderByDescending(index => index)
                .ToList();
            if (indices.Count == 0 || indices.Count >= PageCount)
                return;

            ApplyEdit(doc =>
            {
                foreach (int pageIndex in indices)
                    DeletePageInternal(doc, pageIndex);
            });
            PageStructureChanged?.Invoke(this, EventArgs.Empty);
        }

        public void DeletePageInternal(PdfDocument doc, int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= doc.GetNumberOfPages()) return;
            doc.RemovePage(pageIndex + 1);
        }

        public void MovePage(int fromIndex, int toIndex)
        {
            if (fromIndex == toIndex) return;

            ApplyEdit(doc => MovePageInternal(doc, fromIndex, toIndex));
            PageStructureChanged?.Invoke(this, EventArgs.Empty);
        }

        public void MovePageInternal(PdfDocument doc, int fromIndex, int toIndex)
        {
            int pageCount = doc.GetNumberOfPages();
            if (fromIndex < 0 || fromIndex >= pageCount ||
                toIndex < 0 || toIndex >= pageCount || fromIndex == toIndex)
                return;

            // iText page numbers are one-based. MovePage's second argument is
            // the new one-based position in the same document.
            doc.MovePage(fromIndex + 1, toIndex + 1);
        }

        public void NewDocument()
        {
            lock (_docLock)
            {
                using var ms = new MemoryStream();
                using (var writer = new PdfWriter(ms))
                using (var doc = new PdfDocument(writer))
                {
                    doc.AddNewPage();
                }
                _pdfBytes = ms.ToArray();
                _filePath = null;
                _isModified = false;
                _openPassword = null;
                _sourceEncrypted = false;
                _passwordRequiredToOpen = false;
                _sourcePermissions = -1;
                _undoStack.Clear();
                _redoStack.Clear();
                DocumentChanged?.Invoke(this, EventArgs.Empty);
                PageStructureChanged?.Invoke(this, EventArgs.Empty);
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Close()
        {
            _pdfBytes = null;
            _filePath = null;
            _isModified = false;
            _openPassword = null;
            _sourceEncrypted = false;
            _passwordRequiredToOpen = false;
            _sourcePermissions = -1;
            _undoStack.Clear();
            _redoStack.Clear();
            DocumentChanged?.Invoke(this, EventArgs.Empty);
            PageStructureChanged?.Invoke(this, EventArgs.Empty);
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Undo()
        {
            if (!CanUndo || _pdfBytes == null) return;

            lock (_docLock)
            {
                var currentState = GetUIStateFunc?.Invoke();
                _redoStack.Push(((byte[])_pdfBytes.Clone(), currentState));

                var past = _undoStack.Pop();
                _pdfBytes = past.Bytes;

                _isModified = true;
                _contentCache.Clear();

                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                DocumentChanged?.Invoke(this, EventArgs.Empty);
                PageStructureChanged?.Invoke(this, EventArgs.Empty);
                UndoRedoPerformed?.Invoke(this, past.UIState);
            }
        }

        public void Redo()
        {
            if (!CanRedo || _pdfBytes == null) return;

            lock (_docLock)
            {
                var currentState = GetUIStateFunc?.Invoke();
                _undoStack.Push(((byte[])_pdfBytes.Clone(), currentState));

                var next = _redoStack.Pop();
                _pdfBytes = next.Bytes;

                _isModified = true;
                _contentCache.Clear();

                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                DocumentChanged?.Invoke(this, EventArgs.Empty);
                PageStructureChanged?.Invoke(this, EventArgs.Empty);
                UndoRedoPerformed?.Invoke(this, next.UIState);
            }
        }

        public (double width, double height) GetPageSize(int pageIndex)
        {
            if (_pdfBytes == null) return (0, 0);
            try
            {
                using var reader = new PdfReader(new MemoryStream(_pdfBytes));
                using var doc = new PdfDocument(reader);
                if (pageIndex < 0 || pageIndex >= doc.GetNumberOfPages()) return (0, 0);
                var size = doc.GetPage(pageIndex + 1).GetPageSize();
                return (size.GetWidth(), size.GetHeight());
            }
            catch { return (0, 0); }
        }

        public async Task<List<PdfPageContent>> ExtractPageContentsAsync(int pageIndex)
        {
            byte[] pdfSnapshot;
            byte[] sourceVersion;
            TextEditingMode mode;
            lock (_docLock)
            {
                if (_pdfBytes == null)
                    return new List<PdfPageContent>();
                if (_contentCache.TryGetValue(pageIndex, out List<PdfPageContent>? cached))
                    return cached;
                pdfSnapshot = (byte[])_pdfBytes.Clone();
                sourceVersion = _pdfBytes;
                mode = TextEditingMode;
            }

            List<PdfPageContent> contents =
                await _contentExtractor.ExtractAsync(pdfSnapshot, pageIndex, mode);

            lock (_docLock)
                if (ReferenceEquals(_pdfBytes, sourceVersion) && TextEditingMode == mode)
                    _contentCache[pageIndex] = contents;
            return contents;
        }


        public async Task<bool> RemoveOriginalImageAnnotationsAsync(
            int pageIndex,
            IReadOnlyCollection<PdfAnnotation> annotations)
        {
            if (annotations == null || annotations.Count == 0)
                return true;

            List<PdfPageContent> pageContents = await ExtractPageContentsAsync(pageIndex);
            List<PdfAnnotation>? targets =
                _annotationTargetResolver.ResolveImages(annotations, pageContents);
            if (targets == null || targets.Count != annotations.Count)
                return false;

            return await Task.Run(() =>
            {
                bool success = false;
                ApplyEdit(document =>
                    success = _contentStreamEditor.RemoveGraphics(document, pageIndex, targets));
                return success;
            });
        }





        public async Task<bool> RemoveTextAsync(
            int pageIndex,
            double x,
            double y,
            double width,
            double height,
            int contentStreamIndex = -1,
            int operationIndex = -1,
            int textRenderMode = 0)
        {
            if (contentStreamIndex < 0 || operationIndex < 0)
                return false;

            return await Task.Run(() =>
            {
                bool success = false;
                ApplyEdit(document =>
                {
                    success = _contentStreamEditor.RemoveTextOperation(
                        document,
                        pageIndex,
                        contentStreamIndex,
                        operationIndex,
                        textRenderMode);
                });
                return success;
            });
        }

        public async Task<bool> RemoveOriginalTextAnnotationsAsync(
            int pageIndex,
            IReadOnlyCollection<PdfAnnotation> annotations)
        {
            if (annotations.Any(a => a.NativeText != null))
            {
                if (annotations.Any(a => a.NativeText == null)) return false;
                try
                {
                    await ApplyNativeTextEditsAsync(pageIndex, annotations.Select(a => new NativePdfTextEdit(a.NativeText!, "")).ToList());
                    return true;
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); return false; }
            }
            if (annotations == null || annotations.Count == 0)
                return true;

            // Operation indexes can become stale after an earlier stream rewrite.
            // Resolve the current fragments before asking the stream editor to remove them.
            List<PdfPageContent> pageContents = await _contentExtractor.ExtractAsync(
                _pdfBytes!, pageIndex, TextEditingMode.Legacy);
            List<PdfAnnotation>? currentAnnotations =
                _annotationTargetResolver.ResolveText(annotations, pageContents);
            if (currentAnnotations == null)
                return false;

            bool hasInvalidTarget = currentAnnotations.Any(annotation =>
                annotation.TextFragments.Count > 0
                    ? annotation.TextFragments.Any(fragment =>
                        fragment.OperationIndex < 0 ||
                        (fragment.ContentStreamObjectNumber <= 0 &&
                            fragment.ContentStreamIndex < 0) ||
                        fragment.TextOperandIndex < 0 ||
                        !fragment.TextAdvanceAdjustment.HasValue ||
                        !double.IsFinite(fragment.TextAdvanceAdjustment.Value))
                    : annotation.OperationIndex < 0 ||
                        (annotation.ContentStreamObjectNumber <= 0 &&
                            annotation.ContentStreamIndex < 0));
            if (hasInvalidTarget)
                return false;

            return await Task.Run(() =>
            {
                bool success = false;
                ApplyEdit(document =>
                {
                    success = _contentStreamEditor.RemoveTextAnnotations(
                        document,
                        pageIndex,
                        currentAnnotations);
                });
                return success;
            });
        }

        public Task<bool> RemoveAppliedTextAnnotationAsync(
            int pageIndex,
            PdfAnnotation annotation,
            double x,
            double y,
            double width,
            double height)
        {
            // Text written by the editor is not part of the annotation's original
            // fragment metadata. Re-resolve it from the current PDF by its saved
            // content and bounds before replacing it.
            PdfAnnotation target = annotation.Clone();
            target.X = x;
            target.Y = y;
            target.Width = width;
            target.Height = height;
            target.OriginalText = annotation.Content;
            target.TextFragments.Clear();
            return RemoveOriginalTextAnnotationsAsync(pageIndex, new[] { target });
        }



        public async Task<bool> RemoveMultipleTextsAsync(
            int pageIndex,
            List<(double x, double y, double w, double h)> targets)
        {
            if (targets == null || targets.Count == 0)
                return true;

            return await Task.Run(() =>
            {
                bool success = false;
                ApplyEdit(document =>
                    success = _contentStreamEditor.RemoveTextByCleanup(
                        document, pageIndex, targets));
                return success;
            });
        }

        public async Task<bool> MoveTextAsync(int pageIndex, string text, double oldX, double oldY, double width, double height, double newX, double newY, string fontFamily = "맑은 고딕", double fontSize = 12, Color? color = null)
        {
            return await Task.Run(() =>
            {
                bool success = false;
                ApplyEdit(doc =>
                {
                    var page = doc.GetPage(pageIndex + 1);
                    var rect = page.GetCropBox();
                    float offsetLeft = rect.GetLeft();
                    float offsetBottom = rect.GetBottom();
                    
                    float oldPdfX = offsetLeft + (float)oldX;
                    float oldPdfY = offsetBottom + (rect.GetHeight() - (float)oldY - (float)height);
                    
                    // 삭제 영역 확장
                    float padding = 2.0f;
                    var location = new PdfCleanUpLocation(pageIndex + 1, 
                        new Rectangle(oldPdfX - padding, oldPdfY - padding, (float)width + (padding*2), (float)height + (padding*2)), 
                        null);
                    
                    // pdfSweep 실행 (생성자 주입 방식)
                    var locations = new List<PdfCleanUpLocation> { location };
                    PdfCleanUpTool cleaner = new PdfCleanUpTool(doc, locations, new CleanUpProperties());
                    cleaner.CleanUp();

                    // 수정된 부분: 하드코딩된 "맑은 고딕" 대신 파라미터로 받은 폰트와 사이즈 사용
                    _contentWriter.AddText(
                        doc,
                        pageIndex,
                        newX,
                        newY,
                        text,
                        fontFamily,
                        fontSize,
                        color ?? ColorConstants.BLACK);
                    success = true;
                });
                return success;
            });
        }

        public PdfDocument? GetReadOnlyDocument()
        {
            if (_pdfBytes == null) return null;
            var reader = new PdfReader(new MemoryStream(_pdfBytes));
            return new PdfDocument(reader);
        }

        public Task<bool> MergeFilesAsync(
            List<string> sourceFiles,
            string outputPath)
        {
            byte[]? pdfSnapshot;
            WriterProperties? protection;
            lock (_docLock)
            {
                pdfSnapshot = _pdfBytes == null ? null : (byte[])_pdfBytes.Clone();
                protection = CreateProtectedWriterProperties();
            }

            return _fileOperationService.MergeAsync(pdfSnapshot, sourceFiles, outputPath, protection);
        }

        public Task<int> SplitFileAsync(
            string outputFolder,
            List<(int start, int end)> ranges)
        {
            byte[] pdfSnapshot;
            string? sourceFilePath;
            WriterProperties? protection;
            lock (_docLock)
            {
                if (_pdfBytes == null)
                    return Task.FromResult(0);
                pdfSnapshot = (byte[])_pdfBytes.Clone();
                sourceFilePath = _filePath;
                protection = CreateProtectedWriterProperties();
            }

            return _fileOperationService.SplitAsync(
                pdfSnapshot,
                sourceFilePath,
                outputFolder,
                ranges,
                protection);
        }

        public Task<bool> ExportPagesAsync(
            string outputPath,
            IEnumerable<int> pageIndices)
        {
            byte[] pdfSnapshot;
            WriterProperties? protection;
            lock (_docLock)
            {
                if (_pdfBytes == null)
                    return Task.FromResult(false);
                pdfSnapshot = (byte[])_pdfBytes.Clone();
                protection = CreateProtectedWriterProperties();
            }

            return _fileOperationService.ExportPagesAsync(
                pdfSnapshot,
                outputPath,
                pageIndices,
                protection);
        }
    }
}
