using System;
using System.Collections.Generic;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace HtmlPdfNative.Dom
{
    /// <summary>A parsed HTML node (element or text) in the engine's lightweight tree.</summary>
    public sealed class Node
    {
        /// <summary>Lower-case tag name, or null for a text node.</summary>
        public string? Tag { get; set; }
        /// <summary>Text content for a text node.</summary>
        public string? Text { get; set; }
        public Node? Parent { get; set; }
        public List<Node> Children { get; } = new List<Node>();
        public Dictionary<string, string> Attributes { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool IsText => Tag == null;
        public string? Id => Attributes.TryGetValue("id", out var v) ? v : null;
        public string? Class => Attributes.TryGetValue("class", out var v) ? v : null;
        public string? InlineStyle => Attributes.TryGetValue("style", out var v) ? v : null;
    }

    /// <summary>
    /// Parsed HTML document. Port of <c>src/dom.rs</c> (Rust used <c>scraper</c>; .NET uses AngleSharp).
    /// Converts the AngleSharp DOM into the engine's <see cref="Node"/> tree and collects the CSS text
    /// from all <c>&lt;style&gt;</c> elements.
    /// </summary>
    public sealed class Document
    {
        /// <summary>The <c>&lt;body&gt;</c> node (root of the render tree).</summary>
        public Node Root { get; }

        /// <summary>Concatenated text of every <c>&lt;style&gt;</c> element in document order.</summary>
        public string StyleText { get; }

        private Document(Node root, string styleText) { Root = root; StyleText = styleText; }

        public static Document Parse(string html)
        {
            if (html == null) throw new ArgumentNullException(nameof(html));
            var parser = new HtmlParser();
            IDocument doc = parser.ParseDocument(html);

            var styles = new StringBuilder();
            foreach (var s in doc.QuerySelectorAll("style"))
                styles.Append(s.TextContent).Append('\n');

            // External stylesheets: fetch each `<link rel="stylesheet" href>` (e.g. Google Fonts) and treat its CSS
            // like an inline <style> so its @font-face rules load. Without this, web fonts silently fall back.
            foreach (var link in doc.QuerySelectorAll("link"))
            {
                var rel = link.GetAttribute("rel") ?? "";
                bool isSheet = false;
                foreach (var tok in rel.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    if (string.Equals(tok, "stylesheet", StringComparison.OrdinalIgnoreCase)) { isSheet = true; break; }
                if (!isSheet) continue;
                var href = link.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href)) continue;
                if (href!.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    var css = Images.ImageLoader.FetchStylesheet(href);
                    if (css != null) { System.Console.WriteLine("Loaded external stylesheet: " + href); styles.Append(css).Append('\n'); }
                }
            }

            IElement bodyEl = doc.Body ?? doc.DocumentElement;
            Node root = Convert(bodyEl, null);
            return new Document(root, styles.ToString());
        }

        /// <summary>Parse a standalone SVG document/fragment (e.g. from <c>&lt;img src="…svg…"&gt;</c>) into the engine's
        /// Node tree — the &lt;svg&gt; element — so it can be drawn by the same vector path as an inline &lt;svg&gt;.</summary>
        public static Node? ParseSvgFragment(string svgXml)
        {
            if (string.IsNullOrWhiteSpace(svgXml)) return null;
            try
            {
                var parser = new HtmlParser();
                IDocument doc = parser.ParseDocument("<!doctype html><body>" + svgXml + "</body>");
                var svgEl = doc.QuerySelector("svg");
                return svgEl != null ? Convert(svgEl, null) : null;
            }
            catch { return null; }
        }

        private static readonly HashSet<string> Skip =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "script", "style", "head", "meta", "link", "title", "noscript" };

        private static Node Convert(INode node, Node? parent)
        {
            if (node is IText t)
            {
                return new Node { Text = t.Data, Parent = parent };
            }

            var el = (IElement)node;
            var n = new Node { Tag = el.LocalName.ToLowerInvariant(), Parent = parent };
            foreach (var attr in el.Attributes)
                n.Attributes[attr.Name] = attr.Value;

            foreach (var child in el.ChildNodes)
            {
                if (child is IElement ce)
                {
                    if (Skip.Contains(ce.LocalName)) continue;
                    n.Children.Add(Convert(child, n));
                }
                else if (child is IText ct)
                {
                    if (string.IsNullOrEmpty(ct.Data)) continue;
                    n.Children.Add(Convert(child, n));
                }
                // ignore comments / other node types
            }
            return n;
        }
    }
}
