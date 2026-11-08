using System.Text;

namespace TypeSwitcher.Core;

/// <summary>
/// Накопление набираемого слова. Буквы добавляются, Backspace убирает
/// последний символ, Reset очищает (например, при перемещении каретки).
/// Определение границ слова делает вызывающая сторона (InputProcessor).
/// </summary>
public sealed class WordBuffer
{
    private readonly StringBuilder _sb = new();

    public int Length => _sb.Length;
    public string Current => _sb.ToString();
    public bool IsEmpty => _sb.Length == 0;

    public void Append(string s) => _sb.Append(s);

    public void Backspace()
    {
        if (_sb.Length > 0) _sb.Remove(_sb.Length - 1, 1);
    }

    public void Reset() => _sb.Clear();
}
