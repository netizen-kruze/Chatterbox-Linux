using System;
using System.Collections.Generic;
using System.Linq;

namespace Chatterbox.Stt;

// Rolling transcript window for the VRChat chatbox: hard 144-char cap with
// drop-leading-whole-words windowing (never mid-word). Text is organized in
// lines — an utterance that follows a longer pause starts on a new line, so
// separate thoughts read as separate lines in the chatbox (VRChat renders
// "\n"). Holds finalized utterances plus a replaceable "live" segment (the
// current utterance's committed words from LocalAgreement).
public sealed class RollingChatboxBuffer
{
    public const int DefaultMaxChars = 144; // VRChat chatbox hard cap

    private readonly int _maxChars;
    private readonly List<List<string>> _lines = new();
    private string[] _liveWords = Array.Empty<string>();
    private bool _liveOnNewLine;

    // maxChars below 144 supports hosts that reserve trailing glyphs.
    public RollingChatboxBuffer(int maxChars = DefaultMaxChars)
    {
        if (maxChars < 8) throw new ArgumentOutOfRangeException(nameof(maxChars));
        _maxChars = maxChars;
    }

    public bool IsEmpty => _lines.Count == 0 && _liveWords.Length == 0;

    // Replaces the live segment with the current utterance's committed text.
    // onNewLine renders it as the start of a new line.
    public void UpdateLive(string committedText, bool onNewLine = false)
    {
        _liveWords = Tokenize(committedText);
        _liveOnNewLine = onNewLine;
    }

    // The utterance is final: absorb it and clear the live segment.
    public void CommitUtterance(string finalText, bool onNewLine = false)
    {
        var words = Tokenize(finalText);
        _liveWords = Array.Empty<string>();
        _liveOnNewLine = false;
        if (words.Length == 0) return;

        if (onNewLine || _lines.Count == 0) _lines.Add(new List<string>());
        _lines[^1].AddRange(words);

        // Words that can never re-enter the window are gone for good.
        TrimFront(_lines, _maxChars, keepAtLeastOneWord: true);
    }

    public void Clear()
    {
        _lines.Clear();
        _liveWords = Array.Empty<string>();
        _liveOnNewLine = false;
    }

    // The chatbox text: most recent words, whole words dropped from the front
    // until the cap fits (empty leading lines drop with them). A single
    // overlong word keeps its tail.
    public string Window
    {
        get
        {
            var lines = _lines.Select(l => new List<string>(l)).ToList();
            if (_liveWords.Length > 0)
            {
                if (_liveOnNewLine || lines.Count == 0) lines.Add(new List<string>());
                lines[^1].AddRange(_liveWords);
            }

            TrimFront(lines, _maxChars, keepAtLeastOneWord: true);

            var text = string.Join('\n', lines.Select(l => string.Join(' ', l)));
            return text.Length > _maxChars ? text[^_maxChars..] : text;
        }
    }

    private static int TotalLength(List<List<string>> lines)
    {
        int len = lines.Count > 0 ? lines.Count - 1 : 0; // newlines
        foreach (var line in lines)
        {
            len += line.Count > 0 ? line.Count - 1 : 0;  // spaces
            foreach (var w in line) len += w.Length;
        }
        return len;
    }

    private static void TrimFront(List<List<string>> lines, int maxChars, bool keepAtLeastOneWord)
    {
        while (TotalLength(lines) > maxChars)
        {
            if (lines.Count == 0) return;
            var first = lines[0];
            if (first.Count > 0)
            {
                if (keepAtLeastOneWord && lines.Count == 1 && first.Count == 1) return;
                first.RemoveAt(0);
            }
            if (first.Count == 0) lines.RemoveAt(0);
        }
    }

    private static string[] Tokenize(string text) =>
        NormalizePunctuation(text)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Whisper emits typographic punctuation; map it to ASCII so the chatbox
    // (whose OSC input is ASCII-oriented) renders predictably. Other
    // characters pass through untouched.
    internal static string NormalizePunctuation(string text)
    {
        if (text.AsSpan().IndexOfAnyExceptInRange((char)0x20, (char)0x7E) < 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '‘' or '’': sb.Append('\''); break;
                case '“' or '”': sb.Append('"'); break;
                case '–' or '—': sb.Append('-'); break;
                case '…': sb.Append("..."); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
