using System.Collections.Generic;
using System.Text;

namespace HtmlPdfNative.Layout
{
    /// <summary>Arabic contextual shaping: maps base Arabic letters (U+0621–064A) to their Presentation Forms-B
    /// glyphs (U+FE70–FEFF) based on joining context, so fonts without a shaping engine still render connected
    /// script. Applied in LOGICAL order (before the bidi visual reversal). Lam-alef ligatures + marks-as-
    /// transparent handled; anything else passes through unchanged.</summary>
    public static class ArabicShaper
    {
        // base → (dual?, isolated, final, initial, medial). For right-joining letters initial=isolated, medial=final.
        private struct Form { public bool Dual; public char Iso, Fin, Ini, Med; }
        private static readonly Dictionary<char, Form> Table = Build();

        private static Form D(int iso, int fin, int ini, int med) => new Form { Dual = true, Iso = (char)iso, Fin = (char)fin, Ini = (char)ini, Med = (char)med };
        private static Form R(int iso, int fin) => new Form { Dual = false, Iso = (char)iso, Fin = (char)fin, Ini = (char)iso, Med = (char)fin };

        private static Dictionary<char, Form> Build() => new Dictionary<char, Form>
        {
            [(char)0x0622] = R(0xFE81, 0xFE82), [(char)0x0623] = R(0xFE83, 0xFE84), [(char)0x0624] = R(0xFE85, 0xFE86),
            [(char)0x0625] = R(0xFE87, 0xFE88), [(char)0x0626] = D(0xFE89, 0xFE8A, 0xFE8B, 0xFE8C), [(char)0x0627] = R(0xFE8D, 0xFE8E),
            [(char)0x0628] = D(0xFE8F, 0xFE90, 0xFE91, 0xFE92), [(char)0x0629] = R(0xFE93, 0xFE94),
            [(char)0x062A] = D(0xFE95, 0xFE96, 0xFE97, 0xFE98), [(char)0x062B] = D(0xFE99, 0xFE9A, 0xFE9B, 0xFE9C),
            [(char)0x062C] = D(0xFE9D, 0xFE9E, 0xFE9F, 0xFEA0), [(char)0x062D] = D(0xFEA1, 0xFEA2, 0xFEA3, 0xFEA4),
            [(char)0x062E] = D(0xFEA5, 0xFEA6, 0xFEA7, 0xFEA8), [(char)0x062F] = R(0xFEA9, 0xFEAA), [(char)0x0630] = R(0xFEAB, 0xFEAC),
            [(char)0x0631] = R(0xFEAD, 0xFEAE), [(char)0x0632] = R(0xFEAF, 0xFEB0),
            [(char)0x0633] = D(0xFEB1, 0xFEB2, 0xFEB3, 0xFEB4), [(char)0x0634] = D(0xFEB5, 0xFEB6, 0xFEB7, 0xFEB8),
            [(char)0x0635] = D(0xFEB9, 0xFEBA, 0xFEBB, 0xFEBC), [(char)0x0636] = D(0xFEBD, 0xFEBE, 0xFEBF, 0xFEC0),
            [(char)0x0637] = D(0xFEC1, 0xFEC2, 0xFEC3, 0xFEC4), [(char)0x0638] = D(0xFEC5, 0xFEC6, 0xFEC7, 0xFEC8),
            [(char)0x0639] = D(0xFEC9, 0xFECA, 0xFECB, 0xFECC), [(char)0x063A] = D(0xFECD, 0xFECE, 0xFECF, 0xFED0),
            [(char)0x0641] = D(0xFED1, 0xFED2, 0xFED3, 0xFED4), [(char)0x0642] = D(0xFED5, 0xFED6, 0xFED7, 0xFED8),
            [(char)0x0643] = D(0xFED9, 0xFEDA, 0xFEDB, 0xFEDC), [(char)0x0644] = D(0xFEDD, 0xFEDE, 0xFEDF, 0xFEE0),
            [(char)0x0645] = D(0xFEE1, 0xFEE2, 0xFEE3, 0xFEE4), [(char)0x0646] = D(0xFEE5, 0xFEE6, 0xFEE7, 0xFEE8),
            [(char)0x0647] = D(0xFEE9, 0xFEEA, 0xFEEB, 0xFEEC), [(char)0x0648] = R(0xFEED, 0xFEEE),
            [(char)0x0649] = R(0xFEEF, 0xFEF0), [(char)0x064A] = D(0xFEF1, 0xFEF2, 0xFEF3, 0xFEF4),
        };

        private static bool IsTransparent(char c) { int u = c; return (u >= 0x064B && u <= 0x065F) || u == 0x0670 || (u >= 0x0610 && u <= 0x061A); }

        public static bool HasArabic(string s) { foreach (var c in s) if (Table.ContainsKey(c)) return true; return false; }

        public static string Shape(string s)
        {
            if (!HasArabic(s)) return s;
            var outc = s.ToCharArray();
            for (int i = 0; i < s.Length; i++)
            {
                if (!Table.TryGetValue(s[i], out var f)) continue;
                // Previous non-transparent letter joins to us on our right side?
                bool prevJoins = false;
                for (int p = i - 1; p >= 0; p--)
                {
                    if (IsTransparent(s[p])) continue;
                    if (Table.TryGetValue(s[p], out var pf) && pf.Dual) prevJoins = true; // prev joins-left only if dual
                    break;
                }
                // Next non-transparent letter can join to us on our left side (we must be dual)?
                bool nextJoins = false;
                if (f.Dual)
                    for (int q = i + 1; q < s.Length; q++)
                    {
                        if (IsTransparent(s[q])) continue;
                        if (Table.ContainsKey(s[q])) nextJoins = true; // any joining letter accepts a right connection
                        break;
                    }
                outc[i] = (prevJoins && nextJoins) ? f.Med : prevJoins ? f.Fin : nextJoins ? f.Ini : f.Iso;
            }
            return HandleLamAlef(new string(outc), s);
        }

        // Collapse lam(final/initial) + alef into the lam-alef ligature (FEFB isolated / FEFC final).
        private static string HandleLamAlef(string shaped, string orig)
        {
            var sb = new StringBuilder(shaped.Length);
            for (int i = 0; i < orig.Length; i++)
            {
                if (orig[i] == 0x0644 && i + 1 < orig.Length && IsAlef(orig[i + 1]))
                {
                    bool joinsPrev = shaped[i] == (char)0xFEDE || shaped[i] == (char)0xFEE0; // lam was final/medial
                    sb.Append((char)(joinsPrev ? 0xFEFC : 0xFEFB));
                    i++; // consume the alef
                    continue;
                }
                sb.Append(shaped[i]);
            }
            return sb.ToString();
        }

        private static bool IsAlef(char c) { int u = c; return u == 0x0627 || u == 0x0622 || u == 0x0623 || u == 0x0625; }
    }
}
