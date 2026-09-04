using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PDF_simple_edit.Services;

/// <summary>Actual glyph outline bounds from PDFium, including embedded CFF fonts.
/// All PDFium calls are serialized; document memory is pinned until close.</summary>
internal static class NativePdfGlyphGeometry
{
    private static readonly object Gate = new();
    private static bool _initialized;
    internal sealed record GlyphBox(double X, double Y, PdfTextBox Box);

    public static List<GlyphBox> Read(byte[] bytes, int pageIndex)
    {
        lock (Gate)
        {
            if (!_initialized) { FPDF_InitLibrary(); _initialized = true; }
            var memory = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            IntPtr doc = IntPtr.Zero, page = IntPtr.Zero, text = IntPtr.Zero;
            try
            {
                doc = FPDF_LoadMemDocument64(memory.AddrOfPinnedObject(), (nuint)bytes.Length, IntPtr.Zero);
                if (doc == IntPtr.Zero) throw new InvalidOperationException("PDF 글리프 데이터를 읽을 수 없습니다.");
                page = FPDF_LoadPage(doc, pageIndex);
                if (page == IntPtr.Zero) throw new InvalidOperationException("PDF 페이지를 읽을 수 없습니다.");
                text = FPDFText_LoadPage(page);
                if (text == IntPtr.Zero) throw new InvalidOperationException("PDF 텍스트 데이터를 읽을 수 없습니다.");
                var result = new List<GlyphBox>();
                for (int i = 0, count = FPDFText_CountChars(text); i < count; i++)
                {
                    if (FPDFText_GetCharBox(text, i, out double left, out double right, out double bottom, out double top) == 0 ||
                        FPDFText_GetCharOrigin(text, i, out double x, out double y) == 0 || right <= left || top <= bottom) continue;
                    result.Add(new(x, y, new(left, bottom, right - left, top - bottom)));
                }
                return result;
            }
            finally
            {
                if (text != IntPtr.Zero) FPDFText_ClosePage(text);
                if (page != IntPtr.Zero) FPDF_ClosePage(page);
                if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
                memory.Free();
            }
        }
    }

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_InitLibrary();
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, IntPtr password);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDFText_ClosePage(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern int FPDFText_CountChars(IntPtr text);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern int FPDFText_GetCharBox(IntPtr text, int index, out double left, out double right, out double bottom, out double top);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern int FPDFText_GetCharOrigin(IntPtr text, int index, out double x, out double y);
}
