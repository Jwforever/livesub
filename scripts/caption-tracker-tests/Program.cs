using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LiveSub.Services;

// No test framework, external packages, wall-clock delays, or application startup.
// Failures are contract regressions, not assertions of the current implementation's bugs.
Console.OutputEncoding = Encoding.UTF8;

var tests = new List<(string Name, Action<Scenario> Run)>
{
    ("append_and_exact_180ms_stability", t =>
    {
        t.Step(0, "Hello");
        t.Step(100, "Hello world.");
        t.Step(279, "Hello world.");
        t.Step(280, "Hello world.", "Hello world.");
        t.Step(981, "Hello world.");
    }),
    ("appended_tail_does_not_restart_first_sentence_stability", t =>
    {
        t.Step(0, "First sentence.");
        t.Step(100, "First sentence. Next");
        t.Step(180, "First sentence. Next", "First sentence.");
        t.Step(200, "First sentence. Next sentence.");
        t.Step(379, "First sentence. Next sentence.");
        t.Step(380, "First sentence. Next sentence.", "Next sentence.");
        t.Step(1080, "First sentence. Next sentence.");
    }),
    ("equal_length_pending_revision_restarts_stability", t =>
    {
        t.Step(0, "The cat sleeps.");
        t.Step(100, "The dog sleeps.");
        t.Step(180, "The dog sleeps.");
        t.Step(279, "The dog sleeps.");
        t.Step(280, "The dog sleeps.", "The dog sleeps.");
        t.Step(980, "The dog sleeps.");
    }),
    ("shorter_pending_revision_restarts_stability", t =>
    {
        t.Step(0, "We really need help.");
        t.Step(100, "We need help.");
        t.Step(180, "We need help.");
        t.Step(279, "We need help.");
        t.Step(280, "We need help.", "We need help.");
        t.Step(980, "We need help.");
    }),
    ("late_punctuation_restarts_idle_and_stability", t =>
    {
        t.Step(0, "Hello there");
        t.Step(699, "Hello there.");
        t.Step(700, "Hello there.");
        t.Step(878, "Hello there.");
        t.Step(879, "Hello there.", "Hello there.");
    }),
    ("equal_length_committed_revision_is_not_retranslated", t =>
    {
        t.Step(0, "The cat sleeps.");
        t.Step(180, "The cat sleeps.", "The cat sleeps.");
        t.Step(200, "The dog sleeps.");
        t.Step(900, "The dog sleeps.");
        t.Step(920, "The dog sleeps. Fresh news.");
        t.Step(1100, "The dog sleeps. Fresh news.", "Fresh news.");
    }),
    ("shorter_committed_revision_is_not_retranslated", t =>
    {
        t.Step(0, "We really need help.");
        t.Step(180, "We really need help.", "We really need help.");
        t.Step(200, "We need help.");
        t.Step(900, "We need help.");
        t.Step(920, "We need help. Next item.");
        t.Step(1100, "We need help. Next item.", "Next item.");
    }),
    ("longer_committed_revision_is_not_retranslated", t =>
    {
        t.Step(0, "We need help.");
        t.Step(180, "We need help.", "We need help.");
        t.Step(200, "We really need help.");
        t.Step(900, "We really need help.");
        t.Step(920, "We really need help. Next item.");
        t.Step(1100, "We really need help. Next item.", "Next item.");
    }),
    ("committed_revision_preserves_unpunctuated_pending_tail", t =>
    {
        t.Step(0, "The cat sleeps. More news");
        t.Step(180, "The cat sleeps. More news", "The cat sleeps.");
        t.Step(200, "The dog sleeps. More news");
        t.Step(899, "The dog sleeps. More news");
        t.Step(900, "The dog sleeps. More news", "More news");
    }),
    ("shorter_committed_revision_preserves_complete_pending_sentence", t =>
    {
        t.Step(0, "We really need help. Next message.");
        t.Step(180, "We really need help. Next message.", "We really need help.");
        t.Step(200, "We need help. Next message.");
        t.Step(380, "We need help. Next message.", "Next message.");
        t.Step(1080, "We need help. Next message.");
    }),
    ("punctuation_revision_with_append_preserves_new_speech", t =>
    {
        t.Reset("hello.", 0);
        t.Step(10, "hello this");
        t.Step(710, "hello this", "this");
    }),
    ("committed_case_revision_with_pending_growth_preserves_tail", t =>
    {
        t.Reset("a test.", 0);
        t.Step(10, "a test. x");
        t.Step(20, "a Test. xy");
        t.Step(720, "a Test. xy", "xy");
    }),
    ("committed_rewrite_and_new_sentence_in_same_snapshot", t =>
    {
        t.Reset("We need help.", 0);
        t.Step(10, "We really need help. Fresh news.");
        t.Step(190, "We really need help. Fresh news.", "Fresh news.");
    }),
    ("committed_shortening_and_new_sentence_in_same_snapshot", t =>
    {
        t.Reset("We really need help.", 0);
        t.Step(10, "We need help. Fresh news.");
        t.Step(190, "We need help. Fresh news.", "Fresh news.");
    }),
    ("live_audio_revisions_preserve_every_recognized_word", t =>
    {
        var snapshots = new (long At, string Text)[]
        {
            (0, "hello."),
            (729, "hello this"),
            (788, "hello this is"),
            (1070, "hello this is a test"),
            (1363, "hello this is a test of"),
            (1592, "hello this is a test of life"),
            (1870, "hello this is a test of life. Sub"),
            (1916, "hello this is a test of life. Subtitles"),
            (3226, "hello this is a test of life. Subtitles the"),
            (3531, "hello this is a test of life. Subtitles the meeting"),
            (3919, "hello this is a test of life. Subtitles the meeting will"),
            (4200, "hello, this is a test of Life subtitles. The meeting will start, in"),
            (4483, "hello, this is a test of Life subtitles. The meeting will start, in five"),
            (4775, "hello, this is a test of Life subtitles. The meeting will start, in five minutes"),
            (6192, "hello, this is a test of Life subtitles. The meeting will start in five minutes. Please,"),
            (6464, "hello, this is a test of Life subtitles. The meeting will start in five minutes. Please, open the"),
            (7077, "hello, this is a test of Life subtitles. The meeting will start in five minutes. Please, open the document"),
            (7363, "hello, this is a test of Life subtitles. The meeting will start in five minutes. Please open the document and"),
            (7406, "hello, this is a test of Life subtitles. The meeting will start in five minutes. Please open the document and review"),
            (7685, "hello, this is a test of Life subtitles. The meeting will start in five minutes. Please open the document and review the"),
            (7960, "hello, this is a test of Life subtitles. The meeting will start in five minutes. Please open the document and review the first"),
            (8662, "hello, this is a Test of Life subtitles. The meeting will start in five minutes. Please open the document and review the first section,"),
        };
        var index = 0;
        for (long now = 0; now <= 10000; now += 40)
        {
            while (index + 1 < snapshots.Length && snapshots[index + 1].At <= now) index++;
            t.Observe(now, snapshots[index].Text);
        }
        t.ExpectWords(snapshots[^1].Text);
    }),
    ("shortening_to_committed_prefix_discards_only_pending_draft", t =>
    {
        t.Step(0, "First sentence. unwanted draft");
        t.Step(180, "First sentence. unwanted draft", "First sentence.");
        t.Step(200, "First sentence.");
        t.Step(900, "First sentence.");
        t.Step(920, "First sentence. New sentence.");
        t.Step(1100, "First sentence. New sentence.", "New sentence.");
    }),
    ("rolling_committed_prefix_is_not_retranslated", t =>
    {
        t.Step(0, "First sentence. Second sentence.");
        t.Step(180, "First sentence. Second sentence.", "First sentence.");
        t.Step(200, "First sentence. Second sentence.");
        t.Step(380, "First sentence. Second sentence.", "Second sentence.");
        t.Step(400, "Second sentence. Third sentence.");
        t.Step(579, "Second sentence. Third sentence.");
        t.Step(580, "Second sentence. Third sentence.", "Third sentence.");
        t.Step(1280, "Second sentence. Third sentence.");
    }),
    ("rolling_prefix_preserves_uncommitted_overlap", t =>
    {
        t.Step(0, "Old sentence. The next part");
        t.Step(180, "Old sentence. The next part", "Old sentence.");
        t.Step(200, "The next part grows.");
        t.Step(379, "The next part grows.");
        t.Step(380, "The next part grows.", "The next part grows.");
        t.Step(1080, "The next part grows.");
    }),
    ("rolling_short_overlap_does_not_drop_new_sentence", t =>
    {
        t.Step(0, "One. Two.");
        t.Step(180, "One. Two.", "One.");
        t.Step(200, "One. Two.");
        t.Step(380, "One. Two.", "Two.");
        t.Step(400, "Two. Three.");
        // Check beyond the idle deadline so delayed output is not mistaken for loss.
        t.Observe(580, "Two. Three.");
        t.Observe(1100, "Two. Three.");
        t.Observe(1800, "Two. Three.");
        t.ExpectAll("One.", "Two.", "Three.");
    }),
    ("idle_tail_flushes_at_700ms_while_caption_stays_nonempty", t =>
    {
        t.Step(0, "Words without punctuation");
        t.Step(699, "Words without punctuation");
        t.Step(700, "Words without punctuation", "Words without punctuation");
        t.Step(1400, "Words without punctuation");
    }),
    ("append_restarts_700ms_idle_timer", t =>
    {
        t.Step(0, "Words");
        t.Step(650, "Words keep");
        t.Step(1000, "Words keep arriving");
        t.Step(1699, "Words keep arriving");
        t.Step(1700, "Words keep arriving", "Words keep arriving");
    }),
    ("multiple_sentences_dispatch_separately_in_order", t =>
    {
        const string text = "First sentence. Second sentence! Third sentence?";
        t.Step(0, text);
        t.Step(180, text, "First sentence.");
        t.Step(200, text);
        t.Step(380, text, "Second sentence!");
        t.Step(400, text);
        t.Step(580, text, "Third sentence?");
        t.Step(1280, text);
    }),
    ("multiple_sentences_remain_separate_after_idle_with_sparse_polling", t =>
    {
        const string text = "First sentence. Second sentence! Third sentence?";
        t.Step(0, text);
        // Permit one or multiple results per poll, but never merge distinct sentences.
        t.Observe(700, text);
        t.Observe(880, text);
        t.Observe(1060, text);
        t.Observe(1240, text);
        t.ExpectAll("First sentence.", "Second sentence!", "Third sentence?");
    }),
    ("complete_sentence_followed_by_idle_unpunctuated_tail", t =>
    {
        const string text = "Complete sentence. unfinished tail";
        t.Step(0, text);
        t.Step(180, text, "Complete sentence.");
        t.Step(699, text);
        t.Step(700, text, "unfinished tail");
        t.Step(1400, text);
    }),
    ("decimal_is_not_a_sentence_boundary", t =>
    {
        t.Step(0, "The value is 3.14 meters.");
        t.Step(179, "The value is 3.14 meters.");
        t.Step(180, "The value is 3.14 meters.", "The value is 3.14 meters.");
        t.Step(880, "The value is 3.14 meters.");
    }),
    ("identical_sentence_appended_again_is_legitimate_speech", t =>
    {
        t.Step(0, "Try again.");
        t.Step(180, "Try again.", "Try again.");
        t.Step(200, "Try again. Try again.");
        t.Step(380, "Try again. Try again.", "Try again.");
        t.Step(1080, "Try again. Try again.");
    }),
    ("unchanged_caption_is_not_repeated_speech", t =>
    {
        t.Step(0, "Try again.");
        t.Step(180, "Try again.", "Try again.");
        t.Step(880, "Try again.");
        t.Step(1580, "Try again.");
    }),
    ("identical_sentence_after_clear_is_legitimate_speech", t =>
    {
        t.Step(0, "Try again.");
        t.Step(180, "Try again.", "Try again.");
        t.Step(200, "");
        t.Step(380, "");
        t.Step(400, "Try again.");
        t.Step(580, "Try again.", "Try again.");
    }),
    ("reset_skips_complete_and_partial_initial_history", t =>
    {
        t.Reset("Old sentence. Partial history", 0);
        t.Step(700, "Old sentence. Partial history");
        t.Step(720, "Old sentence. Partial history New sentence.");
        t.Step(900, "Old sentence. Partial history New sentence.", "New sentence.");
        t.Step(1600, "Old sentence. Partial history New sentence.");
    }),
    ("reset_discards_existing_candidate_and_skips_replacement_history", t =>
    {
        t.Step(0, "Stale sentence.");
        t.Reset("Existing history.", 100);
        t.Step(280, "Existing history.");
        t.Step(800, "Existing history.");
        t.Step(820, "Existing history. New sentence.");
        t.Step(999, "Existing history. New sentence.");
        t.Step(1000, "Existing history. New sentence.", "New sentence.");
    }),
    ("reset_history_can_roll_without_retranslation", t =>
    {
        t.Reset("Older sentence. Existing sentence.", 0);
        t.Step(100, "Existing sentence. New sentence.");
        t.Step(280, "Existing sentence. New sentence.", "New sentence.");
        t.Step(980, "Existing sentence. New sentence.");
    }),
    ("clear_discards_pending_draft_then_accepts_new_sentence", t =>
    {
        t.Step(0, "Discarded draft");
        t.Step(100, "");
        t.Step(800, "");
        t.Step(820, "Fresh sentence.");
        t.Step(1000, "Fresh sentence.", "Fresh sentence.");
        t.Step(1700, "Fresh sentence.");
    }),
    ("clear_after_commit_accepts_different_new_sentence", t =>
    {
        t.Step(0, "Old sentence.");
        t.Step(180, "Old sentence.", "Old sentence.");
        t.Step(200, "");
        t.Step(220, "New sentence.");
        t.Step(399, "New sentence.");
        t.Step(400, "New sentence.", "New sentence.");
    }),
    ("chinese_punctuation_dispatches_each_sentence", t =>
    {
        const string text = "第一句。第二句！第三句？";
        t.Step(0, text);
        t.Step(180, text, "第一句。");
        t.Step(200, text);
        t.Step(380, text, "第二句！");
        t.Step(400, text);
        t.Step(580, text, "第三句？");
        t.Step(1280, text);
    }),
    ("exactly_240_unpunctuated_characters_wait_for_idle", t =>
    {
        var text = new string('字', 240);
        t.Step(0, text);
        t.Step(699, text);
        t.Step(700, text, text);
        t.Step(1400, text);
    }),
    ("over_240_unpunctuated_characters_split_at_word_boundary", t =>
    {
        var first = string.Join(" ", Enumerable.Repeat("alpha", 40));
        var text = first + " omega";
        t.Step(0, text, first);
        t.Step(699, text);
        t.Step(700, text, "omega");
        t.Step(1400, text);
    }),
    ("over_240_unpunctuated_characters_without_spaces_split_at_limit", t =>
    {
        var first = new string('字', 240);
        var text = first + "尾";
        // Observe the idle flush too: preserving all characters in one oversized
        // result is still a failure of the requested 240-character chunking.
        t.Observe(0, text);
        t.Observe(699, text);
        t.Observe(700, text);
        t.Observe(1400, text);
        t.ExpectAll(first, "尾");
    }),
    ("crlf_and_outer_whitespace_are_normalized", t =>
    {
        const string text = "  First line\r\nSecond line  ";
        t.Step(0, text);
        t.Step(180, text);
        t.Step(699, text);
        t.Step(700, text, "First line Second line");
        t.Step(1400, text);
    }),
    ("native_multiline_caption_is_not_split_at_line_wraps", t =>
    {
        const string text = "why work this hard\nwhy go this far?";
        t.Step(0, text);
        t.Step(179, text);
        t.Step(180, text, "why work this hard why go this far?");
        t.Step(880, text);
    }),
    ("whitespace_and_punctuation_only_do_not_emit_translation", t =>
    {
        t.Step(0, " \r\n ");
        t.Step(700, " \r\n ");
        t.Step(800, "!");
        t.Step(980, "!");
        t.Step(1680, "!");
    }),
};

foreach (var (name, text) in new[]
{
    ("Dr", "Dr. Smith is here."),
    ("Mr", "Mr. Jones is here."),
    ("eg", "Use e.g. red or blue."),
    ("am", "Meet at 9 a.m. for tea."),
    ("initial", "J. Smith is here."),
})
{
    tests.Add(($"abbreviation_{name}_is_not_a_sentence_boundary", t =>
    {
        t.Step(0, text);
        t.Step(179, text);
        t.Step(180, text, text);
        t.Step(880, text);
    }));
}

var passed = 0;
var failed = 0;
foreach (var (name, run) in tests)
{
    var scenario = new Scenario();
    try
    {
        run(scenario);
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failed++;
        Console.WriteLine($"FAIL {name}");
        foreach (var entry in scenario.Trace) Console.WriteLine($"  {entry}");
        Console.WriteLine(error.ToString());
    }
}

Console.WriteLine($"TOTAL {tests.Count}; PASS {passed}; FAIL {failed}");
return failed == 0 ? 0 : 1;

internal sealed class Scenario
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly CaptionTextTracker _tracker = new();
    private readonly List<string> _outputs = new();
    public List<string> Trace { get; } = new();

    public Scenario() => Reset("", 0);

    public void Reset(string text, long now)
    {
        Trace.Add($"Reset({Format(text)}, {now})");
        _tracker.Reset(text, now);
    }

    public void Step(long now, string text, params string[] expected)
    {
        var actual = Observe(now, text);
        AssertEqual(expected, actual);
    }

    public string[] Observe(long now, string text)
    {
        var call = $"Update({Format(text)}, {now})";
        Trace.Add(call + " [invoking]");
        var actual = _tracker.Update(text, now).ToArray();
        Trace[^1] = call + " -> " + Format(actual);
        _outputs.AddRange(actual);
        return actual;
    }

    public void ExpectAll(params string[] expected)
    {
        Trace.Add("All dispatched sentences -> " + Format(_outputs));
        AssertEqual(expected, _outputs);
    }

    public void ExpectWords(string expected)
    {
        static IEnumerable<string> Words(string value) =>
            System.Text.RegularExpressions.Regex.Matches(value.ToLowerInvariant(), @"[a-z]+")
                .Select(match => match.Value);
        Trace.Add("All dispatched sentences -> " + Format(_outputs));
        AssertEqual(Words(expected), Words(string.Join(" ", _outputs)));
    }

    private static void AssertEqual(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            throw new InvalidOperationException($"Expected: {Format(expected)}\nActual:   {Format(actual)}");
    }

    private static string Format<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
