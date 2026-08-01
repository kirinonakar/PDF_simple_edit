using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PDF_simple_edit.Services;

public sealed class RecentFilesService
{
    private const int MaxFiles = 10;
    private readonly string _filePath;
    private readonly List<string> _files = new();

    public IReadOnlyList<string> Files => _files;

    public RecentFilesService()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PDF_simple_edit");
        _filePath = Path.Combine(folder, "recent_files.txt");
    }

    public void Load()
    {
        try
        {
            _files.Clear();
            if (File.Exists(_filePath))
                _files.AddRange(File.ReadAllLines(_filePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Recent files load error: {ex.Message}");
        }
    }

    public void Add(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        _files.Remove(filePath);
        _files.Insert(0, filePath);
        if (_files.Count > MaxFiles)
            _files.RemoveRange(MaxFiles, _files.Count - MaxFiles);
        Save();
    }

    public void Remove(string filePath)
    {
        if (_files.Remove(filePath))
            Save();
    }

    public void Clear()
    {
        _files.Clear();
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllLines(_filePath, _files);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Recent files save error: {ex.Message}");
        }
    }
}
