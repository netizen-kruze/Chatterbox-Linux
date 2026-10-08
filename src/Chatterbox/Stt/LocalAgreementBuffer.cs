using System;
using System.Linq;

namespace Chatterbox.Stt;

// LocalAgreement-2 partial commit (whisper_streaming policy): a word becomes
// "committed" once two consecutive full-window hypotheses agree on it.
// Committed WORDS never retract, but their display form (punctuation, casing)
// refreshes from the latest hypothesis — the chatbox resends the whole window
// each time anyway, and this avoids fossilized mid-sentence punctuation like
// "Thank you. so much." Comparison is case/punctuation-insensitive so engines
// ("hello world") and Whisper ("Hello, world.") hypotheses both commit.
//
// Long utterances: the pipeline trims audio whose words are committed
// (FreezeCommittedPrefix). Those words move to a frozen prefix no later
// hypothesis can touch, and the agreement continues on the remaining audio
// alone — so a pass never has to cover more than the recent window.
public sealed class LocalAgreementBuffer
{
    private string[] _prevNorm = Array.Empty<string>();
    private string _committed = "";      // committed words of the audio still in the window
    private int _committedWords;
    private string _frozen = "";         // committed words whose audio was trimmed away
    private int _frozenWords;

    public string Committed =>
        _frozen.Length == 0 ? _committed
        : _committed.Length == 0 ? _frozen
        : _frozen + " " + _committed;
    public string Pending { get; private set; } = "";
    public int CommittedWordCount => _frozenWords + _committedWords;
    // Committed words still backed by audio in the window — what a trim may take.
    public int ActiveCommittedWordCount => _committedWords;

    public void Reset()
    {
        _prevNorm = Array.Empty<string>();
        _committed = "";
        _committedWords = 0;
        _frozen = "";
        _frozenWords = 0;
        Pending = "";
    }

    // Feeds the next hypothesis over the current window of the utterance.
    public void Update(string hypothesis)
    {
        var words = Tokenize(hypothesis);
        var norm = words.Select(Normalize).ToArray();

        int agree = 0;
        int max = Math.Min(norm.Length, _prevNorm.Length);
        while (agree < max && norm[agree] == _prevNorm[agree] && norm[agree].Length > 0)
            agree++;

        if (agree > _committedWords) _committedWords = agree;

        // Refresh the committed display from the newest hypothesis when it
        // still spans all committed words; keep the old text otherwise.
        if (_committedWords > 0 && norm.Length >= _committedWords)
            _committed = string.Join(' ', words.Take(_committedWords));

        Pending = norm.Length > _committedWords
            ? string.Join(' ', words.Skip(_committedWords))
            : "";
        _prevNorm = norm;
    }

    // The utterance ended: everything in the final hypothesis is committed.
    public void Finalize(string finalHypothesis)
    {
        var words = Tokenize(finalHypothesis);
        if (words.Length >= _committedWords)
        {
            _committed = string.Join(' ', words);
            _committedWords = words.Length;
        }
        Pending = "";
    }

    // The pipeline cut the audio under the first `words` committed words:
    // they become frozen text, and the comparison state is rebased so the
    // next hypothesis — which starts right after the cut — lines up.
    public void FreezeCommittedPrefix(int words)
    {
        words = Math.Min(words, _committedWords);
        if (words <= 0) return;
        var committed = Tokenize(_committed);
        var moved = string.Join(' ', committed.Take(words));
        _frozen = _frozen.Length == 0 ? moved : _frozen + " " + moved;
        _frozenWords += words;
        _committed = string.Join(' ', committed.Skip(words));
        _committedWords -= words;
        _prevNorm = _prevNorm.Skip(words).ToArray();
    }

    internal static string[] Tokenize(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Normalize(string word)
    {
        Span<char> buf = stackalloc char[word.Length];
        int n = 0;
        foreach (var c in word)
            if (char.IsLetterOrDigit(c))
                buf[n++] = char.ToLowerInvariant(c);
        return new string(buf[..n]);
    }
}
