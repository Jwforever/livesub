namespace LiveSub.Services;

internal sealed class CaptionTextTracker
{
    private const int StableSentenceMilliseconds = 180;
    private const int CommaStabilityMilliseconds = 1500;
    private const int IdleMilliseconds = 700;
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mr", "Mrs", "Ms", "Dr", "Prof", "Sr", "Jr", "St", "vs", "etc", "e.g", "i.e", "a.m", "p.m",
    };

    private string _snapshot = "";
    private int _committed;
    private long _lastChange;
    private string _candidate = "";
    private long _candidateSince;

    public void Reset(string initialText, long now)
    {
        _snapshot = Normalize(initialText);
        _committed = _snapshot.Length;
        _lastChange = now;
        _candidate = "";
    }

    public IReadOnlyList<string> Update(string text, long now)
    {
        text = Normalize(text);
        if (text != _snapshot)
        {
            MapCommittedPosition(text);
            _snapshot = text;
            _lastChange = now;
        }

        var results = new List<string>();
        var pending = _snapshot[_committed..];
        var end = FindSentenceEnd(pending);
        var candidate = end >= 0 ? pending[..(end + 1)] : "";
        if (_candidate != candidate)
        {
            _candidate = candidate;
            _candidateSince = now;
        }

        var requiredStability = candidate.Length > 0 && candidate[^1] is ',' or '，' or '、'
            ? CommaStabilityMilliseconds
            : StableSentenceMilliseconds;
        if (candidate.Length > 0 && now - _candidateSince >= requiredStability)
        {
            Commit(candidate.Length, results);
            _candidate = "";
        }
        else if (candidate.Length == 0 && pending.Length > 0 && now - _lastChange >= IdleMilliseconds)
        {
            Commit(Math.Min(pending.Length, 240), results);
            _candidate = "";
        }
        else if (pending.Length > 240 && candidate.Length == 0)
        {
            var boundary = pending.LastIndexOf(' ', 240);
            Commit(boundary > 0 ? boundary + 1 : 240, results);
        }
        return results;
    }

    private void MapCommittedPosition(string text)
    {
        if (text.StartsWith(_snapshot, StringComparison.Ordinal)) return;

        // A rolling caption buffer removes its oldest prefix without repeating that speech.
        for (var overlap = Math.Min(_snapshot.Length, text.Length); overlap >= 2; overlap--)
        {
            var start = _snapshot.Length - overlap;
            if (start > 0 && char.IsLetterOrDigit(_snapshot[start - 1])) continue;
            if (_snapshot.AsSpan(start).SequenceEqual(text.AsSpan(0, overlap)))
            {
                _committed = Math.Max(0, _committed - start);
                return;
            }
        }

        var prefix = 0;
        while (prefix < _snapshot.Length && prefix < text.Length && _snapshot[prefix] == text[prefix]) prefix++;
        var suffix = 0;
        while (suffix < _snapshot.Length - prefix && suffix < text.Length - prefix &&
               _snapshot[^(suffix + 1)] == text[^(suffix + 1)]) suffix++;

        if (prefix == 0 && suffix == 0)
        {
            _committed = 0;
            return;
        }
        if (_committed > prefix)
            _committed = FindRevisedCommittedEnd(text, prefix);
        _committed = Math.Clamp(_committed, 0, text.Length);
    }

    private int FindRevisedCommittedEnd(string text, int prefix)
    {
        // A revision can also append speech, so align only the already committed prefix.
        var width = text.Length - prefix;
        var previous = Enumerable.Range(0, width + 1).ToArray();
        var current = new int[width + 1];
        for (var i = 1; i <= _committed - prefix; i++)
        {
            current[0] = i;
            for (var j = 1; j <= width; j++)
            {
                var cost = char.ToUpperInvariant(_snapshot[prefix + i - 1]) ==
                           char.ToUpperInvariant(text[prefix + j - 1]) ? 0 : 1;
                current[j] = Math.Min(previous[j - 1] + cost,
                    Math.Min(previous[j] + 1, current[j - 1] + 1));
            }
            (previous, current) = (current, previous);
        }

        var best = 0;
        for (var j = 1; j <= width; j++)
        {
            var end = prefix + j;
            if (end < text.Length && char.IsLetterOrDigit(text[end - 1]) && char.IsLetterOrDigit(text[end]))
                continue;
            if (previous[j] <= previous[best]) best = j;
        }
        return prefix + best;
    }

    private void Commit(int length, List<string> results)
    {
        var sentence = _snapshot.Substring(_committed, length).Trim();
        _committed += length;
        if (sentence.Any(char.IsLetterOrDigit)) results.Add(sentence);
    }

    private static int FindSentenceEnd(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is ';' or '。' or '！' or '？' or '；') return i;
            if (ch is '，' or '、') return i;
            if (ch == ',')
            {
                if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])) continue;
                // Do not turn an opener like "By," into a subtitle by itself.
                if (CountWords(text, i) >= 2) return i;
                continue;
            }
            if (ch is '!' or '?') return i;
            if (ch != '.') continue;
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])) continue;
            var start = i;
            while (start > 0 && (char.IsLetter(text[start - 1]) || text[start - 1] == '.')) start--;
            var word = text[start..i];
            if (word.Length == 1 || Abbreviations.Contains(word)) continue;
            if (word.Contains('.') && word.Split('.').All(part => part.Length == 1)) continue;
            return i;
        }
        return -1;
    }

    private static int CountWords(string text, int end)
    {
        var count = 0;
        var inWord = false;
        for (var i = 0; i < end; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                count++;
                inWord = true;
            }
        }
        return count;
    }

    private static string Normalize(string text)
    {
        // Windows Live Captions wraps phrases with newlines; they are layout, not sentence ends.
        var parts = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }
}
