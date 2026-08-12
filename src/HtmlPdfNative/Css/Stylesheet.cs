using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace HtmlPdfNative.Css
{
    /// <summary>One <c>property: value</c> declaration, with the <c>!important</c> flag.</summary>
    public sealed class Declaration
    {
        public string Property = "";
        public string Value = "";
        public bool Important;
    }

    /// <summary>A simple compound selector: optional tag + classes + id (no combinators in this slice).</summary>
    /// <summary>A structural pseudo-class: Kind + the an+b coefficients (for nth-*).</summary>
    public struct PseudoClass { public string Kind; public int A, B; }

    /// <summary>An attribute selector: name, operator ("" exists, =, ^=, $=, *=, ~=, |=), and value.</summary>
    public struct AttrSel { public string Name; public string Op; public string Value; }

    public sealed class SimpleSelector
    {
        public string? Tag;                 // null = universal
        public List<string> Classes = new List<string>();
        public string? Id;
        public List<PseudoClass> Pseudos = new List<PseudoClass>(); // :first-child, :nth-child(...), …
        public List<AttrSel> Attrs = new List<AttrSel>();           // [attr], [attr^=val], …
        public List<SimpleSelector> Not = new List<SimpleSelector>(); // :not(simple) negations
        public List<List<SimpleSelector>> MatchAny = new List<List<SimpleSelector>>(); // :is()/:where() groups (element must match ≥1 per group)
        public List<int> MatchAnySpec = new List<int>();               // per-group specificity contribution (:where = 0)
        public List<SimpleSelector> Has = new List<SimpleSelector>();   // :has() relational args (a matching descendant must exist)

        /// <summary>Specificity: id*10000 + (class+attr+pseudo-class)*100 + tag (+ each :not's argument).</summary>
        public int Specificity
        {
            get
            {
                int s = (Id != null ? 1 : 0) * 10000 + (Classes.Count + Pseudos.Count + Attrs.Count) * 100 + (Tag != null && Tag != "*" ? 1 : 0);
                foreach (var nt in Not) s += nt.Specificity;
                foreach (var sp in MatchAnySpec) s += sp;
                foreach (var h in Has) s += h.Specificity;
                return s;
            }
        }

        public bool Matches(Dom.Node n)
        {
            if (n.IsText) return false;
            if (Tag != null && Tag != "*" && !string.Equals(Tag, n.Tag, StringComparison.OrdinalIgnoreCase)) return false;
            if (Id != null && !string.Equals(Id, n.Id, StringComparison.Ordinal)) return false;
            if (Classes.Count > 0)
            {
                var cls = (n.Class ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                foreach (var need in Classes)
                    if (Array.IndexOf(cls, need) < 0) return false;
            }
            foreach (var at in Attrs) if (!MatchesAttr(at, n)) return false;
            foreach (var pc in Pseudos) if (!MatchesPseudo(pc, n)) return false;
            foreach (var nt in Not) if (nt.Matches(n)) return false;   // :not(x) — fails if x matches
            foreach (var group in MatchAny) { bool any = false; foreach (var s in group) if (s.Matches(n)) { any = true; break; } if (!any) return false; } // :is()/:where()
            foreach (var h in Has) if (!HasDescendant(n, h)) return false;   // :has() — a descendant must match
            return true;
        }

        private static bool HasDescendant(Dom.Node n, SimpleSelector arg)
        {
            foreach (var c in n.Children)
            {
                if (c.IsText) continue;
                if (arg.Matches(c) || HasDescendant(c, arg)) return true;
            }
            return false;
        }

        private static bool MatchesAttr(AttrSel a, Dom.Node n)
        {
            if (!n.Attributes.TryGetValue(a.Name, out var v)) return false;
            switch (a.Op)
            {
                case "": return true;                                  // [attr] exists
                case "=": return v == a.Value;
                case "^=": return a.Value.Length > 0 && v.StartsWith(a.Value, StringComparison.Ordinal);
                case "$=": return a.Value.Length > 0 && v.EndsWith(a.Value, StringComparison.Ordinal);
                case "*=": return a.Value.Length > 0 && v.IndexOf(a.Value, StringComparison.Ordinal) >= 0;
                case "~=": return Array.IndexOf(v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), a.Value) >= 0;
                case "|=": return v == a.Value || v.StartsWith(a.Value + "-", StringComparison.Ordinal);
                default: return false;
            }
        }

        private static bool MatchesPseudo(PseudoClass pc, Dom.Node n)
        {
            if (pc.Kind == "root") return n.Parent == null || n.Parent.Tag == null;
            if (pc.Kind == "link") return n.Tag == "a" && n.Attributes.ContainsKey("href");
            if (pc.Kind == "empty") { foreach (var c in n.Children) if (!c.IsText || !string.IsNullOrWhiteSpace(c.Text)) return false; return true; }

            // Sibling position among element siblings (optionally of the same type).
            var parent = n.Parent;
            bool ofType = pc.Kind.EndsWith("of-type", StringComparison.Ordinal);
            var sibs = new List<Dom.Node>();
            if (parent != null) foreach (var c in parent.Children) { if (c.IsText) continue; if (!ofType || string.Equals(c.Tag, n.Tag, StringComparison.OrdinalIgnoreCase)) sibs.Add(c); }
            else sibs.Add(n);
            int idx = sibs.IndexOf(n); if (idx < 0) return false;
            int i1 = idx + 1, count = sibs.Count;      // 1-based from start
            switch (pc.Kind)
            {
                case "first-child": case "first-of-type": return i1 == 1;
                case "last-child": case "last-of-type": return i1 == count;
                case "only-child": case "only-of-type": return count == 1;
                case "nth-child": case "nth-of-type": return NthMatch(pc.A, pc.B, i1);
                case "nth-last-child": case "nth-last-of-type": return NthMatch(pc.A, pc.B, count - i1 + 1);
                default: return false; // :hover/:focus/etc. never match in print
            }
        }

        // an+b: index matches if (index-b) is a non-negative multiple of a (a==0 → index==b).
        private static bool NthMatch(int a, int b, int index)
        {
            if (a == 0) return index == b;
            int diff = index - b;
            return diff % a == 0 && diff / a >= 0;
        }
    }

    /// <summary>A selector with descendant combinators: a chain of compounds, rightmost = subject.</summary>
    public sealed class ComplexSelector
    {
        public List<SimpleSelector> Compounds = new List<SimpleSelector>(); // ancestor .. subject
        public List<char> Combinators = new List<char>();  // Combinators[i] joins Compounds[i-1]→[i]: ' ' descendant, '>' child
        public string? Pseudo;   // "before" | "after" (pseudo-element on the subject), else null

        public int Specificity
        {
            get { int s = 0; foreach (var c in Compounds) s += c.Specificity; return s; }
        }

        public bool Matches(Dom.Node node)
        {
            if (Compounds.Count == 0) return false;
            if (!Compounds[Compounds.Count - 1].Matches(node)) return false;
            // Walk leftwards through combinators (greedy). '>' immediate parent, ' ' any ancestor,
            // '+' immediate preceding element sibling, '~' any preceding element sibling.
            var cur = node;
            for (int ci = Compounds.Count - 2; ci >= 0; ci--)
            {
                char comb = ci + 1 < Combinators.Count ? Combinators[ci + 1] : ' ';
                if (comb == '>')
                {
                    var par = cur.Parent;
                    if (par == null || !Compounds[ci].Matches(par)) return false;
                    cur = par;
                }
                else if (comb == '+')
                {
                    var prev = PrevElement(cur);
                    if (prev == null || !Compounds[ci].Matches(prev)) return false;
                    cur = prev;
                }
                else if (comb == '~')
                {
                    bool found = false;
                    for (var pr = PrevElement(cur); pr != null; pr = PrevElement(pr))
                        if (Compounds[ci].Matches(pr)) { cur = pr; found = true; break; }
                    if (!found) return false;
                }
                else // descendant
                {
                    bool found = false;
                    for (var an = cur.Parent; an != null; an = an.Parent)
                        if (Compounds[ci].Matches(an)) { cur = an; found = true; break; }
                    if (!found) return false;
                }
            }
            return true;
        }

        private static Dom.Node? PrevElement(Dom.Node n)
        {
            if (n.Parent == null) return null;
            var sibs = n.Parent.Children;
            int idx = sibs.IndexOf(n);
            for (int i = idx - 1; i >= 0; i--) if (!sibs[i].IsText) return sibs[i];
            return null;
        }
    }

    public sealed class Rule
    {
        public List<ComplexSelector> Selectors = new List<ComplexSelector>();
        public List<Declaration> Declarations = new List<Declaration>();
    }

    /// <summary>
    /// Parsed stylesheet. Minimal subset of <c>src/css.rs</c>: rules with simple selectors, declarations,
    /// and <c>@page</c> extraction. Combinators, <c>@media</c>, <c>@font-face</c>, and the full value
    /// model land in later phases.
    /// </summary>
    public sealed class Stylesheet
    {
        public List<Rule> Rules { get; } = new List<Rule>();

        private static readonly Regex CommentRx = new Regex(@"/\*.*?\*/", RegexOptions.Singleline);

        public static (Stylesheet stylesheet, PageConfig page) Parse(string? externalCss, Dom.Document document, float mediaWidthPx = 794f)
        {
            var sheet = new Stylesheet();
            var page = new PageConfig();
            Fonts.FontManager.ClearFontFaces(); // @font-face registry is per-conversion

            // Author order: document <style> blocks, then the external -c CSS (later wins at equal specificity).
            ParseInto(sheet, page, document.StyleText, mediaWidthPx);
            ParseInto(sheet, page, externalCss, mediaWidthPx);
            return (sheet, page);
        }

        private static void ParseInto(Stylesheet sheet, PageConfig page, string? css, float mediaWidthPx)
        {
            if (string.IsNullOrWhiteSpace(css)) return;
            css = CommentRx.Replace(css!, "");

            int i = 0, n = css.Length;
            while (i < n)
            {
                int brace = css.IndexOf('{', i);
                if (brace < 0) break;
                int close = FindMatchingBrace(css, brace);
                if (close < 0) break;

                string prelude = css.Substring(i, brace - i).Trim();
                string body = css.Substring(brace + 1, close - brace - 1);

                if (prelude.StartsWith("@", StringComparison.Ordinal))
                {
                    if (prelude.StartsWith("@page", StringComparison.OrdinalIgnoreCase))
                        ApplyPage(page, body);
                    else if (prelude.StartsWith("@media", StringComparison.OrdinalIgnoreCase))
                    {
                        // Evaluate the query for the PRINT medium; if it matches, parse the nested rules.
                        string query = prelude.Substring("@media".Length).Trim();
                        if (MediaMatches(query, mediaWidthPx)) ParseInto(sheet, page, body, mediaWidthPx);
                    }
                    else if (prelude.StartsWith("@font-face", StringComparison.OrdinalIgnoreCase))
                        ApplyFontFace(body);
                    else if (prelude.StartsWith("@supports", StringComparison.OrdinalIgnoreCase))
                    {
                        // Optimistic: we support most modern CSS, so parse the nested rules unless it's a negation.
                        string cond = prelude.Substring("@supports".Length);
                        if (!System.Text.RegularExpressions.Regex.IsMatch(cond, @"\bnot\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            ParseInto(sheet, page, body, mediaWidthPx);
                    }
                    else if (prelude.StartsWith("@layer", StringComparison.OrdinalIgnoreCase))
                        ParseInto(sheet, page, body, mediaWidthPx);   // cascade layers: parse rules (priority ≈ source order)
                    // other @-rules: skipped.
                }
                else if (prelude.Length > 0)
                {
                    // Native CSS nesting: a rule body may contain nested rules (`&:hover{…}`, `.child{…}`).
                    bool hasNesting = body.IndexOf('{') >= 0;
                    string declText = body; var nested = new List<(string sel, string body)>();
                    if (hasNesting) { declText = SplitBodyNesting(body, nested); }

                    var parents = new List<string>();
                    var rule = new Rule();
                    foreach (var selText in prelude.Split(','))
                    {
                        var t = selText.Trim(); if (t.Length == 0) continue; parents.Add(t);
                        var sel = ParseSelector(t);
                        if (sel != null) rule.Selectors.Add(sel);
                    }
                    rule.Declarations = ParseDeclarations(declText);
                    if (rule.Selectors.Count > 0 && rule.Declarations.Count > 0)
                        sheet.Rules.Add(rule);

                    // Emit each nested rule with its selector combined against every parent, recursing for depth.
                    foreach (var (nsel, nbody) in nested)
                    {
                        var combined = new List<string>();
                        foreach (var p in parents) foreach (var ns in SplitTopLevel(nsel, ',')) combined.Add(CombineNesting(p, ns.Trim()));
                        ParseInto(sheet, page, string.Join(", ", combined) + " {" + nbody + "}", mediaWidthPx);
                    }
                }
                i = close + 1;
            }
        }

        /// <summary>Evaluate a media query list for the PRINT medium at the given page width (px).
        /// Comma = OR; <c>and</c> = all terms; supports print/screen/all types + min/max-width/height.</summary>
        private static bool MediaMatches(string query, float widthPx)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            foreach (var part in query.Split(','))
                if (EvalMediaQuery(part.Trim().ToLowerInvariant(), widthPx)) return true;
            return false;
        }

        private static bool EvalMediaQuery(string q, float widthPx)
        {
            bool negate = false;
            if (q.StartsWith("not ", StringComparison.Ordinal)) { negate = true; q = q.Substring(4).Trim(); }
            else if (q.StartsWith("only ", StringComparison.Ordinal)) q = q.Substring(5).Trim();

            bool ok = true;
            foreach (var raw in q.Split(new[] { " and " }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = raw.Trim().Trim('(', ')').Trim();
                if (t.Length == 0 || t == "print" || t == "all") continue; // print medium: these match
                if (t == "screen" || t == "speech" || t == "tv" || t == "handheld" || t == "projection") { ok = false; continue; } // non-print type
                int colon = t.IndexOf(':');
                if (colon < 0) { ok = false; continue; } // an unknown bare type → not print
                string feat = t.Substring(0, colon).Trim();
                _ = FeatureValuePx(t.Substring(colon + 1).Trim());
                // Desktop-layout policy (user-chosen; matches Rust media_query_applies): render the
                // author's DESKTOP design with an effectively UNBOUNDED viewport, NOT Chrome's narrow
                // print page. So `max-width` (mobile/tablet) breakpoints never apply (keeps multi-column
                // grids), while `min-width` (desktop) breakpoints are treated as satisfied.
                if (feat == "max-width" || feat == "max-device-width") ok = false;
                // min-width/min-device-width and other features: satisfied by the unbounded viewport.
                // min/max-height/orientation/resolution: not modeled → ignored (lenient match)
            }
            return negate ? !ok : ok;
        }

        private static float FeatureValuePx(string v)
        {
            v = v.Trim();
            float num(string s) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;
            if (v.EndsWith("px", StringComparison.Ordinal)) return num(v.Substring(0, v.Length - 2));
            if (v.EndsWith("rem", StringComparison.Ordinal)) return num(v.Substring(0, v.Length - 3)) * 16f;
            if (v.EndsWith("em", StringComparison.Ordinal)) return num(v.Substring(0, v.Length - 2)) * 16f;
            if (v.EndsWith("cm", StringComparison.Ordinal)) return num(v.Substring(0, v.Length - 2)) * 37.795f;
            if (v.EndsWith("mm", StringComparison.Ordinal)) return num(v.Substring(0, v.Length - 2)) * 3.7795f;
            if (v.EndsWith("in", StringComparison.Ordinal)) return num(v.Substring(0, v.Length - 2)) * 96f;
            return num(v);
        }

        /// <summary>Register an @font-face: pick a raw TTF/OTF src url (skipping woff/woff2) and load it.</summary>
        private static void ApplyFontFace(string body)
        {
            string? family = null, srcVal = null;
            int weight = 400; bool italic = false;
            foreach (var d in ParseDeclarations(body))
            {
                switch (d.Property)
                {
                    case "font-family": family = d.Value.Trim().Trim('"', '\''); break;
                    case "src": srcVal = d.Value; break;
                    case "font-weight":
                        // A @font-face may declare a single weight or a range ("400 800"); key off the first number.
                        var wv = d.Value.Trim();
                        if (wv == "bold") weight = 700;
                        else if (wv == "normal") weight = 400;
                        else { var wm = Regex.Match(wv, @"\d+"); if (wm.Success && int.TryParse(wm.Value, out var wn)) weight = wn; }
                        break;
                    case "font-style": var fs = d.Value.Trim(); italic = fs == "italic" || fs == "oblique"; break;
                }
            }
            if (family == null || srcVal == null) return;

            // Pick a usable src url: prefer a raw sfnt (ttf/otf), then WOFF1 (decodable), skipping only WOFF2 (Brotli).
            string? chosen = null;
            foreach (var part in Gradients.SplitTopLevel(srcVal, ','))
            {
                var um = Regex.Match(part, @"url\(\s*([^)]*?)\s*\)", RegexOptions.IgnoreCase);
                if (!um.Success) continue;
                string url = um.Groups[1].Value.Trim().Trim('"', '\'');
                var fm = Regex.Match(part, @"format\(\s*[""']?([a-z0-9-]+)", RegexOptions.IgnoreCase);
                string fmt = fm.Success ? fm.Groups[1].Value.ToLowerInvariant() : "";
                string ul = url.ToLowerInvariant();
                bool woff2 = fmt == "woff2" || ul.EndsWith(".woff2");
                bool woff1 = !woff2 && (fmt == "woff" || ul.EndsWith(".woff"));
                bool sfnt = fmt == "truetype" || fmt == "opentype" || ul.EndsWith(".ttf") || ul.EndsWith(".otf") ||
                            ul.StartsWith("data:font") || ul.StartsWith("data:application/font") || ul.Contains("font/ttf") || ul.Contains("font/otf");
                if (!sfnt && !woff1 && !woff2) continue;        // unknown format
                chosen = url; if (sfnt) break;                 // prefer sfnt; else woff1/woff2 (decoded in FontManager)
            }
            if (chosen != null) Fonts.FontManager.RegisterFontFace(family, chosen, weight, italic);
        }

        private static int FindMatchingBrace(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static ComplexSelector? ParseSelector(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            // Pull off a ::before / ::after pseudo-element (also legacy single-colon), remembered on the selector.
            string? pseudo = null;
            foreach (var pe in new[] { "::before", "::after", "::first-letter", "::first-line", "::marker", ":before", ":after", ":first-letter", ":first-line" })
            {
                int pi = text.IndexOf(pe, StringComparison.OrdinalIgnoreCase);
                if (pi >= 0) { pseudo = pe.TrimStart(':').ToLowerInvariant(); text = text.Remove(pi, pe.Length); break; }
            }
            if (text.Trim().Length == 0 && pseudo != null) text = "*"; // bare ::before → universal subject

            // Tokenize into (combinator, compound) pairs, honouring >,+,~ but NOT those inside [ ] or ( ).
            var complex = new ComplexSelector();
            var sb = new StringBuilder();
            char pendingComb = ' ';
            int depth = 0;
            void Flush()
            {
                if (sb.Length == 0) return;
                var sel = new SimpleSelector();
                string compound = ExtractAttrs(ExtractPseudoClasses(sb.ToString(), sel), sel);
                foreach (Match tok in Regex.Matches(compound, @"([.#]?)([A-Za-z0-9_\-\*]+)"))
                {
                    string kind = tok.Groups[1].Value, name = tok.Groups[2].Value;
                    if (kind == ".") sel.Classes.Add(name);
                    else if (kind == "#") sel.Id = name;
                    else sel.Tag = name.ToLowerInvariant();
                }
                complex.Compounds.Add(sel);
                complex.Combinators.Add(pendingComb);
                sb.Clear(); pendingComb = ' ';
            }
            foreach (char c in text)
            {
                if (c == '[' || c == '(') depth++;
                else if (c == ']' || c == ')') depth--;
                if (depth == 0 && (c == '>' || c == '+' || c == '~')) { Flush(); pendingComb = c; continue; }
                if (depth == 0 && char.IsWhiteSpace(c)) { Flush(); continue; }
                sb.Append(c);
            }
            Flush();

            complex.Pseudo = pseudo;
            return complex.Compounds.Count > 0 ? complex : null;
        }

        /// <summary>Peel attribute selectors <c>[name]</c> / <c>[name op "val"]</c> off a compound.</summary>
        private static string ExtractAttrs(string compound, SimpleSelector sel)
        {
            return Regex.Replace(compound, @"\[([^\]]*)\]", mm =>
            {
                string body = mm.Groups[1].Value.Trim();
                var om = Regex.Match(body, @"^([A-Za-z0-9_\-]+)\s*([~^$*|]?=)?\s*(.*)$");
                if (!om.Success) return "";
                string name = om.Groups[1].Value.ToLowerInvariant();
                string op = om.Groups[2].Value;
                string val = om.Groups[3].Value.Trim().Trim('"', '\'');
                sel.Attrs.Add(new AttrSel { Name = name, Op = op, Value = val });
                return "";
            });
        }

        /// <summary>Peel structural pseudo-classes (:first-child, :nth-child(...), :root, …) off a compound
        /// into <paramref name="sel"/>, returning the remaining tag/class/id text.</summary>
        /// <summary>Pull out :is()/:where()/:has() (balanced parens, so nested pseudos survive) and populate the
        /// selector's MatchAny/Has lists; returns the compound with those functions removed.</summary>
        private static string ExtractRelational(string compound, SimpleSelector sel)
        {
            foreach (var fn in new[] { ":is(", ":where(", ":has(" })
            {
                int idx;
                while ((idx = compound.IndexOf(fn, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    int open = idx + fn.Length - 1, depth = 0, j = open;
                    for (; j < compound.Length; j++) { if (compound[j] == '(') depth++; else if (compound[j] == ')') { depth--; if (depth == 0) break; } }
                    if (j >= compound.Length) break;
                    string args = compound.Substring(open + 1, j - open - 1);
                    if (fn == ":has(")
                    {
                        var a = args.Trim().TrimStart('>', '+', '~', ' ');   // combinator ignored → any descendant
                        if (a.Length > 0) sel.Has.Add(ParseCompound(a));
                    }
                    else
                    {
                        var group = new List<SimpleSelector>(); int maxSpec = 0;
                        foreach (var part in SplitTopLevel(args, ',')) { var t = part.Trim(); if (t.Length == 0) continue; var s = ParseCompound(t); group.Add(s); maxSpec = Math.Max(maxSpec, s.Specificity); }
                        if (group.Count > 0) { sel.MatchAny.Add(group); sel.MatchAnySpec.Add(fn == ":where(" ? 0 : maxSpec); }
                    }
                    compound = compound.Remove(idx, j - idx + 1);
                }
            }
            return compound;
        }

        /// <summary>Split a rule body into its plain declarations (returned) + nested rules (appended to
        /// <paramref name="nested"/> as (selector, body) pairs) for native CSS nesting.</summary>
        private static string SplitBodyNesting(string body, List<(string sel, string body)> nested)
        {
            var decls = new StringBuilder();
            int i = 0, n = body.Length;
            while (i < n)
            {
                // Read up to the next ';' or '{' at depth 0.
                int start = i; int brace = -1;
                while (i < n && body[i] != ';' && body[i] != '{') i++;
                if (i < n && body[i] == '{') brace = i;
                if (brace >= 0)
                {
                    string sel = body.Substring(start, brace - start).Trim();
                    int depth = 0, j = brace;
                    for (; j < n; j++) { if (body[j] == '{') depth++; else if (body[j] == '}') { depth--; if (depth == 0) break; } }
                    string inner = j < n ? body.Substring(brace + 1, j - brace - 1) : body.Substring(brace + 1);
                    if (sel.Length > 0) nested.Add((sel, inner));
                    i = j + 1;
                }
                else { decls.Append(body.Substring(start, i - start)); if (i < n) { decls.Append(';'); i++; } }
            }
            return decls.ToString();
        }

        /// <summary>Combine a parent selector with a nested one: `&` → parent (per-occurrence); no `&` → descendant.</summary>
        private static string CombineNesting(string parent, string nested)
        {
            if (nested.IndexOf('&') >= 0) return nested.Replace("&", parent);
            return parent + " " + nested;
        }

        /// <summary>Split on a delimiter at the top level (ignoring content inside () and []).</summary>
        private static List<string> SplitTopLevel(string s, char delim)
        {
            var outl = new List<string>(); var sb = new StringBuilder(); int depth = 0;
            foreach (char c in s)
            {
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                if (c == delim && depth == 0) { outl.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            outl.Add(sb.ToString());
            return outl;
        }

        private static string ExtractPseudoClasses(string compound, SimpleSelector sel)
        {
            compound = ExtractRelational(compound, sel);
            return Regex.Replace(compound, @":([a-zA-Z\-]+)(?:\(([^)]*)\))?", mm =>
            {
                string name = mm.Groups[1].Value.ToLowerInvariant();
                string args = mm.Groups[2].Value;
                switch (name)
                {
                    case "first-child": case "last-child": case "only-child":
                    case "first-of-type": case "last-of-type": case "only-of-type":
                    case "root": case "link": case "empty":
                        sel.Pseudos.Add(new PseudoClass { Kind = name }); break;
                    case "nth-child": case "nth-last-child": case "nth-of-type": case "nth-last-of-type":
                    { var (a, b) = ParseNth(args); sel.Pseudos.Add(new PseudoClass { Kind = name, A = a, B = b }); break; }
                    case "not":
                        if (!string.IsNullOrWhiteSpace(args)) sel.Not.Add(ParseCompound(args)); break;
                    case "hover": case "focus": case "active": case "visited": case "checked":
                    case "disabled": case "enabled": case "target": case "focus-within": case "focus-visible":
                        sel.Pseudos.Add(new PseudoClass { Kind = "__never" }); break; // interactive states never match in print
                    default: break; // unknown (:lang, …): ignore the condition (lenient)
                }
                return "";
            });
        }

        /// <summary>Parse a single compound (tag/class/id + attrs + pseudo-classes) into a SimpleSelector.</summary>
        private static SimpleSelector ParseCompound(string compound)
        {
            var sel = new SimpleSelector();
            string rest = ExtractAttrs(ExtractPseudoClasses(compound, sel), sel);
            foreach (Match tok in Regex.Matches(rest, @"([.#]?)([A-Za-z0-9_\-\*]+)"))
            {
                string kind = tok.Groups[1].Value, name = tok.Groups[2].Value;
                if (kind == ".") sel.Classes.Add(name);
                else if (kind == "#") sel.Id = name;
                else sel.Tag = name.ToLowerInvariant();
            }
            return sel;
        }

        /// <summary>Parse an <c>an+b</c> nth expression (also <c>odd</c>/<c>even</c>/<c>N</c>).</summary>
        private static (int a, int b) ParseNth(string expr)
        {
            expr = expr.Replace(" ", "").ToLowerInvariant();
            if (expr == "odd") return (2, 1);
            if (expr == "even") return (2, 0);
            int nPos = expr.IndexOf('n');
            if (nPos < 0) { int.TryParse(expr, out var bb); return (0, bb); }
            string aPart = expr.Substring(0, nPos), bPart = expr.Substring(nPos + 1);
            int a = aPart == "" || aPart == "+" ? 1 : aPart == "-" ? -1 : (int.TryParse(aPart, out var av) ? av : 1);
            int b = bPart == "" ? 0 : (int.TryParse(bPart, out var bv) ? bv : 0);
            return (a, b);
        }

        // Split a declaration block on ';' at the TOP level only — semicolons inside url(...)/calc(...) or quotes
        // (e.g. a data: URI's "…;base64,…") are NOT separators.
        private static IEnumerable<string> SplitDeclList(string body)
        {
            int depth = 0, start = 0; char quote = '\0';
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c == '"' || c == '\'') quote = c;
                else if (c == '(') depth++;
                else if (c == ')') { if (depth > 0) depth--; }
                else if (c == ';' && depth == 0) { yield return body.Substring(start, i - start); start = i + 1; }
            }
            if (start < body.Length) yield return body.Substring(start);
        }

        public static List<Declaration> ParseDeclarations(string body)
        {
            var list = new List<Declaration>();
            foreach (var part in SplitDeclList(body))
            {
                int colon = part.IndexOf(':');
                if (colon <= 0) continue;
                string propRaw = part.Substring(0, colon).Trim();
                string prop = propRaw.StartsWith("--", StringComparison.Ordinal) ? propRaw : propRaw.ToLowerInvariant(); // custom props are case-sensitive
                string val = part.Substring(colon + 1).Trim();
                if (prop.Length == 0 || val.Length == 0) continue;

                bool important = false;
                int bang = val.LastIndexOf('!');
                if (bang >= 0 && val.Substring(bang + 1).Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
                {
                    important = true;
                    val = val.Substring(0, bang).Trim();
                }
                list.Add(new Declaration { Property = prop, Value = val, Important = important });
            }
            return list;
        }

        private static void ApplyPage(PageConfig page, string body)
        {
            foreach (var d in ParseDeclarations(body))
            {
                switch (d.Property)
                {
                    case "margin":
                        var m = Values.LengthPt(d.Value, 12f, 0f) ?? 0f;
                        page.MarginTop = page.MarginRight = page.MarginBottom = page.MarginLeft = m;
                        page.MarginExplicit = true;
                        break;
                    case "margin-top": page.MarginTop = Values.LengthPt(d.Value) ?? 0f; page.MarginExplicit = true; break;
                    case "margin-right": page.MarginRight = Values.LengthPt(d.Value) ?? 0f; page.MarginExplicit = true; break;
                    case "margin-bottom": page.MarginBottom = Values.LengthPt(d.Value) ?? 0f; page.MarginExplicit = true; break;
                    case "margin-left": page.MarginLeft = Values.LengthPt(d.Value) ?? 0f; page.MarginExplicit = true; break;
                    case "size": page.SizeExplicit = true; break;
                }
            }
        }
    }
}
