using System.Collections.Generic;
using HtmlPdfNative.Dom;

namespace HtmlPdfNative.MathTex
{
    /// <summary>
    /// Scans the DOM's text nodes for LaTeX math delimiters (<c>$…$</c>, <c>$$…$$</c>, <c>\(…\)</c>, <c>\[…\]</c>)
    /// and replaces each match with an <c>&lt;img data-latex&gt;</c> placeholder. The actual bitmap is rendered
    /// lazily during layout (in <c>LayoutImage</c>), where the surrounding computed font-size and colour are known —
    /// so inline math matches the running text and display math its block. Mirrors the hook the Rust engine performs
    /// in <c>dom::from_string</c>. No-op when the math backend (SkiaSharp) is unavailable: the raw source is left intact.
    /// </summary>
    public static class MathPreprocessor
    {
        public static void Process(Node root)
        {
            if (!MathRenderer.Available) return;
            Walk(root);
        }

        private static void Walk(Node n)
        {
            bool any = false;
            var rebuilt = new List<Node>(n.Children.Count);
            foreach (var ch in n.Children)
            {
                if (ch.IsText)
                {
                    var segs = Split(ch.Text ?? "");
                    if (segs == null) { rebuilt.Add(ch); continue; }
                    any = true;
                    foreach (var seg in segs)
                    {
                        if (!seg.IsMath) { rebuilt.Add(new Node { Text = seg.Text, Parent = n }); continue; }
                        if (seg.Display)
                        {
                            var wrap = new Node { Tag = "div", Parent = n };
                            // break-inside:avoid — a display equation is an unbreakable atomic image; if it would straddle
                            // the page bottom it must shift WHOLE to the next page (else it renders truncated off the bottom).
                            wrap.Attributes["style"] = "text-align:center;margin:0.7em 0;break-inside:avoid;";
                            var img = new Node { Tag = "img", Parent = wrap };
                            img.Attributes["data-latex"] = seg.Text;
                            img.Attributes["data-display"] = "1";
                            wrap.Children.Add(img);
                            rebuilt.Add(wrap);
                        }
                        else
                        {
                            var img = new Node { Tag = "img", Parent = n };
                            img.Attributes["data-latex"] = seg.Text;
                            rebuilt.Add(img);
                        }
                    }
                }
                else { Walk(ch); rebuilt.Add(ch); }
            }
            if (any) { n.Children.Clear(); n.Children.AddRange(rebuilt); }
        }

        private static readonly char[] SignalChars = { '\\', '^', '_' };

        private struct Seg { public bool IsMath; public bool Display; public string Text; }

        /// <summary>Split a raw text into text/math segments, or null when it contains no math.</summary>
        private static List<Seg>? Split(string s)
        {
            if (string.IsNullOrEmpty(s) || (s.IndexOf('$') < 0 && s.IndexOf('\\') < 0)) return null;
            List<Seg>? outp = null;
            int i = 0, textStart = 0, n = s.Length;
            while (i < n)
            {
                string? open = null, close = null; bool display = false;
                if (s[i] == '$')
                {
                    if (i + 1 < n && s[i + 1] == '$') { open = "$$"; close = "$$"; display = true; }
                    else { open = "$"; close = "$"; display = false; }
                }
                else if (s[i] == '\\' && i + 1 < n)
                {
                    if (s[i + 1] == '[') { open = "\\["; close = "\\]"; display = true; }
                    else if (s[i + 1] == '(') { open = "\\("; close = "\\)"; display = false; }
                }
                if (open == null) { i++; continue; }

                int contentStart = i + open.Length;
                int end = s.IndexOf(close, contentStart, System.StringComparison.Ordinal);
                if (end < 0) { i++; continue; }          // no closer: treat the delimiter as literal text
                string content = s.Substring(contentStart, end - contentStart);
                if (content.Trim().Length == 0) { i = end + close.Length; continue; }
                // Single-$ is ambiguous with currency ("$5 … $10"): only treat it as math when the span carries a
                // LaTeX signal (a command, super/sub-script). $$…$$, \[…\], \(…\) are unambiguous and always math.
                if (open == "$" && content.IndexOfAny(SignalChars) < 0) { i++; continue; }

                outp ??= new List<Seg>();
                if (i > textStart) outp.Add(new Seg { IsMath = false, Text = s.Substring(textStart, i - textStart) });
                outp.Add(new Seg { IsMath = true, Display = display, Text = content });
                i = end + close.Length;
                textStart = i;
            }
            if (outp == null) return null;
            if (textStart < n) outp.Add(new Seg { IsMath = false, Text = s.Substring(textStart) });
            return outp;
        }
    }
}
