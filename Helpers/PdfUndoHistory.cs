using System;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Helpers;

internal sealed record PdfHistoryEntry(byte[] Bytes, object? UIState, Guid StateId, PdfDocumentChange Change);

/// <summary>One shared byte budget for undo and redo, counting shared snapshots only once.</summary>
internal sealed class PdfUndoHistory(long byteLimit = 300L * 1024 * 1024, int stepLimit = 30)
{
    internal readonly Stack<PdfHistoryEntry> Undo = new();
    internal readonly Stack<PdfHistoryEntry> Redo = new();

    internal long RetainedBytes => Undo.Concat(Redo).Select(e => e.Bytes)
        .Distinct<byte[]>(ReferenceEqualityComparer.Instance).Sum(b => (long)b.Length);

    internal void Clear() { Undo.Clear(); Redo.Clear(); }

    internal void Trim(Stack<PdfHistoryEntry> preferred)
    {
        TrimSteps(Undo);
        TrimSteps(Redo);
        var other = ReferenceEquals(preferred, Undo) ? Redo : Undo;
        while (RetainedBytes > byteLimit)
        {
            var stack = other.Count > 0 ? other : preferred;
            if (stack.Count == 0) break;
            var keep = stack.Take(stack.Count - 1).Reverse().ToArray();
            stack.Clear();
            foreach (var entry in keep) stack.Push(entry);
        }
    }

    private void TrimSteps(Stack<PdfHistoryEntry> stack)
    {
        if (stack.Count <= stepLimit) return;
        var keep = stack.Take(stepLimit).Reverse().ToArray();
        stack.Clear();
        foreach (var entry in keep) stack.Push(entry);
    }
}
