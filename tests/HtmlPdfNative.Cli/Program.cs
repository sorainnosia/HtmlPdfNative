using System;
using System.Collections.Generic;
using System.Globalization;
using HtmlPdfNative;

// Test CLI for HtmlPdfNative. Mirrors the Rust pdfmaker CLI's core switches:
//   -i, --input        Input HTML/Markdown file (required)
//   -c, --css          Optional external CSS file
//   -o, --output       Output PDF file (default: output.pdf)
//   -s, --paper-size   a3 | a4 | a5 | letter | legal   (default: a4)
//       --width        Page width in points  (overrides paper size)
//       --height       Page height in points (overrides paper size)
//   -e, --encrypt      User password (AES-encrypt the PDF)
//   -h, --help         Show usage
//
// Example:  htmlpdf -i page.html -c style.css -o page.pdf -s a4

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var opts = ParseArgs(args, out string? input, out string? css, out string output);
            if (input == null)
            {
                PrintUsage();
                return args.Length == 0 ? 0 : 2;
            }

            Console.WriteLine($"Converting {input} -> {output} ({opts.Paper}" +
                (opts.Width.HasValue || opts.Height.HasValue
                    ? $", {opts.Width?.ToString(CultureInfo.InvariantCulture) ?? "auto"}x{opts.Height?.ToString(CultureInfo.InvariantCulture) ?? "auto"}pt"
                    : "") + ")...");

            HtmlToPdf.ConvertFile(input, css, output, opts);

            Console.WriteLine($"PDF saved to {output}");
            return 0;
        }
        catch (NotImplementedException ex)
        {
            // Expected while the engine is being ported — surface which stage is pending.
            Console.Error.WriteLine("Not yet implemented: " + ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static ConversionOptions ParseArgs(string[] args, out string? input, out string? css, out string output)
    {
        input = null; css = null; output = "output.pdf";
        var opts = new ConversionOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-i": case "--input": input = Next(args, ref i, a); break;
                case "-c": case "--css": css = Next(args, ref i, a); break;
                case "-o": case "--output": output = Next(args, ref i, a); break;
                case "-s": case "--paper-size": opts.Paper = ParsePaper(Next(args, ref i, a)); break;
                case "--width": opts.Width = ParseFloat(Next(args, ref i, a)); break;
                case "--height": opts.Height = ParseFloat(Next(args, ref i, a)); break;
                case "-e": case "--encrypt": opts.UserPassword = Next(args, ref i, a); break;
                case "-h": case "--help": input = null; return opts;
                default:
                    if (a.StartsWith("-", StringComparison.Ordinal))
                        throw new ArgumentException($"unexpected argument '{a}'");
                    // bare positional -> treat as input if not set yet
                    if (input == null) input = a; else output = a;
                    break;
            }
        }
        return opts;
    }

    private static string Next(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"missing value for '{flag}'");
        return args[++i];
    }

    private static float ParseFloat(string s) =>
        float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static PaperSize ParsePaper(string s)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "a3": return PaperSize.A3;
            case "a4": return PaperSize.A4;
            case "a5": return PaperSize.A5;
            case "letter": return PaperSize.Letter;
            case "legal": return PaperSize.Legal;
            default: throw new ArgumentException($"unknown paper size '{s}' (a3|a4|a5|letter|legal)");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(@"htmlpdf — HtmlPdfNative test CLI

USAGE:
    htmlpdf -i <input.html> [-c <style.css>] [-o <output.pdf>] [-s a4]

OPTIONS:
    -i, --input <FILE>        Input HTML or Markdown file (required)
    -c, --css <FILE>          Optional external CSS file
    -o, --output <FILE>       Output PDF file (default: output.pdf)
    -s, --paper-size <SIZE>   a3 | a4 | a5 | letter | legal (default: a4)
        --width <PT>          Page width in points (overrides paper size)
        --height <PT>         Page height in points (overrides paper size)
    -e, --encrypt <PASSWORD>  AES-encrypt the output with a user password
    -h, --help                Show this help");
    }
}
