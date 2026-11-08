using System.IO;
using TypeSwitcher.Config;
using TypeSwitcher.Core;
using Xunit;

namespace TypeSwitcher.Tests;

public sealed class DictionaryImportTests
{
    private sealed class ExpiringClock : TimeProvider
    {
        private long _ticks;
        public override long GetTimestamp() => Interlocked.Add(ref _ticks, TimestampFrequency);
    }
    private static AppConfig ImportedOnly(AppConfig.DictionaryEntry entry)
    {
        var cfg = new AppConfig();
        cfg.Languages.Enabled = [entry.Language];
        cfg.Dictionaries.DisabledBuiltins = DictionaryManager.Builtins(cfg).Select(b => b.Id).ToList();
        cfg.Dictionaries.Installed.Add(entry);
        return cfg;
    }

    [Fact]
    public void ImportedWordsSurviveSourceRemovalAndConfigRoundTrip()
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(System.IO.Path.Combine(temp.Path, "data"));
        string source = temp.Write("words.txt", "зумбопак\nзумболекс\n");
        var entry = DictionaryManager.Import(store, source, "uk", "words");
        var cfg = ImportedOnly(entry);
        store.Save(cfg);
        File.Delete(source);
        var loaded = DictionaryManager.Load(store, store.Load());
        Assert.Empty(loaded.Errors);
        Assert.True(loaded.Dictionaries.IsValid(LayoutTarget.Uk, "зумбопак"));
        Assert.False(loaded.Dictionaries.IsValid(LayoutTarget.Ru, "зумбопак"));
        Assert.Equal(2, entry.WordCount);
    }

    [Fact]
    public void ImportedHunspellAppliesItsOwnAffixes()
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(System.IO.Path.Combine(temp.Path, "data"));
        string dic = temp.Write("custom.dic", "1\nзумбопак/Q\n");
        string aff = temp.Write("custom.aff", "SET UTF-8\nSFX Q Y 1\nSFX Q 0 и .\n");
        var entry = DictionaryManager.Import(store, dic, "uk", "hunspell", aff);
        var result = DictionaryManager.Load(store, ImportedOnly(entry));
        Assert.Empty(result.Errors);
        Assert.True(result.Dictionaries.IsValid(LayoutTarget.Uk, "зумбопаки"));
        Assert.NotNull(entry.AffSha256);
    }

    [Theory]
    [InlineData("words", "")]
    [InlineData("words", "two words")]
    [InlineData("frequency", "слово -1")]
    [InlineData("frequency", "слово not-a-number")]
    [InlineData("frequency", "слово 9999999999999999999")]
    public void InvalidImportLeavesNoPublishedOrStagingDirectory(string kind, string content)
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(System.IO.Path.Combine(temp.Path, "data"));
        string source = temp.Write("bad.txt", content);
        Assert.Throws<InvalidDataException>(() => DictionaryManager.Import(store, source, "uk", kind));
        Assert.Empty(Directory.GetDirectories(System.IO.Path.Combine(store.DataDir, "imported")));
    }

    [Fact]
    public void MissingAffixOrIncorrectCountIsRejected()
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(System.IO.Path.Combine(temp.Path, "data"));
        string dic = temp.Write("bad.dic", "2\nслово\n");
        string aff = temp.Write("bad.aff", "SET UTF-8\n");
        Assert.Throws<InvalidDataException>(() => DictionaryManager.Import(store, dic, "uk", "hunspell"));
        Assert.Throws<InvalidDataException>(() => DictionaryManager.Import(store, dic, "uk", "hunspell", aff));
        Assert.Empty(Directory.GetDirectories(System.IO.Path.Combine(store.DataDir, "imported")));
    }

    [Fact]
    public void CancelledImportIsRolledBack()
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(System.IO.Path.Combine(temp.Path, "data"));
        string source = temp.Write("words.txt", "приклад\n");
        Assert.ThrowsAny<OperationCanceledException>(() => DictionaryManager.Import(store, source, "uk", "words", cancellation: new CancellationToken(true)));
        Assert.Empty(Directory.GetDirectories(System.IO.Path.Combine(store.DataDir, "imported")));
    }

    [Fact]
    public void TamperingIsReportedAndExistingSnapshotRemainsUsable()
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(System.IO.Path.Combine(temp.Path, "data"));
        string source = temp.Write("words.txt", "зумбопак\n");
        var entry = DictionaryManager.Import(store, source, "uk", "words");
        var cfg = ImportedOnly(entry);
        var old = DictionaryManager.Load(store, cfg);
        File.AppendAllText(DictionaryManager.ResolveOwnedPath(store, entry.File, entry.Id), "нове\n");
        var invalid = DictionaryManager.Load(store, cfg);
        Assert.Single(invalid.Errors);
        Assert.True(old.Dictionaries.IsValid(LayoutTarget.Uk, "зумбопак"));
        Assert.False(old.Dictionaries.IsValid(LayoutTarget.Uk, "нове"));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("imported/other/words.txt")]
    [InlineData("C:\\outside.txt")]
    public void ImportedPathsCannotEscapeOwnedDirectory(string path)
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(temp.Path);
        Assert.Throws<InvalidDataException>(() => DictionaryManager.ResolveOwnedPath(store, path, Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void RemovalOnlyDeletesOwnedImportedFiles()
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(System.IO.Path.Combine(temp.Path, "data"));
        string source = temp.Write("words.txt", "зумбопак\n");
        var entry = DictionaryManager.Import(store, source, "uk", "words");
        DictionaryManager.RemoveFiles(store, entry);
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetDirectories(System.IO.Path.Combine(store.DataDir, "imported")));
    }

    [Fact]
    public void FrequencyCorpusCannotDeclareUnknownWordCorrect()
    {
        using var temp = new TestDirectory();
        string words = temp.Write("words.txt", "привіт\n");
        string freq = temp.Write("freq.txt", "привет 1000000\nпривіт 10\n");
        var dictionary = new Dictionaries();
        dictionary.LoadExtraWords(LayoutTarget.Uk, words);
        dictionary.LoadFrequency(LayoutTarget.Uk, freq);
        Assert.False(dictionary.IsValid(LayoutTarget.Uk, "привет"));
        Assert.Equal("привіт", dictionary.SuggestTypo(LayoutTarget.Uk, "привт", new TypoOptions(1, 5, 50, false, 1, 1, 1.5)));
    }

    [Fact]
    public void ExpiredBudgetDoesNotTurnPartialCandidatesIntoAnAutomaticCorrection()
    {
        using var temp = new TestDirectory();
        string source = temp.Write("words.txt", "привіт\n");
        var dictionary = new Dictionaries(new ExpiringClock());
        dictionary.LoadExtraWords(LayoutTarget.Uk, source);
        Assert.Null(dictionary.SuggestTypo(LayoutTarget.Uk, "привт", new TypoOptions(1, 5, 50, false, 1, 1, 1.5)));
        Assert.True(dictionary.IsValid(LayoutTarget.Uk, "привіт"));
    }
}
