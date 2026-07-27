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
Sources live in [(https://pdfmaker.ink/web/)](https://pdfmaker.ink/web/).


<table>
  <tr>
    <td width="50%" align="center">
    <img width="957" height="1349" alt="Example16" src="https://github.com/user-attachments/assets/0055fdc5-6c5e-4fad-82e2-68ee95c528e7" />
      <br><b>Analytics Dashboard</b><br><sub>Dark mode · gradients · KPI cards</sub>
    </td>
    <td width="50%" align="center">
      <img width="959" height="1351" alt="Example9" src="https://github.com/user-attachments/assets/a764ea15-fc8d-47ef-9a4b-f2e5e3795e73" />
      <br><b>Professional Invoice</b><br><sub>Clean tables · business layout</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" align="center">
      <img width="958" height="866" alt="Example25" src="https://github.com/user-attachments/assets/02916462-edf5-4e53-9f9f-ea951b43e8df" />
      <br><b>Creative Vision</b><br><sub>Bold type · vivid gradients</sub>
    </td>
    <td width="50%" align="center">
      <img width="1110" height="944" alt="Example28" src="https://github.com/user-attachments/assets/d61551f2-72de-4ef6-b631-6f509cc92a8d" />
      <br><b>Annual Performance Report</b><br><sub>Charts · tables · corporate style</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" align="center">
      <img width="954" height="1350" alt="Example18" src="https://github.com/user-attachments/assets/fb5c87b2-dd23-409a-966c-23362c35dd2e" />
      <br><b>Arabic (RTL)</b><br><sub>Right‑to‑left shaping &amp; bidi</sub>
    </td>
    <td width="50%" align="center">
      <img width="1112" height="1367" alt="Example17" src="https://github.com/user-attachments/assets/09ada836-2669-4dc3-a609-4a0e99c0a00c" />
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

HtmlPdfNative is the .NET port of **[PDFMaker]([..](https://github.com/sorainnosia/PDFMaker/README.md)** (Rust). See the parent project for the
website, the browser (WASM) app, and the Windows/Android downloads.

<div align="center"><sub>HTML to beautiful PDF, in managed .NET — anywhere.</sub></div>
