using System.Text;

namespace PdfKnead.Cli;

/// <summary>
/// Output without control characters: everything below 0x20 except line breaks and tab, plus DEL and C1 (0x7F–0x9F), becomes
/// "�". The command line prints content of untrusted files; escape sequences in it would otherwise be executed by the terminal.
/// </summary>
internal sealed class SafeConsoleWriter(TextWriter inner) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;

    private static char Safe(char c) => c is '\n' or '\r' or '\t' || (c >= ' ' && c is < '\u007F' or > '\u009F') ? c : '\uFFFD';

    public override void Write(char value) => inner.Write(Safe(value));

    public override void Write(string? value)
    {
        if (value is null) return;
        foreach (var c in value)
            if (Safe(c) != c)
            {
                inner.Write(string.Create(value.Length, value, static (span, s) => { for (var i = 0; i < s.Length; i++) span[i] = Safe(s[i]); }));
                return;
            }
        inner.Write(value);
    }

    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    public override void WriteLine(string? value)
    {
        Write(value);
        inner.WriteLine();
    }

    public override void Flush() => inner.Flush();
}
