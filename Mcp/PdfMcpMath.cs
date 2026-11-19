using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;

namespace PDF_simple_edit.Mcp;

/// <summary>Infers simple math structures from the visible positions of PDF glyphs.</summary>
internal static class PdfMcpMath
{
    internal sealed record Glyph(string Text, PdfTextBox Bounds, double Left, double Right, double Baseline, double Em,
        double? RuleY = null, bool Roman = false);
    internal sealed record Equation(string text, string? latex, PdfMcpPageDetails.Bounds bounds, string status);
    private sealed record Part(Glyph Position, string Latex, bool Unmapped = false);

    private static readonly string[] MathFonts =
        ["CambriaMath", "LatinModernMath", "STIXTwoMath", "STIXMath", "XITSMath", "AsanaMath", "TeXGyrePagellaMath",
         "TeXGyreTermesMath", "CMMI", "CMSY", "CMEX", "LMMI", "LMSY", "LMEX"];

    public static bool IsMathFont(string? name)
    {
        if (HwpEquationText.IsFont(name)) return true;
        if (name == null) return false;
        int subset = name.IndexOf('+');
        string family = (subset == 6 ? name[7..] : name).Replace(" ", "", StringComparison.Ordinal);
        return MathFonts.Any(font => family.StartsWith(font, StringComparison.OrdinalIgnoreCase));
    }

    public static bool ContainsMathSymbol(string text) => text.AsSpan().IndexOfAny("=±×÷−∓∑∏∫∬∭∮√∞∂∇≠≤≥≈∈∉⊂⊆∩∪") >= 0;
    public static bool IsNamedOperator(string text) => text is "lim" or "sin" or "cos" or "tan" or "log" or "ln" or "exp" or "min" or "max";

    public static IReadOnlyList<Equation> Read(List<Glyph> glyphs, List<PdfTextBox> paths, CancellationToken cancellation)
    {
        if (glyphs.Count == 0) return [];
        var parts = glyphs.Select(glyph => new Part(glyph, ToLatex(glyph.Text),
            glyph.Text.EnumerateRunes().Any(rune => PdfMcpPageDetails.IsPrivateUse(rune) || rune == Rune.ReplacementChar))).ToList();
        var rules = paths.Select(path => (Box: path, Source: (Part?)null))
            .Concat(parts.Where(part => part.Position.RuleY != null).Select(part =>
                (Box: new PdfTextBox(part.Position.Left, part.Position.RuleY!.Value,
                    part.Position.Right - part.Position.Left, 0), Source: (Part?)part)));

        foreach (var rule in rules.OrderBy(rule => rule.Box.Width).ToArray())
        {
            cancellation.ThrowIfCancellationRequested();
            FoldRule(parts, rule.Box, rule.Source);
        }

        var equations = new List<(Equation Equation, double Baseline)>();
        foreach (var group in Group(parts, cancellation))
        {
            cancellation.ThrowIfCancellationRequested();
            Part equation = Compose(group);
            var position = equation.Position;
            string status = equation.Unmapped ? "unmapped" : group.Any(part => part.Position.RuleY != null) ? "unresolved" : "inferred";
            equations.Add((new(position.Text, status == "inferred" ? equation.Latex : null,
                PdfMcpPageDetails.Bounds.From(position.Bounds), status),
                position.Baseline));
        }
        return equations.OrderBy(entry => entry.Baseline).ThenBy(entry => entry.Equation.bounds.x)
            .Select(entry => entry.Equation).ToArray();
    }

    private static void FoldRule(List<Part> parts, PdfTextBox bar, Part? source)
    {
        if (source != null && !parts.Contains(source)) return;
        double y = bar.Y + bar.Height / 2;
        double tolerance = Math.Clamp(bar.Width * .06, .5, 1.5);
        var enclosed = parts.Where(part => part != source && part.Position.RuleY == null &&
            part.Position.Left >= bar.X - tolerance && part.Position.Right <= bar.Right + tolerance).ToArray();

        // Radical overbars can be drawn as paths or as a stretched rule glyph.
        var radical = parts.Where(part => part.Position.Text == "√" && part.Position.Left <= bar.X + tolerance &&
            Math.Abs(part.Position.Right - bar.X) <= part.Position.Em * .35 &&
            part.Position.Baseline > y && part.Position.Baseline - y <= part.Position.Em * 1.35)
            .MinBy(part => Math.Abs(part.Position.Right - bar.X));
        if (radical != null)
        {
            var root = radical.Position;
            var inside = enclosed.Where(part => part != radical && part.Position.Baseline > y &&
                Math.Abs(part.Position.Baseline - root.Baseline) <= root.Em * .5).ToList();
            if (inside.Count == 0) return;
            Part content = Compose(inside);
            var degree = parts.Where(part => part != radical && !inside.Contains(part) && part.Position.Em < root.Em * .8 &&
                part.Position.Left <= root.Left + root.Em * .25 && part.Position.Right >= root.Left - root.Em * .2 &&
                part.Position.Baseline >= y - root.Em * .25 && part.Position.Baseline < root.Baseline - root.Em * .25).ToArray();
            Part? index = degree.Length > 0 ? Compose(degree.ToList()) : null;
            string latex = index == null ? $@"\sqrt{{{content.Latex}}}" : $@"\sqrt[{index.Latex}]{{{content.Latex}}}";
            Replace(parts, [radical, .. inside, .. degree], source, bar, root.Baseline, root.Em,
                latex, (index?.Position.Text ?? "") + "√" + content.Position.Text);
            return;
        }

        var above = FractionSide(enclosed, y, above: true);
        var below = FractionSide(enclosed, y, above: false);
        if (above.Count == 0 || below.Count == 0) return;
        var numerator = Compose(above);
        var denominator = Compose(below);
        double middle = (bar.X + bar.Right) / 2;
        if (!Centered(numerator.Position, middle, bar.Width) || !Centered(denominator.Position, middle, bar.Width)) return;
        double widerTerm = Math.Max(numerator.Position.Right - numerator.Position.Left,
            denominator.Position.Right - denominator.Position.Left);
        if (widerTerm < bar.Width * .45) return;
        double em = Math.Max(numerator.Position.Em, denominator.Position.Em);
        double baseline = (numerator.Position.Baseline + denominator.Position.Baseline) / 2;
        Replace(parts, [.. above, .. below], source, bar, baseline, em,
            $@"\frac{{{numerator.Latex}}}{{{denominator.Latex}}}",
            numerator.Position.Text + "/" + denominator.Position.Text);
    }

    private static List<Part> FractionSide(Part[] enclosed, double ruleY, bool above)
    {
        var onSide = enclosed.Where(part => (part.Position.Baseline <= ruleY) == above).ToArray();
        var baseline = onSide.Where(part => Math.Abs(part.Position.Baseline - ruleY) <=
            part.Position.Em * (above ? .95 : 1.3)).ToArray();
        // Scripts can lie further from the rule than a full-size term on their row.
        return onSide.Where(part => baseline.Contains(part) || baseline.Any(parent =>
            part.Position.Em <= parent.Position.Em * .88 &&
            Math.Abs(part.Position.Baseline - parent.Position.Baseline) <= parent.Position.Em * .9 &&
            NearScript(parent.Position, part.Position, limits: false))).ToList();
    }

    private static bool Centered(Glyph glyph, double center, double width) =>
        Math.Abs((glyph.Left + glyph.Right) / 2 - center) <= width * .28 + .8;

    private static void Replace(List<Part> parts, Part[] members, Part? source, PdfTextBox bar,
        double baseline, double em, string latex, string text)
    {
        if (source != null) parts.Remove(source);
        var consumed = members.ToHashSet();
        parts.RemoveAll(consumed.Contains);
        var result = Combine(members, text, latex, baseline, em);
        var position = result.Position;
        parts.Add(result with { Position = position with
        {
            Bounds = PdfTextBox.Union(new[] { position.Bounds, bar }),
            Left = Math.Min(position.Left, bar.X), Right = Math.Max(position.Right, bar.Right)
        } });
    }

    private static IEnumerable<List<Part>> Group(List<Part> parts, CancellationToken cancellation)
    {
        var groups = new List<List<Part>>();
        var active = new List<(Part Part, List<Part> Group)>();
        double maxEm = parts.Count == 0 ? 0 : parts.Max(part => part.Position.Em);
        foreach (Part part in parts.OrderBy(part => part.Position.Left))
        {
            cancellation.ThrowIfCancellationRequested();
            Glyph glyph = part.Position;
            active.RemoveAll(entry => entry.Part.Position.Right + maxEm * .72 < glyph.Left);
            var matches = active.Where(entry => Touches(entry.Part.Position, glyph)).Select(entry => entry.Group)
                .Distinct().ToArray();
            List<Part> group;
            if (matches.Length == 0)
            {
                group = [];
                groups.Add(group);
            }
            else
            {
                group = matches[0];
                foreach (var merged in matches.Skip(1))
                {
                    group.AddRange(merged);
                    merged.Clear();
                    for (int i = 0; i < active.Count; i++)
                        if (ReferenceEquals(active[i].Group, merged))
                            active[i] = (active[i].Part, group);
                }
            }
            group.Add(part);
            active.Add((part, group));
        }
        return groups.Where(group => group.Count > 0);
    }

    private static bool Touches(Glyph left, Glyph right)
    {
        double gap = right.Left - left.Right;
        double em = Math.Max(left.Em, right.Em);
        double rise = Math.Abs(left.Baseline - right.Baseline);
        if (rise <= em * .20) return gap <= em * .72;
        Glyph parent = left.Em >= right.Em ? left : right;
        Glyph script = left.Em >= right.Em ? right : left;
        bool limits = IsLargeOperator(parent.Text);
        return rise <= em && script.Em <= em * .94 && NearScript(parent, script, limits);
    }

    private static Part Compose(List<Part> parts)
    {
        var ordered = parts.OrderBy(part => part.Position.Left).ThenBy(part => part.Position.Baseline).ToList();
        MergeNamedOperators(ordered);
        var scriptBases = ordered.Where(part => !IsLargeOperator(part.Position.Text))
            .OrderBy(part => part.Position.Em).ToArray();
        // Handle centered and side limits before ordinary subscripts/superscripts.
        foreach (Part part in ordered.Where(part => IsLargeOperator(part.Position.Text)).ToArray())
            if (ordered.Contains(part)) AttachScripts(ordered, part, limits: true);
        foreach (Part part in scriptBases)
            if (ordered.Contains(part)) AttachScripts(ordered, part, limits: false);

        var anchor = ordered.MaxBy(part => part.Position.Em)!.Position;
        return Combine(ordered, string.Concat(ordered.Select(part => part.Position.Text)),
            JoinLatex(ordered), anchor.Baseline, anchor.Em);
    }

    private static void MergeNamedOperators(List<Part> parts)
    {
        // HYhwpEQ uses plain Latin glyphs for named operators and PUA glyphs
        // for italic variables. An operator can be split across text operands.
        for (int i = 0; i < parts.Count; i++)
        {
            Glyph first = parts[i].Position;
            if (!first.Roman) continue;
            string text = first.Text;
            for (int j = i + 1; j < parts.Count && text.Length < 3; j++)
            {
                Glyph current = parts[j].Position;
                if (!current.Roman || Math.Abs(current.Baseline - first.Baseline) > first.Em * .06 ||
                    Math.Abs(current.Left - parts[j - 1].Position.Right) > first.Em * .13) break;
                text += current.Text;
                if (!IsNamedOperator(text)) continue;
                int count = j - i + 1;
                Part combined = Combine(parts.GetRange(i, count), text, ToLatex(text), first.Baseline, first.Em);
                parts.RemoveRange(i, count);
                parts.Insert(i, combined);
                break;
            }
        }
    }

    private static bool IsLargeOperator(string text) => text is "∑" or "∏" or "∐" or "∫" or "∬" or "∭" or "∮" or "∯" or "∰" or "lim";

    private static void AttachScripts(List<Part> parts, Part parent, bool limits)
    {
        var baseGlyph = parent.Position;
        Rune last = baseGlyph.Text.EnumerateRunes().LastOrDefault();
        if (!limits && !Rune.IsLetterOrDigit(last) && last.Value is not (')' or ']' or '}' or '|' or '⎢')
            && !parent.Latex.StartsWith(@"\frac{", StringComparison.Ordinal))
            return;

        double maxSize = baseGlyph.Em * (limits ? .94 : .88);
        var small = parts.Where(part => part != parent && part.Position.Em <= maxSize &&
            Math.Abs(part.Position.Baseline - baseGlyph.Baseline) <= baseGlyph.Em).ToArray();
        Part[] subscript = FindScript(small, baseGlyph, limits, above: false);
        Part[] superscript = FindScript(small, baseGlyph, limits, above: true);
        if (subscript.Length + superscript.Length == 0) return;

        string latex = parent.Latex;
        if (latex.Length > 1 && !latex.StartsWith('\\') && baseGlyph.Text.Length > 1)
            latex = "{" + latex + "}";
        if (subscript.Length > 0) latex += "_{" + JoinLatex(subscript) + "}";
        if (superscript.Length > 0) latex += "^{" + JoinLatex(superscript) + "}";
        int index = parts.IndexOf(parent);
        var attached = subscript.Concat(superscript).ToHashSet();
        string text = baseGlyph.Text + string.Concat(subscript.Select(part => part.Position.Text))
            + string.Concat(superscript.Select(part => part.Position.Text));
        Part[] members = [parent, .. subscript, .. superscript];
        parts[index] = Combine(members, text, latex, baseGlyph.Baseline, baseGlyph.Em);
        parts.RemoveAll(attached.Contains);
    }

    private static Part[] FindScript(Part[] candidates, Glyph parent, bool limits, bool above)
    {
        var row = candidates.Where(part => (above ? parent.Baseline - part.Position.Baseline
                : part.Position.Baseline - parent.Baseline) >= parent.Em * .16)
            .OrderBy(part => part.Position.Left).ToArray();
        var first = row.FirstOrDefault(part => NearScript(parent, part.Position, limits));
        if (first == null) return [];
        var script = new List<Part> { first };
        foreach (var next in row.Skip(Array.IndexOf(row, first) + 1))
        {
            var last = script[^1].Position;
            if (next.Position.Left - last.Right > parent.Em * .34 ||
                Math.Abs(next.Position.Baseline - first.Position.Baseline) > first.Position.Em * .35) break;
            script.Add(next);
        }
        return script.ToArray();
    }

    private static bool NearScript(Glyph parent, Glyph script, bool limits)
    {
        bool centered = limits && parent.Text is ("lim" or "∑" or "∏" or "∐");
        double start = centered ? parent.Left - parent.Em * .38 : parent.Right - parent.Em * .24;
        double end = parent.Right + parent.Em * (limits ? .30 : .46);
        return script.Left >= start && script.Left <= end
            && script.Right >= parent.Left - parent.Em * .15;
    }

    private static Part Combine(IReadOnlyList<Part> parts, string text, string latex, double baseline, double em) =>
        new(new(text, PdfTextBox.Union(parts.Select(part => part.Position.Bounds)),
            parts.Min(part => part.Position.Left), parts.Max(part => part.Position.Right), baseline, em),
            latex, parts.Any(part => part.Unmapped));

    private static string JoinLatex(IEnumerable<Part> parts)
    {
        var result = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Latex.Length == 0) continue;
            if (char.IsAsciiLetter(part.Latex[0]) && EndsInCommand(result))
                result.Append(' ');
            result.Append(part.Latex);
        }
        return result.ToString();
    }

    private static bool EndsInCommand(StringBuilder latex)
    {
        int index = latex.Length - 1;
        while (index >= 0 && char.IsAsciiLetter(latex[index])) index--;
        return index >= 0 && index < latex.Length - 1 && latex[index] == '\\';
    }

    private static string ToLatex(string text)
    {
        if (IsNamedOperator(text)) return @"\" + text;
        var latex = new StringBuilder();
        foreach (Rune rune in text.EnumerateRunes())
        {
            string? command = rune.Value switch
            {
                '±' => "pm", '∓' => "mp", '×' => "times", '÷' => "div", '·' => "cdot",
                '∑' => "sum", '∏' => "prod", '∐' => "coprod", '∫' => "int", '∬' => "iint", '∭' => "iiint",
                '∮' => "oint", '∯' => "oiint", '∰' => "oiiint", '√' => "surd", '∞' => "infty",
                '∂' => "partial", '∇' => "nabla", '≠' => "ne", '≤' => "le", '≥' => "ge", '≈' => "approx",
                '∈' => "in", '∉' => "notin", '⊂' => "subset", '⊆' => "subseteq", '∩' => "cap", '∪' => "cup",
                '←' => "leftarrow", '→' => "to", '↔' => "leftrightarrow", '⇒' => "Rightarrow",
                'α' => "alpha", 'β' => "beta", 'γ' => "gamma", 'δ' => "delta", 'ε' => "epsilon", 'ζ' => "zeta",
                'η' => "eta", 'θ' => "theta", 'ι' => "iota", 'κ' => "kappa", 'λ' => "lambda", 'μ' => "mu", 'ν' => "nu",
                'ξ' => "xi", 'π' => "pi", 'ρ' => "rho", 'σ' => "sigma", 'τ' => "tau", 'υ' => "upsilon",
                'φ' => "phi", 'χ' => "chi", 'ψ' => "psi", 'ω' => "omega",
                'Γ' => "Gamma", 'Δ' => "Delta", 'Θ' => "Theta", 'Λ' => "Lambda", 'Ξ' => "Xi", 'Π' => "Pi",
                'Σ' => "Sigma", 'Υ' => "Upsilon", 'Φ' => "Phi", 'Ψ' => "Psi", 'Ω' => "Omega",
                '\\' => "backslash", '^' => "hat{}", '~' => "sim", '―' => "text{―}",
                _ => null
            };
            if (command != null) latex.Append('\\').Append(command);
            else if (rune.Value is '−' or '‐') latex.Append('-');
            else if (rune.Value == '⎢') latex.Append('|');
            else if (rune.Value is '′' or '″') latex.Append(rune.Value == '′' ? "'" : "''");
            else if (rune.Value is '{' or '}' or '#' or '$' or '%' or '&' or '_') latex.Append('\\').Append(rune.ToString());
            else
            {
                if (rune.IsAscii && char.IsAsciiLetter((char)rune.Value) && EndsInCommand(latex)) latex.Append(' ');
                latex.Append(rune.ToString());
            }
        }
        return latex.ToString();
    }
}
