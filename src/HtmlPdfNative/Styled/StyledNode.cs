using System;
using System.Collections.Generic;
using System.Text;

namespace HtmlPdfNative.Styled
{
    /// <summary>
    /// A DOM node paired with its resolved <see cref="Style.ComputedStyle"/>. Subset of
    /// <c>src/styled.rs</c> (pseudo-elements, counters, inline-SVG serialization come later).
    /// </summary>
    public sealed class StyledNode
    {
        public Dom.Node Node { get; }
        public Style.ComputedStyle Style { get; }
        public List<StyledNode> Children { get; } = new List<StyledNode>();
        public Style.ComputedStyle? FirstLetterStyle { get; set; } // ::first-letter (drop-cap), null if none
        public Style.ComputedStyle? FirstLineStyle { get; set; }   // ::first-line, null if none
        public Style.ComputedStyle? MarkerStyle { get; set; }      // ::marker (list bullet/number), null if none

        public bool IsText => Node.IsText;
        public string Text => Node.Text ?? "";

        private StyledNode(Dom.Node node, Style.ComputedStyle style) { Node = node; Style = style; }

        /// <summary>Create an anonymous styled node (e.g. an anonymous flex/grid item wrapping a text run).</summary>
        public static StyledNode CreateAnonymous(Dom.Node node, Style.ComputedStyle style) => new StyledNode(node, style);

        /// <summary>Build the styled tree, skipping <c>display:none</c>. Analogue of <c>StyledNode::build</c>.</summary>
        public static StyledNode Build(Dom.Node root, Style.StyleComputer computer)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            return BuildNode(root, null, computer, new Dictionary<string, int>())!;
        }

        private static StyledNode? BuildNode(Dom.Node node, Style.ComputedStyle? parent, Style.StyleComputer computer, Dictionary<string, int> counters)
        {
            if (node.IsText)
            {
                // Text inherits its parent's computed style.
                var ts = new Style.ComputedStyle();
                if (parent != null)
                {
                    ts.FontSizePt = parent.FontSizePt; ts.Bold = parent.Bold; ts.Weight = parent.Weight; ts.Italic = parent.Italic;
                    ts.FontFamily = parent.FontFamily; ts.Color = parent.Color; ts.TextAlign = parent.TextAlign; ts.TextAlignLast = parent.TextAlignLast;
                    ts.LineHeightPt = parent.LineHeightPt; ts.LineHeightMul = parent.LineHeightMul; // BOTH — unitless line-height inherits as a multiplier
                    ts.TextDecorationLine = parent.TextDecorationLine; ts.TextDecorationColor = parent.TextDecorationColor; ts.TextDecorationStyle = parent.TextDecorationStyle;
                    ts.TextDecorationThickness = parent.TextDecorationThickness; ts.TextUnderlineOffset = parent.TextUnderlineOffset;
                    ts.TextStrokeWidth = parent.TextStrokeWidth; ts.TextStrokeColor = parent.TextStrokeColor;
                    ts.LetterSpacing = parent.LetterSpacing; ts.WordSpacing = parent.WordSpacing;
                    ts.TextTransform = parent.TextTransform; ts.WhiteSpace = parent.WhiteSpace; ts.Visibility = parent.Visibility; ts.TabSize = parent.TabSize; ts.TextWrap = parent.TextWrap;
                    ts.OverflowWrap = parent.OverflowWrap; ts.WordBreak = parent.WordBreak;
                    ts.VerticalAlign = parent.VerticalAlign; // propagate a run's shift to its own text
                    ts.Direction = parent.Direction; ts.TextShadows = parent.TextShadows;
                }
                return new StyledNode(node, ts);
            }

            var cs = computer.Compute(node, parent);
            // <dialog open> overrides the UA display:none BEFORE the none-check drops it.
            if (node.Tag == "dialog" && node.Attributes.ContainsKey("open") && cs.Display == "none") { cs.Display = "block"; cs.BorderTop = cs.BorderRight = cs.BorderBottom = cs.BorderLeft = new Style.BorderEdge { Width = 1f * Lib.PxToPt, Color = new Render.Color(0, 0, 0) }; cs.PadTop = cs.PadRight = cs.PadBottom = cs.PadLeft = 16f * Lib.PxToPt; }
            if (cs.Display == "none") return null;

            // CSS counters take effect on the element in source order: reset, then increment.
            if (cs.CounterReset != null) foreach (var (name, val) in cs.CounterReset) counters[name] = val;
            if (cs.CounterSet != null) foreach (var (name, val) in cs.CounterSet) counters[name] = val;
            if (cs.CounterIncrement != null) foreach (var (name, delta) in cs.CounterIncrement) counters[name] = (counters.TryGetValue(name, out var cur) ? cur : 0) + delta;

            var sn = new StyledNode(node, cs);
            sn.FirstLetterStyle = computer.ComputePseudoStyle(node, cs, "first-letter");
            sn.FirstLineStyle = computer.ComputePseudoStyle(node, cs, "first-line");
            if (node.Tag == "li") sn.MarkerStyle = computer.ComputePseudoStyle(node, cs, "marker");
            // Form controls: <input> is void — synthesize its rendered content (value/placeholder/checkbox).
            if (node.Tag == "input") { BuildInput(sn, node, cs, computer, counters); return sn; }
            if (node.Tag == "select") { BuildSelect(sn, node, cs, computer, counters); return sn; }
            if (node.Tag == "progress" || node.Tag == "meter") { BuildBar(sn, node, cs); return sn; }
            // <details> hides non-summary children unless [open].
            bool detailsClosed = node.Tag == "details" && !node.Attributes.ContainsKey("open");
            var before = BuildPseudo(node, cs, computer, "before", counters);
            if (before != null) sn.Children.Add(before);
            // <q>: UA wraps content in quotation marks (open-quote / close-quote from the `quotes` property).
            if (node.Tag == "q") { var oq = cs.Quotes != null && cs.Quotes.Length >= 1 ? cs.Quotes[0] : "“"; var qn = BuildNode(new Dom.Node { Text = oq, Parent = node }, cs, computer, counters); if (qn != null) sn.Children.Add(qn); }
            bool sawSummary = false;
            foreach (var child in node.Children)
            {
                // A closed <details> shows only its first <summary> (the rest of the content is collapsed).
                if (detailsClosed)
                {
                    if (child.IsText) continue;
                    if (child.Tag == "summary" && !sawSummary) sawSummary = true; else continue;
                }
                var built = BuildNode(child, cs, computer, counters);
                if (built != null) sn.Children.Add(built);
            }
            if (node.Tag == "q") { var cq = cs.Quotes != null && cs.Quotes.Length >= 2 ? cs.Quotes[1] : "”"; var qn = BuildNode(new Dom.Node { Text = cq, Parent = node }, cs, computer, counters); if (qn != null) sn.Children.Add(qn); }
            var after = BuildPseudo(node, cs, computer, "after", counters);
            if (after != null) sn.Children.Add(after);
            return sn;
        }

        /// <summary>Synthesize the rendered content of an &lt;input&gt; (a void element): value/placeholder text,
        /// or a checkbox/radio glyph. Static (no interactivity).</summary>
        private static void BuildInput(StyledNode sn, Dom.Node node, Style.ComputedStyle cs, Style.StyleComputer computer, Dictionary<string, int> counters)
        {
            node.Attributes.TryGetValue("type", out var type);
            type = (type ?? "text").ToLowerInvariant();
            if (type == "hidden") { cs.Display = "none"; return; }
            if (type == "checkbox" || type == "radio")
            {
                cs.Display = "inline-block";
                cs.Width ??= 13f * Lib.PxToPt; cs.Height ??= 13f * Lib.PxToPt;
                if (type == "radio") cs.BorderRadius = new[] { 6.5f, 6.5f, 6.5f, 6.5f };
                if (node.Attributes.ContainsKey("checked")) cs.BackgroundColor = cs.AccentColor ?? new Render.Color(51, 51, 51); // filled when checked (accent-color tints it)
                return;
            }
            string text = node.Attributes.TryGetValue("value", out var v) ? v
                        : node.Attributes.TryGetValue("placeholder", out var ph) ? ph : "";
            if (text.Length == 0 && (type == "submit" || type == "reset" || type == "button")) text = type == "submit" ? "Submit" : type == "reset" ? "Reset" : "";
            if (text.Length == 0) return;
            var tnode = new Dom.Node { Text = text, Parent = node };
            var tstyled = BuildNode(tnode, cs, computer, counters);
            if (tstyled != null) sn.Children.Add(tstyled);
        }

        /// <summary>Synthesize a &lt;progress&gt;/&lt;meter&gt; fill: an inner block sized to value/max, coloured
        /// (meter → green/yellow/red by low/high thresholds).</summary>
        private static void BuildBar(StyledNode sn, Dom.Node node, Style.ComputedStyle cs)
        {
            float A(string k, float d) => node.Attributes.TryGetValue(k, out var s) && float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : d;
            float max = A("max", 1f), val = A("value", 0f);
            if (max <= 0) max = 1f;
            float frac = Math.Max(0f, Math.Min(1f, val / max));
            float fullW = cs.Width ?? (160f * Lib.PxToPt), fullH = cs.Height ?? (14f * Lib.PxToPt);
            Render.Color fill = cs.AccentColor ?? new Render.Color(51, 133, 255);     // progress: blue (accent-color tints it)
            if (node.Tag == "meter")
            {
                float low = A("low", 0f), high = A("high", max), opt = A("optimum", max);
                fill = new Render.Color(120, 190, 60);                                // meter: green (in range)
                if (val < low || val > high) fill = new Render.Color(230, 190, 40);   // caution → yellow
            }
            var barStyle = new Style.ComputedStyle { Display = "block", Width = Math.Max(0f, frac * fullW), Height = fullH, BackgroundColor = fill };
            var barNode = new Dom.Node { Tag = "div", Parent = node };
            sn.Children.Add(new StyledNode(barNode, barStyle));
        }

        /// <summary>Synthesize a &lt;select&gt;'s rendered content: the selected (or first) &lt;option&gt;'s text.</summary>
        private static void BuildSelect(StyledNode sn, Dom.Node node, Style.ComputedStyle cs, Style.StyleComputer computer, Dictionary<string, int> counters)
        {
            Dom.Node? chosen = null, first = null;
            void Scan(Dom.Node p) { foreach (var c in p.Children) { if (c.IsText) continue; if (c.Tag == "option") { first ??= c; if (c.Attributes.ContainsKey("selected")) chosen ??= c; } else Scan(c); } }
            Scan(node);
            var opt = chosen ?? first;
            if (opt == null) return;
            var sb = new StringBuilder();
            void Gather(Dom.Node p) { foreach (var c in p.Children) { if (c.IsText) sb.Append(c.Text); else Gather(c); } }
            Gather(opt);
            var text = string.Join(" ", sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length == 0) return;
            var tnode = new Dom.Node { Text = text, Parent = node };
            var tstyled = BuildNode(tnode, cs, computer, counters);
            if (tstyled != null) sn.Children.Add(tstyled);
        }

        /// <summary>Build a ::before / ::after pseudo-element as a synthetic inline node with its resolved
        /// <c>content</c> as a text child (or null when there's no paintable content).</summary>
        private static StyledNode? BuildPseudo(Dom.Node el, Style.ComputedStyle elStyle, Style.StyleComputer computer, string which, Dictionary<string, int> counters)
        {
            var res = computer.ComputePseudo(el, elStyle, which);
            if (res == null) return null;
            var (pstyle, content) = res.Value;
            // counter-reset/set/increment declared ON the pseudo take effect before its content() is resolved.
            if (pstyle.CounterReset != null) foreach (var (nm, vl) in pstyle.CounterReset) counters[nm] = vl;
            if (pstyle.CounterSet != null) foreach (var (nm, vl) in pstyle.CounterSet) counters[nm] = vl;
            if (pstyle.CounterIncrement != null) foreach (var (nm, dl) in pstyle.CounterIncrement) counters[nm] = (counters.TryGetValue(nm, out var cur) ? cur : 0) + dl;
            // content: url(...) → the pseudo-element is a replaced <img>.
            var ct = content.Trim();
            if (ct.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            {
                var url = PseudoUrl(ct);
                if (url != null)
                {
                    var imgNode = new Dom.Node { Tag = "img", Parent = el };
                    imgNode.Attributes["src"] = url;
                    pstyle.Display = "inline-block";   // flow the icon inline with surrounding text
                    return new StyledNode(imgNode, pstyle);
                }
            }
            string text = ResolveContent(content, el, counters, pstyle.Quotes);
            var pnode = new Dom.Node { Tag = "span", Parent = el };
            var pStyled = new StyledNode(pnode, pstyle);
            if (text.Length > 0)
            {
                var tnode = new Dom.Node { Text = text, Parent = pnode };
                pnode.Children.Add(tnode);
                var tStyled = BuildNode(tnode, pstyle, computer, counters);
                if (tStyled != null) pStyled.Children.Add(tStyled);
            }
            return pStyled;
        }

        /// <summary>Pull the path out of a leading <c>url( ... )</c> (strips quotes), or null.</summary>
        private static string? PseudoUrl(string s)
        {
            int o = s.IndexOf('('); int c = o >= 0 ? s.IndexOf(')', o + 1) : -1;
            if (o < 0 || c < 0) return null;
            var u = s.Substring(o + 1, c - o - 1).Trim().Trim('"', '\'');
            return u.Length > 0 ? u : null;
        }

        /// <summary>Resolve a CSS <c>content</c> value: concatenation of quoted strings, <c>attr(x)</c>,
        /// <c>counter(name[,style])</c>, and <c>counters(name,sep[,style])</c>.</summary>
        private static string ResolveContent(string content, Dom.Node el, Dictionary<string, int> counters, string[]? quotes = null)
        {
            var sb = new StringBuilder();
            int i = 0, n = content.Length;
            while (i < n)
            {
                char c = content[i];
                if (c == '"' || c == '\'')
                {
                    int j = i + 1;
                    while (j < n && content[j] != c)
                    {
                        if (content[j] == '\\' && j + 1 < n)
                        {
                            // CSS escape: \XXXXXX (1-6 hex → codepoint, one trailing space consumed) or \<char>.
                            int h = j + 1, hexEnd = h;
                            while (hexEnd < n && hexEnd - h < 6 && Uri.IsHexDigit(content[hexEnd])) hexEnd++;
                            if (hexEnd > h)
                            {
                                int cp = Convert.ToInt32(content.Substring(h, hexEnd - h), 16);
                                sb.Append(char.ConvertFromUtf32(cp));
                                j = hexEnd;
                                if (j < n && (content[j] == ' ' || content[j] == '\t')) j++; // one whitespace after hex escape
                            }
                            else { sb.Append(content[j + 1]); j += 2; }
                        }
                        else { sb.Append(content[j]); j++; }
                    }
                    i = j + 1;
                }
                else if (char.IsLetter(c))
                {
                    // A bare identifier — either a quote keyword or a function name(...).
                    int idEnd = i; while (idEnd < n && (char.IsLetterOrDigit(content[idEnd]) || content[idEnd] == '-')) idEnd++;
                    string ident = content.Substring(i, idEnd - i).ToLowerInvariant();
                    if (ident == "open-quote" || ident == "close-quote" || ident == "no-open-quote" || ident == "no-close-quote")
                    {
                        // Use the `quotes` property's first pair when provided, else default curly quotes.
                        if (ident == "open-quote") sb.Append(quotes != null && quotes.Length >= 1 ? quotes[0] : "“");
                        else if (ident == "close-quote") sb.Append(quotes != null && quotes.Length >= 2 ? quotes[1] : "”");
                        i = idEnd; continue;
                    }
                    int p = content.IndexOf('(', i);
                    if (p < 0) break;
                    string fn = content.Substring(i, p - i).Trim().ToLowerInvariant();
                    int q = content.IndexOf(')', p);
                    if (q < 0) break;
                    var args = content.Substring(p + 1, q - p - 1);
                    i = q + 1;
                    if (fn == "attr")
                    {
                        el.Attributes.TryGetValue(args.Trim(), out var av);
                        sb.Append(av ?? "");
                    }
                    else if (fn == "counter" || fn == "counters")
                    {
                        var parts = args.Split(',');
                        string name = parts[0].Trim();
                        int val = counters.TryGetValue(name, out var v) ? v : 0;
                        string style = parts.Length >= (fn == "counters" ? 3 : 2) ? parts[parts.Length - 1].Trim() : "decimal";
                        sb.Append(FormatCounter(val, style));
                    }
                }
                else i++;
            }
            return sb.ToString();
        }

        private static string FormatCounter(int nv, string style)
        {
            switch (style)
            {
                case "lower-alpha": case "lower-latin": return Alpha(nv, 'a');
                case "upper-alpha": case "upper-latin": return Alpha(nv, 'A');
                case "lower-roman": return Roman(nv).ToLowerInvariant();
                case "upper-roman": return Roman(nv);
                case "lower-greek": { if (nv <= 0) return nv.ToString(System.Globalization.CultureInfo.InvariantCulture); var g = new StringBuilder(); int m = nv; while (m > 0) { m--; g.Insert(0, (char)(0x03B1 + m % 24)); m /= 24; } return g.ToString(); }
                case "armenian": case "upper-armenian": return Layout.LayoutBox.ArmenianCounter(nv, true);
                case "lower-armenian": return Layout.LayoutBox.ArmenianCounter(nv, false);
                case "georgian": return Layout.LayoutBox.GeorgianCounter(nv);
                case "decimal-leading-zero": return nv >= 0 && nv < 10 ? "0" + nv : nv.ToString(System.Globalization.CultureInfo.InvariantCulture);
                default: return nv.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private static string Alpha(int nv, char b)
        {
            if (nv <= 0) return nv.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var s = new StringBuilder();
            while (nv > 0) { nv--; s.Insert(0, (char)(b + nv % 26)); nv /= 26; }
            return s.ToString();
        }

        private static string Roman(int nv)
        {
            if (nv <= 0 || nv >= 4000) return nv.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int[] vals = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            string[] sym = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
            var s = new StringBuilder();
            for (int k = 0; k < vals.Length; k++) while (nv >= vals[k]) { s.Append(sym[k]); nv -= vals[k]; }
            return s.ToString();
        }
    }
}
