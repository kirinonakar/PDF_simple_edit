using iText.Kernel.Geom;

namespace PDF_simple_edit.Helpers
{
    /// <summary>
    /// PDF 좌표계(Bottom-Left), iText 좌표계(Bottom-Left), WinUI 좌표계를 상호 변환하는 유틸리티 클래스입니다.
    /// </summary>
    public static class CoordinateMapper
    {
        /*
         * [좌표계 변환 논리]
         * 1. PDF (ISO 32000-1): 원점이 좌하단(Bottom-Left)이며, Y축이 위로 갈수록 증가합니다.
         * 2. iText: PDF 표준을 따르며 원점이 좌하단(Bottom-Left)입니다.
         * 3. WinUI 3 (Canvas): 원점이 좌상단(Top-Left)이며, Y축이 아래로 갈수록 증가합니다.
         * 
         * 변환 공식:
         * - X좌표: 모든 좌표계에서 왼쪽에서 오른쪽으로 증가하므로 동일합니다 (X_ui = X_pdf).
         * - Y좌표: 페이지 높이(PageHeight)를 기준으로 반전됩니다. (Y_ui = PageHeight - Y_pdf).
         */

        /// <summary>
        /// PDF 좌표(Y축 Bottom-Up)를 UI 좌표(Y축 Top-Down)로 변환합니다.
        /// </summary>
        /// <param name="pdfY">PDF Y 좌표 (좌하단 원점)</param>
        /// <param name="pageHeight">해당 PDF 페이지의 높이(Points)</param>
        public static double MapToUiY(double pdfY, double pageHeight)
        {
            return pageHeight - pdfY;
        }

        /// <summary>
        /// UI 좌표(Y축 Top-Down)를 PDF 좌표(Y축 Bottom-Up)로 변환합니다.
        /// </summary>
        public static double MapToPdfY(double uiY, double pageHeight)
        {
            return pageHeight - uiY;
        }

        /// <summary>
        /// iText의 Rectangle을 UI에서 사용 가능한 좌표로 변환합니다.
        /// </summary>
        public static (double x, double y, double width, double height) MapRectToUi(Rectangle pdfRect, double pageHeight)
        {
            double x = pdfRect.GetLeft();
            double y = pageHeight - pdfRect.GetTop();
            return (x, y, pdfRect.GetWidth(), pdfRect.GetHeight());
        }

        /// <summary>
        /// UI 좌표를 iText의 Rectangle로 변환합니다.
        /// </summary>
        public static Rectangle MapRectToPdf(double uiX, double uiY, double width, double height, double pageHeight)
        {
            float left = (float)uiX;
            float top = (float)(pageHeight - uiY);
            float bottom = (float)(top - height);
            return new Rectangle(left, bottom, (float)width, (float)height);
        }
    }
}

