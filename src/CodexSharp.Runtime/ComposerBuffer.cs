using System.Text;

namespace CodexSharp.Runtime;

/// In-memory composer buffer matching vendor/codex/codex-rs/tui editor bindings.
public sealed class ComposerBuffer
{
    private readonly StringBuilder _text = new();
    private readonly Stack<(string Text, int Cursor)> _undo = new();
    private readonly Stack<(string Text, int Cursor)> _redo = new();

    public int Cursor { get; private set; }
    public string Yank { get; private set; } = "";
    public string Text => _text.ToString();
    public bool IsEmpty => _text.Length == 0;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _text.Clear();
        Cursor = 0;
    }

    public void Set(string? value)
    {
        _undo.Clear();
        _redo.Clear();
        _text.Clear();
        _text.Append(value ?? "");
        Cursor = _text.Length;
    }

    public void MoveTo(int index) =>
        Cursor = Math.Clamp(index, 0, _text.Length);

    public void Insert(char ch) => Insert(ch.ToString());

    public void Insert(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Checkpoint();
        _text.Insert(Cursor, text);
        Cursor += text.Length;
    }

    public void Newline() => Insert('\n');

    public void Backspace()
    {
        if (Cursor <= 0) return;
        Checkpoint();
        _text.Remove(Cursor - 1, 1);
        Cursor--;
    }

    public void DeleteForward()
    {
        if (Cursor >= _text.Length) return;
        Checkpoint();
        _text.Remove(Cursor, 1);
    }

    public void MoveLeft()
    {
        if (Cursor > 0) Cursor--;
    }

    public void MoveRight()
    {
        if (Cursor < _text.Length) Cursor++;
    }

    public void MoveBufferStart() => Cursor = 0;

    public void MoveBufferEnd() => Cursor = _text.Length;

    public void OpenLineAbove()
    {
        var start = LineStart();
        MoveTo(start);
        Insert("\n");
        MoveTo(start);
    }

    public void MoveHome() => Cursor = LineStart();

    public void MoveEnd() => Cursor = LineEnd();

    public void MoveUp()
    {
        var start = LineStart();
        if (start == 0)
        {
            Cursor = 0;
            return;
        }

        var col = Cursor - start;
        var prevEnd = start - 1;
        var prevStart = prevEnd;
        while (prevStart > 0 && _text[prevStart - 1] != '\n') prevStart--;
        var prevLen = prevEnd - prevStart;
        Cursor = prevStart + Math.Min(col, prevLen);
    }

    public void MoveDown()
    {
        var start = LineStart();
        var col = Cursor - start;
        var end = LineEnd();
        if (end >= _text.Length)
        {
            Cursor = _text.Length;
            return;
        }

        var nextStart = end + 1;
        var nextEnd = nextStart;
        while (nextEnd < _text.Length && _text[nextEnd] != '\n') nextEnd++;
        var nextLen = nextEnd - nextStart;
        Cursor = nextStart + Math.Min(col, nextLen);
    }

    public void KillWordBack()
    {
        if (Cursor <= 0) return;
        Checkpoint();
        var end = Cursor;
        var i = Cursor;
        while (i > 0 && char.IsWhiteSpace(_text[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(_text[i - 1])) i--;
        Yank = _text.ToString(i, end - i);
        _text.Remove(i, end - i);
        Cursor = i;
    }

    public void KillToStart()
    {
        var start = LineStart();
        if (Cursor <= start) return;
        Checkpoint();
        Yank = _text.ToString(start, Cursor - start);
        _text.Remove(start, Cursor - start);
        Cursor = start;
    }

    public void KillToEnd()
    {
        var end = LineEnd();
        if (Cursor >= end) return;
        Checkpoint();
        Yank = _text.ToString(Cursor, end - Cursor);
        _text.Remove(Cursor, end - Cursor);
    }

    public void YankInsert() => Insert(Yank);

    public void PasteBefore() => Insert(Yank);

    public void PasteAfter()
    {
        if (Cursor < _text.Length) MoveRight();
        Insert(Yank);
    }

    /// Sync from an external editor (FuncUI TextBox) without wiping undo/redo.
    public void Adopt(string? value, int cursor)
    {
        var next = value ?? "";
        if (next == Text)
        {
            MoveTo(cursor);
            return;
        }

        Checkpoint();
        _text.Clear();
        _text.Append(next);
        Cursor = Math.Clamp(cursor, 0, _text.Length);
    }

    public void DeleteRange(int start, int end)
    {
        start = Math.Clamp(start, 0, _text.Length);
        end = Math.Clamp(end, 0, _text.Length);
        if (end < start) (start, end) = (end, start);
        if (end == start) return;
        Checkpoint();
        Yank = _text.ToString(start, end - start);
        _text.Remove(start, end - start);
        Cursor = start;
    }

    public void YankRange(int start, int end)
    {
        start = Math.Clamp(start, 0, _text.Length);
        end = Math.Clamp(end, 0, _text.Length);
        if (end < start) (start, end) = (end, start);
        Yank = end == start ? "" : _text.ToString(start, end - start);
    }

    public (int Start, int End) SpanLines(int a, int b)
    {
        var left = Math.Min(a, b);
        var right = Math.Max(a, b);
        var saved = Cursor;
        MoveTo(left);
        var start = LineStart();
        MoveTo(right);
        var end = LineEnd();
        if (end < _text.Length && _text[end] == '\n') end++;
        Cursor = saved;
        return (start, end);
    }

    public void ReplaceChar(char ch)
    {
        if (Cursor >= _text.Length)
        {
            Insert(ch.ToString());
            if (Cursor > 0) Cursor--;
            return;
        }

        Checkpoint();
        _text[Cursor] = ch;
    }

    public bool FindChar(char target, bool forward, bool till)
    {
        var start = LineStart();
        var end = LineEnd();
        if (forward)
        {
            for (var i = Cursor + 1; i < end; i++)
            {
                if (_text[i] != target) continue;
                Cursor = till ? Math.Max(start, i - 1) : i;
                return true;
            }
        }
        else
        {
            for (var i = Cursor - 1; i >= start; i--)
            {
                if (_text[i] != target) continue;
                Cursor = till ? Math.Min(end, i + 1) : i;
                return true;
            }
        }

        return false;
    }

    public void MoveWordEnd()
    {
        if (_text.Length == 0) return;
        var i = Cursor;
        var onWord = i < _text.Length && !char.IsWhiteSpace(_text[i]);
        var nextIsWord = i + 1 < _text.Length && !char.IsWhiteSpace(_text[i + 1]);
        if (onWord && nextIsWord)
        {
            while (i + 1 < _text.Length && !char.IsWhiteSpace(_text[i + 1])) i++;
        }
        else
        {
            if (onWord) i++;
            while (i < _text.Length && char.IsWhiteSpace(_text[i])) i++;
            if (i >= _text.Length)
            {
                Cursor = _text.Length;
                return;
            }
            while (i + 1 < _text.Length && !char.IsWhiteSpace(_text[i + 1])) i++;
        }

        Cursor = i;
    }

    public void YankLine()
    {
        var start = LineStart();
        var end = LineEnd();
        if (end < _text.Length && _text[end] == '\n') end++;
        Yank = _text.ToString(start, end - start);
    }

    public void YankWordForward()
    {
        if (Cursor >= _text.Length)
        {
            Yank = "";
            return;
        }

        var start = Cursor;
        var i = Cursor;
        if (!char.IsWhiteSpace(_text[i]))
        {
            while (i < _text.Length && !char.IsWhiteSpace(_text[i])) i++;
            while (i < _text.Length && char.IsWhiteSpace(_text[i])) i++;
        }
        else
        {
            while (i < _text.Length && char.IsWhiteSpace(_text[i])) i++;
        }

        Yank = _text.ToString(start, i - start);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push((_text.ToString(), Cursor));
        var (text, cur) = _undo.Pop();
        _text.Clear();
        _text.Append(text);
        Cursor = Math.Clamp(cur, 0, _text.Length);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push((_text.ToString(), Cursor));
        var (text, cur) = _redo.Pop();
        _text.Clear();
        _text.Append(text);
        Cursor = Math.Clamp(cur, 0, _text.Length);
        return true;
    }

    public void MoveWordRight()
    {
        var i = Cursor;
        if (i >= _text.Length) return;
        if (!char.IsWhiteSpace(_text[i]))
        {
            while (i < _text.Length && !char.IsWhiteSpace(_text[i])) i++;
        }
        while (i < _text.Length && char.IsWhiteSpace(_text[i])) i++;
        Cursor = i;
    }

    public void MoveWordLeft()
    {
        var i = Cursor;
        while (i > 0 && char.IsWhiteSpace(_text[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(_text[i - 1])) i--;
        Cursor = i;
    }

    public void KillWordForward()
    {
        if (Cursor >= _text.Length) return;
        Checkpoint();
        var start = Cursor;
        var i = Cursor;
        if (!char.IsWhiteSpace(_text[i]))
        {
            while (i < _text.Length && !char.IsWhiteSpace(_text[i])) i++;
            while (i < _text.Length && char.IsWhiteSpace(_text[i])) i++;
        }
        else
        {
            while (i < _text.Length && char.IsWhiteSpace(_text[i])) i++;
        }
        Yank = _text.ToString(start, i - start);
        _text.Remove(start, i - start);
    }

    public void KillLine()
    {
        var start = LineStart();
        Checkpoint();
        var end = LineEnd();
        if (end < _text.Length && _text[end] == '\n') end++;
        Yank = _text.ToString(start, end - start);
        _text.Remove(start, end - start);
        Cursor = start;
    }

    public string DisplayWithCursor()
    {
        var text = Text.Replace('\r', '\n');
        var cur = Math.Clamp(Cursor, 0, text.Length);
        return text[..cur] + "|" + text[cur..];
    }

    private void Checkpoint()
    {
        _redo.Clear();
        _undo.Push((_text.ToString(), Cursor));
        if (_undo.Count > 80)
        {
            var keep = _undo.ToArray()[..80];
            _undo.Clear();
            for (var i = keep.Length - 1; i >= 0; i--) _undo.Push(keep[i]);
        }
    }

    private int LineStart()
    {
        var i = Cursor;
        while (i > 0 && _text[i - 1] != '\n') i--;
        return i;
    }

    private int LineEnd()
    {
        var i = Cursor;
        while (i < _text.Length && _text[i] != '\n') i++;
        return i;
    }
}
