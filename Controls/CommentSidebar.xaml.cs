using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Loc = PDF_simple_edit.Services.LocalizationService;

namespace PDF_simple_edit.Controls;

public sealed partial class CommentSidebar : UserControl
{
    private bool _updating;
    public event Action? CloseRequested;
    public event Action<PdfAnnotation>? CommentSelected;
    public event Action<PdfAnnotation>? DeleteRequested;
    public event Action<PdfAnnotation>? ToggleNoteRequested;
    public event Action<PdfAnnotation>? EditRequested;
    public event Action<PdfBookmark>? BookmarkInvoked;
    public event Action? AddBookmarkRequested;
    public event Action<PdfBookmark>? DeleteBookmarkRequested;

    public CommentSidebar()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateBookmarkWidths();
    }

    public static string CommentTitle(PdfAnnotation annotation) => Loc.Get(annotation.PdfCommentSubtype switch
    {
        "Highlight" => "TextHighlightComment", "Underline" => "UnderlineComment", "Text" => "StickyNote",
        "FreeText" => "CommentTextBox", _ => "Comments"
    });

    public void ShowComments(IReadOnlyList<PdfAnnotation> annotations, string? selectedKey)
    {
        _updating = true;
        try
        {
            CommentsList.Items.Clear();
            CommentCount.Text = annotations.Count == 0 ? Loc.Get("NoCommentsInThisDocument") : Loc.Format("CommentsInDocument", annotations.Count);
            foreach (var group in annotations.GroupBy(a => a.PageIndex).OrderBy(g => g.Key))
            {
                CommentsList.Items.Add(new ListViewItem { IsEnabled = false, Content = new TextBlock { Text = Loc.Format("CommentPageHeader", group.Key + 1), FontWeight = Microsoft.UI.Text.FontWeights.Bold, Margin = new Thickness(4, 12, 4, 4) } });
                foreach (var annotation in group)
                {
                var panel = new StackPanel { Spacing = 6, Padding = new Thickness(4, 6, 4, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
                panel.Children.Add(new TextBlock { Text = CommentTitle(annotation), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                panel.Children.Add(new TextBlock { Text = annotation.Content, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                if (annotation.PdfCommentSubtype is "Text" or "FreeText")
                {
                    var edit = new Button { Content = Loc.Get("EditComment"), Padding = new Thickness(6, 3, 6, 3) };
                    edit.Click += (_, _) => EditRequested?.Invoke(annotation); buttons.Children.Add(edit);
                }
                if (annotation.PdfCommentSubtype == "Text")
                {
                    var toggle = new Button { Content = Loc.Get(annotation.IsCommentOpen ? "HideNote" : "ShowNote"), Padding = new Thickness(6, 3, 6, 3) };
                    toggle.Click += (_, _) => ToggleNoteRequested?.Invoke(annotation);
                    buttons.Children.Add(toggle);
                }
                var delete = new Button { Content = Loc.Get("Delete2"), Padding = new Thickness(6, 3, 6, 3) };
                delete.Click += (_, _) => DeleteRequested?.Invoke(annotation);
                buttons.Children.Add(delete); panel.Children.Add(buttons);
                var item = new ListViewItem { Content = panel, Tag = annotation, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                CommentsList.Items.Add(item);
                if (annotation.Id == selectedKey) CommentsList.SelectedItem = item;
                }
            }
        }
        finally { _updating = false; }
    }

    public void SelectComment(string? key)
    {
        _updating = true;
        CommentsList.SelectedItem = CommentsList.Items.OfType<ListViewItem>().FirstOrDefault(item => (item.Tag as PdfAnnotation)?.Id == key);
        _updating = false;
    }

    public void ShowBookmarks(IReadOnlyList<PdfBookmark> bookmarks)
    {
        BookmarksTree.ItemsSource = bookmarks;
        NoBookmarks.Visibility = bookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBookmarkWidths();
    }

    private void UpdateBookmarkWidths()
    {
        void Update(DependencyObject element)
        {
            if (element is TextBlock { Tag: PdfBookmark bookmark } text) text.MaxWidth = Math.Max(40, ActualWidth - 64 - bookmark.Depth * 16);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Update(VisualTreeHelper.GetChild(element, i));
        }
        Update(BookmarksTree);
    }
    private void BookmarkTitle_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock { Tag: PdfBookmark bookmark } text) text.MaxWidth = Math.Max(40, ActualWidth - 64 - bookmark.Depth * 16);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void Comments_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && CommentsList.SelectedItem is ListViewItem { Tag: PdfAnnotation annotation }) CommentSelected?.Invoke(annotation);
    }
    private void Bookmark_Invoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode { Content: PdfBookmark bookmark }) BookmarkInvoked?.Invoke(bookmark);
        else if (args.InvokedItem is TreeViewNode { Content: TextBlock { Tag: PdfBookmark target } }) BookmarkInvoked?.Invoke(target);
        else if (args.InvokedItem is TextBlock { Tag: PdfBookmark title }) BookmarkInvoked?.Invoke(title);
        else if (args.InvokedItem is PdfBookmark item) BookmarkInvoked?.Invoke(item);
    }
    private void Bookmark_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args) => DeleteBookmarkButton.IsEnabled = sender.SelectedItem is PdfBookmark;
    private void AddBookmark_Click(object sender, RoutedEventArgs e) => AddBookmarkRequested?.Invoke();
    private void DeleteBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarksTree.SelectedItem is PdfBookmark bookmark) DeleteBookmarkRequested?.Invoke(bookmark);
    }
}
