using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ArtistFixer
{
    public enum SepKind { List, Featuring, Custom }

    /// <summary>One separator found inside an artist string.</summary>
    public class Sep
    {
        public int Start;
        public int Length;
        public string Name;      // short label, e.g. "&", "feat."
        public SepKind Kind;
        public bool Enabled;     // false = ignored (part of an artist name)
        public bool Protected;   // disabled because it sits inside a known single-artist name
        public int End { get { return Start + Length; } }

        /// <summary>True if the separator's visible part (without surrounding spaces) lies inside [from, to).</summary>
        public bool CoreWithin(string text, int from, int to)
        {
            int s = Start, e = End;
            while (s < e && char.IsWhiteSpace(text[s])) s++;
            while (e > s && char.IsWhiteSpace(text[e - 1])) e--;
            return s >= from && e <= to;
        }
    }

    /// <summary>A separator choice offered in the dropdowns.</summary>
    public class SepChoice
    {
        public const string Keep = "\u0001keep";
        public const string Same = "\u0001same";
        public const string Multi = "\u0001multi";
        public const string Global = "\u0001global";

        public string Label;
        public string Value;     // literal separator text, or one of the special values above
        public string ParenWord; // for "(feat. …)" style: the word inside the brackets

        public SepChoice(string label, string value) { Label = label; Value = value; }
        public override string ToString() { return Label; }

        public static SepChoice Paren(string word)
        {
            var c = new SepChoice("(" + word + " …)", "\u0001paren");
            c.ParenWord = word;
            return c;
        }

        public static readonly SepChoice[] MainChoices = new[]
        {
            new SepChoice(",   (comma)", ", "),
            new SepChoice(";   (semicolon)", "; "),
            new SepChoice("&   (and symbol)", " & "),
            new SepChoice("/   (slash, spaced)", " / "),
            new SepChoice("/   (slash, no spaces)", "/"),
            new SepChoice("and", " and "),
            new SepChoice("x", " x "),
            new SepChoice("X", " X "),
            new SepChoice("with", " with "),
            new SepChoice("vs.", " vs. "),
            new SepChoice("feat.", " feat. "),
            new SepChoice("ft.", " ft. "),
            new SepChoice("featuring", " featuring "),
            new SepChoice("Separate values  (Mp3tag \\\\ multi-value)", Multi),
            new SepChoice("Keep as is", Keep),
        };

        public static readonly SepChoice[] FeatChoices = new[]
        {
            new SepChoice("Same as other artists", Same),
            new SepChoice("Keep as is", Keep),
            new SepChoice("feat.", " feat. "),
            new SepChoice("Feat.", " Feat. "),
            new SepChoice("ft.", " ft. "),
            new SepChoice("featuring", " featuring "),
            Paren("feat."),
            Paren("ft."),
            new SepChoice("&", " & "),
            new SepChoice(",", ", "),
            new SepChoice("x", " x "),
            new SepChoice("with", " with "),
        };
    }

    public static class ArtistParser
    {
        class Pattern
        {
            public string Name; public Regex Rx; public SepKind Kind;
            public Pattern(string name, string rx, SepKind kind)
            {
                Name = name; Kind = kind;
                Rx = new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        const string FeatWord = @"(?:feat\.|ft\.|feat\b|ft\b|featuring\b|features\b)\s*";

        // Order matters only for ties; longer matches win overlaps.
        static readonly Pattern[] Patterns = new[]
        {
            new Pattern("feat.", @"\s*[\(\[]\s*" + FeatWord + @"|\s+" + FeatWord, SepKind.Featuring),
            new Pattern("\\\\", @"\s*\\\\\s*", SepKind.List),
            new Pattern(",", @"\s*,\s*", SepKind.List),
            new Pattern(";", @"\s*;\s*", SepKind.List),
            new Pattern("/", @"\s*/\s*", SepKind.List),
            new Pattern("&", @"\s+&\s+", SepKind.List),
            new Pattern("and", @"\s+and\s+", SepKind.List),
            new Pattern("x", @"\s+[x×]\s+", SepKind.List),
            new Pattern("with", @"\s+with\s+", SepKind.List),
            new Pattern("vs.", @"\s+vs\.?\s+", SepKind.List),
        };

        static readonly Regex FeatRx = new Regex(@"^\s*[\(\[]?\s*" + FeatWord + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Finds separators. Separators inside a protected (single-artist) name are returned disabled.</summary>
        public static List<Sep> Detect(string text, IEnumerable<string> protectedNames)
        {
            var found = new List<Sep>();
            if (string.IsNullOrEmpty(text)) return found;

            var candidates = new List<Sep>();
            foreach (var p in Patterns)
            {
                foreach (Match m in p.Rx.Matches(text))
                {
                    // A separator needs an artist on both sides.
                    if (m.Index == 0 || m.Index + m.Length >= text.Length) continue;
                    candidates.Add(new Sep { Start = m.Index, Length = m.Length, Name = p.Name, Kind = p.Kind, Enabled = true });
                }
            }

            // Resolve overlaps on the visible part only, so "Brand X & Phil" keeps both " X " and " & "
            // even though they share the space between them.
            Func<Sep, int> coreStart = s => { int i = s.Start; while (i < s.End && char.IsWhiteSpace(text[i])) i++; return i; };
            Func<Sep, int> coreEnd = s => { int i = s.End; while (i > s.Start && char.IsWhiteSpace(text[i - 1])) i--; return i; };
            foreach (var c in candidates.OrderByDescending(c => coreEnd(c) - coreStart(c)).ThenByDescending(c => c.Length).ThenBy(c => c.Start))
            {
                if (found.Any(f => coreStart(c) < coreEnd(f) && coreStart(f) < coreEnd(c))) continue;
                found.Add(c);
            }
            found.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int i = 1; i < found.Count; i++)
            {
                var prev = found[i - 1]; var cur = found[i];
                if (cur.Start < prev.End) { cur.Length -= prev.End - cur.Start; cur.Start = prev.End; }
            }

            // Disable separators that are part of a known single-artist name (e.g. "Simon & Garfunkel").
            foreach (var name in protectedNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                int idx = 0;
                while ((idx = text.IndexOf(name, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    int end = idx + name.Length;
                    foreach (var s in found)
                        if (s.CoreWithin(text, idx, end)) { s.Enabled = false; s.Protected = true; }
                    idx = end;
                }
            }

            found.Sort((a, b) => a.Start.CompareTo(b.Start));
            return found;
        }

        /// <summary>Creates a manual separator from a text selection, classifying it if it looks like a known one.</summary>
        public static Sep Manual(string text, int start, int length)
        {
            string s = text.Substring(start, length);
            var sep = new Sep { Start = start, Length = length, Enabled = true, Kind = SepKind.Custom, Name = s.Trim() };
            if (FeatRx.IsMatch(s)) { sep.Kind = SepKind.Featuring; sep.Name = "feat."; }
            if (sep.Name.Length == 0) sep.Name = "␣";
            return sep;
        }

        public static bool HasActive(List<Sep> seps)
        {
            return seps.Any(s => s.Enabled);
        }

        public static string Describe(List<Sep> seps)
        {
            var names = seps.Where(s => s.Enabled).Select(s => s.Name.ToLowerInvariant()).Distinct();
            return string.Join("  ", names);
        }

        static string TrimUnbalanced(string part)
        {
            part = part.Trim();
            while (part.EndsWith(")") && part.Count(ch => ch == ')') > part.Count(ch => ch == '('))
                part = part.Substring(0, part.Length - 1).TrimEnd();
            while (part.EndsWith("]") && part.Count(ch => ch == ']') > part.Count(ch => ch == '['))
                part = part.Substring(0, part.Length - 1).TrimEnd();
            return part;
        }

        static string Closer(string sepText)
        {
            if (sepText.Contains("(")) return ")";
            if (sepText.Contains("[")) return "]";
            return "";
        }

        /// <summary>
        /// Rebuilds the artist value(s) using the chosen separators.
        /// Returns more than one value only for the "separate values" choice.
        /// </summary>
        public static string[] Rebuild(string text, List<Sep> allSeps, SepChoice main, SepChoice feat)
        {
            var seps = allSeps.Where(s => s.Enabled).OrderBy(s => s.Start).ToList();
            if (seps.Count == 0 || main == null || feat == null) return new[] { text };

            // Split into artist names and the separator text that followed each one.
            var parts = new List<string>();
            var between = new List<Sep>();
            int pos = 0;
            foreach (var s in seps)
            {
                string piece = TrimUnbalanced(text.Substring(pos, s.Start - pos));
                if (piece.Length > 0) { parts.Add(piece); between.Add(s); }
                else if (between.Count > 0) between[between.Count - 1] = s.Kind == SepKind.Featuring ? s : between[between.Count - 1];
                pos = s.End;
            }
            string last = TrimUnbalanced(text.Substring(pos));
            if (last.Length > 0) parts.Add(last);
            else if (between.Count > 0) between.RemoveAt(between.Count - 1);
            if (parts.Count < 2) return new[] { text };

            int featAt = -1;
            if (feat.Value != SepChoice.Same)
                featAt = between.FindIndex(s => s.Kind == SepKind.Featuring);

            int mainCount = featAt >= 0 ? featAt + 1 : parts.Count;
            var closers = new StringBuilder();

            Func<int, int, string> joinRange = (from, to) =>
            {
                var sb = new StringBuilder(parts[from]);
                for (int i = from; i < to - 1; i++)
                {
                    string orig = text.Substring(between[i].Start, between[i].Length);
                    if (main.Value == SepChoice.Keep) { sb.Append(orig); closers.Insert(0, Closer(orig)); }
                    else sb.Append(main.Value == SepChoice.Multi ? ", " : main.Value);
                    sb.Append(parts[i + 1]);
                }
                return sb.ToString();
            };

            string featTail = "";
            if (featAt >= 0)
            {
                string featNames = joinRange(featAt + 1, parts.Count);
                string orig = text.Substring(between[featAt].Start, between[featAt].Length);
                if (feat.Value == SepChoice.Keep)
                {
                    featTail = orig + featNames;
                    closers.Insert(0, Closer(orig));
                }
                else if (feat.ParenWord != null) featTail = " (" + feat.ParenWord + " " + featNames + ")";
                else featTail = feat.Value + featNames;
            }

            if (main.Value == SepChoice.Multi)
            {
                var values = parts.Take(mainCount).ToList();
                values[values.Count - 1] += featTail + closers;
                return values.ToArray();
            }

            string mainStr = joinRange(0, mainCount);
            return new[] { mainStr + featTail + closers };
        }
    }
}
