using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;

namespace PDF_simple_edit
{
    public class PdfDocumentTab : INotifyPropertyChanged
    {
        private string _header = "새 문서";
        public string? Id { get; set; } = Guid.NewGuid().ToString();
        
        public string Header 
        { 
            get => _header; 
            set { if (_header != value) { _header = value; OnPropertyChanged(); } } 
        }
        
        public string? FilePath { get; set; }
        
        public PdfDocumentManager PdfManager { get; set; } = new();
        public ObservableCollection<PageThumbnailData> PageThumbnails { get; set; } = new();
        public List<PdfAnnotation> Annotations { get; set; } = new();
        
        public int CurrentPageIndex { get; set; } = 0;
        public double ZoomLevel { get; set; } = 1.0;
        public string? RenderTempPath { get; set; }
        public bool IsFirstLoad { get; set; } = true;
        
        public bool IsModified => PdfManager.IsModified;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
