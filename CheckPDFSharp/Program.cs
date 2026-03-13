using System;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

internal class Program
{
    static void Main(string[] args)
    {
        foreach (var p in typeof(OpCodes).GetProperties()) Console.WriteLine(p.Name);
        foreach (var f in typeof(OpCodes).GetFields()) Console.WriteLine(f.Name);
    }
}
