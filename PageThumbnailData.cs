using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PDF_simple_edit
{
    /// <summary>
    /// Page thumbnail data for the page list view.
    /// Must be in root namespace for x:DataType binding in XAML.
    /// </summary>
    public class PageThumbnailData : INotifyPropertyChanged
    {
        private int _pageNumber;
        private BitmapImage? _thumbnail;
        private Thickness _dropMargin = new(8);
        private double _thumbnailWidth = 120;

        public double ThumbnailWidth
        {
            get => _thumbnailWidth;
            set { if (Math.Abs(_thumbnailWidth - value) < .1) return; _thumbnailWidth = value; OnPropertyChanged(); OnPropertyChanged(nameof(ThumbnailHeight)); OnPropertyChanged(nameof(CardWidth)); }
        }
        public double CardWidth => ThumbnailWidth + 20;
        public double ThumbnailHeight => ThumbnailWidth * (_thumbnail?.PixelWidth > 0 ? (double)_thumbnail.PixelHeight / _thumbnail.PixelWidth : 4.0 / 3);

        public int PageNumber
        {
            get => _pageNumber;
            set { _pageNumber = value; OnPropertyChanged(); OnPropertyChanged(nameof(PageLabel)); }
        }

        public int PageIndex => PageNumber - 1;

        public string PageLabel => PageNumber.ToString();

        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; OnPropertyChanged(); OnPropertyChanged(nameof(ThumbnailHeight)); }
        }

        /// <summary>
        /// Temporary margin used to make the current page drop position visible.
        /// </summary>
        public Thickness DropMargin
        {
            get => _dropMargin;
            set
            {
                if (_dropMargin != value)
                {
                    _dropMargin = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
