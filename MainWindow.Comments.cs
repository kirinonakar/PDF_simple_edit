using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDF_simple_edit.Controls;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Loc = PDF_simple_edit.Services.LocalizationService;

namespace PDF_simple_edit;

public sealed partial class MainWindow
{
    private byte[]? _commentSource;
    private byte[]? _bookmarkSource;
    private long _commentReadSequence;
    private bool _commentPanelVisible;
    private double _pageSidebarWidth = 200, _commentSidebarWidth = 300, _findSidebarWidth = 300;
    private EditToolMode _commentSettingsMode = EditToolMode.TextHighlight;
    private readonly Dictionary<EditToolMode, string> _commentColors = new()
    {
        [EditToolMode.TextHighlight] = "#FFFF00",
        [EditToolMode.TextUnderline] = "#FF0000",
        [EditToolMode.StickyNote] = "#FFFF00",
        [EditToolMode.CommentTextBox] = "#FF0000"
    };

    private ToggleMenuFlyoutItem[] CommentToolItems => new[] { EditorToolbar.TextHighlightItem, EditorToolbar.TextUnderlineItem,
        EditorToolbar.StickyNoteItem, EditorToolbar.CommentTextBoxItem, EditorToolbar.SelectAnnotationsItem };

    private void InitializeCommentControls()
    {
        void Resize(ColumnDefinition column, double delta, double minimum, Action<double> remember)
        {
            double other = PagePanelColumn.Width.Value + FindPanelColumn.Width.Value + CommentPanelColumn.Width.Value - column.Width.Value;
            double maximum = Math.Max(minimum, Math.Min(800, EditorContentArea.ActualWidth - other - 255));
            double width = Math.Clamp(column.Width.Value + delta, minimum, maximum);
            column.Width = new GridLength(width); remember(width);
        }
        PagePanelGrip.DragDelta += (_, e) => Resize(PagePanelColumn, e.HorizontalChange, 140, width => _pageSidebarWidth = width);
        var thumbnailResizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        thumbnailResizeTimer.Tick += (_, _) => { thumbnailResizeTimer.Stop(); if (_pdfManager.IsLoaded) _pageViewController.ScheduleThumbnailRefresh(); };
        PagePanel.SizeChanged += (_, _) =>
        {
            if (PagePanel.ActualWidth <= 0) return;
            double width = Math.Max(60, PagePanel.ActualWidth - 80);
            if (Math.Abs(_pageRenderService.ThumbnailDisplayWidth - width) < 1) return;
            _pageRenderService.ThumbnailDisplayWidth = width;
            foreach (var thumbnail in _pageThumbnails) thumbnail.ThumbnailWidth = width;
            thumbnailResizeTimer.Stop(); thumbnailResizeTimer.Start();
        };
        FindPanelGrip.DragDelta += (_, e) => Resize(FindPanelColumn, -e.HorizontalChange, 220, width => _findSidebarWidth = width);
        CommentPanelGrip.DragDelta += (_, e) => Resize(CommentPanelColumn, -e.HorizontalChange, 240, width => _commentSidebarWidth = width);
        foreach (var item in CommentToolItems) item.Click += DrawingTool_Click;
        EditorToolbar.DeleteAllCommentsItem.Click += async (_, _) => await DeleteAllCommentsAsync();
        EditorToolbar.ShowCommentSidebarItem.Click += (_, _) => ToggleCommentSidebar();
        EditorToolbar.CommentSidebarButton.Click += (_, _) => ToggleCommentSidebar();
        EditorToolbar.CommentColorPicker.ColorChanged += (_, args) =>
        {
            _annotationCanvasController.CommentColor = args.NewColor.ToString();
            _commentColors[_commentSettingsMode] = args.NewColor.ToString();
            EditorToolbar.CommentColorIndicator.Background = new SolidColorBrush(args.NewColor);
        };
        EditorToolbar.CommentOpacitySlider.ValueChanged += (_, args) =>
        {
            _annotationCanvasController.CommentOpacity = args.NewValue;
            EditorToolbar.CommentOpacityText.Text = $"{args.NewValue:P0}";
        };
        EditorToolbar.CommentFontSizeBox.ValueChanged += (_, args) =>
        {
            if (double.IsFinite(args.NewValue)) _annotationCanvasController.CommentFontSize = args.NewValue;
        };
        _annotationCanvasController.RequestCommentText = RequestCommentTextAsync;
        _annotationCanvasController.ToggleNoteRequested = note => _ = ToggleNoteAsync(note);
        _annotationCanvasController.EditCommentRequested = annotation => _ = EditCommentAsync(annotation);
        PdfSurface.EditCommentItem.Click += async (_, _) =>
        {
            if (_annotationCanvasController.PrimarySelection is { } annotation) await EditCommentAsync(annotation);
        };
        CommentPanel.CloseRequested += ToggleCommentSidebar;
        CommentPanel.CommentSelected += SelectSidebarComment;
        CommentPanel.DeleteRequested += async annotation =>
        {
            SelectSidebarComment(annotation);
            await DeleteSelectedAnnotationsAsync();
        };
        CommentPanel.ToggleNoteRequested += note => _ = ToggleNoteAsync(note);
        CommentPanel.EditRequested += annotation => _ = EditCommentAsync(annotation);
        CommentPanel.AddBookmarkRequested += () => _ = AddBookmarkAsync();
        CommentPanel.DeleteBookmarkRequested += bookmark => _ = DeleteBookmarkAsync(bookmark);
        CommentPanel.BookmarkInvoked += async bookmark =>
        {
            var manager = _pdfManager;
            if (bookmark.PageIndex is not int page || page < 0 || page >= manager.PageCount) return;
            await _annotationCanvasController.FinishActiveInlineEditAsync();
            if (manager != _pdfManager || page >= manager.PageCount) return;
            _annotationCanvasController.CancelSelectionGesture();
            _annotationCanvasController.ClearSelection();
            _currentPageIndex = page;
            await RenderCurrentPageAsync();
            _pageViewController.SyncSelection();
        };
        _annotationCanvasController.PrimarySelectionChanged += selected => CommentPanel.SelectComment(selected?.Type == AnnotationType.PdfComment ? selected.Id : null);
    }

    private void SyncCommentToolColor(EditToolMode mode)
    {
        if (!_commentColors.TryGetValue(mode, out string? color)) return;
        _commentSettingsMode = mode;
        _annotationCanvasController.CommentColor = color;
        EditorToolbar.CommentColorPicker.Color = ParseColor(color);
        EditorToolbar.CommentColorIndicator.Background = new SolidColorBrush(ParseColor(color));
    }

    private Task<string?> RequestCommentTextAsync(bool sticky) => ShowCommentTextDialogAsync(sticky, "");

    private async Task<string?> ShowCommentTextDialogAsync(bool sticky, string initialText)
    {
        var text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 340,
            Height = 180, PlaceholderText = Loc.Get("CommentContents"), MaxLength = 20000, Text = initialText };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = Loc.Get(sticky ? "StickyNote" : "CommentTextBox"),
            Content = text, PrimaryButtonText = Loc.Get("AddComment"), CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(initialText) };
        text.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(text.Text);
        dialog.Opened += (_, _) => text.Focus(FocusState.Programmatic);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? text.Text : null;
    }

    private void ToggleCommentSidebar()
    {
        _commentPanelVisible = !_commentPanelVisible;
        EditorToolbar.CommentSidebarButton.IsChecked = _commentPanelVisible;
        CommentPanel.Visibility = _commentPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        CommentPanelColumn.Width = new GridLength(_commentPanelVisible ? _commentSidebarWidth : 0);
        CommentGripColumn.Width = new GridLength(_commentPanelVisible ? 5 : 0);
        if (_commentPanelVisible) _ = RefreshCommentsAsync(force: true);
    }

    private async Task EditCommentAsync(PdfAnnotation annotation)
    {
        if (annotation.PdfCommentSubtype is not ("Text" or "FreeText")) return;
        var manager = _pdfManager; long version = manager.EditVersion;
        string? text = await ShowCommentTextDialogAsync(annotation.PdfCommentSubtype == "Text", annotation.Content);
        if (string.IsNullOrWhiteSpace(text) || text == annotation.Content || manager != _pdfManager || version != manager.EditVersion) return;
        try
        {
            await manager.UpdatePdfCommentAsync(annotation, new(annotation.X, annotation.Y, annotation.Width, annotation.Height), text);
            if (manager == _pdfManager) await RefreshCommentsAsync(force: true);
        }
        catch (Exception error) { if (manager == _pdfManager) TxtStatus.Text = error.Message; }
    }

    private async Task AddBookmarkAsync()
    {
        var manager = _pdfManager;
        if (!manager.IsLoaded) return;
        int page = _currentPageIndex; long version = manager.EditVersion;
        var title = new TextBox { Text = Loc.Format("BookmarkPageTitle", page + 1), MinWidth = 300, MaxLength = 1000 };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = Loc.Get("AddCurrentPageBookmark"), Content = title,
            PrimaryButtonText = Loc.Get("AddBookmark"), CloseButtonText = Loc.Get("Cancel"), DefaultButton = ContentDialogButton.Primary };
        title.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(title.Text);
        dialog.Opened += (_, _) => { title.Focus(FocusState.Programmatic); title.SelectAll(); };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || manager != _pdfManager || version != manager.EditVersion) return;
        try
        {
            await manager.AddBookmarkAsync(page, title.Text);
            if (manager == _pdfManager) { await RefreshCommentsAsync(force: true); TxtStatus.Text = Loc.Get("BookmarkAdded"); }
        }
        catch (Exception error) { if (manager == _pdfManager) TxtStatus.Text = error.Message; }
    }

    private async Task DeleteBookmarkAsync(PdfBookmark bookmark)
    {
        var manager = _pdfManager;
        if (!manager.IsLoaded) return;
        if (!ReferenceEquals(_bookmarkSource, manager.GetPdfBytes()))
        { await RefreshCommentsAsync(force: true); TxtStatus.Text = Loc.Get("BookmarkChangedSelectAgain"); return; }
        try
        {
            await manager.RemoveBookmarkAsync(bookmark);
            if (manager == _pdfManager) { await RefreshCommentsAsync(force: true); TxtStatus.Text = Loc.Get("BookmarkDeleted"); }
        }
        catch (Exception error) { if (manager == _pdfManager) TxtStatus.Text = error.Message; }
    }

    private void SelectSidebarComment(PdfAnnotation annotation)
    {
        if (!_pdfManager.IsLoaded) return;
        SetToolMode(EditToolMode.SelectAnnotations);
        var current = _annotations.FirstOrDefault(a => a.Type == AnnotationType.PdfComment && a.PageIndex == annotation.PageIndex && a.PdfCommentKey == annotation.PdfCommentKey);
        if (current == null) return;
        _annotationCanvasController.SelectOnly(current);
        _annotationCanvasController.Render();
        TxtStatus.Text = Loc.Get("AnnotationSelectedDeleteToRemove");
    }

    private async Task ToggleNoteAsync(PdfAnnotation annotation)
    {
        var manager = _pdfManager;
        if (annotation.PdfCommentSubtype != "Text") return;
        try
        {
            await manager.SetNoteOpenAsync(annotation, !annotation.IsCommentOpen);
            if (manager == _pdfManager) await RefreshCommentsAsync(force: true);
        }
        catch (Exception error) { if (manager == _pdfManager) TxtStatus.Text = error.Message; }
    }

    private async Task DeleteAllCommentsAsync()
    {
        if (!_pdfManager.IsLoaded) return;
        await _annotationCanvasController.FinishActiveInlineEditAsync();
        _annotationCanvasController.CancelSelectionGesture();
        var manager = _pdfManager;
        var annotations = _annotations;
        int paths = annotations.Count(a => a.IsEditablePath);
        try
        {
            int removed = await manager.RemovePdfCommentsAsync(hasPendingPaths: paths > 0);
            annotations.RemoveAll(a => a.Type == AnnotationType.PdfComment || a.IsEditablePath);
            if (manager != _pdfManager) return;
            _annotationCanvasController.ClearSelection();
            await RenderCurrentPageAsync();
            await RefreshCommentsAsync(force: true);
            TxtStatus.Text = removed + paths > 0 ? Loc.Format("AnnotationsDeleted", removed + paths) : Loc.Get("NoCommentsInThisDocument");
        }
        catch (Exception error) { if (manager == _pdfManager) TxtStatus.Text = error.Message; }
    }

    private async Task RefreshCommentsAsync(bool force = false)
    {
        var manager = _pdfManager;
        byte[]? source = manager.GetPdfBytes();
        int page = _currentPageIndex;
        if (source == null)
        {
            _commentReadSequence++; _commentSource = null; _bookmarkSource = null;
            CommentPanel.ShowComments(Array.Empty<PdfAnnotation>(), null);
            CommentPanel.ShowBookmarks(Array.Empty<PdfBookmark>());
            return;
        }
        bool commentsNeeded = force || !ReferenceEquals(source, _commentSource);
        bool bookmarksNeeded = _commentPanelVisible && (force || !ReferenceEquals(source, _bookmarkSource));
        if (!commentsNeeded && !bookmarksNeeded) return;
        long sequence = ++_commentReadSequence;
        try
        {
            var data = await Task.Run(() => (Comments: commentsNeeded ? PdfCommentService.ReadAll(source) : null,
                Bookmarks: bookmarksNeeded ? PdfBookmarkService.Read(source) : null));
            if (sequence != _commentReadSequence || manager != _pdfManager || page != _currentPageIndex || !ReferenceEquals(source, manager.GetPdfBytes())) return;
            if (data.Comments != null)
            {
                string? selectedKey = _annotationCanvasController.PrimarySelection?.Type == AnnotationType.PdfComment ? _annotationCanvasController.PrimarySelection.Id : null;
                string[] selectedKeys = _annotationCanvasController.SelectedAnnotations.Where(a => a.Type == AnnotationType.PdfComment).Select(a => a.Id).ToArray();
                _annotations.RemoveAll(a => a.Type == AnnotationType.PdfComment);
                _annotations.AddRange(data.Comments);
                _annotationCanvasController.SelectedAnnotations.RemoveAll(a => a.Type == AnnotationType.PdfComment);
                _annotationCanvasController.SelectedAnnotations.AddRange(data.Comments.Where(a => selectedKeys.Contains(a.Id)));
                if (selectedKey != null) _annotationCanvasController.PrimarySelection = data.Comments.FirstOrDefault(a => a.Id == selectedKey);
                _commentSource = source;
                CommentPanel.ShowComments(data.Comments, selectedKey);
                _annotationCanvasController.Render();
            }
            if (data.Bookmarks != null) { CommentPanel.ShowBookmarks(data.Bookmarks); _bookmarkSource = source; }
        }
        catch (Exception error) { if (sequence == _commentReadSequence && manager == _pdfManager) TxtStatus.Text = error.Message; }
    }
}
