using System.IO;
using TypeSwitcher.Config;
using TypeSwitcher.Core;
using Xunit;

namespace TypeSwitcher.Tests;

public sealed class TestEnvironment : IDisposable
{
    public string Root { get; }
    public string Temp { get; } = Path.Combine(Path.GetTempPath(), "TypeSwitcher.Tests", Guid.NewGuid().ToString("N"));
    // Лингвистические ожидания независимы от profiler/coverage и нагрузки CI.
    // Реальный бюджет отдельно проверяют тест тайм-аута и опубликованный --eval.
    private sealed class FrozenClock : TimeProvider { public override long GetTimestamp() => 0; }
    public Dictionaries Dictionary { get; } = new(new FrozenClock());

    public TestEnvironment()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TypeSwitcher.sln"))) dir = dir.Parent;
        Root = dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        Directory.CreateDirectory(Temp);
        string dict = Path.Combine(Root, "dict");
        string ru = Path.Combine(dict, "hunspell", "ru_RU.dic");
        Assert.True(Dictionary.LoadRuHunspell(ru));
        Assert.True(Dictionary.LoadEnHunspell(Path.Combine(dict, "hunspell", "en_US.dic")));
        Assert.True(Dictionary.LoadFreq(Path.Combine(dict, "ru-300k.txt")));
        Assert.True(Dictionary.LoadExtra(Path.Combine(dict, "ru_extra.txt")));
        Assert.True(Dictionary.LoadRuExtraHunspell(Path.Combine(dict, "ru_extra.dic"), Path.ChangeExtension(ru, ".aff")));
        Assert.True(Dictionary.LoadEnExtra(Path.Combine(dict, "en_extra.txt")));
        string uk = Path.Combine(dict, "hunspell", "uk_UA.dic");
        Assert.True(Dictionary.LoadUkHunspell(uk));
        Assert.True(Dictionary.LoadFrequency(LayoutTarget.Uk, Path.Combine(dict, "uk-50k.txt")));
        Dictionary.SetNgram(NgramModel.BuildOrLoad(uk, Path.Combine(dict, "uk-50k.txt"),
            Path.Combine(Temp, "ngram_uk.cache"), true, LayoutTarget.Uk), LayoutTarget.Uk);
        Dictionary.SetNgram(NgramModel.BuildOrLoad(ru, Path.Combine(dict, "ru-300k.txt"),
            Path.Combine(Temp, "ngram_ru.cache"), true));
        Dictionary.PrewarmIndexes();
    }

    public Analyzer Analyzer(AppConfig? config = null) => new(Dictionary, config ?? new AppConfig())
    {
        LayoutDetectionEnabled = true
    };

    public void Dispose() => Directory.Delete(Temp, true);
}

[CollectionDefinition("Dictionaries")]
public sealed class DictionaryCollection : ICollectionFixture<TestEnvironment>;
