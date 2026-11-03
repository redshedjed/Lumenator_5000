using System.Globalization;
using System.Text;
using System.Text.Json;

namespace YargDmx;

/// <summary>Edits rig.json in place for the visualizer: only the "fixtures" array (or one top-level number)
/// is rewritten, so the comments and layout of the rest of the file stay as the user wrote them.</summary>
public static class RigFile
{
    /// <summary><paramref name="text"/> with its top-level "fixtures" array replaced by
    /// <paramref name="fixtures"/>, ordered left to right.</summary>
    public static string ReplaceFixtures(string text, IEnumerable<Fixture> fixtures)
    {
        var (start, indent) = FindValue(text, "fixtures");
        if (start < 0 || text[start] != '[')
            throw new InvalidDataException("rig.json has no \"fixtures\" list");
        int end = MatchingBracket(text, start);
        var sb = new StringBuilder("[\n");
        var list = fixtures.OrderBy(f => f.X).ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var f = list[i];
            sb.Append(indent).Append("  { \"name\": ").Append(JsonSerializer.Serialize(f.Name))
              .Append(", \"address\": ").Append(f.Address)
              .Append(", \"profile\": ").Append(JsonSerializer.Serialize(f.Profile))
              .Append(", \"x\": ").Append(Num(f.X)).Append(", \"y\": ").Append(Num(f.Y));
            if (f.Level < 1)
                sb.Append(", \"level\": ").Append(Num(f.Level));
            sb.Append(" }").Append(i < list.Count - 1 ? ",\n" : "\n");
        }
        sb.Append(indent).Append(']');
        return text[..start] + sb + text[(end + 1)..];
    }

    /// <summary><paramref name="text"/> with the top-level number <paramref name="key"/> set (added at the
    /// top of the object if it isn't there).</summary>
    public static string SetNumber(string text, string key, double value)
    {
        var v = Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);
        var (start, _) = FindValue(text, key);
        if (start < 0)
        {
            int open = SkipToRoot(text);
            return text[..(open + 1)] + $"\n  \"{key}\": {v}," + text[(open + 1)..];
        }
        int end = start;
        while (end < text.Length && "+-.0123456789eE".Contains(text[end])) end++;
        if (end == start)
            throw new InvalidDataException($"rig.json: \"{key}\" isn't a number");
        return text[..start] + v + text[end..];
    }

    static string Num(double v) => Math.Round(Math.Clamp(double.IsNaN(v) ? 0.5 : v, 0, 1), 3).ToString(CultureInfo.InvariantCulture);

    /// <summary>Where the value of the top-level <paramref name="key"/> starts (-1 if there's none) and the
    /// key's indentation. Skips strings and // or /* */ comments.</summary>
    static (int Start, string Indent) FindValue(string t, string key)
    {
        int depth = 0, keyAt = -1;
        bool colon = false;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { i = t.IndexOf('\n', i) is var n and >= 0 ? n : t.Length; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '*') { i = t.IndexOf("*/", i + 2, StringComparison.Ordinal) is var e and >= 0 ? e + 1 : t.Length; continue; }
            if (char.IsWhiteSpace(c)) continue;
            if (keyAt >= 0)
            {
                if (colon) return (i, IndentOf(t, keyAt));
                if (c == ':') { colon = true; continue; }
                keyAt = -1;                                 // it was a value that happened to match
            }
            if (c == '"')
            {
                int s = i;
                for (i++; i < t.Length && t[i] != '"'; i++)
                    if (t[i] == '\\') i++;
                if (depth == 1 && t.AsSpan(s + 1, Math.Max(0, i - s - 1)).SequenceEqual(key))
                    (keyAt, colon) = (s, false);
                continue;
            }
            if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
        }
        return (-1, "  ");
    }

    static int SkipToRoot(string t)
    {
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { i = t.IndexOf('\n', i) is var n and >= 0 ? n : t.Length; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '*') { i = t.IndexOf("*/", i + 2, StringComparison.Ordinal) is var e and >= 0 ? e + 1 : t.Length; continue; }
            if (c == '{') return i;
            if (!char.IsWhiteSpace(c)) break;
        }
        throw new InvalidDataException("rig.json isn't a { } object");
    }

    static int MatchingBracket(string t, int open)
    {
        int depth = 0;
        for (int i = open; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { i = t.IndexOf('\n', i) is var n and >= 0 ? n : t.Length; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '*') { i = t.IndexOf("*/", i + 2, StringComparison.Ordinal) is var e and >= 0 ? e + 1 : t.Length; continue; }
            if (c == '"') { for (i++; i < t.Length && t[i] != '"'; i++) if (t[i] == '\\') i++; continue; }
            if (c is '[' or '{') depth++;
            else if (c is ']' or '}' && --depth == 0) return i;
        }
        throw new InvalidDataException("rig.json: the \"fixtures\" list isn't closed");
    }

    static string IndentOf(string t, int at)
    {
        int lineStart = t.LastIndexOf('\n', at) + 1;
        return t[lineStart..at].All(char.IsWhiteSpace) ? t[lineStart..at] : "  ";
    }
}
