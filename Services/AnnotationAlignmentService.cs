using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Services;

public enum AnnotationAlignment
{
    Left,
    Right,
    Top,
    Bottom
}

public sealed class AnnotationAlignmentService
{
    public void Align(IReadOnlyList<PdfAnnotation> annotations, AnnotationAlignment alignment)
    {
        if (annotations.Count < 2)
            return;

        double edge = alignment switch
        {
            AnnotationAlignment.Left => annotations.Min(annotation => annotation.X),
            AnnotationAlignment.Right => annotations.Max(annotation => annotation.X + annotation.Width),
            AnnotationAlignment.Top => annotations.Min(annotation => annotation.Y),
            AnnotationAlignment.Bottom => annotations.Max(annotation => annotation.Y + annotation.Height),
            _ => throw new ArgumentOutOfRangeException(nameof(alignment))
        };

        foreach (PdfAnnotation annotation in annotations)
        {
            switch (alignment)
            {
                case AnnotationAlignment.Left:
                    annotation.X = edge;
                    break;
                case AnnotationAlignment.Right:
                    annotation.X = edge - annotation.Width;
                    break;
                case AnnotationAlignment.Top:
                    annotation.Y = edge;
                    break;
                case AnnotationAlignment.Bottom:
                    annotation.Y = edge - annotation.Height;
                    break;
            }
            annotation.IsApplied = false;
        }
    }
}
