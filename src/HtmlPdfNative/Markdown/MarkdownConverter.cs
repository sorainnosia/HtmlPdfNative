using System;
using Markdig;

namespace HtmlPdfNative.Markdown
{
    /// <summary>
    /// Markdown (CommonMark + GFM) -> HTML via <c>Markdig</c>. Port of <c>src/markdown.rs</c>
    /// (Rust used <c>pulldown-cmark</c>). The user-editable <c>md_template.html</c> wrapper and the
    /// syntax-highlight one-block-per-line trick (memory "markdown-support") come in a later phase.
    /// </summary>
    public static class MarkdownConverter
    {
        private static readonly MarkdownPipeline Pipeline =
            new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        public static string ToHtml(string markdown)
        {
            if (markdown == null) throw new ArgumentNullException(nameof(markdown));
            string body = Markdig.Markdown.ToHtml(markdown, Pipeline);
            return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>" +
                   "body{font-family:sans-serif;font-size:14px;line-height:1.5} " +
                   "code,pre{font-family:monospace}</style></head><body>" + body + "</body></html>";
        }
    }
}
