using PdfSharp.Drawing;
using UglyToad.PdfPig.Core;

namespace PDF_simple_edit.Helpers
{
    /// <summary>
    /// PDF 좌표계(Bottom-Left), PdfSharp 좌표계(Top-Left), WinUI 좌표계를 상호 변환하는 유틸리티 클래스입니다.
    /// </summary>
    public static class CoordinateMapper
    {
        /*
         * [좌표계 변환 논리]
         * 1. PDF (ISO 32000-1): 원점이 좌하단(Bottom-Left)이며, Y축이 위로 갈수록 증가합니다.
         * 2. PdfSharp (XGraphics): 원점이 좌상단(Top-Left)이며, Y축이 아래로 갈수록 증가합니다. (기본 설정 시)
         * 3. WinUI 3 (Canvas): 원점이 좌상단(Top-Left)이며, Y축이 아래로 갈수록 증가합니다.
         * 
         * 변환 공식:
         * - X좌표: 모든 좌표계에서 왼쪽에서 오른쪽으로 증가하므로 동일합니다 (X_ui = X_pdf).
         * - Y좌표: 페이지 높이(PageHeight)를 기준으로 반전됩니다. (Y_ui = PageHeight - Y_pdf).
         * 
         * 상세: 
         * PdfPig에서 가져온 Word.BoundingBox.Bottom은 글자의 하단 라인입니다.
         * PdfSharp의 DrawString(..., XStringFormats.TopLeft)은 글자의 상단 라인을 기준으로 그립니다.
         * 따라서 정확한 위치에 덧쓰기 위해서는 PdfPig의 'Top' 좌표를 UI Y로 변환하여 사용해야 합니다.
         */

        /// <summary>
        /// PDF 좌표(Y축 Bottom-Up)를 UI/PdfSharp 좌표(Y축 Top-Down)로 변환합니다.
        /// </summary>
        /// <param name="pdfX">PDF X 좌표</param>
        /// <param name="pdfY">PDF Y 좌표 (좌하단 원점)</param>
        /// <param name="pageHeight">해당 PDF 페이지의 높이(Points)</param>
        public static double MapToUiY(double pdfY, double pageHeight)
        {
            return pageHeight - pdfY;
        }

        /// <summary>
        /// UI/PdfSharp 좌표(Y축 Top-Down)를 PDF 좌표(Y축 Bottom-Up)로 변환합니다.
        /// </summary>
        public static double MapToPdfY(double uiY, double pageHeight)
        {
            return pageHeight - uiY;
        }

        /// <summary>
        /// PdfPig의 BoundingBox를 PdfSharp에서 사용 가능한 XRect(Top-Left 기준)로 변환합니다.
        /// </summary>
        /// <param name="pdfRect">PdfPig의 PdfRectangle</param>
        /// <param name="pageHeight">페이지 높이</param>
        public static XRect MapRectToUi(PdfRectangle pdfRect, double pageHeight)
        {
            // PDF의 Top(가장 큰 Y)이 UI에서는 상단(가장 작은 Y)이 됩니다.
            double x = pdfRect.Left;
            double y = pageHeight - pdfRect.Top;
            return new XRect(x, y, pdfRect.Width, pdfRect.Height);
        }

        /// <summary>
        /// PdfSharp의 XRect를 PDF 좌표계의 PdfRectangle로 변환합니다.
        /// </summary>
        public static PdfRectangle MapRectToPdf(XRect uiRect, double pageHeight)
        {
            double left = uiRect.Left;
            double top = pageHeight - uiRect.Top;
            double bottom = top - uiRect.Height;
            return new PdfRectangle(left, bottom, left + uiRect.Width, top);
        }
    }
}
