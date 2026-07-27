<div align="center">

# HtmlPdfNative

### HTML &amp; CSS → PDF for .NET, powered by its own native rendering engine

**No headless browser. No Chromium. No WebView. No native dependencies.**

HtmlPdfNative is a **pure‑managed .NET port** of the Rust [`pdfmaker`](../README.md) engine. It parses
HTML, computes the CSS cascade, lays out the page, and draws every glyph, gradient, and shape itself —
so it's small, deterministic, fully self‑contained, and produces identical output everywhere .NET runs.

Multi‑targets **`netstandard2.0` · `net6.0` · `net8.0`** → runs on .NET Framework 4.6.1+, .NET Core/5+,
Mono, Unity, Xamarin, and modern .NET.

</div>

---

## ✨ Why HtmlPdfNative

Most HTML‑to‑PDF libraries for .NET shell out to a headless browser (Chromium, Puppeteer, wkhtmltopdf)
or wrap a native binary. HtmlPdfNative is different — it's a **real, standalone rendering engine written
entirely in C#**:

- 🧩 **Own engine, pure managed** — no browser, no Node.js, no unmanaged runtime to deploy.
- ⚡ **Fast & lightweight** — a single DLL; deterministic, pixel‑consistent output.
- 🌍 **Runs everywhere .NET does** — server, desktop, container, or wherever `netstandard2.0` is supported.
- 🔒 **100% offline** — your documents never leave the process.

## 🖼 Showcase

Documents rendered entirely by HtmlPdfNative — straight from HTML/CSS, no browser involved.
Sources live in [`../tauri_web/ui/content/`](../tauri_web/ui/content/).

<table>
  <tr>
    <td width="50%" align="center">
      <a href="../tauri_web/ui/content/Example16.html"><img src="../docs/screenshots/analytics-dashboard.png" alt="Analytics dashboard (dark mode)"></a>
      <br><b>Analytics Dashboard</b><br><sub>Dark mode · gradients · KPI cards</sub>
    </td>
    <td width="50%" align="center">
      <a href="../tauri_web/ui/content/Example9.html"><img src="../docs/screenshots/invoice.png" alt="Professional invoice"></a>
      <br><b>Professional Invoice</b><br><sub>Clean tables · business layout</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" align="center">
      <a href="../tauri_web/ui/content/Example25.html"><img src="../docs/screenshots/creative-vision.png" alt="Creative vision report"></a>
      <br><b>Creative Vision</b><br><sub>Bold type · vivid gradients</sub>
    </td>
    <td width="50%" align="center">
      <a href="../tauri_web/ui/content/Example29.html"><img src="../docs/screenshots/performance-report.png" alt="Business performance report"></a>
      <br><b>Performance Report</b><br><sub>Charts · tables · corporate style</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" align="center">
      <a href="../tauri_web/ui/content/Example18.html"><img src="../docs/screenshots/arabic-rtl.png" alt="Arabic right-to-left document"></a>
      <br><b>Arabic (RTL)</b><br><sub>Right‑to‑left shaping &amp; bidi</sub>
    </td>
    <td width="50%" align="center">
      <a href="../tauri_web/ui/content/Example17.html"><img src="../docs/screenshots/chinese-cjk.png" alt="Chinese CJK report"></a>
      <br><b>Chinese (CJK)</b><br><sub>Embedded CJK fonts</sub>
    </td>
  </tr>
</table>

> The screenshots above are the exact PDFs HtmlPdfNative produces from those `.html` sources.

## 🧩 Features

| Category | What's supported |
|---|---|
| **Layout** | Block / inline / inline‑block, **Flexbox**, **CSS Grid**, **multi‑column**, `position` (relative/absolute/fixed), `float`, `overflow` clipping |
| **Typography** | Web‑safe + **`@font-face` web fonts**, `font-weight/style/stretch/variant`, `letter/word‑spacing`, `text-align: justify`, `text-shadow`, `text-decoration`, **drop caps (`::first-letter`)**, unitless & explicit `line-height` |
| **Color & Backgrounds** | Named/hex/`rgb()`/`rgba()`, **linear / radial / conic / repeating gradients**, multi‑layer backgrounds, **`background-clip: text` (gradient text)** |
| **Borders & Effects** | `border-radius` (incl. non‑uniform borders), per‑side borders, **`box-shadow`** (soft blur), `opacity`, **`mix-blend-mode`**, **`clip-path`**, **`filter`** (blur, grayscale, sepia…), **`backdrop-filter`** |
| **Transforms** | `translate`, `rotate`, `scale`, `skew`, `matrix`, 3D transforms, `transform-origin` |
| **Tables** | `border-collapse`, `border-spacing`, `colspan`/`rowspan`, striped rows, section (thead/tbody) colour & gradient backgrounds, rounded clipping, repeating headers |
| **Lists & Content** | Many `list-style-type`s, **CSS counters** & generated content (`::before`/`::after`) |
| **Graphics** | Inline **SVG** (paths, gradients, shapes), raster images (PNG/JPEG/GIF/BMP/WebP), full‑color **emoji** |
| **Internationalization** | **CJK** (中文 · 日本語 · 한국어), **Arabic / Hebrew (RTL)** with shaping & bidi, Cyrillic, vertical `writing-mode` |
| **Paged media** | `@page` sizes (A4, Letter, custom…), margins, automatic & forced page breaks, `@media print` |
| **PDF features** | Embedded subsetted fonts, clickable link annotations, password **encryption** |

## 🚀 Quick start

### Library

```csharp
using HtmlPdfNative;

// From strings → PDF bytes
byte[] pdf = HtmlToPdf.Convert(
    "<h1 style='color:#6366f1'>Hello, PDF</h1>",
    css: "h1 { font-family: Arial; }",
    new ConversionOptions { Paper = PaperSize.A4 });
File.WriteAllBytes("hello.pdf", pdf);

// From files → PDF file
HtmlToPdf.ConvertFile("invoice.html", "styles.css", "invoice.pdf",
    new ConversionOptions { Paper = PaperSize.Letter });
```

`ConversionOptions`: `Paper` (`A3` · `A4` · `A5` · `Letter` · `Legal`), explicit `Width`/`Height` (pt),
and `UserPassword`/`OwnerPassword` for AES encryption.

### Command‑line (`htmlpdf` test CLI)

```bash
dotnet run --project tests/HtmlPdfNative.Cli -- -i page.html -c style.css -o page.pdf -s a4
```

```
-i, --input <FILE>          Input HTML or Markdown file (required)
-c, --css <FILE>            Optional external CSS file
-o, --output <FILE>         Output PDF file (default: output.pdf)
-s, --paper-size <SIZE>     a3 | a4 | a5 | letter | legal (default: a4)
    --width / --height <PT>  Explicit page size in points
-e, --encrypt <PASSWORD>    AES-encrypt the output
```

## 🔨 Build

```bash
cd dotnet
dotnet build HtmlPdfNative.sln -c Release
```

The **software rasterizer** (used by `filter: blur()` / `backdrop-filter`) is on by default via SkiaSharp.
Turn it off for a minimal, fully pure‑managed build:

```bash
dotnet build -c Release -p:HtmlPdfUseExternalPackages=false
```

Ship a self‑contained DLL with the fonts embedded (mirrors the Rust binary — one file, no `Assets/` folder):

```bash
dotnet build -c Release -p:HtmlPdfEmbedAssets=true
```

## 📦 Project structure

Two projects — one library, one CLI:

| Project | Purpose |
|---|---|
| [`src/HtmlPdfNative`](src/HtmlPdfNative) | The engine — the whole HTML → cascade → layout → paint → PDF pipeline in a **single assembly**. `Vendor/` holds the ports of crates .NET lacks (PdfWriter, TtfParser, Subsetter, UnicodeBidi, Hyphenation, Shaping, SvgRender, EmojiColr, VariableFonts, Compression). |
| [`tests/HtmlPdfNative.Cli`](tests/HtmlPdfNative.Cli) | The `htmlpdf` test CLI (`net8.0`) that renders any `.html`/`.md` to PDF. |

Shared build settings (multi‑targeting, `netstandard2.0` polyfills) live in
[`Directory.Build.props`](Directory.Build.props).

## 📎 Dependencies

Pure‑managed by default: **AngleSharp** (HTML parse/DOM), **Markdig** (Markdown), **SixLabors.ImageSharp**
(raster decode). The optional native‑backed rasterizer adds **SkiaSharp** (for `filter`/`backdrop-filter`).

## 🔗 About

HtmlPdfNative is the .NET port of **[PDFMaker](../README.md)** (Rust). See the parent project for the
website, the browser (WASM) app, and the Windows/Android downloads — and [`PLAN.md`](PLAN.md) for the
port's crate map and design notes.

<div align="center"><sub>HTML to beautiful PDF, in managed .NET — anywhere.</sub></div>
