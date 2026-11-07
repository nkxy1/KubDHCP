namespace KubDHCP.App.Tui;

internal readonly record struct TuiRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

internal sealed class TuiCanvas
{
    public const ushort Normal = 0x07, Muted = 0x0D, White = 0x0F, Green = 0x0A, Red = 0x0C, Cyan = 0x0B, Yellow = 0x0E;
    public const ushort Card = 0x87, Input = 0x17, Focus = 0xB0, Accent = 0x90, Monitor = 0x0A;
    public int Width { get; }
    public int Height { get; }
    public ConsoleTerminal.Cell[] Cells { get; }
    public TuiRect Clip { get; set; }
    public TuiCanvas(int width, int height)
    {
        Width = Math.Max(1, width); Height = Math.Max(1, height); Cells = new ConsoleTerminal.Cell[Width * Height]; Clip = new(0, 0, Width, Height); Fill(Clip, ' ', Normal);
    }
    public void Put(int x, int y, char c, ushort color = Normal)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height || !Clip.Contains(x, y)) return;
        Cells[y * Width + x] = new() { Character = char.IsControl(c) ? ' ' : c, Attributes = color };
    }
    public void Text(int x, int y, string? text, int maximum = int.MaxValue, ushort color = Normal)
    {
        if (text is null) return;
        var n = 0; foreach (var c in text) { if (n >= maximum) break; Put(x + n++, y, c, color); }
    }
    public void Fill(TuiRect rect, char c = ' ', ushort color = Normal)
    {
        for (var y = Math.Max(0, rect.Y); y < Math.Min(Height, rect.Bottom); y++)
            for (var x = Math.Max(0, rect.X); x < Math.Min(Width, rect.Right); x++) Put(x, y, c, color);
    }
    public void Box(TuiRect rect, string? title = null, ushort color = Normal)
    {
        if (rect.Width < 2 || rect.Height < 2) return; Fill(rect, ' ', color);
        for (var x = rect.X + 1; x < rect.Right - 1; x++) { Put(x, rect.Y, '─', color); Put(x, rect.Bottom - 1, '─', color); }
        for (var y = rect.Y + 1; y < rect.Bottom - 1; y++) { Put(rect.X, y, '│', color); Put(rect.Right - 1, y, '│', color); }
        Put(rect.X, rect.Y, '┌', color); Put(rect.Right - 1, rect.Y, '┐', color); Put(rect.X, rect.Bottom - 1, '└', color); Put(rect.Right - 1, rect.Bottom - 1, '┘', color);
        if (!string.IsNullOrEmpty(title)) Text(rect.X + 2, rect.Y, " " + title + " ", rect.Width - 4, color);
    }
    public string PlainText() => string.Join(Environment.NewLine, Enumerable.Range(0, Height).Select(y => new string(Cells.AsSpan(y * Width, Width).ToArray().Select(c => c.Character).ToArray()).TrimEnd()));
    public static List<string> Wrap(string text, int width)
    {
        width = Math.Max(1, width); var result = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            if (paragraph.Length == 0) result.Add("");
            for (var start = 0; start < paragraph.Length; start += width) result.Add(paragraph.Substring(start, Math.Min(width, paragraph.Length - start)));
        }
        return result;
    }
}

internal sealed class TuiField(string text = "", bool multiline = false)
{
    public string Text = text;
    public int Cursor = text.Length;
    public bool Selected, Multiline = multiline;
    public int ViewStart;
    public int ViewTop;
    public List<(string Text, int Start)> VisualLines = [];
    public Action<string>? Changed;
    public void Set(string text) { Text = text; Cursor = Math.Min(Cursor, text.Length); Selected = false; }
    public void Edit(TerminalInput input)
    {
        if (input.Control && input.Key == 65) { Selected = true; Cursor = Text.Length; return; }
        if (input.Control && input.Key == 67) { ConsoleTerminal.Copy(Text); return; }
        var insert = input.Control && input.Key == 86 ? ConsoleTerminal.Paste() : input.Character >= ' ' && !input.Control ? input.Character.ToString() : "";
        if (!Multiline) insert = insert.Replace("\r", "").Replace("\n", " "); else insert = insert.Replace("\r", "");
        if (insert.Length > 0)
        {
            if (Selected) { Text = ""; Cursor = 0; } Selected = false;
            if (Text.Length + insert.Length > 8192) return;
            Text = Text.Insert(Cursor, insert); Cursor += insert.Length; Changed?.Invoke(Text); return;
        }
        switch (input.Key)
        {
            case 37: Cursor = Math.Max(0, Cursor - 1); Selected = false; return;
            case 39: Cursor = Math.Min(Text.Length, Cursor + 1); Selected = false; return;
            case 36: Cursor = 0; Selected = false; return;
            case 35: Cursor = Text.Length; Selected = false; return;
            case 38 when Multiline:
            {
                var start = Text[..Cursor].LastIndexOf('\n') + 1;
                if (start == 0) { Cursor = 0; return; }
                var previous = Text[..(start - 1)].LastIndexOf('\n') + 1;
                Cursor = previous + Math.Min(Cursor - start, start - previous - 1); Selected = false; return;
            }
            case 40 when Multiline:
            {
                var start = Text[..Cursor].LastIndexOf('\n') + 1;
                var next = Text.IndexOf('\n', Cursor); if (next < 0) { Cursor = Text.Length; return; }
                var end = Text.IndexOf('\n', next + 1); if (end < 0) end = Text.Length;
                Cursor = next + 1 + Math.Min(Cursor - start, end - next - 1); Selected = false; return;
            }
            case 8:
                if (Selected) { Text = ""; Cursor = 0; } else if (Cursor > 0) Text = Text.Remove(--Cursor, 1); break;
            case 46:
                if (Selected) { Text = ""; Cursor = 0; } else if (Cursor < Text.Length) Text = Text.Remove(Cursor, 1); break;
            case 13 when Multiline: if (Selected) { Text = ""; Cursor = 0; } Text = Text.Insert(Cursor++, "\n"); break;
            default: return;
        }
        Selected = false; Changed?.Invoke(Text);
    }
}

internal sealed class TuiControl(string id, TuiRect rect)
{
    public string Id = id;
    public TuiRect Rect = rect;
    public bool Enabled = true;
    public Action? Activate;
    public Action<TerminalInput>? Key, Mouse;
    public TuiField? Field;
}

internal sealed class TuiSurface
{
    public TuiCanvas Canvas { get; private set; } = new(1, 1);
    public readonly List<TuiControl> Controls = [];
    public string? FocusId;
    private string? interactionId;
    private string original = "";
    public bool Editing => interactionId == FocusId && Focused?.Field is not null;
    public bool Interacting => interactionId == FocusId && interactionId is not null;
    public bool EndInteraction(bool cancel = false)
    {
        var wasActive = Interacting;
        if (wasActive && cancel && Focused?.Field is { } field) { field.Set(original); field.Changed?.Invoke(field.Text); }
        interactionId = null; return wasActive;
    }
    public int OffsetY;
    public TuiRect MainViewport;
    public TuiControl? Focused => Controls.FirstOrDefault(c => c.Id == FocusId && c.Enabled);
    public void Begin(int width, int height) { Canvas = new(width, height); Controls.Clear(); OffsetY = 0; MainViewport = new(0, 0, width, height); }
    public TuiRect Rect(int x, int y, int w, int h) => new(x, y - OffsetY, Math.Max(1, w), Math.Max(1, h));
    public void Label(int x, int y, string text, int maximum = int.MaxValue, ushort color = TuiCanvas.Normal) => Canvas.Text(x, y - OffsetY, text, maximum, color);
    public void Box(int x, int y, int w, int h, string? title = null, ushort color = TuiCanvas.Normal) => Canvas.Box(Rect(x, y, w, h), title, color);
    public TuiControl Custom(string id, TuiRect rect, Action<TerminalInput>? key = null, Action<TerminalInput>? mouse = null)
    {
        var control = new TuiControl(id, rect) { Key = key, Mouse = mouse }; Controls.Add(control); return control;
    }
    public TuiControl Button(string id, int x, int y, int width, string title, Action action, bool enabled = true, bool accent = false)
    {
        var rect = Rect(x, y, width, 3); var control = new TuiControl(id, rect) { Enabled = enabled, Activate = action }; Controls.Add(control);
        var color = !enabled ? (ushort)0x88 : FocusId == id ? TuiCanvas.Focus : accent ? TuiCanvas.Accent : TuiCanvas.Card;
        Canvas.Box(rect, color: color); Canvas.Text(rect.X + Math.Max(1, (rect.Width - title.Length) / 2), rect.Y + 1, title, rect.Width - 2, color); return control;
    }
    public void Field(string id, int x, int y, int width, TuiField field, bool enabled = true, int height = 3, bool invalid = false)
    {
        var rect = Rect(x, y, width, height); var control = new TuiControl(id, rect) { Field = field, Enabled = enabled }; Controls.Add(control);
        var color = !enabled ? (ushort)0x18 : FocusId == id ? TuiCanvas.Focus : invalid ? (ushort)0x1C : TuiCanvas.Input;
        Canvas.Box(rect, color: color); var available = Math.Max(1, rect.Width - 2); var start = field.Multiline ? 0 : Math.Max(0, field.Cursor - available + 1);
        field.ViewStart = start;
        var visual = new List<(string Text, int Start)>();
        if (field.Multiline)
        {
            var offset = 0;
            foreach (var paragraph in field.Text.Split('\n'))
            {
                if (paragraph.Length == 0) visual.Add(("", offset));
                for (var i = 0; i < paragraph.Length; i += available) visual.Add((paragraph.Substring(i, Math.Min(available, paragraph.Length - i)), offset + i));
                offset += paragraph.Length + 1;
            }
            if (visual.Count > 0 && visual[^1].Text.Length == available && field.Cursor == field.Text.Length) visual.Add(("", field.Text.Length));
        }
        else visual.Add((field.Text[start..], start));
        field.VisualLines = visual;
        var caretRow = Math.Max(0, visual.FindLastIndex(line => field.Cursor >= line.Start));
        field.ViewTop = Math.Max(0, caretRow - Math.Max(0, height - 3));
        for (var i = 0; i < Math.Min(visual.Count - field.ViewTop, height - 2); i++) Canvas.Text(rect.X + 1, rect.Y + 1 + i, visual[field.ViewTop + i].Text, available, color);
        if (FocusId == id && Editing)
        {
            var cy = caretRow - field.ViewTop; var cx = Math.Min(available - 1, field.Cursor - visual[caretRow].Start);
            var character = field.Cursor < field.Text.Length ? field.Text[field.Cursor] : ' ';
            Canvas.Put(rect.X + 1 + cx, rect.Y + 1 + Math.Max(0, cy), character, 0xF0);
        }
    }
    public void Choice(string id, int x, int y, int width, string text, Action open, bool enabled = true)
    {
        var control = Button(id, x, y, width, "", open, enabled); var color = enabled ? FocusId == id ? TuiCanvas.Focus : TuiCanvas.Input : (ushort)0x18;
        Canvas.Fill(new(control.Rect.X + 1, control.Rect.Y + 1, Math.Max(1, width - 2), 1), ' ', color);
        Canvas.Text(control.Rect.X + 1, control.Rect.Y + 1, text, width - 5, color); Canvas.Text(control.Rect.Right - 3, control.Rect.Y + 1, "v", 1, color);
        control.Key = input => { if (input.Key is 13 or 32 or 38 or 40) open(); };
    }
    public void CheckBox(string id, int x, int y, string text, bool value, Action toggle, bool enabled = true)
    {
        var rect = Rect(x, y, text.Length + 4, 1); var c = new TuiControl(id, rect) { Activate = toggle, Enabled = enabled }; Controls.Add(c);
        Canvas.Text(rect.X, rect.Y, $"[{(value ? 'x' : ' ')}] {text}", rect.Width, !enabled ? TuiCanvas.Muted : FocusId == id ? TuiCanvas.Focus : TuiCanvas.Normal);
    }
    public void EnsureFocus() { if (Focused is null) FocusId = Controls.FirstOrDefault(c => c.Enabled)?.Id; }
    public void NextFocus(bool backwards)
    {
        EndInteraction();
        var available = Controls.Where(c => c.Enabled).ToList(); if (available.Count == 0) return;
        var i = available.FindIndex(c => c.Id == FocusId); FocusId = available[(i + (backwards ? available.Count - 1 : 1) + available.Count) % available.Count].Id;
    }
    private void MoveFocus(ushort key)
    {
        EnsureFocus(); if (Focused is not { } current) return;
        var horizontal = key is 37 or 39; var positive = key is 39 or 40;
        var a = current.Rect;
        var candidates = Controls.Where(c => c.Enabled && c.Id != current.Id).Select(c =>
        {
            var b = c.Rect;
            var forward = horizontal ? (b.X + b.Width / 2d - a.X - a.Width / 2d) : (b.Y + b.Height / 2d - a.Y - a.Height / 2d);
            var gap = horizontal ? Math.Max(0, Math.Max(a.Y - b.Bottom, b.Y - a.Bottom)) : Math.Max(0, Math.Max(a.X - b.Right, b.X - a.Right));
            var cross = horizontal ? Math.Abs(b.Y + b.Height / 2d - a.Y - a.Height / 2d) : Math.Abs(b.X + b.Width / 2d - a.X - a.Width / 2d);
            return (Control: c, Forward: positive ? forward : -forward, Score: gap * 10000 + Math.Abs(forward) * 10 + cross);
        }).Where(c => c.Forward > 0).OrderBy(c => c.Score).FirstOrDefault();
        if (candidates.Control is not null) FocusId = candidates.Control.Id;
    }
    public void Handle(TerminalInput input)
    {
        if (input.Mouse)
        {
            var target = Controls.LastOrDefault(c => c.Enabled && c.Rect.Contains(input.X, input.Y));
            if (target is null) return;
            if (input.Wheel != 0 || input.Move) { target.Mouse?.Invoke(input); return; }
            if ((input.Buttons & 1) != 0)
            {
                if (target.Id != FocusId) EndInteraction();
                FocusId = target.Id;
                if (target.Field is { } field && Editing)
                {
                    field.Selected = false;
                    var row = Math.Clamp(field.ViewTop + input.Y - target.Rect.Y - 1, 0, Math.Max(0, field.VisualLines.Count - 1));
                    var line = field.VisualLines.Count == 0 ? (Text: "", Start: field.ViewStart) : field.VisualLines[row];
                    field.Cursor = Math.Clamp(line.Start + Math.Clamp(input.X - target.Rect.X - 1, 0, line.Text.Length), 0, field.Text.Length);
                }
                else target.Activate?.Invoke();
            }
            if ((input.Buttons & 2) != 0 && target.Field is { } selected) ConsoleTerminal.Copy(selected.Text);
            target.Mouse?.Invoke(input); return;
        }
        if (input.Key == 9) { NextFocus(input.Shift); return; }
        var focus = Focused;
        if (input.Key == 27 && EndInteraction(true)) return;
        if (focus?.Field is { } edit)
        {
            if (Editing)
            {
                if (input.Key == 13 && (!edit.Multiline || input.Control)) EndInteraction();
                else edit.Edit(input);
            }
            else if (input.Key == 13) { original = edit.Text; interactionId = FocusId; }
            else if (input.Key is >= 37 and <= 40) MoveFocus(input.Key);
            return;
        }
        if (input.Key is >= 37 and <= 40 && !Interacting && focus?.Id != "popup-list") { MoveFocus(input.Key); return; }
        if (input.Key == 13 && focus?.Key is not null && focus.Activate is null && focus.Id != "popup-list" && !Interacting) { interactionId = FocusId; return; }
        if (focus?.Key is { } key) { key(input); return; }
        if (input.Key is 13 or 32) focus?.Activate?.Invoke();
    }
}
