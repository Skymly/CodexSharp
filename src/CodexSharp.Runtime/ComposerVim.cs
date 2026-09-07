namespace CodexSharp.Runtime;

public enum ComposerVimMode
{
    Insert,
    Normal,
    Visual,
    VisualLine,
}

public readonly record struct ComposerVimInput(
    char Char,
    bool Escape,
    bool Enter,
    bool Left,
    bool Right,
    bool Home,
    bool End,
    bool Delete,
    bool Backspace,
    bool Ctrl,
    bool Alt);

enum VimRepeat
{
    None,
    DeleteChar,
    DeleteLine,
    DeleteWord,
    DeleteToEnd,
    YankLine,
    YankWord,
    Paste,
    PasteBefore,
    ChangeLine,
    ChangeWord,
    ChangeToEnd,
    Substitute,
    ReplaceChar,
    InsertText,
}

/// Modal composer editing inspired by vendor/codex/codex-rs/tui textarea vim.
/// Subset: insert/normal/visual, operators d/c/y, f/t finds, and `.` operator replay.
/// Not a full vim emulator and not ratatui.
public sealed class ComposerVim(ComposerBuffer buffer)
{
    public ComposerBuffer Buffer { get; } = buffer;
    public ComposerVimMode Mode { get; private set; } = ComposerVimMode.Insert;
    public bool Enabled { get; set; }
    public int VisualAnchor { get; private set; }
    public string ModeLabel => Mode switch
    {
        ComposerVimMode.Normal => "normal",
        ComposerVimMode.Visual => "visual",
        ComposerVimMode.VisualLine => "visual-line",
        _ => "insert",
    };

    private char? _pending;
    private char _findChar;
    private char _findKind;
    private char _replaceChar;
    private VimRepeat _repeat;
    private string _insertBefore = "";
    private int _insertAt;
    private string _lastInsert = "";
    private bool _insertOpen;

    public void SetMode(ComposerVimMode mode)
    {
        if (Mode == ComposerVimMode.Insert && mode != ComposerVimMode.Insert)
        {
            CaptureInsert();
        }

        Mode = mode;
        _pending = null;
        if (mode == ComposerVimMode.Insert)
        {
            BeginInsert();
        }
        if (mode is ComposerVimMode.Visual or ComposerVimMode.VisualLine)
        {
            VisualAnchor = Buffer.Cursor;
        }
    }

    public bool TryHandle(ConsoleKeyInfo key)
    {
        if (Enabled && Mode != ComposerVimMode.Insert
            && key.Modifiers.HasFlag(ConsoleModifiers.Control)
            && key.Key == ConsoleKey.R)
        {
            Buffer.Redo();
            return true;
        }

        return TryHandle(new ComposerVimInput(
            key.KeyChar,
            key.Key == ConsoleKey.Escape,
            key.Key == ConsoleKey.Enter,
            key.Key == ConsoleKey.LeftArrow,
            key.Key == ConsoleKey.RightArrow,
            key.Key == ConsoleKey.Home,
            key.Key == ConsoleKey.End,
            key.Key == ConsoleKey.Delete,
            key.Key == ConsoleKey.Backspace,
            key.Modifiers.HasFlag(ConsoleModifiers.Control),
            key.Modifiers.HasFlag(ConsoleModifiers.Alt)));
    }

    public bool TryHandle(ComposerVimInput key)
    {
        if (!Enabled) return false;
        if (Mode == ComposerVimMode.Insert)
        {
            if (key.Escape)
            {
                CaptureInsert();
                Mode = ComposerVimMode.Normal;
                _pending = null;
                return true;
            }

            return false;
        }

        if (key.Ctrl)
        {
            if (key.Char is 'r' or 'R' or '')
            {
                Buffer.Redo();
                return true;
            }

            return false;
        }

        if (key.Enter || key.Alt) return false;

        if (key.Escape)
        {
            if (Mode is ComposerVimMode.Visual or ComposerVimMode.VisualLine)
            {
                Mode = ComposerVimMode.Normal;
            }

            _pending = null;
            return true;
        }

        if (_pending is 'f' or 'F' or 't' or 'T' or 'r')
        {
            var pending = _pending.Value;
            _pending = null;
            if (key.Char == '\0') return true;
            if (pending == 'r')
            {
                Buffer.ReplaceChar(key.Char);
                _replaceChar = key.Char;
                _repeat = VimRepeat.ReplaceChar;
                return true;
            }

            var forward = pending is 'f' or 't';
            var till = pending is 't' or 'T';
            Buffer.FindChar(key.Char, forward, till);
            _findChar = key.Char;
            _findKind = pending;
            return true;
        }

        if (key.Left) { Buffer.MoveLeft(); return true; }
        if (key.Right) { Buffer.MoveRight(); return true; }
        if (key.Home) { Buffer.MoveHome(); return true; }
        if (key.End) { Buffer.MoveEnd(); return true; }
        if (key.Delete) { ApplyVisualOr(Buffer.DeleteForward, VimRepeat.DeleteChar); return true; }
        if (key.Backspace) { Buffer.MoveLeft(); return true; }

        var ch = key.Char;
        if (_pending == 'd')
        {
            _pending = null;
            if (ch == 'd')
            {
                Buffer.KillLine();
                _repeat = VimRepeat.DeleteLine;
            }
            else if (ch == 'w')
            {
                Buffer.KillWordForward();
                _repeat = VimRepeat.DeleteWord;
            }
            else if (ch is '$')
            {
                Buffer.KillToEnd();
                _repeat = VimRepeat.DeleteToEnd;
            }

            return true;
        }

        if (_pending == 'c')
        {
            _pending = null;
            if (ch == 'c')
            {
                Buffer.KillLine();
                _repeat = VimRepeat.ChangeLine;
                BeginInsert();
            }
            else if (ch == 'w')
            {
                Buffer.KillWordForward();
                _repeat = VimRepeat.ChangeWord;
                BeginInsert();
            }
            else if (ch is '$')
            {
                Buffer.KillToEnd();
                _repeat = VimRepeat.ChangeToEnd;
                BeginInsert();
            }

            return true;
        }

        if (_pending == 'y')
        {
            _pending = null;
            if (ch == 'y')
            {
                Buffer.YankLine();
                _repeat = VimRepeat.YankLine;
            }
            else if (ch == 'w')
            {
                Buffer.YankWordForward();
                _repeat = VimRepeat.YankWord;
            }

            return true;
        }

        if (_pending == 'g')
        {
            _pending = null;
            if (ch == 'g') Buffer.MoveBufferStart();
            return true;
        }

        if (Mode is ComposerVimMode.Visual or ComposerVimMode.VisualLine)
        {
            return HandleVisual(ch);
        }

        switch (ch)
        {
            case 'i':
                BeginInsert();
                return true;
            case 'a':
                Buffer.MoveRight();
                BeginInsert();
                return true;
            case 'I':
                Buffer.MoveHome();
                BeginInsert();
                return true;
            case 'A':
                Buffer.MoveEnd();
                BeginInsert();
                return true;
            case 'o':
                Buffer.MoveEnd();
                Buffer.Newline();
                BeginInsert();
                return true;
            case 'O':
                Buffer.OpenLineAbove();
                BeginInsert();
                return true;
            case 'G':
                Buffer.MoveBufferEnd();
                return true;
            case 'g':
                _pending = 'g';
                return true;
            case 'h':
                Buffer.MoveLeft();
                return true;
            case 'j':
                Buffer.MoveDown();
                return true;
            case 'k':
                Buffer.MoveUp();
                return true;
            case 'l':
                Buffer.MoveRight();
                return true;
            case '0':
                Buffer.MoveHome();
                return true;
            case '$':
                Buffer.MoveEnd();
                return true;
            case 'w':
                Buffer.MoveWordRight();
                return true;
            case 'b':
                Buffer.MoveWordLeft();
                return true;
            case 'e':
                Buffer.MoveWordEnd();
                return true;
            case 'x':
                Buffer.DeleteForward();
                _repeat = VimRepeat.DeleteChar;
                return true;
            case 's':
                Buffer.DeleteForward();
                _repeat = VimRepeat.Substitute;
                BeginInsert();
                return true;
            case 'C':
                Buffer.KillToEnd();
                _repeat = VimRepeat.ChangeToEnd;
                BeginInsert();
                return true;
            case 'D':
                Buffer.KillToEnd();
                _repeat = VimRepeat.DeleteToEnd;
                return true;
            case 'd':
                _pending = 'd';
                return true;
            case 'c':
                _pending = 'c';
                return true;
            case 'y':
                _pending = 'y';
                return true;
            case 'p':
                Buffer.PasteAfter();
                _repeat = VimRepeat.Paste;
                return true;
            case 'P':
                Buffer.PasteBefore();
                _repeat = VimRepeat.PasteBefore;
                return true;
            case 'u':
                Buffer.Undo();
                return true;
            case 'v':
                Mode = ComposerVimMode.Visual;
                VisualAnchor = Buffer.Cursor;
                return true;
            case 'V':
                Mode = ComposerVimMode.VisualLine;
                VisualAnchor = Buffer.Cursor;
                return true;
            case 'r':
                _pending = 'r';
                return true;
            case 'f':
            case 'F':
            case 't':
            case 'T':
                _pending = ch;
                return true;
            case ';':
                ReplayFind(reverse: false);
                return true;
            case ',':
                ReplayFind(reverse: true);
                return true;
            case '.':
                ReplayLast();
                return true;
            default:
                return true;
        }
    }

    private bool HandleVisual(char ch)
    {
        switch (ch)
        {
            case 'h':
                Buffer.MoveLeft();
                return true;
            case 'j':
                Buffer.MoveDown();
                return true;
            case 'k':
                Buffer.MoveUp();
                return true;
            case 'l':
                Buffer.MoveRight();
                return true;
            case 'w':
                Buffer.MoveWordRight();
                return true;
            case 'b':
                Buffer.MoveWordLeft();
                return true;
            case 'e':
                Buffer.MoveWordEnd();
                return true;
            case '0':
                Buffer.MoveHome();
                return true;
            case '$':
                Buffer.MoveEnd();
                return true;
            case 'G':
                Buffer.MoveBufferEnd();
                return true;
            case 'd':
            case 'x':
                ApplyVisual(delete: true);
                Mode = ComposerVimMode.Normal;
                _repeat = VimRepeat.DeleteChar;
                return true;
            case 'y':
                ApplyVisual(delete: false);
                Mode = ComposerVimMode.Normal;
                _repeat = VimRepeat.YankWord;
                return true;
            case 'c':
            case 's':
                ApplyVisual(delete: true);
                BeginInsert();
                _repeat = VimRepeat.Substitute;
                return true;
            case 'v':
                Mode = Mode == ComposerVimMode.Visual ? ComposerVimMode.Normal : ComposerVimMode.Visual;
                VisualAnchor = Buffer.Cursor;
                return true;
            case 'V':
                Mode = Mode == ComposerVimMode.VisualLine ? ComposerVimMode.Normal : ComposerVimMode.VisualLine;
                VisualAnchor = Buffer.Cursor;
                return true;
            default:
                return true;
        }
    }

    private void ApplyVisualOr(Action fallback, VimRepeat repeat)
    {
        if (Mode is ComposerVimMode.Visual or ComposerVimMode.VisualLine)
        {
            ApplyVisual(delete: true);
            Mode = ComposerVimMode.Normal;
            _repeat = repeat;
            return;
        }

        fallback();
        _repeat = repeat;
    }

    private void ApplyVisual(bool delete)
    {
        int start, end;
        if (Mode == ComposerVimMode.VisualLine)
        {
            (start, end) = Buffer.SpanLines(VisualAnchor, Buffer.Cursor);
        }
        else
        {
            start = Math.Min(VisualAnchor, Buffer.Cursor);
            end = Math.Max(VisualAnchor, Buffer.Cursor);
            if (end == start && end < Buffer.Text.Length) end++;
        }

        if (delete) Buffer.DeleteRange(start, end);
        else Buffer.YankRange(start, end);
    }

    private void ReplayFind(bool reverse)
    {
        if (_findKind == 0) return;
        var kind = _findKind;
        if (reverse)
        {
            kind = kind switch
            {
                'f' => 'F',
                'F' => 'f',
                't' => 'T',
                'T' => 't',
                _ => kind,
            };
        }

        Buffer.FindChar(_findChar, kind is 'f' or 't', kind is 't' or 'T');
    }

    private void ReplayLast()
    {
        switch (_repeat)
        {
            case VimRepeat.DeleteChar:
                Buffer.DeleteForward();
                break;
            case VimRepeat.DeleteLine:
                Buffer.KillLine();
                break;
            case VimRepeat.DeleteWord:
                Buffer.KillWordForward();
                break;
            case VimRepeat.DeleteToEnd:
                Buffer.KillToEnd();
                break;
            case VimRepeat.YankLine:
                Buffer.YankLine();
                break;
            case VimRepeat.YankWord:
                Buffer.YankWordForward();
                break;
            case VimRepeat.Paste:
                Buffer.PasteAfter();
                break;
            case VimRepeat.PasteBefore:
                Buffer.PasteBefore();
                break;
            case VimRepeat.ChangeLine:
                Buffer.KillLine();
                BeginInsert();
                break;
            case VimRepeat.ChangeWord:
                Buffer.KillWordForward();
                BeginInsert();
                break;
            case VimRepeat.ChangeToEnd:
                Buffer.KillToEnd();
                BeginInsert();
                break;
            case VimRepeat.Substitute:
                Buffer.DeleteForward();
                BeginInsert();
                break;
            case VimRepeat.ReplaceChar:
                Buffer.ReplaceChar(_replaceChar);
                break;
            case VimRepeat.InsertText:
                if (!string.IsNullOrEmpty(_lastInsert))
                {
                    Buffer.Insert(_lastInsert);
                }
                break;
        }
    }

    private void BeginInsert()
    {
        _insertBefore = Buffer.Text;
        _insertAt = Buffer.Cursor;
        _insertOpen = true;
        Mode = ComposerVimMode.Insert;
        _pending = null;
    }

    private void CaptureInsert()
    {
        if (!_insertOpen) return;
        _insertOpen = false;
        var before = _insertBefore ?? "";
        var after = Buffer.Text;
        var at = Math.Clamp(_insertAt, 0, before.Length);
        var prefix = before[..at];
        var suffix = before[at..];
        if (after.Length >= prefix.Length + suffix.Length
            && after.StartsWith(prefix, StringComparison.Ordinal)
            && after.EndsWith(suffix, StringComparison.Ordinal))
        {
            var inserted = after.Substring(prefix.Length, after.Length - prefix.Length - suffix.Length);
            if (inserted.Length > 0)
            {
                _lastInsert = inserted;
                _repeat = VimRepeat.InsertText;
            }
        }
    }
}
