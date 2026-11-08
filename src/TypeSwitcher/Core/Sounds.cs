using System.IO;
using System.Media;

namespace TypeSwitcher.Core;

/// <summary>
/// Тихие сигналы при срабатывании: spell.wav для опечаток, lang.wav для смены
/// раскладки. Каждый можно отключить в настройках.
/// </summary>
public sealed class Sounds : IDisposable
{
    private readonly SoundPlayer? _typo;
    private readonly SoundPlayer? _layout;

    public Sounds(string typoWavPath, string layoutWavPath)
    {
        _typo = TryLoad(typoWavPath);
        _layout = TryLoad(layoutWavPath);
    }

    private static SoundPlayer? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var p = new SoundPlayer(path);
            p.Load();
            return p;
        }
        catch
        {
            return null;
        }
    }

    public void PlayTypo() => SafePlay(_typo);
    public void PlayLayout() => SafePlay(_layout);

    private static void SafePlay(SoundPlayer? p)
    {
        try { p?.Play(); } catch { /* звук не критичен */ }
    }

    public void Dispose()
    {
        _typo?.Dispose();
        _layout?.Dispose();
    }
}
