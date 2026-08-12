using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HtmlPdfNative.Compression;
using HtmlPdfNative.Fonts;
using HtmlPdfNative.Images;
using HtmlPdfNative.PdfWriter;
using HtmlPdfNative.Render;

namespace HtmlPdfNative.Pdf
{
    /// <summary>
    /// Serializes a <see cref="DisplayList"/> into a PDF: base-14 text, solid rectangles, and embedded
    /// raster images (DeviceRGB /FlateDecode XObjects, with a DeviceGray /SMask for alpha). Slice of
    /// <c>src/pdf.rs</c> (embedded/CJK font subsetting + encryption come later).
    /// </summary>
    public sealed class PdfGenerator
    {
        private readonly PageSize _size;
        private readonly Css.PageConfig _page;
        private string? _userPw;
        private string? _ownerPw;
        private Dictionary<OpacityGroup, string>? _formNames;   // per-page: OpacityGroup → Form XObject resource name

        public PdfGenerator(PageSize size, Css.PageConfig page) { _size = size; _page = page; }

        public void SetPasswords(string userPassword, string? ownerPassword)
        {
            _userPw = userPassword;
            _ownerPw = ownerPassword;
        }

        public byte[] Save(DisplayList displayList)
        {
            if (displayList == null) throw new ArgumentNullException(nameof(displayList));
            // Print shrink-to-fit: content substantially wider than the page (e.g. a fixed 900px design) is uniformly
            // scaled down to fit the page width. GATED on an explicit `@page{size:…}` (matching the Rust engine): a doc
            // that never declared a page size shouldn't be shrunk just because one stray wide element overflows — that
            // element simply clips, and the rest stays full width (Chrome only fit-scales when the box IS the page).
            // Also gated at >5% overflow so a Letter-designed doc (~612pt) on A4 (595pt) isn't falsely shrunk.
            float maxX = MaxRightX(displayList.Commands);
            if (_page.SizeExplicit && maxX > _size.Width * 1.05f)
            {
                float s = _size.Width / maxX;
                var scaled = displayList.Commands.ConvertAll(c => ScaleCmd(c, s));
                displayList.Commands.Clear();
                displayList.Commands.AddRange(scaled);
            }
            var pages = Paginate(displayList, _size.Height);
            var doc = new PdfDocument();
            int catalogId = doc.Allocate();
            int pagesId = doc.Allocate();

            // Fonts actually used.
            var usedFaces = new SortedSet<FontFace>();
            foreach (var page in pages)
                foreach (var cmd in Flatten(page))
                {
                    if (cmd is TextRun tr) usedFaces.Add(tr.Face);
                    else if (cmd is SvgDraw svf) foreach (var f in SvgPainter.UsedFaces(svf.Svg)) usedFaces.Add(f);
                }
            if (usedFaces.Count == 0) usedFaces.Add(FontFace.Helvetica);
            var fontRes = new StringBuilder();
            var faceRes = new Dictionary<FontFace, string>();
            int fi = 1;
            foreach (var face in usedFaces)
            {
                int fid = doc.Allocate();
                string res = "F" + fi.ToString(CultureInfo.InvariantCulture);
                faceRes[face] = res;
                fontRes.Append("/").Append(res).Append(' ').Append(fid).Append(" 0 R ");
                doc.Set(fid, "<< /Type /Font /Subtype /Type1 /BaseFont /" + Afm.PdfName(face) + " /Encoding /WinAnsiEncoding >>");
                fi++;
            }

            // Embedded Type0 fonts (fonts used for non-WinAnsi text).
            var embRes = new Dictionary<EmbeddedFont, string>();
            int ti = 1;
            void RegisterEmb(EmbeddedFont emb)
            {
                if (embRes.ContainsKey(emb)) return;
                int t0 = BuildEmbeddedFont(doc, emb);
                string nm = "T" + ti.ToString(CultureInfo.InvariantCulture);
                embRes[emb] = nm;
                fontRes.Append("/").Append(nm).Append(' ').Append(t0).Append(" 0 R ");
                ti++;
            }
            foreach (var page in pages)
                foreach (var cmd in Flatten(page))
                {
                    if (cmd is TextRun etr && etr.Emb != null) RegisterEmb(etr.Emb);
                    // Non-WinAnsi <text> inside an inline SVG (Arabic/CJK/emoji/symbols) also needs an embedded face.
                    else if (cmd is SvgDraw esv) foreach (var e in SvgPainter.UsedEmbeddedFonts(esv.Svg)) RegisterEmb(e);
                }

            // Unique images -> XObjects.
            var imgRes = new StringBuilder();
            var imgName = new Dictionary<DecodedImage, string>();
            int ii = 1;
            foreach (var page in pages)
                foreach (var cmd in Flatten(page))
                    if (cmd is ImageDraw id && !imgName.ContainsKey(id.Image))
                    {
                        var img = id.Image;
                        int smaskId = -1;
                        if (img.Alpha != null)
                        {
                            smaskId = doc.Allocate();
                            doc.SetStream(smaskId,
                                "/Type /XObject /Subtype /Image /Width " + img.Width + " /Height " + img.Height +
                                " /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode",
                                Codecs.ZlibCompress(img.Alpha));
                        }
                        int xid = doc.Allocate();
                        string dict = "/Type /XObject /Subtype /Image /Width " + img.Width + " /Height " + img.Height +
                                      " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode" +
                                      (smaskId >= 0 ? " /SMask " + smaskId + " 0 R" : "");
                        doc.SetStream(xid, dict, Codecs.ZlibCompress(img.Rgb));
                        string name = "Im" + ii.ToString(CultureInfo.InvariantCulture);
                        imgName[img] = name;
                        imgRes.Append("/").Append(name).Append(' ').Append(xid).Append(" 0 R ");
                        ii++;
                    }

            // Alpha (rgba / box-shadow) -> shared /ExtGState objects, one per distinct opacity.
            var alphaRes = new Dictionary<byte, string>();
            var extRes = new StringBuilder();
            int gk = 1;
            foreach (var page in pages)
                foreach (var cmd in Flatten(page))
                    foreach (var a in CmdAlphas(cmd))
                        if (a < 255 && !alphaRes.ContainsKey(a))
                        {
                            int gid = doc.Allocate();
                            string nm = "GS" + gk.ToString(CultureInfo.InvariantCulture);
                            alphaRes[a] = nm;
                            doc.Set(gid, "<< /Type /ExtGState /ca " + F(a / 255f) + " /CA " + F(a / 255f) + " >>");
                            extRes.Append("/").Append(nm).Append(' ').Append(gid).Append(" 0 R ");
                            gk++;
                        }

            // mix-blend-mode -> shared /ExtGState objects, one per distinct /BM.
            var blendGs = new Dictionary<string, string>();
            int bk = 1;
            foreach (var page in pages)
                foreach (var cmd in Flatten(page))
                    if (cmd is BlendGroup bg && !blendGs.ContainsKey(bg.Mode))
                    {
                        int gid = doc.Allocate();
                        string nm = "GB" + bk.ToString(CultureInfo.InvariantCulture);
                        blendGs[bg.Mode] = nm;
                        doc.Set(gid, "<< /Type /ExtGState /BM /" + bg.Mode + " >>");
                        extRes.Append("/").Append(nm).Append(' ').Append(gid).Append(" 0 R ");
                        bk++;
                    }

            var pageIds = new List<int>();
            foreach (var page in pages)
            {
                // Gradient fills -> axial shading patterns (per page, per fill).
                var patName = new Dictionary<GradientFill, string>();
                var smaskName = new Dictionary<GradientFill, string>();  // per-stop-alpha soft masks
                var patRes = new StringBuilder();
                var smaskRes = new StringBuilder();
                int pk = 1, sk = 1;
                foreach (var cmd in Flatten(page))
                    if (cmd is GradientFill gf && !patName.ContainsKey(gf))
                    {
                        int pid = BuildGradientPattern(doc, gf);
                        string nm = "P" + pk.ToString(CultureInfo.InvariantCulture);
                        patName[gf] = nm;
                        patRes.Append("/").Append(nm).Append(' ').Append(pid).Append(" 0 R ");
                        pk++;
                        if (HasVaryingAlpha(gf))
                        {
                            int gsId = BuildGradientSoftMask(doc, gf);
                            if (gsId >= 0) { string sn = "GsM" + sk.ToString(CultureInfo.InvariantCulture); smaskName[gf] = sn; smaskRes.Append("/").Append(sn).Append(' ').Append(gsId).Append(" 0 R "); sk++; }
                        }
                    }

                // opacity groups -> transparency-group Form XObjects. Forms share the page's /Resources object
                // (resId), so they can reference the same fonts/images/patterns/nested-forms by name. Built
                // post-order (inner groups first) so an outer form's content can `Do` the inner forms by name.
                int resId = doc.Allocate();
                _formNames = new Dictionary<OpacityGroup, string>();
                var formRes = new StringBuilder();
                int fmk = 1;
                void BuildForms(List<DrawCommand> cmds)
                {
                    foreach (var c in cmds)
                        switch (c)
                        {
                            case OpacityGroup og:
                                BuildForms(og.Sub);                       // inner groups first
                                if (!_formNames.ContainsKey(og))
                                {
                                    int fid = doc.Allocate();
                                    string nm = "Fm" + fmk.ToString(CultureInfo.InvariantCulture); fmk++;
                                    _formNames[og] = nm;
                                    var body = BuildContentStream(og.Sub, faceRes, imgName, patName, embRes, alphaRes, smaskName, blendGs);
                                    string fdict = "/Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 " + F(_size.Width) + " " + F(_size.Height) + "]" +
                                                   " /Group << /Type /Group /S /Transparency /I true >> /Resources " + resId + " 0 R";
                                    doc.SetStream(fid, fdict, body);
                                    formRes.Append("/").Append(nm).Append(' ').Append(fid).Append(" 0 R ");
                                }
                                break;
                            case ClipGroup cg2: BuildForms(cg2.Sub); break;
                            case BlendGroup bg2: BuildForms(bg2.Sub); break;
                            case TransformGroup tg2: BuildForms(tg2.Sub); break;
                        }
                }
                BuildForms(page);

                // Shared resources object (referenced by the page and every form on it).
                string xobj = (imgRes.ToString() + formRes.ToString()).Trim();
                string res = "/Font << " + fontRes.ToString().Trim() + " >>";
                if (xobj.Length > 0) res += " /XObject << " + xobj + " >>";
                if (patRes.Length > 0) res += " /Pattern << " + patRes.ToString().Trim() + " >>";
                if (extRes.Length > 0 || smaskRes.Length > 0) res += " /ExtGState << " + (extRes.ToString() + smaskRes.ToString()).Trim() + " >>";
                doc.Set(resId, "<< " + res + " >>");

                int contentId = doc.Allocate();
                doc.SetStream(contentId, "", BuildContentStream(page, faceRes, imgName, patName, embRes, alphaRes, smaskName, blendGs));
                int pageId = doc.Allocate();
                pageIds.Add(pageId);
                doc.Set(pageId,
                    "<< /Type /Page /Parent " + pagesId + " 0 R /MediaBox [0 0 " + F(_size.Width) + " " + F(_size.Height) + "]" +
                    " /Resources " + resId + " 0 R /Contents " + contentId + " 0 R >>");
            }

            var kids = new StringBuilder();
            foreach (var pid in pageIds) kids.Append(pid).Append(" 0 R ");
            doc.Set(pagesId, "<< /Type /Pages /Kids [" + kids.ToString().Trim() + "] /Count " + pageIds.Count + " >>");
            doc.Set(catalogId, "<< /Type /Catalog /Pages " + pagesId + " 0 R >>");

            if (!string.IsNullOrEmpty(_userPw) || !string.IsNullOrEmpty(_ownerPw))
            {
                var id = Encryption.PdfEncryptor.NewId();
                var enc = new Encryption.PdfEncryptor(_userPw, _ownerPw, id);
                int encId = doc.Allocate();
                doc.Set(encId, enc.EncryptDict());
                return doc.Build(catalogId, encId, Encryption.PdfEncryptor.Hex(id), enc.Encrypt);
            }
            return doc.Build(catalogId);
        }

        private static float TopY(DrawCommand c) => c switch
        {
            TextRun tr => tr.BaselineY, SolidRect r => r.Y, ImageDraw im => im.Y, Polygon pg => PolyMinY(pg),
            GradientFill g => g.Y, SvgDraw sv => sv.Y, RoundRect rr => rr.Y, TransformGroup tg => tg.OyGlobal, ClipGroup cg => cg.Y,
            BlendGroup bg => bg.Sub.Count > 0 ? TopY(bg.Sub[0]) : 0f,
            OpacityGroup og => og.Sub.Count > 0 ? TopY(og.Sub[0]) : 0f,
            TextClipFill tcf => tcf.Fill.Count > 0 ? TopY(tcf.Fill[0]) : (tcf.Runs.Count > 0 ? tcf.Runs[0].BaselineY - tcf.Runs[0].FontSizePt : 0f), _ => 0f,
        };
        private static float BottomY(DrawCommand c) => c switch
        {
            TextRun tr => tr.BaselineY, SolidRect r => r.Y + r.Height, ImageDraw im => im.Y + im.Height, Polygon pg => PolyMaxY(pg),
            GradientFill g => g.Y + g.Height, SvgDraw sv => sv.Y + sv.Height, RoundRect rr => rr.Y + rr.Height, TransformGroup tg => tg.OyGlobal,
            ClipGroup cg => cg.Y + cg.Height, BlendGroup bg => bg.Sub.Count > 0 ? BottomY(bg.Sub[bg.Sub.Count - 1]) : 0f,
            OpacityGroup og => og.Sub.Count > 0 ? BottomY(og.Sub[og.Sub.Count - 1]) : 0f,
            TextClipFill tcf => tcf.Fill.Count > 0 ? BottomY(tcf.Fill[tcf.Fill.Count - 1]) : (tcf.Runs.Count > 0 ? tcf.Runs[tcf.Runs.Count - 1].BaselineY : 0f), _ => 0f,
        };
        private static float PolyMinY(Polygon pg) { float m = float.MaxValue; for (int k = 1; k < pg.Points.Length; k += 2) m = Math.Min(m, pg.Points[k]); return m == float.MaxValue ? 0f : m; }
        private static float PolyMaxY(Polygon pg) { float m = 0f; for (int k = 1; k < pg.Points.Length; k += 2) m = Math.Max(m, pg.Points[k]); return m; }

        /// <summary>Copy a command with its Y coordinates reduced by <paramref name="shift"/> (recurses into groups).</summary>
        private static DrawCommand ShiftCmd(DrawCommand c, float shift) => c switch
        {
            TextRun t => new TextRun { X = t.X, BaselineY = t.BaselineY - shift, Text = t.Text, FontSizePt = t.FontSizePt, Face = t.Face, Color = t.Color, LetterSpacing = t.LetterSpacing, Emb = t.Emb, Hidden = t.Hidden, StrokeWidth = t.StrokeWidth, StrokeColor = t.StrokeColor },
            SolidRect s => new SolidRect { X = s.X, Y = s.Y - shift, Width = s.Width, Height = s.Height, Color = s.Color, Clip = s.Clip, ClipX = s.ClipX, ClipY = s.ClipY - shift, ClipW = s.ClipW, ClipH = s.ClipH, ClipRtl = s.ClipRtl, ClipRtr = s.ClipRtr, ClipRbr = s.ClipRbr, ClipRbl = s.ClipRbl },
            Polygon pg => new Polygon { Points = ShiftPts(pg.Points, shift), Color = pg.Color },
            ImageDraw im => new ImageDraw { X = im.X, Y = im.Y - shift, Width = im.Width, Height = im.Height, Image = im.Image, Clip = im.Clip, ClipX = im.ClipX, ClipY = im.ClipY - shift, ClipW = im.ClipW, ClipH = im.ClipH, Rtl = im.Rtl, Rtr = im.Rtr, Rbr = im.Rbr, Rbl = im.Rbl, Alpha = im.Alpha },
            GradientFill gf => new GradientFill { X = gf.X, Y = gf.Y - shift, Width = gf.Width, Height = gf.Height, Gradient = gf.Gradient, Rtl = gf.Rtl, Rtr = gf.Rtr, Rbr = gf.Rbr, Rbl = gf.Rbl, Alpha = gf.Alpha },
            RoundRect rr => new RoundRect { X = rr.X, Y = rr.Y - shift, Width = rr.Width, Height = rr.Height, Rtl = rr.Rtl, Rtr = rr.Rtr, Rbr = rr.Rbr, Rbl = rr.Rbl, Fill = rr.Fill, Stroke = rr.Stroke, StrokeW = rr.StrokeW, Clip = rr.Clip, ClipX = rr.ClipX, ClipY = rr.ClipY - shift, ClipW = rr.ClipW, ClipH = rr.ClipH, ClipRtl = rr.ClipRtl, ClipRtr = rr.ClipRtr, ClipRbr = rr.ClipRbr, ClipRbl = rr.ClipRbl },
            SvgDraw sv => new SvgDraw { X = sv.X, Y = sv.Y - shift, Width = sv.Width, Height = sv.Height, Svg = sv.Svg, Styles = sv.Styles, Vars = sv.Vars },
            TransformGroup tg => new TransformGroup { Matrix = tg.Matrix, OxGlobal = tg.OxGlobal, OyGlobal = tg.OyGlobal - shift, Sub = tg.Sub.ConvertAll(x => ShiftCmd(x, shift)) },
            ClipGroup cg => new ClipGroup { X = cg.X, Y = cg.Y - shift, Width = cg.Width, Height = cg.Height, Rtl = cg.Rtl, Rtr = cg.Rtr, Rbr = cg.Rbr, Rbl = cg.Rbl, Contours = ShiftContours(cg.Contours, shift), Sub = cg.Sub.ConvertAll(x => ShiftCmd(x, shift)) },
            BlendGroup bg => new BlendGroup { Mode = bg.Mode, Sub = bg.Sub.ConvertAll(x => ShiftCmd(x, shift)) },
            OpacityGroup og => new OpacityGroup { Alpha = og.Alpha, Sub = og.Sub.ConvertAll(x => ShiftCmd(x, shift)) },
            TextClipFill tcf => new TextClipFill { Runs = tcf.Runs.ConvertAll(x => (TextRun)ShiftCmd(x, shift)), Fill = tcf.Fill.ConvertAll(x => ShiftCmd(x, shift)) },
            _ => c,
        };

        /// <summary>Rightmost x-extent (pt) of the display list, for shrink-to-fit. ClipGroups clamp to their clip box
        /// (content beyond a clip is invisible and must not force a shrink). Transforms are approximated by their subtree.</summary>
        private float MaxRightX(IEnumerable<DrawCommand> cmds)
        {
            float m = 0f;
            foreach (var c in cmds)
            {
                float r;
                switch (c)
                {
                    case ClipGroup cg: r = Math.Min(cg.X + cg.Width, MaxRightX(cg.Sub)); break;
                    case BlendGroup bg: r = MaxRightX(bg.Sub); break;
                    case OpacityGroup og: r = MaxRightX(og.Sub); break;
                    case TransformGroup tg: r = MaxRightX(tg.Sub); break;
                    case TextRun t: r = t.X + (t.Emb != null ? t.Emb.MeasurePt(t.Text, t.FontSizePt) : Afm.MeasurePt(t.Face, t.Text, t.FontSizePt)) + t.LetterSpacing * (t.Text?.Length ?? 0); break;
                    case SolidRect sr: r = sr.X + sr.Width; break;
                    case Polygon pg: r = 0f; for (int k = 0; k + 1 < pg.Points.Length; k += 2) r = Math.Max(r, pg.Points[k]); break;
                    case ImageDraw im: r = im.X + im.Width; break;
                    case GradientFill g: r = g.X + g.Width; break;
                    case SvgDraw sv: r = sv.X + sv.Width; break;
                    case RoundRect rr: r = rr.X + rr.Width; break;
                    default: r = 0f; break;
                }
                if (r > m) m = r;
            }
            return m;
        }

        /// <summary>Uniformly scale a command's geometry by <paramref name="s"/> about the page origin (top-left).
        /// Used by print shrink-to-fit before pagination so page count reflects the scaled height.</summary>
        private static DrawCommand ScaleCmd(DrawCommand c, float s) => c switch
        {
            TextRun t => new TextRun { X = t.X * s, BaselineY = t.BaselineY * s, Text = t.Text, FontSizePt = t.FontSizePt * s, Face = t.Face, Color = t.Color, LetterSpacing = t.LetterSpacing * s, Hidden = t.Hidden, StrokeWidth = t.StrokeWidth * s, StrokeColor = t.StrokeColor, Emb = t.Emb },
            SolidRect r => new SolidRect { X = r.X * s, Y = r.Y * s, Width = r.Width * s, Height = r.Height * s, Color = r.Color, Clip = r.Clip, ClipX = r.ClipX * s, ClipY = r.ClipY * s, ClipW = r.ClipW * s, ClipH = r.ClipH * s, ClipRtl = r.ClipRtl * s, ClipRtr = r.ClipRtr * s, ClipRbr = r.ClipRbr * s, ClipRbl = r.ClipRbl * s },
            Polygon pg => new Polygon { Points = ScalePts(pg.Points, s), Color = pg.Color },
            ImageDraw im => new ImageDraw { X = im.X * s, Y = im.Y * s, Width = im.Width * s, Height = im.Height * s, Image = im.Image, Clip = im.Clip, ClipX = im.ClipX * s, ClipY = im.ClipY * s, ClipW = im.ClipW * s, ClipH = im.ClipH * s, Rtl = im.Rtl * s, Rtr = im.Rtr * s, Rbr = im.Rbr * s, Rbl = im.Rbl * s, Alpha = im.Alpha },
            GradientFill g => new GradientFill { X = g.X * s, Y = g.Y * s, Width = g.Width * s, Height = g.Height * s, Gradient = g.Gradient, Rtl = g.Rtl * s, Rtr = g.Rtr * s, Rbr = g.Rbr * s, Rbl = g.Rbl * s, Alpha = g.Alpha },
            SvgDraw sv => new SvgDraw { X = sv.X * s, Y = sv.Y * s, Width = sv.Width * s, Height = sv.Height * s, Svg = sv.Svg, Styles = sv.Styles, Vars = sv.Vars },
            RoundRect rr => new RoundRect { X = rr.X * s, Y = rr.Y * s, Width = rr.Width * s, Height = rr.Height * s, Rtl = rr.Rtl * s, Rtr = rr.Rtr * s, Rbr = rr.Rbr * s, Rbl = rr.Rbl * s, Fill = rr.Fill, Stroke = rr.Stroke, StrokeW = rr.StrokeW * s, Clip = rr.Clip, ClipX = rr.ClipX * s, ClipY = rr.ClipY * s, ClipW = rr.ClipW * s, ClipH = rr.ClipH * s, ClipRtl = rr.ClipRtl * s, ClipRtr = rr.ClipRtr * s, ClipRbr = rr.ClipRbr * s, ClipRbl = rr.ClipRbl * s },
            TransformGroup tg => new TransformGroup { Matrix = new[] { tg.Matrix[0], tg.Matrix[1], tg.Matrix[2], tg.Matrix[3], tg.Matrix[4] * s, tg.Matrix[5] * s }, OxGlobal = tg.OxGlobal * s, OyGlobal = tg.OyGlobal * s, Sub = tg.Sub.ConvertAll(x => ScaleCmd(x, s)) },
            ClipGroup cg => new ClipGroup { X = cg.X * s, Y = cg.Y * s, Width = cg.Width * s, Height = cg.Height * s, Rtl = cg.Rtl * s, Rtr = cg.Rtr * s, Rbr = cg.Rbr * s, Rbl = cg.Rbl * s, Contours = ScaleContours(cg.Contours, s), Sub = cg.Sub.ConvertAll(x => ScaleCmd(x, s)) },
            BlendGroup bg => new BlendGroup { Mode = bg.Mode, Sub = bg.Sub.ConvertAll(x => ScaleCmd(x, s)) },
            OpacityGroup og => new OpacityGroup { Alpha = og.Alpha, Sub = og.Sub.ConvertAll(x => ScaleCmd(x, s)) },
            TextClipFill tcf => new TextClipFill { Runs = tcf.Runs.ConvertAll(x => (TextRun)ScaleCmd(x, s)), Fill = tcf.Fill.ConvertAll(x => ScaleCmd(x, s)) },
            _ => c,
        };

        private static List<float[]>? ScaleContours(List<float[]>? cs, float s)
        {
            if (cs == null) return null;
            var outl = new List<float[]>(cs.Count);
            foreach (var c in cs) { var p = (float[])c.Clone(); for (int i = 0; i < p.Length; i++) p[i] *= s; outl.Add(p); }
            return outl;
        }

        // Shift each contour's Y by -shift (contours are flat [x0,y0,x1,y1,…]).
        private static List<float[]>? ShiftContours(List<float[]>? cs, float shift)
        {
            if (cs == null) return null;
            var outl = new List<float[]>(cs.Count);
            foreach (var c in cs) { var p = (float[])c.Clone(); for (int i = 1; i < p.Length; i += 2) p[i] -= shift; outl.Add(p); }
            return outl;
        }

        /// <summary>All commands, descending into transform groups (for resource collection).</summary>
        private static IEnumerable<DrawCommand> Flatten(IEnumerable<DrawCommand> cmds)
        {
            foreach (var c in cmds)
            {
                if (c is TransformGroup tg) { foreach (var s in Flatten(tg.Sub)) yield return s; }
                else if (c is ClipGroup cg) { foreach (var s in Flatten(cg.Sub)) yield return s; }
                else if (c is BlendGroup bg) { yield return c; foreach (var s in Flatten(bg.Sub)) yield return s; }
                else if (c is OpacityGroup og) { yield return c; foreach (var s in Flatten(og.Sub)) yield return s; }
                else if (c is TextClipFill tcf) { foreach (var run in tcf.Runs) yield return run; foreach (var s in Flatten(tcf.Fill)) yield return s; }
                else yield return c;
            }
        }

        private static List<List<DrawCommand>> Paginate(DisplayList list, float pageHeight)
        {
            float maxY = 0f;
            foreach (var c in Flatten(list.Commands)) { float y = BottomY(c); if (y > maxY) maxY = y; }
            int pageCount = Math.Max(1, (int)Math.Ceiling(maxY / pageHeight - 1e-4));
            var pages = new List<List<DrawCommand>>();
            for (int p = 0; p < pageCount; p++) pages.Add(new List<DrawCommand>());

            for (int p = 0; p < pageCount; p++)
                foreach (var c in list.Commands)
                {
                    var portion = PortionOnPage(c, p, pageHeight, pageCount);
                    if (portion != null) pages[p].Add(portion);
                }
            // Drop EVERY page that received no commands (trailing OR mid-document). A trailing margin/tall box pushes
            // maxY a few pt past a boundary (phantom last page); a break-before landing on a full page, or a too-tall
            // keep-together block, can skip a whole page. Each command is binned by its own absolute Y, so removing a
            // zero-command page simply closes the gap — the remaining pages stay in order and render identically.
            var nonEmpty = new List<List<DrawCommand>>(pages.Count);
            foreach (var pg in pages) if (pg.Count > 0) nonEmpty.Add(pg);
            if (nonEmpty.Count == 0) nonEmpty.Add(new List<DrawCommand>());
            pages = nonEmpty;
            return pages;
        }

        /// <summary>The part of a command that belongs on page <paramref name="p"/>, shifted into that page's coordinates
        /// (or null if none). A ClipGroup is SPLIT across pages — each page gets a copy of the clip wrapping only the
        /// sub-commands on that page — so an <c>overflow:hidden</c> container taller than a page renders on every page it
        /// spans (rather than dumping all its content onto its first page). Leaf commands and transform/blend groups are
        /// binned whole by their top edge (splitting a transform would distort it).</summary>
        private static DrawCommand? PortionOnPage(DrawCommand c, int p, float pageHeight, int pageCount)
        {
            float shift = p * pageHeight;
            if (c is ClipGroup cg)
            {
                var sub = new List<DrawCommand>();
                foreach (var s in cg.Sub) { var ps = PortionOnPage(s, p, pageHeight, pageCount); if (ps != null) sub.Add(ps); }
                if (sub.Count == 0) return null;
                return new ClipGroup
                {
                    X = cg.X, Y = cg.Y - shift, Width = cg.Width, Height = cg.Height,
                    Rtl = cg.Rtl, Rtr = cg.Rtr, Rbr = cg.Rbr, Rbl = cg.Rbl,
                    Contours = ShiftContours(cg.Contours, shift), Sub = sub,
                };
            }
            // A solid background rect taller than a page (e.g. the <body> colour on a multi-page doc) must appear on
            // EVERY page it spans, clipped to that page — not just the page of its top edge. Otherwise later pages
            // render white and light text on a dark theme becomes invisible.
            if (c is SolidRect sr)
            {
                float top = sr.Y, bot = sr.Y + sr.Height, pTop = p * pageHeight, pBot = (p + 1) * pageHeight;
                if (bot <= pTop + 0.01f || top >= pBot - 0.01f) return null;
                if (top < pTop - 0.01f || bot > pBot + 0.01f)   // spans a page boundary → clip to this page
                {
                    float ct = Math.Max(top, pTop), cb = Math.Min(bot, pBot);
                    return new SolidRect { X = sr.X, Y = ct - shift, Width = sr.Width, Height = cb - ct, Color = sr.Color };
                }
                return ShiftCmd(sr, shift);
            }
            int page = Math.Min(pageCount - 1, Math.Max(0, (int)(TopY(c) / pageHeight)));
            return page == p ? ShiftCmd(c, shift) : null;
        }

        private static IEnumerable<byte> CmdAlphas(DrawCommand c)
        {
            switch (c)
            {
                case SolidRect r: yield return r.Color.A; break;
                case Polygon pg: yield return pg.Color.A; break;
                case TextRun t: yield return t.Color.A; break;
                case RoundRect rr: if (rr.Fill.HasValue) yield return rr.Fill.Value.A; if (rr.Stroke.HasValue) yield return rr.Stroke.Value.A; break;
                case GradientFill g: yield return (byte)Math.Round(g.Alpha * 255); break;
                case ImageDraw im: yield return (byte)Math.Round(im.Alpha * 255); break;
                case OpacityGroup og: yield return (byte)Math.Round(og.Alpha * 255); break;
                case SvgDraw sv: foreach (var a in SvgPainter.UsedAlphas(sv)) yield return a; break;
            }
        }

        private byte[] BuildContentStream(List<DrawCommand> cmds, Dictionary<FontFace, string> faceRes, Dictionary<DecodedImage, string> imgName, Dictionary<GradientFill, string> patName, Dictionary<EmbeddedFont, string> embRes, Dictionary<byte, string> alphaRes, Dictionary<GradientFill, string> smaskName, Dictionary<string, string> blendGs)
        {
            var sb = new StringBuilder();
            EmitCommands(sb, cmds, faceRes, imgName, patName, embRes, alphaRes, smaskName, blendGs);
            return PdfDocument.Latin1(sb.ToString());
        }

        // Single ordered pass: commands are emitted in DisplayList (tree/paint) order — the Renderer already
        // produces correct back-to-front order (a box's bg before its text before its children). Each
        // transform group is `q <cm> ... Q` with its subtree emitted recursively.
        private void EmitCommands(StringBuilder sb, List<DrawCommand> cmds, Dictionary<FontFace, string> faceRes, Dictionary<DecodedImage, string> imgName, Dictionary<GradientFill, string> patName, Dictionary<EmbeddedFont, string> embRes, Dictionary<byte, string> alphaRes, Dictionary<GradientFill, string> smaskName, Dictionary<string, string> blendGs)
        {
            string? Gs(byte a) => a < 255 && alphaRes.TryGetValue(a, out var n) ? n : null;
            foreach (var c in cmds)
            {
                if (c is GradientFill g)
                {
                    sb.Append("q ");
                    if (smaskName.TryGetValue(g, out var sm)) sb.Append('/').Append(sm).Append(" gs "); // per-stop alpha soft mask
                    else { string? gsg = Gs((byte)Math.Round(g.Alpha * 255)); if (gsg != null) sb.Append('/').Append(gsg).Append(" gs "); }
                    sb.Append("/Pattern cs /").Append(patName[g]).Append(" scn ");
                    if (g.Rtl > 0 || g.Rtr > 0 || g.Rbr > 0 || g.Rbl > 0) { RoundedPath(sb, g.X, g.Y, g.Width, g.Height, g.Rtl, g.Rtr, g.Rbr, g.Rbl); sb.Append("f Q\n"); }
                    else { float pdfY = _size.Height - (g.Y + g.Height); sb.Append(F(g.X)).Append(' ').Append(F(pdfY)).Append(' ').Append(F(g.Width)).Append(' ').Append(F(g.Height)).Append(" re f Q\n"); }
                }
                else if (c is SolidRect r)
                {
                    var (cr, cg, cb) = r.Color.Rgb01();
                    float pdfY = _size.Height - (r.Y + r.Height);
                    string? gs = Gs(r.Color.A);
                    bool wrap = gs != null || r.Clip;
                    if (wrap) sb.Append("q ");
                    if (gs != null) sb.Append('/').Append(gs).Append(" gs ");
                    // Rounded border-radius clip: round off the square corners of a rounded box's border edges.
                    if (r.Clip) { RoundedPath(sb, r.ClipX, r.ClipY, r.ClipW, r.ClipH, r.ClipRtl, r.ClipRtr, r.ClipRbr, r.ClipRbl); sb.Append("W n "); }
                    sb.Append(F(cr)).Append(' ').Append(F(cg)).Append(' ').Append(F(cb)).Append(" rg ")
                      .Append(F(r.X)).Append(' ').Append(F(pdfY)).Append(' ').Append(F(r.Width)).Append(' ').Append(F(r.Height)).Append(" re f");
                    sb.Append(wrap ? " Q\n" : "\n");
                }
                else if (c is Polygon pg && pg.Points.Length >= 6)
                {
                    var (cr, cg, cb) = pg.Color.Rgb01();
                    string? gs = Gs(pg.Color.A);
                    if (gs != null) sb.Append("q /").Append(gs).Append(" gs ");
                    sb.Append(F(cr)).Append(' ').Append(F(cg)).Append(' ').Append(F(cb)).Append(" rg ");
                    for (int k = 0; k + 1 < pg.Points.Length; k += 2)
                        sb.Append(F(pg.Points[k])).Append(' ').Append(F(_size.Height - pg.Points[k + 1])).Append(k == 0 ? " m " : " l ");
                    sb.Append("h f");
                    sb.Append(gs != null ? " Q\n" : "\n");
                }
                else if (c is RoundRect rr)
                {
                    string? gs = Gs(rr.Fill?.A ?? rr.Stroke?.A ?? 255);
                    bool wrap = gs != null || rr.Clip;
                    if (wrap) sb.Append("q ");
                    if (gs != null) sb.Append('/').Append(gs).Append(" gs ");
                    if (rr.Clip) { RoundedPath(sb, rr.ClipX, rr.ClipY, rr.ClipW, rr.ClipH, rr.ClipRtl, rr.ClipRtr, rr.ClipRbr, rr.ClipRbl); sb.Append("W n\n"); }
                    if (rr.Fill.HasValue) { var (fr, fg, fb) = rr.Fill.Value.Rgb01(); sb.Append(F(fr)).Append(' ').Append(F(fg)).Append(' ').Append(F(fb)).Append(" rg\n"); }
                    if (rr.Stroke.HasValue) { var (sr, sg, sb2) = rr.Stroke.Value.Rgb01(); sb.Append(F(sr)).Append(' ').Append(F(sg)).Append(' ').Append(F(sb2)).Append(" RG\n").Append(F(rr.StrokeW)).Append(" w\n"); }
                    RoundedPath(sb, rr.X, rr.Y, rr.Width, rr.Height, rr.Rtl, rr.Rtr, rr.Rbr, rr.Rbl);
                    sb.Append(rr.Fill.HasValue && rr.Stroke.HasValue ? "B\n" : rr.Fill.HasValue ? "f\n" : "S\n");
                    if (wrap) sb.Append("Q\n");
                }
                else if (c is ImageDraw im)
                {
                    float pdfY = _size.Height - (im.Y + im.Height);
                    sb.Append("q ");
                    { string? gsi = Gs((byte)Math.Round(im.Alpha * 255)); if (gsi != null) sb.Append('/').Append(gsi).Append(" gs "); }
                    if (im.Clip)
                    {
                        if (im.Rtl > 0 || im.Rtr > 0 || im.Rbr > 0 || im.Rbl > 0) { RoundedPath(sb, im.ClipX, im.ClipY, im.ClipW, im.ClipH, im.Rtl, im.Rtr, im.Rbr, im.Rbl); sb.Append("W n "); }
                        else { float clipPdfY = _size.Height - (im.ClipY + im.ClipH); sb.Append(F(im.ClipX)).Append(' ').Append(F(clipPdfY)).Append(' ').Append(F(im.ClipW)).Append(' ').Append(F(im.ClipH)).Append(" re W n "); }
                    }
                    sb.Append(F(im.Width)).Append(" 0 0 ").Append(F(im.Height)).Append(' ')
                      .Append(F(im.X)).Append(' ').Append(F(pdfY)).Append(" cm /").Append(imgName[im.Image]).Append(" Do Q\n");
                }
                else if (c is SvgDraw sv)
                    sb.Append(SvgPainter.Paint(sv, _size.Height, faceRes, embRes, alphaRes));
                else if (c is TextRun t && !string.IsNullOrEmpty(t.Text))
                {
                    var (cr, cg, cb) = t.Color.Rgb01();
                    float pdfY = _size.Height - t.BaselineY;
                    string? gs = Gs(t.Color.A);
                    bool needQ = gs != null || t.StrokeWidth > 0f;
                    if (needQ) sb.Append("q ");
                    if (gs != null) sb.Append('/').Append(gs).Append(" gs\n");
                    // Always emit Tc (char spacing): it is a sticky text-state parameter, so a run with
                    // letter-spacing would otherwise leak its spacing into later runs (which reset to 0 here).
                    string tc = F(t.LetterSpacing) + " Tc ";
                    // -webkit-text-stroke: text render mode 2 (fill+stroke) with the stroke colour/width.
                    string stroke = "";
                    if (t.StrokeWidth > 0f) { var (sr, sg2, sb2) = t.StrokeColor.Rgb01(); stroke = F(sr) + " " + F(sg2) + " " + F(sb2) + " RG " + F(t.StrokeWidth) + " w 2 Tr "; }
                    if (t.Emb != null)
                    {
                        sb.Append("BT ").Append(tc).Append(stroke).Append(F(cr)).Append(' ').Append(F(cg)).Append(' ').Append(F(cb)).Append(" rg /")
                          .Append(embRes[t.Emb]).Append(' ').Append(F(t.FontSizePt)).Append(" Tf ")
                          .Append(F(t.X)).Append(' ').Append(F(pdfY)).Append(" Td <").Append(HexGids(t.Emb, t.Text)).Append("> Tj ET\n");
                    }
                    else
                    {
                        sb.Append("BT ").Append(tc).Append(stroke).Append(F(cr)).Append(' ').Append(F(cg)).Append(' ').Append(F(cb)).Append(" rg /")
                          .Append(faceRes[t.Face]).Append(' ').Append(F(t.FontSizePt)).Append(" Tf ")
                          .Append(F(t.X)).Append(' ').Append(F(pdfY)).Append(" Td (").Append(PdfDocument.EscapeLiteral(t.Text)).Append(") Tj ET\n");
                    }
                    if (needQ) sb.Append("Q\n");
                }
                else if (c is TextClipFill tcf && tcf.Runs.Count > 0 && tcf.Fill.Count > 0)
                {
                    // CSS background-clip:text (gradient text). Preferred + reliable path: when the fill is a single
                    // linear/radial gradient, fill the glyphs DIRECTLY with its shading pattern colourspace
                    // (`/Pattern cs /Pn scn` + normal text) — the pattern's own matrix positions the gradient across
                    // the glyphs. (A Tr-7 text clip + pattern fill renders blank in several viewers, incl. the app's.)
                    var grad = tcf.Fill.Count == 1 ? tcf.Fill[0] as GradientFill : null;
                    string? pn = grad != null && patName.TryGetValue(grad, out var pnm) ? pnm : null;
                    if (pn != null)
                    {
                        sb.Append("q /Pattern cs /").Append(pn).Append(" scn\n");
                        foreach (var gr in tcf.Runs)
                        {
                            if (string.IsNullOrEmpty(gr.Text)) continue;
                            float grY = _size.Height - gr.BaselineY;
                            if (gr.Emb != null)
                                sb.Append("BT ").Append(F(gr.LetterSpacing)).Append(" Tc /").Append(embRes[gr.Emb]).Append(' ').Append(F(gr.FontSizePt)).Append(" Tf ")
                                  .Append(F(gr.X)).Append(' ').Append(F(grY)).Append(" Td <").Append(HexGids(gr.Emb, gr.Text)).Append("> Tj ET\n");
                            else
                                sb.Append("BT ").Append(F(gr.LetterSpacing)).Append(" Tc /").Append(faceRes[gr.Face]).Append(' ').Append(F(gr.FontSizePt)).Append(" Tf ")
                                  .Append(F(gr.X)).Append(' ').Append(F(grY)).Append(" Td (").Append(PdfDocument.EscapeLiteral(gr.Text)).Append(") Tj ET\n");
                        }
                        sb.Append("Q\n");
                    }
                    else
                    {
                        // Fallback (image / solid fill): build a clip path from the glyph outlines (render mode 7) and
                        // paint the fill through it.
                        sb.Append("q BT 7 Tr\n");
                        foreach (var gr in tcf.Runs)
                        {
                            if (string.IsNullOrEmpty(gr.Text)) continue;
                            float grY = _size.Height - gr.BaselineY;
                            sb.Append(F(gr.LetterSpacing)).Append(" Tc ");
                            if (gr.Emb != null)
                                sb.Append('/').Append(embRes[gr.Emb]).Append(' ').Append(F(gr.FontSizePt)).Append(" Tf 1 0 0 1 ")
                                  .Append(F(gr.X)).Append(' ').Append(F(grY)).Append(" Tm <").Append(HexGids(gr.Emb, gr.Text)).Append("> Tj\n");
                            else
                                sb.Append('/').Append(faceRes[gr.Face]).Append(' ').Append(F(gr.FontSizePt)).Append(" Tf 1 0 0 1 ")
                                  .Append(F(gr.X)).Append(' ').Append(F(grY)).Append(" Tm (").Append(PdfDocument.EscapeLiteral(gr.Text)).Append(") Tj\n");
                        }
                        sb.Append("ET\n");
                        EmitCommands(sb, tcf.Fill, faceRes, imgName, patName, embRes, alphaRes, smaskName, blendGs);
                        sb.Append("Q\n");
                    }
                }
                else if (c is BlendGroup bgc)
                {
                    sb.Append("q ");
                    if (blendGs.TryGetValue(bgc.Mode, out var bn)) sb.Append('/').Append(bn).Append(" gs ");
                    EmitCommands(sb, bgc.Sub, faceRes, imgName, patName, embRes, alphaRes, smaskName, blendGs);
                    sb.Append("Q\n");
                }
                else if (c is OpacityGroup og)
                {
                    // True group opacity: draw the group's transparency-group Form XObject once at the group alpha.
                    sb.Append("q ");
                    string? gso = Gs((byte)Math.Round(og.Alpha * 255));
                    if (gso != null) sb.Append('/').Append(gso).Append(" gs ");
                    if (_formNames != null && _formNames.TryGetValue(og, out var fmn)) sb.Append('/').Append(fmn).Append(" Do");
                    sb.Append(" Q\n");
                }
                else if (c is TransformGroup tg)
                {
                    var f = TransformCtm(tg);
                    sb.Append("q ").Append(F(f[0])).Append(' ').Append(F(f[1])).Append(' ').Append(F(f[2])).Append(' ')
                      .Append(F(f[3])).Append(' ').Append(F(f[4])).Append(' ').Append(F(f[5])).Append(" cm\n");
                    EmitCommands(sb, tg.Sub, faceRes, imgName, patName, embRes, alphaRes, smaskName, blendGs);
                    sb.Append("Q\n");
                }
                else if (c is ClipGroup cg)
                {
                    sb.Append("q ");
                    if (cg.Contours != null && cg.Contours.Count > 0)
                    {
                        // clip-path polygon(s): build each closed contour (top-origin → PDF y-up), then W n.
                        foreach (var contour in cg.Contours)
                        {
                            for (int i = 0; i + 1 < contour.Length; i += 2)
                            {
                                float px = contour[i], py = _size.Height - contour[i + 1];
                                sb.Append(F(px)).Append(' ').Append(F(py)).Append(i == 0 ? " m " : " l ");
                            }
                            sb.Append("h ");
                        }
                        sb.Append("W n\n");
                    }
                    else if (cg.Rtl > 0 || cg.Rtr > 0 || cg.Rbr > 0 || cg.Rbl > 0) { RoundedPath(sb, cg.X, cg.Y, cg.Width, cg.Height, cg.Rtl, cg.Rtr, cg.Rbr, cg.Rbl); sb.Append("W n\n"); }
                    else { float pdfY = _size.Height - (cg.Y + cg.Height); sb.Append(F(cg.X)).Append(' ').Append(F(pdfY)).Append(' ').Append(F(cg.Width)).Append(' ').Append(F(cg.Height)).Append(" re W n\n"); }
                    EmitCommands(sb, cg.Sub, faceRes, imgName, patName, embRes, alphaRes, smaskName, blendGs);
                    sb.Append("Q\n");
                }
            }
        }

        /// <summary>Append a rounded-rectangle path (top-origin input → PDF y-up), for fill/stroke/clip.</summary>
        private void RoundedPath(StringBuilder sb, float x, float y, float w, float h, float rtl, float rtr, float rbr, float rbl)
        {
            const float k = 0.5522847498f;
            float mx = Math.Min(w, h) / 2f;
            rtl = Math.Min(rtl, mx); rtr = Math.Min(rtr, mx); rbr = Math.Min(rbr, mx); rbl = Math.Min(rbl, mx);
            float left = x, right = x + w;
            float top = _size.Height - y, bot = _size.Height - (y + h);
            sb.Append(F(left + rtl)).Append(' ').Append(F(top)).Append(" m\n");
            sb.Append(F(right - rtr)).Append(' ').Append(F(top)).Append(" l\n");
            sb.Append(F(right - rtr + rtr * k)).Append(' ').Append(F(top)).Append(' ').Append(F(right)).Append(' ').Append(F(top - rtr + rtr * k)).Append(' ').Append(F(right)).Append(' ').Append(F(top - rtr)).Append(" c\n");
            sb.Append(F(right)).Append(' ').Append(F(bot + rbr)).Append(" l\n");
            sb.Append(F(right)).Append(' ').Append(F(bot + rbr - rbr * k)).Append(' ').Append(F(right - rbr + rbr * k)).Append(' ').Append(F(bot)).Append(' ').Append(F(right - rbr)).Append(' ').Append(F(bot)).Append(" c\n");
            sb.Append(F(left + rbl)).Append(' ').Append(F(bot)).Append(" l\n");
            sb.Append(F(left + rbl - rbl * k)).Append(' ').Append(F(bot)).Append(' ').Append(F(left)).Append(' ').Append(F(bot + rbl - rbl * k)).Append(' ').Append(F(left)).Append(' ').Append(F(bot + rbl)).Append(" c\n");
            sb.Append(F(left)).Append(' ').Append(F(top - rtl)).Append(" l\n");
            sb.Append(F(left)).Append(' ').Append(F(top - rtl + rtl * k)).Append(' ').Append(F(left + rtl - rtl * k)).Append(' ').Append(F(top)).Append(' ').Append(F(left + rtl)).Append(' ').Append(F(top)).Append(" c\n");
            sb.Append("h\n");
        }

        /// <summary>PDF CTM for a CSS transform: conjugate the CSS (top-origin) transform with the page y-flip.</summary>
        private float[] TransformCtm(TransformGroup tg)
        {
            float H = _size.Height, ox = tg.OxGlobal, oy = tg.OyGlobal;
            float[] Y = { 1, 0, 0, -1, 0, H };                       // PDF <-> screen (top-origin) involution
            float[] toO = { 1, 0, 0, 1, ox, oy }, fromO = { 1, 0, 0, 1, -ox, -oy };
            float[] tScreen = Style.StyleComputer.MatMul(toO, Style.StyleComputer.MatMul(tg.Matrix, fromO));
            return Style.StyleComputer.MatMul(Y, Style.StyleComputer.MatMul(tScreen, Y));
        }

        // ---- embedded Type0 / CIDFontType2 font ---------------------------------------------------

        private static int BuildEmbeddedFont(PdfDocument doc, EmbeddedFont emb)
        {
            var face = emb.Face;
            float sc = emb.Scale1000;
            int Sc(int v) => (int)Math.Round(v * sc);

            // Font file. TrueType (glyf) -> FontFile2 / CIDFontType2. CFF OpenType -> FontFile3.
            // A CID-keyed CFF is subset (unused glyphs emptied) and embedded as the bare CFF program
            // (/Subtype /CIDFontType0C), which also drops all the other sfnt tables (cmap/GPOS/…) —
            // together this collapses a multi-MB CJK face to a few KB. Everything else stays whole sfnt.
            bool otf = emb.IsOpenType;
            byte[] raw = face.Data;
            string ffDict, ffKey;
            byte[]? cff = otf && face.IsCidKeyed ? face.CffTable() : null;
            if (cff != null)
            {
                var usedGids = new HashSet<int> { 0 };
                foreach (var cp in emb.UsedCodepoints) { ushort g = face.GlyphId(cp); if (g != 0) usedGids.Add(g); }
                byte[] sub = HtmlPdfNative.Subsetter.Subsetter.SubsetCidCff(cff, usedGids) ?? cff;
                raw = sub;
                ffDict = "/Subtype /CIDFontType0C /Filter /FlateDecode";
                ffKey = "/FontFile3";
            }
            else if (otf)
            {
                ffDict = "/Subtype /OpenType /Filter /FlateDecode";
                ffKey = "/FontFile3";
            }
            else
            {
                // TrueType (glyf): subset to the used glyphs (empty the rest), keeping GID numbering so the
                // CIDFontType2 /W and CIDToGIDMap /Identity need no changes.
                var usedGids = new HashSet<int> { 0 };
                foreach (var cp in emb.UsedCodepoints) { ushort g = face.GlyphId(cp); if (g != 0) usedGids.Add(g); }
                byte[] sub = HtmlPdfNative.Subsetter.Subsetter.SubsetTrueType(raw, usedGids) ?? raw;
                raw = sub;
                ffDict = "/Length1 " + raw.Length + " /Filter /FlateDecode";
                ffKey = "/FontFile2";
            }
            int ffId = doc.Allocate();
            doc.SetStream(ffId, ffDict, Codecs.ZlibCompress(raw));

            int fdId = doc.Allocate();
            doc.Set(fdId,
                "<< /Type /FontDescriptor /FontName /" + emb.BaseName + " /Flags 32 /FontBBox [" +
                Sc(face.XMin) + " " + Sc(face.YMin) + " " + Sc(face.XMax) + " " + Sc(face.YMax) + "]" +
                " /ItalicAngle 0 /Ascent " + Sc(face.Ascent) + " /Descent " + Sc(face.Descent) +
                " /CapHeight " + Sc(face.Ascent) + " /StemV 80 " + ffKey + " " + ffId + " 0 R >>");

            // /W width array keyed by CID (== GID unless a CID-keyed CFF remaps via the charset).
            var wsb = new StringBuilder();
            foreach (var cp in emb.UsedCodepoints)
            {
                ushort gid = face.GlyphId(cp);
                if (gid == 0) continue;
                wsb.Append(face.Cid(gid)).Append(" [").Append(Sc(face.Advance(gid))).Append("] ");
            }

            int tuId = doc.Allocate();
            doc.SetStream(tuId, "", ToUnicodeCMap(emb));

            int cidId = doc.Allocate();
            string cidSubtype = otf ? "/CIDFontType0" : "/CIDFontType2";
            string cidToGid = otf ? "" : " /CIDToGIDMap /Identity";
            doc.Set(cidId,
                "<< /Type /Font /Subtype " + cidSubtype + " /BaseFont /" + emb.BaseName +
                " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>" +
                " /FontDescriptor " + fdId + " 0 R" + cidToGid + " /DW 1000 /W [" + wsb.ToString().Trim() + "] >>");

            int type0Id = doc.Allocate();
            doc.Set(type0Id,
                "<< /Type /Font /Subtype /Type0 /BaseFont /" + emb.BaseName + " /Encoding /Identity-H" +
                " /DescendantFonts [" + cidId + " 0 R] /ToUnicode " + tuId + " 0 R >>");
            return type0Id;
        }

        private static string HexGids(EmbeddedFont emb, string text)
        {
            var sb = new StringBuilder();
            foreach (var cp in EmbeddedFont.Codepoints(text))
            {
                ushort gid = emb.Face.GlyphId(cp);
                sb.Append(emb.Face.Cid(gid).ToString("X4", CultureInfo.InvariantCulture)); // CID (== GID unless CID-keyed CFF)
            }
            return sb.ToString();
        }

        private static byte[] ToUnicodeCMap(EmbeddedFont emb)
        {
            var entries = new List<(ushort gid, int cp)>();
            foreach (var cp in emb.UsedCodepoints)
            {
                ushort gid = emb.Face.GlyphId(cp);
                if (gid != 0) entries.Add((emb.Face.Cid(gid), cp)); // key ToUnicode by the written CID
            }
            var sb = new StringBuilder();
            sb.Append("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n");
            sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
            sb.Append("/CMapName /Adobe-Identity-UCS def /CMapType 2 def\n");
            sb.Append("1 begincodespacerange <0000> <FFFF> endcodespacerange\n");
            for (int i = 0; i < entries.Count; i += 100)
            {
                int n = Math.Min(100, entries.Count - i);
                sb.Append(n).Append(" beginbfchar\n");
                for (int k = 0; k < n; k++)
                {
                    var (gid, cp) = entries[i + k];
                    sb.Append('<').Append(gid.ToString("X4", CultureInfo.InvariantCulture)).Append("> <").Append(Utf16BeHex(cp)).Append(">\n");
                }
                sb.Append("endbfchar\n");
            }
            sb.Append("endcmap CMapName currentdict /CMap defineresource pop end end");
            return PdfDocument.Latin1(sb.ToString());
        }

        private static string Utf16BeHex(int cp)
        {
            if (cp <= 0xFFFF) return cp.ToString("X4", CultureInfo.InvariantCulture);
            cp -= 0x10000;
            int hi = 0xD800 + (cp >> 10), lo = 0xDC00 + (cp & 0x3FF);
            return hi.ToString("X4", CultureInfo.InvariantCulture) + lo.ToString("X4", CultureInfo.InvariantCulture);
        }

        // ---- gradient (PDF axial shading, type 2) -------------------------------------------------

        /// <summary>Resolve any absolute-length stop positions (PosPx, in pt) to fractions of this gradient's extent —
        /// the axial half-length for linear, the radius for radial. Enables hard-stop cutouts like
        /// <c>radial-gradient(circle at 0 0, transparent 0 24px, #fff7ed 25px)</c>.</summary>
        private List<Css.GradientStop> ResolveStops(GradientFill g)
        {
            var stops = g.Gradient.Stops;
            bool anyPx = false; foreach (var s in stops) if (s.PosPx.HasValue) { anyPx = true; break; }
            if (!anyPx) return stops;
            float extent;
            if (g.Gradient.Radial) { RadialGeometry(g, out _, out _, out float rx, out _); extent = rx > 0 ? rx : 1f; }
            else
            {
                double rad = g.Gradient.AngleDeg * Math.PI / 180.0;
                double Lm = (Math.Abs(g.Width * Math.Sin(rad)) + Math.Abs(g.Height * Math.Cos(rad))) / 2.0;
                extent = (float)(2 * Lm); if (extent <= 0) extent = 1f;
            }
            // repeating-linear/radial-gradient: TILE the px-positioned stop pattern (period = last−first px) across the
            // whole gradient extent [0,1], so e.g. `repeating-linear-gradient(45deg, blue 0 5px, #cfe0ee 5px 10px)`
            // draws stripes across the box instead of one period then a flat last colour.
            if (g.Gradient.Repeating && stops.Count >= 2 && stops[0].PosPx.HasValue && stops[stops.Count - 1].PosPx.HasValue)
            {
                float firstPx = stops[0].PosPx!.Value, periodPx = stops[stops.Count - 1].PosPx!.Value - firstPx;
                if (periodPx > 0.01f)
                {
                    var rep = new List<Css.GradientStop>();
                    float lastFrac = 0f; bool done = false;
                    for (int k = 0; k < 4000 && !done; k++)
                        foreach (var s in stops)
                        {
                            float frac = ((s.PosPx ?? 0f) + k * periodPx) / extent;
                            if (frac >= 1f) { rep.Add(new Css.GradientStop { Color = s.Color, Pos = 1f }); done = true; break; }
                            float p = Math.Max(lastFrac, Math.Max(0f, frac));
                            rep.Add(new Css.GradientStop { Color = s.Color, Pos = p }); lastFrac = p;
                        }
                    if (rep.Count > 0)
                    {
                        if (rep[0].Pos > 0f) rep.Insert(0, new Css.GradientStop { Color = rep[0].Color, Pos = 0f });
                        if (rep[rep.Count - 1].Pos < 1f) rep.Add(new Css.GradientStop { Color = rep[rep.Count - 1].Color, Pos = 1f });
                    }
                    return rep;
                }
            }
            var outp = new List<Css.GradientStop>(stops.Count + 2);
            float last = 0f;
            foreach (var s in stops)
            {
                float pos = s.PosPx.HasValue ? s.PosPx.Value / extent : s.Pos;
                pos = Math.Max(last, Math.Min(1f, Math.Max(0f, pos))); last = pos;
                outp.Add(new Css.GradientStop { Color = s.Color, Pos = pos });
            }
            // Pad to span [0,1]: a px-positioned last stop (e.g. cream at 0.07) must extend its colour flat to the
            // edge — otherwise the PDF stitching function stretches the final transition across [pos,1] (no hard stop).
            if (outp.Count > 0)
            {
                if (outp[0].Pos > 0f) outp.Insert(0, new Css.GradientStop { Color = outp[0].Color, Pos = 0f });
                var lastS = outp[outp.Count - 1];
                if (lastS.Pos < 1f) outp.Add(new Css.GradientStop { Color = lastS.Color, Pos = 1f });
            }
            return outp;
        }

        private int BuildGradientPattern(PdfDocument doc, GradientFill g)
        {
            var grad = g.Gradient;
            int funcId = BuildGradientFunction(doc, FixTransparentColorRgb(ResolveStops(g)));

            if (grad.Radial) return BuildRadialPattern(doc, g, funcId);

            // Linear: gradient-line endpoints in PDF page space (y up).
            double rad = grad.AngleDeg * Math.PI / 180.0;
            double dxl = Math.Sin(rad), dyl = -Math.Cos(rad); // CSS y-down direction
            double cxl = g.X + g.Width / 2.0, cyTop = g.Y + g.Height / 2.0;
            double L = (Math.Abs(g.Width * Math.Sin(rad)) + Math.Abs(g.Height * Math.Cos(rad))) / 2.0;
            double x0 = cxl - dxl * L, y0 = _size.Height - (cyTop - dyl * L);
            double x1 = cxl + dxl * L, y1 = _size.Height - (cyTop + dyl * L);

            int patId = doc.Allocate();
            doc.Set(patId,
                "<< /Type /Pattern /PatternType 2 /Matrix [1 0 0 1 0 0] /Shading << /ShadingType 2 /ColorSpace /DeviceRGB" +
                " /Coords [" + F((float)x0) + " " + F((float)y0) + " " + F((float)x1) + " " + F((float)y1) + "]" +
                " /Extend [true true] /Function " + funcId + " 0 R >> >>");
            return patId;
        }

        // Radial gradient center/radius/ellipse-matrix (PDF space). Circle at CSS center; radius per extent keyword.
        private void RadialGeometry(GradientFill g, out float cx, out float cyPdf, out float rx, out string matrix)
        {
            var grad = g.Gradient;
            cx = g.X + grad.CxFrac * g.Width;
            float cyTop = g.Y + grad.CyFrac * g.Height;
            float xF = Math.Max(cx - g.X, g.X + g.Width - cx), xC = Math.Min(cx - g.X, g.X + g.Width - cx);
            float yF = Math.Max(cyTop - g.Y, g.Y + g.Height - cyTop), yC = Math.Min(cyTop - g.Y, g.Y + g.Height - cyTop);
            float ry;
            switch (grad.Extent)
            {
                case "closest-side": rx = xC; ry = yC; break;
                case "farthest-side": rx = xF; ry = yF; break;
                case "closest-corner": rx = xC * 1.41421356f; ry = yC * 1.41421356f; break;
                default: rx = xF * 1.41421356f; ry = yF * 1.41421356f; break;
            }
            if (grad.Circle)
            {
                float rCorner = (float)Math.Sqrt((double)xF * xF + (double)yF * yF);
                float rCornerC = (float)Math.Sqrt((double)xC * xC + (double)yC * yC);
                rx = ry = grad.Extent switch { "closest-side" => Math.Min(xC, yC), "farthest-side" => Math.Max(xF, yF), "closest-corner" => rCornerC, _ => rCorner };
            }
            if (rx <= 0) rx = 1; if (ry <= 0) ry = 1;
            cyPdf = _size.Height - cyTop;
            float sy = ry / rx;
            matrix = grad.Circle || Math.Abs(sy - 1f) < 1e-4f ? "[1 0 0 1 0 0]" : "[1 0 0 " + F(sy) + " 0 " + F(cyPdf * (1f - sy)) + "]";
        }

        // Radial gradient -> PDF ShadingType 3 pattern (ellipse via the /Matrix from RadialGeometry).
        private int BuildRadialPattern(PdfDocument doc, GradientFill g, int funcId)
        {
            RadialGeometry(g, out float cx, out float cyPdf, out float rx, out string matrix);
            int patId = doc.Allocate();
            doc.Set(patId,
                "<< /Type /Pattern /PatternType 2 /Matrix " + matrix + " /Shading << /ShadingType 3 /ColorSpace /DeviceRGB" +
                " /Coords [" + F(cx) + " " + F(cyPdf) + " 0 " + F(cx) + " " + F(cyPdf) + " " + F(rx) + "]" +
                " /Extend [true true] /Function " + funcId + " 0 R >> >>");
            return patId;
        }

        private int BuildGradientFunction(PdfDocument doc, List<Css.GradientStop> stops)
        {
            int funcId;
            if (stops.Count <= 2)
            {
                funcId = doc.Allocate();
                doc.Set(funcId, Type2Func(stops[0].Color, stops[stops.Count - 1].Color));
            }
            else
            {
                var subIds = new List<int>();
                for (int i = 0; i < stops.Count - 1; i++)
                {
                    int sid = doc.Allocate();
                    doc.Set(sid, Type2Func(stops[i].Color, stops[i + 1].Color));
                    subIds.Add(sid);
                }
                var funcs = new StringBuilder();
                foreach (var sid in subIds) funcs.Append(sid).Append(" 0 R ");
                var bounds = new StringBuilder();
                for (int i = 1; i < stops.Count - 1; i++) bounds.Append(F(stops[i].Pos)).Append(' ');
                var encode = new StringBuilder();
                for (int i = 0; i < subIds.Count; i++) encode.Append("0 1 ");
                funcId = doc.Allocate();
                doc.Set(funcId, "<< /FunctionType 3 /Domain [0 1] /Functions [" + funcs.ToString().Trim() +
                                "] /Bounds [" + bounds.ToString().Trim() + "] /Encode [" + encode.ToString().Trim() + "] >>");
            }
            return funcId;
        }

        /// <summary>For the COLOUR shading only: a fully-transparent stop (rgba x,x,x,0 — usually transparent BLACK)
        /// must not contribute its RGB, else e.g. blue→transparent fades through GREY. Give each transparent stop the
        /// nearest opaque neighbour's RGB (alpha is carried by the separate soft-mask, so keep A=0 here).</summary>
        private static List<Css.GradientStop> FixTransparentColorRgb(List<Css.GradientStop> stops)
        {
            bool any = false; foreach (var s in stops) if (s.Color.A == 0) { any = true; break; }
            if (!any) return stops;
            var outp = new List<Css.GradientStop>(stops.Count);
            for (int i = 0; i < stops.Count; i++)
            {
                var c = stops[i].Color;
                if (c.A == 0)
                {
                    for (int d = 1; d < stops.Count; d++)
                    {
                        if (i - d >= 0 && stops[i - d].Color.A != 0) { c = new Render.Color(stops[i - d].Color.R, stops[i - d].Color.G, stops[i - d].Color.B, 0); break; }
                        if (i + d < stops.Count && stops[i + d].Color.A != 0) { c = new Render.Color(stops[i + d].Color.R, stops[i + d].Color.G, stops[i + d].Color.B, 0); break; }
                    }
                }
                outp.Add(new Css.GradientStop { Color = c, Pos = stops[i].Pos });
            }
            return outp;
        }

        private static string Type2Func(Render.Color c0, Render.Color c1)
        {
            var (r0, g0, b0) = c0.Rgb01();
            var (r1, g1, b1) = c1.Rgb01();
            return "<< /FunctionType 2 /Domain [0 1] /C0 [" + F(r0) + " " + F(g0) + " " + F(b0) + "] /C1 [" +
                   F(r1) + " " + F(g1) + " " + F(b1) + "] /N 1 >>";
        }

        // True when the gradient's stops have VARYING alpha (needs a soft-mask; uniform alpha uses ExtGState ca).
        private static bool HasVaryingAlpha(GradientFill g)
        {
            var st = g.Gradient?.Stops;
            if (st == null || st.Count == 0) return false;
            byte a = st[0].Color.A;
            foreach (var s in st) if (s.Color.A != a) return true;
            return false;
        }

        private int BuildAlphaFunction(PdfDocument doc, List<Css.GradientStop> stops)
        {
            string Gray(Render.Color c0, Render.Color c1) =>
                "<< /FunctionType 2 /Domain [0 1] /C0 [" + F(c0.A / 255f) + "] /C1 [" + F(c1.A / 255f) + "] /N 1 >>";
            if (stops.Count <= 2) { int id = doc.Allocate(); doc.Set(id, Gray(stops[0].Color, stops[stops.Count - 1].Color)); return id; }
            var subIds = new List<int>();
            for (int i = 0; i < stops.Count - 1; i++) { int sid = doc.Allocate(); doc.Set(sid, Gray(stops[i].Color, stops[i + 1].Color)); subIds.Add(sid); }
            var funcs = new StringBuilder(); foreach (var sid in subIds) funcs.Append(sid).Append(" 0 R ");
            var bounds = new StringBuilder(); for (int i = 1; i < stops.Count - 1; i++) bounds.Append(F(stops[i].Pos)).Append(' ');
            var encode = new StringBuilder(); for (int i = 0; i < subIds.Count; i++) encode.Append("0 1 ");
            int fid = doc.Allocate();
            doc.Set(fid, "<< /FunctionType 3 /Domain [0 1] /Functions [" + funcs.ToString().Trim() + "] /Bounds [" + bounds.ToString().Trim() + "] /Encode [" + encode.ToString().Trim() + "] >>");
            return fid;
        }

        /// <summary>Build a /Luminosity soft-mask ExtGState for a gradient's per-stop alpha (a grayscale shading of
        /// the alpha channel inside a transparency-group Form XObject), for both linear and radial.</summary>
        private int BuildGradientSoftMask(PdfDocument doc, GradientFill g)
        {
            var grad = g.Gradient;
            int alphaFunc = BuildAlphaFunction(doc, ResolveStops(g));
            string shDict, formContent = "/Sh sh";
            if (!grad.Radial)
            {
                double rad = grad.AngleDeg * Math.PI / 180.0;
                double dxl = Math.Sin(rad), dyl = -Math.Cos(rad);
                double cxl = g.X + g.Width / 2.0, cyTop = g.Y + g.Height / 2.0;
                double Lm = (Math.Abs(g.Width * Math.Sin(rad)) + Math.Abs(g.Height * Math.Cos(rad))) / 2.0;
                double x0 = cxl - dxl * Lm, y0 = _size.Height - (cyTop - dyl * Lm);
                double x1 = cxl + dxl * Lm, y1 = _size.Height - (cyTop + dyl * Lm);
                shDict = "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [" + F((float)x0) + " " + F((float)y0) + " " + F((float)x1) + " " + F((float)y1) + "] /Extend [true true] /Function " + alphaFunc + " 0 R >>";
            }
            else
            {
                RadialGeometry(g, out float cx, out float cyPdf, out float rx, out string matrix);
                shDict = "<< /ShadingType 3 /ColorSpace /DeviceGray /Coords [" + F(cx) + " " + F(cyPdf) + " 0 " + F(cx) + " " + F(cyPdf) + " " + F(rx) + "] /Extend [true true] /Function " + alphaFunc + " 0 R >>";
                if (matrix != "[1 0 0 1 0 0]") formContent = matrix.Trim('[', ']') + " cm /Sh sh"; // apply the ellipse matrix in the form
            }
            int shId = doc.Allocate();
            doc.Set(shId, shDict);
            int formId = doc.Allocate();
            string fdict = "/Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 " + F(_size.Width) + " " + F(_size.Height) + "]" +
                           " /Group << /Type /Group /S /Transparency /CS /DeviceGray >> /Resources << /Shading << /Sh " + shId + " 0 R >> >>";
            doc.SetStream(formId, fdict, System.Text.Encoding.ASCII.GetBytes(formContent));
            int gsId = doc.Allocate();
            doc.Set(gsId, "<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G " + formId + " 0 R /BC [0] >> >>");
            return gsId;
        }

        private static float[] ShiftPts(float[] p, float shift) { var q = (float[])p.Clone(); for (int k = 1; k < q.Length; k += 2) q[k] -= shift; return q; }
        private static float[] ScalePts(float[] p, float s) { var q = (float[])p.Clone(); for (int k = 0; k < q.Length; k++) q[k] *= s; return q; }

        private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
