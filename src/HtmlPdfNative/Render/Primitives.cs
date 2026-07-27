using System.Collections.Generic;

namespace HtmlPdfNative.Render
{
    /// <summary>RGBA color, 0..255. Port of the Rust <c>css::Color</c>.</summary>
    public readonly struct Color
    {
        public readonly byte R, G, B, A;
        public Color(byte r, byte g, byte b, byte a = 255) { R = r; G = g; B = b; A = a; }
        public static readonly Color Black = new Color(0, 0, 0);
        public static readonly Color White = new Color(255, 255, 255);
        /// <summary>Normalized 0..1 components for PDF operators.</summary>
        public (float r, float g, float b) Rgb01() => (R / 255f, G / 255f, B / 255f);
        public bool IsOpaque => A == 255;
    }

    /// <summary>Which of the base-14 faces a text run uses (the vertical slice; embedded fonts come later).</summary>
    public enum FontFace
    {
        Helvetica, HelveticaBold, HelveticaOblique, HelveticaBoldOblique,
        TimesRoman, TimesBold, TimesItalic, TimesBoldItalic,
        Courier, CourierBold, CourierOblique, CourierBoldOblique,
    }

    /// <summary>Base class for a single paint command in a <see cref="DisplayList"/>.</summary>
    public abstract class DrawCommand { }

    /// <summary>A run of text drawn at a baseline origin (x, y) in PDF points (y measured from page top).</summary>
    public sealed class TextRun : DrawCommand
    {
        public float X;
        public float BaselineY;   // distance from the TOP of the page, in pt (converted to PDF space on write)
        public string Text = "";
        public float FontSizePt;
        public FontFace Face;
        public Color Color;
        public float LetterSpacing;  // letter-spacing in pt (PDF Tc), 0 = none
        public bool Hidden;          // visibility:hidden on this run (skipped at paint; keeps its layout advance)
        public float StrokeWidth;    // -webkit-text-stroke width in pt (0 = none)
        public Color StrokeColor;    // -webkit-text-stroke colour
        /// <summary>When non-null, this run is drawn with an embedded Type0 font instead of the base-14 Face.</summary>
        public Fonts.EmbeddedFont? Emb;
    }

    /// <summary>A filled rectangle (x, y = top-left from page top), used for backgrounds.</summary>
    public sealed class SolidRect : DrawCommand
    {
        public float X, Y, Width, Height;
        public Color Color;
        // Optional rounded clip (border-radius): a rounded box's border edges are clipped to the rounded
        // outer border-box so their square corners round off. ClipX/Y/W/H is the border box; radii the corners.
        public bool Clip;
        public float ClipX, ClipY, ClipW, ClipH;
        public float ClipRtl, ClipRtr, ClipRbr, ClipRbl;
    }

    /// <summary>A filled polygon (list of (x,y) points, top-origin pt). Used for mitered border edges / CSS triangles.</summary>
    public sealed class Polygon : DrawCommand
    {
        public float[] Points = System.Array.Empty<float>();   // x0,y0,x1,y1,… (top-origin pt)
        public Color Color;
    }

    /// <summary>A fill (gradient / image / colour) clipped to text glyph outlines — CSS <c>background-clip:text</c>
    /// (gradient text). The <see cref="Runs"/> are emitted as a text CLIP path (PDF render mode 7); the <see cref="Fill"/>
    /// commands are then painted through that clip, so the fill shows only inside the glyph shapes.</summary>
    public sealed class TextClipFill : DrawCommand
    {
        public System.Collections.Generic.List<TextRun> Runs = new System.Collections.Generic.List<TextRun>();
        public System.Collections.Generic.List<DrawCommand> Fill = new System.Collections.Generic.List<DrawCommand>();
    }

    /// <summary>A decoded raster image drawn into the box (x, y = top-left from page top), in pt.</summary>
    public sealed class ImageDraw : DrawCommand
    {
        public float X, Y, Width, Height;
        public Images.DecodedImage Image = null!;
        // Optional clip rect (top-origin, pt) — used by background images that overflow (cover).
        public bool Clip;
        public float ClipX, ClipY, ClipW, ClipH;
        public float Rtl, Rtr, Rbr, Rbl;   // clip corner radii (border-radius), pt
        public float Alpha = 1f;           // opacity multiplier (0..1)
    }

    /// <summary>A rounded rectangle (x, y = top-left from page top), filled and/or stroked. For border-radius.</summary>
    public sealed class RoundRect : DrawCommand
    {
        public float X, Y, Width, Height;
        public float Rtl, Rtr, Rbr, Rbl;   // corner radii TL/TR/BR/BL, pt
        public Color? Fill;
        public Color? Stroke;
        public float StrokeW;
        // Optional rounded clip (used by inset box-shadow: the layers are clipped to the box interior).
        public bool Clip;
        public float ClipX, ClipY, ClipW, ClipH;
        public float ClipRtl, ClipRtr, ClipRbr, ClipRbl;
    }

    /// <summary>A CSS linear-gradient fill of a box (x, y = top-left from page top), in pt.</summary>
    public sealed class GradientFill : DrawCommand
    {
        public float X, Y, Width, Height;
        public Css.LinearGradient Gradient = null!;
        public float Rtl, Rtr, Rbr, Rbl;   // corner radii (border-radius), pt
        public float Alpha = 1f;           // opacity multiplier (0..1)
    }

    /// <summary>An inline &lt;svg&gt; placed at a box (x, y = top-left from page top); painted as PDF vectors.</summary>
    public sealed class SvgDraw : DrawCommand
    {
        public float X, Y, Width, Height;
        public Dom.Node Svg = null!;
        // Cascade context for the SVG subtree: per-element resolved styles (fill/stroke from class/tag rules) and the
        // custom-property map in scope (to resolve var() used inside inline SVG presentation attributes).
        public System.Collections.Generic.Dictionary<Dom.Node, Style.ComputedStyle>? Styles;
        public System.Collections.Generic.Dictionary<string, string>? Vars;
    }

    /// <summary>A CSS-<c>transform</c>ed box: its whole subtree, painted under an affine CTM about an origin.</summary>
    public sealed class TransformGroup : DrawCommand
    {
        public float[] Matrix = { 1, 0, 0, 1, 0, 0 }; // CSS transform matrix [a b c d e f], lengths in pt
        public float OxGlobal, OyGlobal;               // transform origin (top-origin page coords, paginated)
        public List<DrawCommand> Sub = new List<DrawCommand>();
    }

    /// <summary>An overflow:hidden / clip-path clip: its subtree is painted clipped to a (rounded) rectangle,
    /// or — when <see cref="Contours"/> is set — to an arbitrary polygon path (clip-path shapes).</summary>
    public sealed class ClipGroup : DrawCommand
    {
        public float X, Y, Width, Height;              // clip bbox (top-origin page coords; also the rect clip)
        public float Rtl, Rtr, Rbr, Rbl;               // corner radii (border-radius on the padding box)
        public List<float[]>? Contours;                // clip-path: closed contours, each a flat [x0,y0,x1,y1,…] (page coords)
        public List<DrawCommand> Sub = new List<DrawCommand>();
    }

    /// <summary>A mix-blend-mode group: its subtree is painted with a PDF /BM blend mode against the backdrop.</summary>
    public sealed class BlendGroup : DrawCommand
    {
        public string Mode = "Normal";                 // PDF blend-mode name (Multiply, Screen, …)
        public List<DrawCommand> Sub = new List<DrawCommand>();
    }

    /// <summary>CSS <c>opacity</c> on a box with a subtree: TRUE group opacity — the subtree is composited fully
    /// opaque into an isolated transparency-group Form XObject, then that group is drawn at <see cref="Alpha"/>.
    /// (Distinct from per-command alpha, which double-applies where a box's own layers/children overlap.)</summary>
    public sealed class OpacityGroup : DrawCommand
    {
        public float Alpha = 1f;                        // group constant alpha (0..1)
        public List<DrawCommand> Sub = new List<DrawCommand>();
    }

    /// <summary>A <c>backdrop-filter: blur()</c> marker: emitted where the element paints, it is resolved (before
    /// pagination) into a blurred snapshot of everything drawn BEHIND it within [X,Y,W,H], composited under the
    /// element's own content (frosted-glass effect). Requires the SkiaSharp rasterizer; a no-op without it.</summary>
    public sealed class BackdropBlur : DrawCommand
    {
        public float X, Y, Width, Height;   // element border/padding box, top-origin pt (the frosted region)
        public float BlurPt;                // blur radius in pt
    }

    /// <summary>Ordered paint commands produced by <see cref="Renderer"/>, consumed by the PDF writer.</summary>
    public sealed class DisplayList
    {
        public List<DrawCommand> Commands { get; } = new List<DrawCommand>();
        public void Add(DrawCommand c) => Commands.Add(c);
    }
}
