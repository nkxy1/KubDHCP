using System.Globalization;
using System.Text;

namespace KubDHCP.App.Tui;

/// <summary>Decodes terminal keys and SGR mouse reports forwarded through SSH/ConPTY.
/// Native console key and mouse records continue to work unchanged.</summary>
internal sealed class TerminalSequences
{
    private readonly StringBuilder pending = new();
    private long last;
    internal IReadOnlyList<TerminalInput> Decode(IEnumerable<TerminalInput> input)
    {
        var result = new List<TerminalInput>();
        if (pending.Length > 0 && Environment.TickCount64 - last > 150) Flush(result);
        foreach (var item in input)
        {
            if (item.Mouse || item.Character == '\0') { Flush(result); result.Add(item); continue; }
            if (pending.Length == 0 && item.Character != '\u001b')
            {
                // Unix terminals send control characters without Windows virtual keys.
                result.Add(item.Character is >= '\x01' and <= '\x1a' && item.Character is not ('\t' or '\r' or '\n' or '\b')
                    ? new(Key: (ushort)(64 + item.Character), Control: true) : item); continue;
            }
            pending.Append(item.Character); last = Environment.TickCount64;
            if (pending.Length == 1) continue;
            var text = pending.ToString();
            if (pending.Length == 2 && item.Character is '[' or 'O') continue;
            if (text.StartsWith("\u001b[<", StringComparison.Ordinal))
            {
                if (item.Character is not ('M' or 'm')) { if (pending.Length > 64) pending.Clear(); continue; }
                var values = text[3..^1].Split(';');
                if (values.Length == 3 && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var button)
                    && int.TryParse(values[1], out var x) && int.TryParse(values[2], out var y))
                    result.Add(new(X: Math.Max(0, x - 1), Y: Math.Max(0, y - 1), Mouse: true,
                        Buttons: item.Character == 'm' || (button & 64) != 0 ? 0 : (button & 3) switch { 0 => 1, 1 => 4, 2 => 2, _ => 0 },
                        Wheel: (button & 64) == 0 ? 0 : (button & 1) == 0 ? 1 : -1, Move: (button & 32) != 0));
                pending.Clear(); continue;
            }
            if (item.Character is >= '@' and <= '~' && pending.Length > 2)
            {
                var suffix = text[2..];
                var key = item.Character switch { 'A' => 38, 'B' => 40, 'C' => 39, 'D' => 37, 'H' => 36, 'F' => 35, 'Z' => 9, 'P' => 112, 'Q' => 113, 'R' => 114, 'S' => 115, _ => 0 };
                if (item.Character == '~') key = suffix.Split(';')[0].TrimEnd('~') switch { "1" or "7" => 36, "4" or "8" => 35, "3" => 46, "5" => 33, "6" => 34, "11" => 112, "21" => 121, _ => 0 };
                var modifier = 1;
                var parts = suffix.TrimEnd(item.Character).Split(';'); if (parts.Length > 1) int.TryParse(parts[^1], out modifier);
                if (key != 0) result.Add(new(Key: (ushort)key, Shift: item.Character == 'Z' || ((modifier - 1) & 1) != 0, Control: ((modifier - 1) & 4) != 0));
                pending.Clear();
            }
            else if (pending.Length == 2 && item.Character is not ('[' or 'O')) Flush(result);
        }
        return result;
    }
    private void Flush(List<TerminalInput> result)
    {
        if (pending.Length == 1) result.Add(new(Key: 27, Character: '\u001b'));
        pending.Clear();
    }
}
