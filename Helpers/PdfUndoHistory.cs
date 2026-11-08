using System;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Helpers;

// A delta restores PrefixLength bytes from the current revision, followed by Data.
// Undo of an incremental edit needs only the old length; redo retains its suffix.
internal sealed record PdfHistoryEntry(byte[] Data, object? UIState, Guid StateId, PdfDocumentChange Change,
    int? PrefixLength = null)
{
    internal byte[] Restore(byte[] current)
    {
        if (PrefixLength is not { } prefix) return Data;
        if (prefix < 0 || prefix > current.Length) throw new InvalidOperationException("Invalid PDF history prefix.");
        byte[] restored = new byte[checked(prefix + Data.Length)];
        current.AsSpan(0, prefix).CopyTo(restored);
        Data.CopyTo(restored, prefix);
        return restored;
    }

    internal PdfHistoryEntry Reverse(byte[] current, object? uiState, Guid stateId) =>
        PrefixLength is { } prefix
            ? new(current.AsSpan(prefix).ToArray(), uiState, stateId, Change, prefix)
            : new(current, uiState, stateId, Change);
}

/// <summary>One shared byte budget for undo and redo, counting shared snapshots only once.</summary>
internal sealed class PdfUndoHistory(long byteLimit = 300L * 1024 * 1024, int stepLimit = 30)
{
    internal readonly Stack<PdfHistoryEntry> Undo = new();
    internal readonly Stack<PdfHistoryEntry> Redo = new();

    internal long RetainedBytes => Undo.Concat(Redo).Select(e => e.Data)
        .Distinct<byte[]>(ReferenceEqualityComparer.Instance).Sum(b => (long)b.Length);

    internal void Clear() { Undo.Clear(); Redo.Clear(); }

    // A save can replace the current revision with equivalent, rewritten bytes.
    // Only the adjacent entries depend on that revision's prefix. Materialize
    // those boundaries before replacement; the remaining chain stays compact.
    internal void Rebase(byte[] current)
    {
        foreach (var stack in new[] { Undo, Redo })
        {
            if (stack.TryPeek(out var entry) && entry.PrefixLength.HasValue)
            {
                byte[] bytes = entry.Restore(current);
                stack.Pop();
                stack.Push(entry with { Data = bytes, PrefixLength = null });
            }
        }
        Trim(Undo);
    }

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
