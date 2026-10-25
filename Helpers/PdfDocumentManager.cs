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
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace PDF_simple_edit.Helpers
{
    public class PdfDocumentManager
    {
        private byte[]? _pdfBytes;
        private byte[]? _pageCountSource;
        private int _cachedPageCount;
        private byte[]? _geometrySource;
        private readonly Dictionary<int, (double width, double height)> _pageSizes = new();
        public long EditVersion { get; private set; }
        private string? _filePath;
        private PdfFileVersion? _diskVersion;
        private string? _signedSourcePath;

        internal Task<PdfFileVersion> GetSaveFileVersionAsync(string path)
        {
            if (_signedSourcePath != null && SamePath(path, _signedSourcePath))
                throw new InvalidOperationException("전자서명된 원본은 덮어쓸 수 없습니다. 다른 이름으로 저장해 주세요. 편집한 사본의 전자서명은 유효하지 않습니다.");
            if (_filePath != null && SamePath(path, _filePath) && _diskVersion != null)
                return Task.FromResult(_diskVersion);
            return Task.Run(() => PdfFileVersion.Read(path));
        }

        internal void RecordDiskWrite(string path, byte[] hash)
        {
            if (_filePath != null && SamePath(path, _filePath))
                _diskVersion = new(hash);
        }

        private static bool SamePath(string a, string b) => string.Equals(
            System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        public bool HasDigitalSignatures => _signedSourcePath != null;
        public bool RequiresSignedCopySave => _signedSourcePath != null && _filePath != null && SamePath(_signedSourcePath, _filePath);
        private bool _isModified;
        private readonly object _docLock = new();
        private readonly Dictionary<int, List<PdfPageContent>> _contentCache = new();
        private TextEditingMode _textEditingMode = TextEditingMode.PreserveOriginal;
        public TextEditingMode TextEditingMode
        {
            get => _textEditingMode;
            set { lock (_docLock) { if (_textEditingMode != value) { _textEditingMode = value; _contentCache.Clear(); } } }
        }

        private readonly PdfUndoHistory _history = new();
        private Stack<PdfHistoryEntry> _undoStack => _history.Undo;
        private Stack<PdfHistoryEntry> _redoStack => _history.Redo;
        private Guid _stateId = Guid.NewGuid();
        private Guid _savedStateId;
        private PdfProtectionSettings _savedProtection = new();
        private readonly PdfContentWriter _contentWriter = new();
        private readonly PdfPageContentExtractor _contentExtractor = new();
        private readonly PdfContentStreamEditor _contentStreamEditor = new();
        private readonly PdfAnnotationTargetResolver _annotationTargetResolver = new();
        private readonly PdfFileOperationService _fileOperationService = new();
        private readonly PdfSecurityService _securityService = new();
        private PdfProtectionSettings _protection = new();

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        public string? FilePath => _filePath;
        public void SetFilePath(string path) => _filePath = path;

        public bool IsPasswordProtected => _protection.Enabled;
        public string? SavePassword => _protection.OpenPassword ?? _protection.OwnerPassword;
        public PdfProtectionSettings Protection => _protection;

        public void SetProtection(PdfProtectionSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            if (settings.GetValidationError() is { } error)
                throw new InvalidOperationException(error);
            lock (_docLock)
            {
                if (_pdfBytes == null) throw new InvalidOperationException("열린 PDF 문서가 없습니다.");
                if (!settings.Enabled) settings = new PdfProtectionSettings();
                if (_protection == settings) return;
                _protection = settings;
                EditVersion++;
                _isModified = _stateId != _savedStateId || _protection != _savedProtection;
            }
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
        }
        
        public bool IsModified => _isModified;
        public void MarkModified(bool modified = true, int? pageIndex = null)
        {
            lock (_docLock)
            {
                if (modified) _stateId = Guid.NewGuid();
                else { _savedStateId = _stateId; _savedProtection = _protection; }
                _isModified = modified;
                EditVersion++;
            }
            ModifiedStateChanged?.Invoke(this, modified
                ? new PdfDocumentChange(pageIndex.HasValue ? new[] { pageIndex.Value } : null)
                : EventArgs.Empty);
        }

        public int PageCount
        {
            get
            {
                lock (_docLock)
                {
                    if (_pdfBytes == null) return 0;
                    if (ReferenceEquals(_pageCountSource, _pdfBytes)) return _cachedPageCount;
                    try
                    {
                        using var reader = new PdfReader(new MemoryStream(_pdfBytes));
                        using var doc = new PdfDocument(reader);
                        _cachedPageCount = doc.GetNumberOfPages();
                        _pageCountSource = _pdfBytes;
                        return _cachedPageCount;
                    }
                    catch { return 0; }
                }
            }
        }

        public bool IsLoaded => _pdfBytes != null;
        public byte[]? GetPdfBytes() => _pdfBytes;

        // One native inline session is one undo entry. Previews are detached byte
        // snapshots and never modify document state or pollute undo history.
        public void CommitNativeTextEdit(byte[] expectedBytes, byte[] editedBytes, int? pageIndex = null) =>
            CommitPdfBytes(expectedBytes, editedBytes, GetUIStateFunc?.Invoke(),
                pageIndex.HasValue ? PdfDocumentChange.Page(pageIndex.Value) : new());

        private void CommitPdfBytes(byte[] expectedBytes, byte[] editedBytes, object? uiState,
            PdfDocumentChange? change = null, long? expectedVersion = null, int? pageCount = null)
        {
            change ??= new();
            lock (_docLock)
            {
                if (!ReferenceEquals(_pdfBytes, expectedBytes) || expectedVersion.HasValue && EditVersion != expectedVersion)
                    throw new InvalidOperationException("편집 중 문서가 변경되었습니다. 다시 시도해 주세요.");
                if (ReferenceEquals(expectedBytes, editedBytes) || expectedBytes.AsSpan().SequenceEqual(editedBytes)) return;
                _undoStack.Push(new(_pdfBytes!, uiState, _stateId, change));
                _redoStack.Clear();
                _history.Trim(_undoStack);
                _stateId = Guid.NewGuid();
                if (pageCount.HasValue || !change.StructureChanged && change.Pages != null && ReferenceEquals(_pageCountSource, expectedBytes))
                {
                    _cachedPageCount = pageCount ?? _cachedPageCount;
                    _pageCountSource = editedBytes;
                }
                else _pageCountSource = null;
                if (!change.StructureChanged && change.Pages != null && ReferenceEquals(_geometrySource, expectedBytes))
                    _geometrySource = editedBytes;
                else { _geometrySource = null; _pageSizes.Clear(); }
                EditVersion++; _pdfBytes = editedBytes; _isModified = true; _contentCache.Clear();
            }
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
            DocumentChanged?.Invoke(this, change);
            if (change.StructureChanged) PageStructureChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task ApplyNativeTextEditsAsync(int pageIndex, IReadOnlyList<NativePdfTextEdit> edits)
        {
            byte[] snapshot = _pdfBytes ?? throw new InvalidOperationException("열린 PDF 문서가 없습니다.");
            var result = await Task.Run(() => new NativePdfTextService().EditMany(snapshot, pageIndex, edits));
            CommitNativeTextEdit(snapshot, result.Bytes, pageIndex);
        }

        public event EventHandler? DocumentChanged;
        public event EventHandler? Closed;
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

                        byte[] diskBytes = File.ReadAllBytes(filePath);
                        PdfOpenResult result = _securityService.Open(diskBytes, password);
                        if (!result.IsSuccess || result.Bytes == null)
                            return result.Status;

                        _pdfBytes = result.Bytes;
                        _filePath = filePath;
                        _diskVersion = new(SHA256.HashData(diskBytes));
                        _signedSourcePath = result.HasDigitalSignatures ? filePath : null;
                        _isModified = false;
                        _contentCache.Clear();
                        _undoStack.Clear();
                        _redoStack.Clear();
                        _protection = result.Protection ?? new PdfProtectionSettings();
                        _stateId = _savedStateId = Guid.NewGuid();
                        _savedProtection = _protection;
                        EditVersion++;

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
            byte[]? snapshot = _pdfBytes;
            if (snapshot == null) return false;
            long version = EditVersion;
            var protection = _protection;
            var diskVersion = await GetSaveFileVersionAsync(filePath);
            byte[] output = await Task.Run(() => isUserSave ? PdfSecurityService.Encrypt(snapshot, protection) : snapshot);
            await Task.Run(() => PdfSecurityService.Validate(output, protection));
            await PdfAtomicFile.WriteAsync(filePath, output, diskVersion);
            byte[] diskHash = await Task.Run(() => SHA256.HashData(output));
            RecordDiskWrite(filePath, diskHash);
            if (isUserSave) return ReplacePdfBytesAfterSave(snapshot, version, snapshot, filePath, diskHash);
            return true;
        }

        /// <summary>
        /// 사용자 저장(디스크 기록)용 바이트를 만든다. 암호로 보호된 문서는 원래의
        /// 열기·권한 비밀번호와 선택한 암호화 설정을 적용하며, 메모리의
        /// 문서는 계속 평문으로 유지된다.
        /// </summary>
        public byte[] ProtectBytesForSave(byte[] content)
        {
            lock (_docLock)
                return PdfSecurityService.Encrypt(content, _protection);
        }

        /// <summary>
        /// 병합/나누기/페이지 내보내기처럼 원본 문서에서 파생된 파일을 새로 쓸 때
        /// 사용할 보호 설정을 만든다. 보호되지 않은 문서면 null을 반환한다.
        /// </summary>
        public WriterProperties? CreateProtectedWriterProperties()
        {
            lock (_docLock)
                return _protection.Enabled ? PdfSecurityService.CreateEncryptionProperties(_protection) : null;
        }

        public void ApplyBatchEdit(Action<PdfDocument> editAction, IEnumerable<int>? pages = null) => ApplyEdit(editAction, pages: pages);

        private void ApplyEdit(Action<PdfDocument> editAction, int? pageIndex = null, IEnumerable<int>? pages = null)
        {
            byte[]? snapshot = _pdfBytes;
            if (snapshot == null) return;
            long version = EditVersion;
            object? state = GetUIStateFunc?.Invoke();
            byte[] edited = RewritePdfBytes(snapshot, editAction);
            CommitPdfBytes(snapshot, edited, state,
                pageIndex.HasValue ? PdfDocumentChange.Page(pageIndex.Value) : new(pages), version);
        }

        private async Task ApplyEditAsync(Action<PdfDocument> editAction, PdfDocumentChange change)
        {
            byte[] snapshot = _pdfBytes ?? throw new InvalidOperationException("열린 PDF 문서가 없습니다.");
            long version = EditVersion;
            object? state = GetUIStateFunc?.Invoke();
            var edited = await Task.Run(() =>
            {
                byte[] bytes = RewritePdfBytes(snapshot, editAction);
                int? count = null;
                if (change.StructureChanged)
                {
                    using var document = new PdfDocument(new PdfReader(new MemoryStream(bytes, writable: false)));
                    count = document.GetNumberOfPages();
                }
                return (Bytes: bytes, Count: count);
            });
            CommitPdfBytes(snapshot, edited.Bytes, state, change, version, edited.Count);
        }

        public byte[] CreatePdfBytesWithEdits(Action<PdfDocument> editAction) =>
            RewritePdfBytes(_pdfBytes ?? throw new InvalidOperationException("열린 PDF 문서가 없습니다."), editAction);

        public static byte[] RewritePdfBytes(byte[] snapshot, Action<PdfDocument> editAction, WriterProperties? writerProperties = null)
        {
            using var input = new MemoryStream(snapshot, writable: false);
            using var output = new MemoryStream();
            using (var document = new PdfDocument(new PdfReader(input), new PdfWriter(output, writerProperties ?? new WriterProperties())))
                editAction(document);
            return output.ToArray();
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

        public bool ReplacePdfBytesAfterSave(byte[] expectedBytes, long expectedVersion, byte[] pdfBytes, string filePath, byte[]? diskHash = null)
        {
            lock (_docLock)
            {
                if (!ReferenceEquals(_pdfBytes, expectedBytes) || EditVersion != expectedVersion) return false;
                if (ReferenceEquals(_pageCountSource, expectedBytes)) _pageCountSource = pdfBytes;
                if (ReferenceEquals(_geometrySource, expectedBytes)) _geometrySource = pdfBytes;
                _pdfBytes = pdfBytes;
                _filePath = filePath;
                if (diskHash != null) _diskVersion = new(diskHash);
                _savedStateId = _stateId;
                _savedProtection = _protection;
                _isModified = false;
                _contentCache.Clear();
                // Saving is a checkpoint, not a new document. Retain both stacks.
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                DocumentChanged?.Invoke(this, new PdfDocumentChange(Array.Empty<int>()));
                return true;
            }
        }

        public List<PdfAnnotation> LoadSavedSignatures()
        {
            if (_pdfBytes == null) return new();
            var signatures = ReadSavedSignatures(_pdfBytes);
            if (signatures.Count > 0) DetachSavedSignatureAnnotationsForEditing();
            return signatures;
        }

        public static List<PdfAnnotation> ReadSavedSignatures(byte[] bytes)
        {
            using var document = new PdfDocument(new PdfReader(new MemoryStream(bytes, writable: false)));
            var signatures = new List<PdfAnnotation>();
            for (int pageIndex = 0; pageIndex < document.GetNumberOfPages(); pageIndex++)
                foreach (var annotation in document.GetPage(pageIndex + 1).GetAnnotations())
                    if (IsSavedSignatureAnnotation(annotation) &&
                        PdfSignatureMetadata.TryDeserialize(annotation.GetContents()?.ToUnicodeString(), pageIndex, out PdfAnnotation? signature) && signature != null)
                        signatures.Add(signature);
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
                document, pageIndex, x, y, text, fontFamily, fontSize, color, isBold, isItalic), pageIndex);
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
                _contentWriter.AddHighlight(document, pageIndex, x, y, width, height, color, opacity), pageIndex);
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
                _contentWriter.AddImage(document, pageIndex, imagePath, x, y, width, height), pageIndex);
        }

        public void AddImageInternal(
            PdfDocument doc,
            int pageIndex,
            string imagePath,
            double x,
            double y,
            double width,
            double height,
            string? overlayId = null)
        {
            _contentWriter.AddImage(doc, pageIndex, imagePath, x, y, width, height, overlayId);
        }
        public Task DeletePagesAsync(IEnumerable<int> pageIndices)
        {
            int count = PageCount;
            int[] indices = pageIndices.Distinct().Where(i => i >= 0 && i < count).OrderByDescending(i => i).ToArray();
            if (indices.Length == 0 || indices.Length >= count) return Task.CompletedTask;
            return ApplyEditAsync(doc =>
            {
                foreach (int pageIndex in indices) DeletePageInternal(doc, pageIndex);
            }, new(structureChanged: true));
        }

        public void DeletePageInternal(PdfDocument doc, int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= doc.GetNumberOfPages()) return;
            doc.RemovePage(pageIndex + 1);
        }

        public Task MovePageAsync(int fromIndex, int toIndex)
        {
            int count = PageCount;
            if (fromIndex == toIndex || fromIndex < 0 || toIndex < 0 || fromIndex >= count || toIndex >= count)
                return Task.CompletedTask;
            return ApplyEditAsync(doc => MovePageInternal(doc, fromIndex, toIndex), new(structureChanged: true));
        }

        public void MovePageInternal(PdfDocument doc, int fromIndex, int toIndex)
        {
            int pageCount = doc.GetNumberOfPages();
            if (fromIndex < 0 || fromIndex >= pageCount ||
                toIndex < 0 || toIndex >= pageCount || fromIndex == toIndex)
                return;

            // iText inserts BEFORE a page in the original ordering. Moving down
            // must cross the destination page; pageCount + 1 appends at the end.
            int insertBefore = toIndex + (fromIndex < toIndex ? 2 : 1);
            doc.MovePage(fromIndex + 1, insertBefore);
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
                _diskVersion = null;
                _signedSourcePath = null;
                _isModified = false;
                _protection = new PdfProtectionSettings();
                _savedProtection = _protection;
                _stateId = _savedStateId = Guid.NewGuid();
                EditVersion++;
                _contentCache.Clear();
                _undoStack.Clear();
                _redoStack.Clear();
                DocumentChanged?.Invoke(this, EventArgs.Empty);
                PageStructureChanged?.Invoke(this, EventArgs.Empty);
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Close()
        {
            lock (_docLock)
            {
                EditVersion++;
                _contentCache.Clear();
                _pdfBytes = null;
                _pageCountSource = null;
                _geometrySource = null;
                _pageSizes.Clear();
                _cachedPageCount = 0;
                _filePath = null;
                _diskVersion = null;
                _signedSourcePath = null;
                _isModified = false;
                _protection = _savedProtection = new PdfProtectionSettings();
                _history.Clear();
                GetUIStateFunc = null;
            }
            Closed?.Invoke(this, EventArgs.Empty);
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
                var past = _undoStack.Pop();
                _redoStack.Push(new(_pdfBytes, currentState, _stateId, past.Change));
                _history.Trim(_redoStack);
                _stateId = past.StateId;
                _pdfBytes = past.Bytes;
                _pageCountSource = null;
                _geometrySource = null;
                _pageSizes.Clear();
                EditVersion++;

                _isModified = _stateId != _savedStateId || _protection != _savedProtection;
                _contentCache.Clear();

                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                UndoRedoPerformed?.Invoke(this, past.UIState);
                DocumentChanged?.Invoke(this, past.Change);
                if (past.Change.StructureChanged) PageStructureChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Redo()
        {
            if (!CanRedo || _pdfBytes == null) return;

            lock (_docLock)
            {
                var currentState = GetUIStateFunc?.Invoke();
                var next = _redoStack.Pop();
                _undoStack.Push(new(_pdfBytes, currentState, _stateId, next.Change));
                _history.Trim(_undoStack);
                _stateId = next.StateId;
                _pdfBytes = next.Bytes;
                _pageCountSource = null;
                _geometrySource = null;
                _pageSizes.Clear();
                EditVersion++;

                _isModified = _stateId != _savedStateId || _protection != _savedProtection;
                _contentCache.Clear();

                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                UndoRedoPerformed?.Invoke(this, next.UIState);
                DocumentChanged?.Invoke(this, next.Change);
                if (next.Change.StructureChanged) PageStructureChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public (double width, double height) GetPageSize(int pageIndex)
        {
            lock (_docLock)
            {
                if (_pdfBytes == null) return (0, 0);
                if (!ReferenceEquals(_geometrySource, _pdfBytes))
                {
                    using var document = new PdfDocument(new PdfReader(new MemoryStream(_pdfBytes, writable: false)));
                    _pageSizes.Clear();
                    for (int index = 0; index < document.GetNumberOfPages(); index++)
                    {
                        var page = document.GetPage(index + 1);
                        var bounds = PdfPageCoordinates.ToDisplay(page).Map(new PdfTextBox(
                            page.GetCropBox().GetX(), page.GetCropBox().GetY(), page.GetCropBox().GetWidth(), page.GetCropBox().GetHeight()));
                        _pageSizes[index] = (bounds.Width, bounds.Height);
                    }
                    _geometrySource = _pdfBytes;
                }
                return _pageSizes.GetValueOrDefault(pageIndex);
            }
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
                pdfSnapshot = _pdfBytes;
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


        public async Task TransformAnnotationsAsync(int pageIndex,
            IReadOnlyList<(PdfAnnotation Source, PdfAnnotation Target)> edits)
        {
            byte[] snapshot = _pdfBytes ?? throw new InvalidOperationException("열린 PDF 문서가 없습니다.");
            byte[] transformed = snapshot;
            var graphics = edits.Where(e => e.Source.IsOriginalImageReplacement).ToList();
            if (graphics.Count > 0)
            {
                var contents = await ExtractPageContentsAsync(pageIndex);
                var sources = _annotationTargetResolver.ResolveImages(graphics.Select(e => e.Source).ToList(), contents)
                    ?? throw new InvalidOperationException("그래픽을 다시 선택해 주세요.");
                var requests = graphics.Select((e, i) => (sources[i], PdfAffineTransform.Between(
                    new(e.Source.X, e.Source.Y, e.Source.Width, e.Source.Height),
                    new(e.Target.X, e.Target.Y, e.Target.Width, e.Target.Height), e.Target.Rotation - e.Source.Rotation))).ToList();
                transformed = await Task.Run(() => PdfGraphicTransformService.Transform(snapshot, pageIndex, requests));
            }
            var texts = edits.Where(e => e.Source.NativeText != null || e.Source.IsOriginalTextReplacement ||
                (e.Source.IsApplied && e.Source.Type is AnnotationType.Text or AnnotationType.FreeText)).ToList();
            var refreshedText = new List<NativePdfTextBlock>();
            if (texts.Count > 0)
            {
                byte[] textSource = transformed;
                var result = await Task.Run(() =>
                {
                    var service = new NativePdfTextService();
                    var allText = texts.Any(e => e.Source.NativeText == null) ? service.Extract(textSource, pageIndex) : new();
                    var requests = texts.Select(e =>
                    {
                        var source = e.Source;
                        var box = new PdfTextBox(source.X, source.Y, source.Width, source.Height);
                        var selected = source.NativeText == null ? NativePdfTextService.SelectRegion(allText,
                            PdfAffineTransform.Between(box, box, source.Rotation).Map(box)) : new();
                        var block = source.NativeText ?? (selected.Count == 1 ? selected[0] : new NativePdfTextBlock
                        {
                            Text = string.Join("\n", selected.Select(b => b.Text)),
                            Runs = selected.SelectMany(b => b.Runs).DistinctBy(r => r.Id).ToList(),
                            Glyphs = selected.SelectMany(b => b.Glyphs).ToList(),
                            Lines = selected.SelectMany(b => b.Lines).ToList(),
                            Bounds = PdfTextBox.Union(selected.Select(b => b.Bounds))
                        });
                        if (block.Glyphs.Count == 0) throw new InvalidOperationException("텍스트를 다시 선택해 주세요.");
                        return new NativePdfTextEdit(block, block.Text, Transform: PdfAffineTransform.Between(
                            new(source.X, source.Y, source.Width, source.Height),
                            new(e.Target.X, e.Target.Y, e.Target.Width, e.Target.Height), e.Target.Rotation - source.Rotation));
                    }).ToList();
                    var result = service.EditMany(textSource, pageIndex, requests);
                    foreach (var layout in result.Layouts)
                        refreshedText.Add(service.RefreshSelection(result.Bytes, pageIndex, layout) ?? layout);
                    return result;
                });
                transformed = result.Bytes;
            }
            List<PdfPageContent>? refreshedGraphics = graphics.Count > 0
                ? await _contentExtractor.ExtractAsync(transformed, pageIndex, TextEditingMode) : null;
            CommitNativeTextEdit(snapshot, transformed, pageIndex);
            for (int i = 0; i < texts.Count; i++)
            {
                NativePdfTextService.UpdateAnnotation(texts[i].Target, refreshedText[i]);
                texts[i].Target.Rotation = 0;
            }
            if (refreshedGraphics != null)
            {
                var used = new HashSet<PdfPageContent>();
                foreach (var edit in graphics)
                {
                    var expected = PdfAffineTransform.Between(
                        new(edit.Source.X, edit.Source.Y, edit.Source.Width, edit.Source.Height),
                        new(edit.Target.X, edit.Target.Y, edit.Target.Width, edit.Target.Height),
                        edit.Target.Rotation - edit.Source.Rotation).Map(new PdfTextBox(edit.Source.X, edit.Source.Y, edit.Source.Width, edit.Source.Height));
                    var content = PdfAnnotationTargetResolver.FindGraphicOperations(edit.Source, refreshedGraphics) ??
                        refreshedGraphics.Where(c => c.Type == PageContentType.Image && !used.Contains(c))
                        .OrderBy(c => Math.Abs(c.X - expected.X) + Math.Abs(c.Y - expected.Y) +
                            Math.Abs(c.Width - expected.Width) + Math.Abs(c.Height - expected.Height)).FirstOrDefault();
                    if (content == null) continue;
                    used.Add(content);
                    var target = edit.Target;
                    target.X = content.X; target.Y = content.Y; target.Width = content.Width; target.Height = content.Height;
                    target.OriginalPdfX = content.OriginalPdfX; target.OriginalPdfY = content.OriginalPdfY;
                    target.OriginalImageName = content.ImageId; target.GraphicCtm = content.GraphicCtm;
                    target.GraphicOperations = content.GraphicOperations.Select(op => op.Clone()).ToList();
                    target.GraphicHitBounds = new(content.GraphicHitBounds);
                    target.ContentStreamIndex = content.ContentStreamIndex; target.ContentStreamObjectNumber = content.ContentStreamObjectNumber;
                    target.OperationIndex = content.OperationIndex; target.Rotation = 0;
                    target.IsOriginalImageReplacement = true; target.IsApplied = false;
                }
            }
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

            bool success = false;
            await ApplyEditAsync(document =>
                    success = _contentStreamEditor.RemoveGraphics(document, pageIndex, targets), PdfDocumentChange.Page(pageIndex));
            return success;
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

            bool success = false;
            await ApplyEditAsync(document =>
                {
                    success = _contentStreamEditor.RemoveTextOperation(
                        document,
                        pageIndex,
                        contentStreamIndex,
                        operationIndex,
                        textRenderMode);
                }, PdfDocumentChange.Page(pageIndex));
            return success;
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

            bool success = false;
            await ApplyEditAsync(document =>
                {
                    success = _contentStreamEditor.RemoveTextAnnotations(
                        document,
                        pageIndex,
                        currentAnnotations);
                }, PdfDocumentChange.Page(pageIndex));
            return success;
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

            bool success = false;
            await ApplyEditAsync(document =>
                    success = _contentStreamEditor.RemoveTextByCleanup(
                        document, pageIndex, targets), PdfDocumentChange.Page(pageIndex));
            return success;
        }

        public async Task<bool> MoveTextAsync(int pageIndex, string text, double oldX, double oldY, double width, double height, double newX, double newY, string fontFamily = "맑은 고딕", double fontSize = 12, Color? color = null)
        {
            bool success = false;
            await ApplyEditAsync(doc =>
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
                }, PdfDocumentChange.Page(pageIndex));
            return success;
        }

        public PdfDocument? GetReadOnlyDocument()
        {
            if (_pdfBytes == null) return null;
            var reader = new PdfReader(new MemoryStream(_pdfBytes));
            return new PdfDocument(reader);
        }

        public async Task<List<PdfAnnotation>> MergeIntoDocumentAsync(IReadOnlyCollection<string> sourceFiles)
        {
            byte[] snapshot = _pdfBytes ?? throw new InvalidOperationException("열린 PDF 문서가 없습니다.");
            object? uiState = GetUIStateFunc?.Invoke();
            long version = EditVersion;
            byte[] merged = await _fileOperationService.CreateMergedBytesAsync(snapshot, sourceFiles);
            var signatures = await Task.Run(() => ReadSavedSignatures(merged));
            if (signatures.Count > 0)
                merged = await Task.Run(() => RewritePdfBytes(merged, RemoveSavedSignatureAnnotations));
            if (version != EditVersion) throw new InvalidOperationException("합치는 동안 문서가 변경되었습니다. 다시 시도해 주세요.");
            CommitPdfBytes(snapshot, merged, uiState, new(structureChanged: true), version);
            return signatures;
        }

        private void EnsureDerivedOutputPath(string outputPath)
        {
            if (_filePath != null && SamePath(outputPath, _filePath) ||
                _signedSourcePath != null && SamePath(outputPath, _signedSourcePath))
                throw new InvalidOperationException("파생 PDF는 원본과 다른 이름으로 저장해 주세요.");
        }

        public Task<bool> MergeFilesAsync(List<string> sourceFiles, string outputPath, byte[]? annotatedSnapshot)
        {
            EnsureDerivedOutputPath(outputPath);
            if (sourceFiles.Any(source => SamePath(source, outputPath)))
                throw new InvalidOperationException("병합 결과는 입력 파일과 다른 이름으로 저장해 주세요.");
            return _fileOperationService.MergeAsync(annotatedSnapshot, sourceFiles, outputPath, CreateProtectedWriterProperties());
        }

        public Task<int> SplitFileAsync(string outputFolder, List<(int start, int end)> ranges, byte[] annotatedSnapshot) =>
            _fileOperationService.SplitAsync(annotatedSnapshot, _filePath, outputFolder, ranges, CreateProtectedWriterProperties());

        public Task<bool> ExportPagesAsync(string outputPath, IEnumerable<int> pageIndices, byte[] annotatedSnapshot)
        {
            EnsureDerivedOutputPath(outputPath);
            return _fileOperationService.ExportPagesAsync(annotatedSnapshot, outputPath, pageIndices, CreateProtectedWriterProperties());
        }
    }
}
