using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Chatterbox.Stt;

// Experimental name recognition, correction stage: snaps transcribed words
// that are near-misses of known player names to the exact spelling. Runs on
// finals/partials after inference — pure string work, engine-agnostic.
// Deliberately conservative: only tokens of 4+ letters, tight edit-distance
// bounds, and exact-window matching for two-word names, so ordinary speech
// is never "corrected" into a name.
public sealed class NameCorrector
{
    private readonly (string Display, string Norm)[] _names;

    private NameCorrector((string, string)[] names) => _names = names;

    public static NameCorrector? Create(IEnumerable<string> displayNames)
    {
        var names = displayNames
            .Select(n => (Display: n, Norm: Normalize(n)))
            .Where(n => n.Norm.Length >= 4)
            .DistinctBy(n => n.Norm)
            .ToArray();
        return names.Length == 0 ? null : new NameCorrector(names);
    }

    public string Correct(string text)
    {
        if (text.Length == 0) return text;
        var tokens = text.Split(' ');
        var output = new List<string>(tokens.Length);

        for (int i = 0; i < tokens.Length; i++)
        {
            // A two-word window first (many names transcribe as two words),
            // then the single token.
            if (i + 1 < tokens.Length &&
                TryMatch(tokens[i] + tokens[i + 1], out var joined))
            {
                output.Add(WithEdges(tokens[i], joined, tokens[i + 1]));
                i++;
                continue;
            }
            output.Add(TryMatch(tokens[i], out var single)
                ? WithEdges(tokens[i], single, tokens[i])
                : tokens[i]);
        }
        return string.Join(' ', output);
    }

    private bool TryMatch(string raw, out string display)
    {
        display = "";
        var norm = Normalize(raw);
        if (norm.Length < 4) return false;

        int budget = norm.Length >= 7 ? 2 : 1;
        foreach (var (name, nameNorm) in _names)
        {
            if (Math.Abs(nameNorm.Length - norm.Length) > budget) continue;
            if (norm == nameNorm || BoundedDistance(norm, nameNorm, budget) <= budget)
            {
                if (raw == name) return false; // already exact — leave it alone
                display = name;
                return true;
            }
        }
        return false;
    }

    // Leading punctuation of the first token and trailing punctuation of the
    // last token survive the replacement ("Alise," -> "Alice,").
    private static string WithEdges(string first, string display, string last)
    {
        int lead = 0;
        while (lead < first.Length && !char.IsLetterOrDigit(first[lead])) lead++;
        int trail = last.Length;
        while (trail > 0 && !char.IsLetterOrDigit(last[trail - 1])) trail--;
        return first[..lead] + display + last[trail..];
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // Levenshtein with an early-out once every path exceeds the budget.
    private static int BoundedDistance(string a, string b, int budget)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1),
                    prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin > budget) return budget + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
