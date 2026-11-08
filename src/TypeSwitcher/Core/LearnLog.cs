using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TypeSwitcher.Core;

/// <summary>
/// Локальный приватный лог правок и откатов (opt-in) для последующего анализа
/// качества и настройки порогов. Пишет JSONL в data/corrections.jsonl.
///
/// Это НЕ keylog: пишутся только события автозамены ("correct") и отказов
/// ("reject" — откат хоткеем или стирание+перенабор), без полного потока набора,
/// без буфера обмена, без содержимого окон. В исключённых контекстах (поля
/// паролей, чёрный список) события не возникают — их отсекает InputProcessor до
/// анализа, поэтому в лог они не попадают.
///
/// Формат строки: {"ts":"...","ev":"correct","orig":"ghbdtn","res":"привет","kind":"Layout"}
/// либо {"ts":"...","ev":"reject","orig":"привет"}.
/// </summary>
public sealed class LearnLog
{
    private readonly string _path;
    private readonly object _lock = new();

    /// <summary>Ведение лога включено (меняется на лету из настроек).</summary>
    public bool Enabled { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // Читаемая кириллица в локальном файле (не \uXXXX).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public LearnLog(string path, bool enabled)
    {
        _path = path;
        Enabled = enabled;
    }

    public void LogCorrection(string original, string corrected, ActionKind kind)
        => Write(new Entry(Now(), "correct", original, corrected, kind.ToString()));

    public void LogRejection(string word)
        => Write(new Entry(Now(), "reject", word, null, null));

    private void Write(Entry e)
    {
        if (!Enabled || string.IsNullOrEmpty(e.orig)) return;
        try
        {
            string line = JsonSerializer.Serialize(e, JsonOpts);
            lock (_lock) File.AppendAllText(_path, line + "\n");
        }
        catch { /* лог не критичен - не роняем ввод из-за него */ }
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

    private readonly record struct Entry(string ts, string ev, string orig, string? res, string? kind);
}
