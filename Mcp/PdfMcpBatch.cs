using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;

namespace PDF_simple_edit.Mcp;

internal static partial class PdfMcpTools
{
    private static string[] BatchEditTools => new[]
    {
        "pdf_add_text", "pdf_edit_text", "pdf_add_graphic", "pdf_edit_graphic",
        "pdf_delete_object", "pdf_redact_region", "pdf_manage_pages", "pdf_set_form_fields",
        "pdf_extract_pages"
    };

    private static void AddBatchTools(Dictionary<string, Tool> catalog)
    {
        void Add(string name, string description, string key, object field)
        {
            var properties = new Dictionary<string, object>(Input);
            foreach (var pair in Output) properties.Add(pair.Key, pair.Value);
            properties.Add(key, field);
            catalog.Add(name, new(name, description,
                new { type = "object", properties, required = new[] { "inputPath", "outputPath", key }, additionalProperties = false },
                new { readOnlyHint = false, destructiveHint = true, openWorldHint = false }));
        }
        Add("pdf_delete_objects", "Permanently delete multiple inspected text blocks, graphics, images or annotations in one atomic call. Pass objectIds from the SAME source revision, including IDs from different pages. Page numbers come from the IDs. Duplicate IDs are deleted once. Surrounding content, including table lines when only text is selected, is retained. No intermediate inspect or Python fallback is needed. Stale, missing, changed or ambiguous targets reject the entire call without writing the destination.",
            "objectIds", new { type = "array", minItems = 1, maxItems = 10000, items = new { type = "string" } });
        Add("pdf_batch", "Apply multiple PDF edits to ONE document and save only after every operation succeeds. operations are ordered {tool, arguments} objects. Supported tools: pdf_add_text, pdf_edit_text, pdf_add_graphic, pdf_edit_graphic, pdf_delete_object, pdf_redact_region, pdf_manage_pages, pdf_set_form_fields, pdf_extract_pages. arguments use that tool's normal schema EXCEPT inputPath/outputPath/overwrite/expectedRevision, which belong only at batch level. Existing-object IDs must all come from the input revision and each ID may be targeted once; combine properties into one edit. IDs are rebound internally after earlier edits, never accepted blindly with a changed index. Put object edits before additions/redactions that change their contents or paragraph grouping, and before page management/extraction. Consecutive text edits/deletions on the same page are applied together without moving surrounding text. Pages for other operations refer to the intermediate document. Nested batches and exports to non-PDF files are not supported. Any failure leaves the destination untouched.",
            "operations", new
            {
                type = "array", minItems = 1, maxItems = 10000,
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        tool = new { type = "string", @enum = BatchEditTools },
                        arguments = new { type = "object", additionalProperties = true }
                    },
                    required = new[] { "tool", "arguments" }, additionalProperties = false
                }
            });
    }

    private sealed record BatchStep(string Tool, PdfMcpArguments Arguments, string? ObjectId);
    private sealed record BatchTarget(int Page, string Kind, int Index, string Signature);

    private static async Task<byte[]> ExecuteBatchAsync(string name, PdfMcpArguments batch, byte[] source,
        string sourceRevision, CancellationToken cancellation)
    {
        var steps = new List<BatchStep>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        if (name == "pdf_delete_objects")
        {
            foreach (var item in batch.Value.GetProperty("objectIds").EnumerateArray())
            {
                string id = item.GetString()!;
                if (!seenIds.Add(id)) continue;
                var args = JsonSerializer.SerializeToElement(new { objectId = id, page = ParseBatchId(id, sourceRevision).Page });
                steps.Add(new("pdf_delete_object", new(args), id));
            }
        }
        else
        {
            foreach (var item in batch.Value.GetProperty("operations").EnumerateArray())
            {
                string tool = item.GetProperty("tool").GetString()!;
                var fields = item.GetProperty("arguments").EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
                foreach (string reserved in new[] { "inputPath", "outputPath", "overwrite", "expectedRevision" })
                    if (fields.ContainsKey(reserved)) throw new ArgumentException($"{reserved} belongs at batch level, not in operation arguments.");
                fields["inputPath"] = batch.Path("inputPath");
                fields["outputPath"] = batch.Path("outputPath");
                if (fields.TryGetValue("objectId", out var suppliedId) && !fields.ContainsKey("page"))
                    fields["page"] = ParseBatchId(((JsonElement)suppliedId).GetString()!, sourceRevision).Page;
                var args = JsonSerializer.SerializeToElement(fields);
                Validate(args, JsonSerializer.SerializeToElement(Catalog[tool].inputSchema), $"operations[{steps.Count + 1}].arguments");
                string? id = args.TryGetProperty("objectId", out var objectId) ? objectId.GetString() : null;
                if (id != null && !seenIds.Add(id))
                    throw new ArgumentException("Each source object ID may be targeted once per batch. Combine its changes into one operation.");
                steps.Add(new(tool, new(args), id));
            }
        }

        // Resolve every supplied ID against the original snapshot before editing.
        var targets = new Dictionary<string, BatchTarget>();
        var sourceContents = new Dictionary<int, List<PdfPageContent>>();
        using (var pdf = Open(source))
        {
            bool pagesChanged = false;
            foreach (var step in steps)
            {
                cancellation.ThrowIfCancellationRequested();
                if (step.ObjectId is { } id)
                {
                    if (pagesChanged) throw new ArgumentException("Place all inspected-object edits before page management or extraction in a batch.");
                    var target = ParseBatchId(id, sourceRevision);
                    if (target.Page > pdf.GetNumberOfPages()) throw new ArgumentException("Object page no longer exists.");
                    if (step.Arguments.Integer("page", 1) != target.Page)
                        throw new ArgumentException("Object ID belongs to another page. Supply its page in the operation arguments.");
                    string signature;
                    if (target.Kind == "content")
                    {
                        if (!sourceContents.TryGetValue(target.Page, out var contents))
                            sourceContents[target.Page] = contents = await ExtractBatchContents(source, target.Page);
                        if (target.Index >= contents.Count) throw new ArgumentException("Object no longer exists.");
                        var content = contents[target.Index];
                        if (step.Tool == "pdf_edit_text" && content.NativeText == null || step.Tool == "pdf_edit_graphic" && content.NativeText != null)
                            throw new ArgumentException("Use pdf_edit_text for text and pdf_edit_graphic for graphics.");
                        signature = ContentSignature(content);
                    }
                    else
                    {
                        if (step.Tool == "pdf_edit_text") throw new ArgumentException("Use pdf_edit_graphic for annotation objects.");
                        var annotations = pdf.GetPage(target.Page).GetAnnotations();
                        if (target.Index >= annotations.Count) throw new ArgumentException("Annotation no longer exists.");
                        signature = AnnotationSignature(annotations[target.Index]);
                    }
                    targets.Add(id, target with { Signature = signature });
                }
                if (step.Tool is "pdf_manage_pages" or "pdf_extract_pages") pagesChanged = true;
            }
        }

        if (name == "pdf_delete_objects")
            steps = steps.OrderBy(s => targets[s.ObjectId!].Page).ThenBy(s =>
            {
                var target = targets[s.ObjectId!];
                return target.Kind == "annotation" ? 2 : sourceContents[target.Page][target.Index].NativeText != null ? 0 : 1;
            }).ToList();

        var sourceSignatureCounts = new Dictionary<int, Dictionary<string, int>>();
        byte[] bytes = source;
        for (int position = 0; position < steps.Count;)
        {
            cancellation.ThrowIfCancellationRequested();
            int first = position;
            var step = steps[position];
            try
            {
                string revision = Hash(bytes);
                var currentContents = new Dictionary<int, List<PdfPageContent>>();
                var currentSignatures = new Dictionary<int, Dictionary<string, int[]>>();
                async Task<(PdfMcpArguments Arguments, PdfPageContent? Content)> Bind(BatchStep pending)
                {
                    if (pending.ObjectId is not { } id) return (pending.Arguments, null);
                    var target = targets[id];
                    int index = target.Index;
                    PdfPageContent? content = null;
                    if (target.Kind == "content")
                    {
                        if (!currentContents.TryGetValue(target.Page, out var contents))
                            currentContents[target.Page] = contents = ReferenceEquals(bytes, source)
                                ? sourceContents[target.Page] : await ExtractBatchContents(bytes, target.Page);
                        if (!ReferenceEquals(bytes, source))
                        {
                            if (!sourceSignatureCounts.TryGetValue(target.Page, out var counts))
                                sourceSignatureCounts[target.Page] = counts = sourceContents[target.Page]
                                    .GroupBy(ContentSignature).ToDictionary(g => g.Key, g => g.Count());
                            if (!currentSignatures.TryGetValue(target.Page, out var signatures))
                                currentSignatures[target.Page] = signatures = contents.Select((c, i) => (Signature: ContentSignature(c), Index: i))
                                    .GroupBy(p => p.Signature).ToDictionary(g => g.Key, g => g.Select(p => p.Index).ToArray());
                            if (counts[target.Signature] != 1 || !signatures.TryGetValue(target.Signature, out var matches) || matches.Length != 1)
                                throw new InvalidOperationException("An inspected object changed, disappeared or is ambiguous after an earlier operation. Put its edit first or inspect the intermediate PDF separately.");
                            index = matches[0];
                        }
                        content = contents[index];
                    }
                    else if (!ReferenceEquals(bytes, source))
                    {
                        using var pdf = Open(bytes);
                        var matches = pdf.GetPage(target.Page).GetAnnotations().Select((a, i) => (a, i))
                            .Where(p => AnnotationSignature(p.a) == target.Signature).ToArray();
                        if (matches.Length != 1) throw new InvalidOperationException("An annotation changed, disappeared or became ambiguous after an earlier operation.");
                        index = matches[0].i;
                    }
                    var fields = pending.Arguments.Value.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
                    fields["objectId"] = $"{revision}:{target.Page}:{target.Kind}:{index}";
                    return (new(JsonSerializer.SerializeToElement(fields)), content);
                }

                var bound = await Bind(step);
                if (step.Tool is "pdf_edit_text" or "pdf_delete_object" && bound.Content?.NativeText != null)
                {
                    int page = targets[step.ObjectId!].Page;
                    var edits = new List<NativePdfTextEdit>();
                    do
                    {
                        edits.Add(BatchTextEdit(steps[position].Tool, bound.Arguments, bound.Content!.NativeText!));
                        position++;
                        if (position == steps.Count) break;
                        var next = steps[position];
                        if (next.Tool is not ("pdf_edit_text" or "pdf_delete_object") || next.ObjectId == null || targets[next.ObjectId].Page != page) break;
                        bound = await Bind(next);
                        if (bound.Content?.NativeText == null) break;
                    } while (true);
                    bytes = new NativePdfTextService().EditMany(bytes, page - 1, edits).Bytes;
                }
                else if (step.Tool == "pdf_delete_object")
                {
                    int page = targets[step.ObjectId!].Page;
                    bool annotations = targets[step.ObjectId!].Kind == "annotation";
                    var graphics = new List<PdfAnnotation>();
                    var annotationIndexes = new List<int>();
                    do
                    {
                        if (annotations) annotationIndexes.Add(int.Parse(bound.Arguments.Text("objectId").Split(':')[3]));
                        else graphics.Add(new AnnotationContentService().ConvertToAnnotation(bound.Content!, page - 1));
                        position++;
                        if (position == steps.Count) break;
                        var next = steps[position];
                        if (next.Tool != "pdf_delete_object" || targets[next.ObjectId!].Page != page ||
                            (targets[next.ObjectId!].Kind == "annotation") != annotations) break;
                        bound = await Bind(next);
                        if (bound.Content?.NativeText != null) break;
                    } while (true);
                    bytes = Rewrite(bytes, pdf =>
                    {
                        if (annotations)
                        {
                            var pdfPage = pdf.GetPage(page);
                            var selected = annotationIndexes.Select(i => pdfPage.GetAnnotations()[i]).ToArray();
                            foreach (var annotation in selected) RemoveAnnotation(pdf, pdfPage, annotation);
                        }
                        else if (!new PdfContentStreamEditor().RemoveGraphics(pdf, page - 1, graphics))
                            throw new InvalidOperationException("The original graphics could not be removed safely.");
                    });
                }
                else
                {
                    bytes = await EditDocumentAsync(step.Tool, bound.Arguments, bytes, revision, cancellation);
                    position++;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Batch operation {first + 1} ({step.Tool}) failed; destination was not written. {error.Message}", error);
            }
        }
        return bytes;
    }

    private static BatchTarget ParseBatchId(string id, string revision)
    {
        string[] parts = id.Split(':');
        if (parts.Length != 4 || parts[0] != revision || !int.TryParse(parts[1], out int page) || page < 1 ||
            parts[2] is not ("content" or "annotation") || !int.TryParse(parts[3], out int index) || index < 0)
            throw new ArgumentException("Object ID is stale or invalid. Use IDs from pdf_inspect on the input revision.");
        return new(page, parts[2], index, "");
    }

    private static Task<List<PdfPageContent>> ExtractBatchContents(byte[] bytes, int page) =>
        new PdfPageContentExtractor().ExtractAsync(bytes, page - 1, strict: true, includePreviews: false);

    // Compare the complete visible selection, not just its old list index or box.
    // An ambiguous match fails closed. Stream/object numbers can change on rewrite.
    private static string ContentSignature(PdfPageContent content) => JsonSerializer.Serialize(new
    {
        content.Type, content.X, content.Y, content.Width, content.Height, content.Color, content.FontFamily,
        content.FontSize, content.IsBold, content.IsItalic,
        text = content.NativeText?.Text,
        glyphs = content.NativeText?.Glyphs.Where(g => !g.IsVirtual).Select(g => new { g.Text, g.Origin, g.End, code = Convert.ToHexString(g.Code) }),
        graphics = content.GraphicOperations.Select(g => new { g.IsImageOperation, g.IsTextOperation, g.IsShadingOperation, g.Bounds, g.Ctm, g.HitGeometry })
    });

    private static string AnnotationSignature(iText.Kernel.Pdf.Annot.PdfAnnotation annotation) =>
        annotation.GetPdfObject().GetIndirectReference()?.GetObjNumber() + ":" + annotation.GetPdfObject();

    private static NativePdfTextEdit BatchTextEdit(string tool, PdfMcpArguments a, NativePdfTextBlock block)
    {
        NativePdfTextStyle? style = Style.Keys.Any(a.Has) ? new(
            a.Has("color") ? a.Color() : null, a.Has("fontSize") ? a.Number("fontSize", 12, .1, 10000) : null,
            a.Has("bold") ? a.Flag("bold") : null, a.Has("italic") ? a.Flag("italic") : null,
            a.Has("fontFamily") ? a.Text("fontFamily") : null) : null;
        var target = a.Bounds(block.Bounds);
        bool transform = a.Has("width") || a.Has("height") || a.Has("rotation"), reflow = a.Flag("reflow");
        if (reflow && a.Number("rotation") != 0) throw new ArgumentException("Reflow and rotation require separate edits.");
        return new(block, tool == "pdf_delete_object" ? "" : a.Text("text", block.Text),
            transform || reflow ? 0 : target.X - block.Bounds.X, transform || reflow ? 0 : target.Y - block.Bounds.Y,
            style, transform && !reflow ? PdfAffineTransform.Between(block.Bounds, target, a.Number("rotation")) : null,
            reflow ? target : null);
    }
}
