using System.IO;
using TypeSwitcher.Config;
using TypeSwitcher.Core;
using Xunit;

namespace TypeSwitcher.Tests;

public sealed class HistoryAndDiagnosticsTests
{
    [Fact]
    public void HistoryIsOptInAndKeepsOnlyTheConfiguredTail()
    {
        using var temp = new TestDirectory();
        string path = Path.Combine(temp.Path, "corrections.jsonl");
        var log = new LearnLog(path, false) { MaxEntries = 10 };
        log.LogCorrection("private", "unused", ActionKind.Typo);
        Assert.False(File.Exists(path));
        log.Enabled = true;
        for (int i = 0; i < 15; i++) log.LogCorrection("word" + i, "fixed" + i, ActionKind.Typo);
        var entries = CorrectionHistory.Read(path, 10);
        Assert.Equal(10, entries.Count);
        Assert.Equal("word5", entries[0].Original);
        Assert.Equal("word14", entries[^1].Original);
        log.Enabled = false; log.LogRejection("not-recorded");
        Assert.Equal(entries, CorrectionHistory.Read(path, 10));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public void CorruptLinesAreSkippedAndClearOnlyRemovesHistory()
    {
        using var temp = new TestDirectory();
        string words = temp.Write("my_words.txt", "моеслово\n");
        string path = temp.Write("corrections.jsonl", "not-json\n{\"ts\":\"2026-01-01\",\"ev\":\"correct\",\"orig\":\"a\",\"res\":\"b\",\"kind\":\"Rule\"}\n");
        Assert.Single(CorrectionHistory.Read(path, 10));
        CorrectionHistory.Clear(path);
        Assert.Empty(CorrectionHistory.Read(path, 10));
        Assert.Equal("моеслово\n", File.ReadAllText(words));
    }

    [Fact]
    public void DiagnosticPreviewUsesUnsavedPersonalRulesAndProtection()
    {
        var dictionary = new Dictionaries();
        dictionary.LoadUserTexts(new Dictionary<(string Kind, string Scope), string>
        {
            [("rules", "global")] = "hello = example\n",
            [("stopwords", "global")] = "protected\n"
        });
        var result = Diagnostics.Analyze(dictionary, new AppConfig(), "hello protected", LayoutTarget.En);
        Assert.Equal("example", result[0].Analysis.Applied.Replacement);
        Assert.Equal("stop-word", result[1].Analysis.Applied.Reason);
    }

    [Fact]
    public void HistoryCanBeClearedWhileAnotherReaderHasItOpen()
    {
        using var temp = new TestDirectory();
        string path = temp.Write("corrections.jsonl", "{\"ts\":\"2026\",\"ev\":\"correct\",\"orig\":\"a\",\"res\":\"b\"}\n");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        CorrectionHistory.Clear(path);
        Assert.Equal(0, new FileInfo(path).Length);
    }
}
