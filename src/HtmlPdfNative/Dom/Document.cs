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

            IElement bodyEl = doc.Body ?? doc.DocumentElement;
            Node root = Convert(bodyEl, null);
            return new Document(root, styles.ToString());
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
