using System.IO;
using System.Text;
using TypeSwitcher.Config;
using TypeSwitcher.Core;
using Xunit;

namespace TypeSwitcher.Tests;

public sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TypeSwitcher.Tests", Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public string Write(string name, string text)
    {
        string file = System.IO.Path.Combine(Path, name);
        File.WriteAllText(file, text, new UTF8Encoding(false));
        return file;
    }
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public sealed class StorageTests
{
    [Fact]
    public void LegacyConfigMigrationKeepsDataAndCreatesOneBackup()
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(temp.Path);
        const string original = "{\"general\":{\"enabled\":false},\"typo\":{\"min_word_length\":7}}";
        File.WriteAllText(store.ConfigPath, original);
        File.WriteAllText(store.MyWordsPath, "авторскоеслово\n");
        var cfg = store.Load();
        Assert.Null(store.LoadProblem);
        Assert.Equal(2, cfg.SchemaVersion);
        Assert.False(cfg.General.Enabled);
        Assert.Equal(7, cfg.Typo.MinWordLength);
        Assert.Equal(original, File.ReadAllText(store.ConfigPath + ".v1.bak"));
        store.Save(cfg);
        store.EnsureUserLists();
        _ = store.Load();
        Assert.Single(Directory.GetFiles(temp.Path, "*.v1.bak"));
        Assert.Equal("авторскоеслово\n", File.ReadAllText(store.MyWordsPath));
        Assert.Equal(original, File.ReadAllText(store.ConfigPath + ".v1.bak"));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("{\"schema_version\":99}")]
    public void InvalidOrFutureConfigIsNotOverwritten(string original)
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(temp.Path);
        File.WriteAllText(store.ConfigPath, original);
        _ = store.Load();
        Assert.NotNull(store.LoadProblem);
        Assert.Equal(original, File.ReadAllText(store.ConfigPath));
        Assert.Single(Directory.GetFiles(temp.Path, "config.json.broken-*"));
    }

    [Fact]
    public void ScopedListsDoNotLeakIntoOtherLanguages()
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(temp.Path);
        store.EnsureUserLists();
        File.WriteAllText(store.UserListPath("my_words", "uk"), "дивопак\n");
        File.WriteAllText(store.UserListPath("stopwords", "uk"), "захищене\n");
        store.AppendRule("скор", "довге", "uk");
        var dictionary = new Dictionaries();
        dictionary.LoadUserLists(store.MyWordsPath, store.StopWordsPath, store.RulesPath);
        Assert.True(dictionary.IsValid(LayoutTarget.Uk, "дивопак"));
        Assert.False(dictionary.IsValid(LayoutTarget.Ru, "дивопак"));
        Assert.True(dictionary.IsStopWord("захищене", LayoutTarget.Uk));
        Assert.False(dictionary.IsStopWord("захищене", LayoutTarget.Ru));
        Assert.True(dictionary.TryRule("скор", out var replacement, LayoutTarget.Uk));
        Assert.Equal("довге", replacement);
        Assert.False(dictionary.TryRule("скор", out _, LayoutTarget.Ru));
    }

    [Fact]
    public void LegacyPersonalDictionaryRetainsItsOriginalValidityScope()
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(temp.Path);
        store.EnsureUserLists();
        File.WriteAllText(store.MyWordsPath, "legacyword\n");
        var dictionary = new Dictionaries();
        dictionary.LoadUserLists(store.MyWordsPath, store.StopWordsPath, store.RulesPath);
        Assert.True(dictionary.IsMyWord("legacyword", LayoutTarget.En));
        Assert.True(dictionary.IsValidRu("legacyword"));
        Assert.False(dictionary.IsValidEn("legacyword"));
        Assert.False(dictionary.IsValid(LayoutTarget.Uk, "legacyword"));
    }

    [Fact]
    public void MergePreservesExistingRulesAndNormalizesWordDuplicates()
    {
        string rules = ConfigStore.MergeUserList("a = old\n", "a = new\nb = value\n", rules: true);
        Assert.Equal("a = old\nb = value\n", rules);
        string words = ConfigStore.MergeUserList("П’ять\n", "п'ять\nнове\n", rules: false);
        Assert.Equal("П’ять\nнове\n", words);
        Assert.Throws<InvalidDataException>(() => ConfigStore.MergeUserList("", "broken", rules: true));
    }

    [Fact]
    public void TextWritesReplaceAtomicallyAndLeaveNoTemporaryFiles()
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(temp.Path);
        store.SaveText(store.MyWordsPath, "перший\n");
        store.SaveText(store.MyWordsPath, "другий\n");
        Assert.Equal("другий\n", File.ReadAllText(store.MyWordsPath));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public void KeyboardRulesBeatPersonalWordsButStopWordsBeatRules()
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(temp.Path);
        store.EnsureUserLists();
        File.WriteAllText(store.MyWordsPath, "ghbdsn\n");
        File.WriteAllText(store.RulesPath, "ghbdsn = привіт\n");
        var dictionary = new Dictionaries();
        dictionary.LoadUserLists(store.MyWordsPath, store.StopWordsPath, store.RulesPath);
        var analyzer = new Analyzer(dictionary, new AppConfig());
        var keyboard = new SelfTest.FakeKeyboard(analyzer);
        keyboard.Type("ghbdsn ");
        Assert.Equal("привіт ", keyboard.Screen);
        File.WriteAllText(store.StopWordsPath, "ghbdsn\n");
        dictionary.LoadUserLists(store.MyWordsPath, store.StopWordsPath, store.RulesPath);
        keyboard = new SelfTest.FakeKeyboard(analyzer);
        keyboard.Type("ghbdsn ");
        Assert.Equal("ghbdsn ", keyboard.Screen);
        var disabled = new AppConfig(); disabled.Languages.Enabled = ["ru"];
        File.WriteAllText(store.StopWordsPath, "");
        dictionary.LoadUserLists(store.MyWordsPath, store.StopWordsPath, store.RulesPath);
        keyboard = new SelfTest.FakeKeyboard(new Analyzer(dictionary, disabled));
        keyboard.Type("ghbdsn ");
        Assert.Equal("ghbdsn ", keyboard.Screen);
    }
}
