using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Calculation as a formula in the "simplified field notation" of form scripts ("Quantity * Price - Discount"): numbers, field names,
/// + - * /, signs and parentheses; multiplication and division before addition and subtraction, operators of equal rank from
/// left to right. Common PDF editors store it as a calculation script (/AA /C) with the formula in a comment followed by the JavaScript
/// generated from it:
/// <c>/** BVCALC (1 - Discount) * Price EVCALC **/event.value=(1-AFMakeNumber(getField("Discount").value))*AFMakeNumber(getField("Price").value)</c>.
/// PDF Knead does the calculation itself (it never executes JavaScript) and writes the script from the parsed expression,
/// never from text: field names only through <see cref="FieldScripts.Quote"/>, numbers only as numbers, operators from a fixed
/// list. In the formula itself, special characters in field names are escaped with "\" (as in common PDF editors: "Total\ 1",
/// "N\.0501").
/// </summary>
public sealed class FieldFormula
{
    private abstract record Node;
    private sealed record Number(double Value) : Node;
    private sealed record Field(string Name) : Node;
    private sealed record Negate(Node Operand) : Node;
    private sealed record Binary(char Operator, Node Left, Node Right) : Node;

    private readonly Node _root;

    private FieldFormula(Node root, int size)
    {
        _root = root;
        Size = size;
        var fields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Collect(root, fields, seen);
        Fields = fields;
        Notation = Write(root, NotationWriter);
    }

    /// <summary>The formula in the notation, written uniformly (spaces around operators, only necessary parentheses, escaping).</summary>
    public string Notation { get; }

    /// <summary>The fields referenced (full names), each once, in order of appearance.</summary>
    public IReadOnlyList<string> Fields { get; }

    /// <summary>Number of tokens, including the end marker (a measure of the work needed for evaluation).</summary>
    public int Size { get; }

    /// <summary>Two formulas are equal if their <see cref="Notation"/> is equal.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><see langword="true"/> for a formula with the same notation.</returns>
    public override bool Equals(object? obj) => obj is FieldFormula other && other.Notation == Notation;

    /// <summary>Hash code of the <see cref="Notation"/>.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => Notation.GetHashCode(StringComparison.Ordinal);

    /// <summary>Returns the <see cref="Notation"/>.</summary>
    /// <returns>The formula in the notation.</returns>
    public override string ToString() => Notation;

    // ------------------------------------------------------------ Parsing

    // Limits against crafted files: length, tokens, nesting depth (the parser is recursive)
    private const int MaxLength = 20_000, MaxTokens = 2_000, MaxDepth = 64;

    private enum TokenKind { Number, Field, Plus, Minus, Times, Divide, Open, Close, End }

    private readonly record struct Token(TokenKind Kind, int At, double Value = 0, string Text = "");

    /// <summary>Parses a formula.</summary>
    /// <param name="text">The formula in simplified field notation; at most 20 000 characters and 2 000 tokens.</param>
    /// <param name="error">The translated error message; <see langword="null"/> on success.</param>
    /// <param name="errorAt">Position of the error (character index from 0); 0 on success.</param>
    /// <returns>The formula; <see langword="null"/> on an error.</returns>
    public static FieldFormula? Parse(string text, out string? error, out int errorAt)
    {
        error = null;
        errorAt = 0;
        if (text.Length > MaxLength)
        {
            error = Tr.T("Die Formel ist zu lang.");
            return null;
        }
        var tokens = new List<Token>();
        if (Tokenize(text, tokens, out error, out errorAt) is false) return null;
        if (tokens.Count == 1)
        {
            error = Tr.T("Die Formel ist leer.");
            return null;
        }
        var parser = new Parser(tokens);
        var root = parser.Expression(0);
        if (parser.Error is null && parser.Current.Kind != TokenKind.End)
            parser.Fail(parser.Current.Kind == TokenKind.Close ? Tr.T("Eine Klammer „)“ zu viel.") : Tr.T("Hier fehlt ein Rechenzeichen (+ - * /)."));
        if (parser.Error is not null || root is null)
        {
            error = parser.Error;
            errorAt = parser.ErrorAt;
            return null;
        }
        return new FieldFormula(root, tokens.Count);
    }

    /// <summary>Parses a formula.</summary>
    /// <param name="text">The formula in simplified field notation.</param>
    /// <returns>The formula; <see langword="null"/> if it is not valid.</returns>
    public static FieldFormula? Parse(string text) => Parse(text, out _, out _);

    /// <summary>
    /// Reads the formula from a calculation script as common PDF editors write it (<c>/** BVCALC … EVCALC **/…</c>). Only the comment is
    /// read; the JavaScript after it is never executed.
    /// </summary>
    /// <param name="script">The calculation script.</param>
    /// <returns>The formula; <see langword="null"/> if the script does not start this way or the formula is not valid.</returns>
    public static FieldFormula? FromScript(string script)
    {
        if (script.Length > MaxLength * 2) return null;
        var span = script.AsSpan().TrimStart();
        if (!span.StartsWith("/**")) return null;
        span = span[3..].TrimStart();
        if (!span.StartsWith("BVCALC")) return null;
        span = span[6..];
        // End of the formula: "EVCALC" before "**/" — escaped, this sequence cannot occur in the formula (spaces and * carry a "\")
        var end = span.IndexOf("EVCALC", StringComparison.Ordinal);
        if (end < 0 || !span[(end + 6)..].TrimStart().StartsWith("**/")) return null;
        return Parse(span[..end].ToString());
    }

    private static bool Tokenize(string text, List<Token> tokens, out string? error, out int errorAt)
    {
        error = null;
        errorAt = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (tokens.Count >= MaxTokens)
            {
                error = Tr.T("Die Formel ist zu lang.");
                errorAt = i;
                return false;
            }
            var kind = c switch
            {
                '+' => TokenKind.Plus,
                '-' => TokenKind.Minus,
                '*' => TokenKind.Times,
                '/' => TokenKind.Divide,
                '(' => TokenKind.Open,
                ')' => TokenKind.Close,
                _ => TokenKind.End,
            };
            if (kind != TokenKind.End)
            {
                tokens.Add(new Token(kind, i));
                i++;
                continue;
            }
            // Word: number or field name; "\" takes the next character into the name (including spaces and operators)
            var start = i;
            var word = new StringBuilder();
            var escaped = false;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('+' or '-' or '*' or '/' or '(' or ')'))
            {
                if (text[i] == '\\')
                {
                    if (i + 1 >= text.Length)
                    {
                        error = Tr.T("Nach „\\“ fehlt ein Zeichen.");
                        errorAt = i;
                        return false;
                    }
                    word.Append(text[i + 1]);
                    escaped = true;
                    i += 2;
                    continue;
                }
                if (text[i] is '=' or '%' or '<' or '>' or '^' or ':' or '~' or '?' or '!' or '"' or '\'' or ';' or '{' or '}' or '[' or ']')
                {
                    error = Tr.T("„{0}“ ist kein Rechenzeichen — erlaubt sind + - * / und Klammern (im Feldnamen mit „\\“ davor).", text[i]);
                    errorAt = i;
                    return false;
                }
                word.Append(text[i]);
                i++;
            }
            var w = word.ToString();
            if (!escaped && ParseNumber(w) is { } number) tokens.Add(new Token(TokenKind.Number, start, number));
            else if (!escaped && char.IsAsciiDigit(w[0]))
            {
                error = Tr.T("„{0}“ ist keine Zahl (Dezimalpunkt oder -komma, etwa 1.5).", w);
                errorAt = start;
                return false;
            }
            else tokens.Add(new Token(TokenKind.Field, start, Text: w));
        }
        tokens.Add(new Token(TokenKind.End, text.Length));
        return true;
    }

    /// <summary>Number "12", "1.5", ".5" or "1,5" (a decimal comma is accepted, a point is written); otherwise null.</summary>
    private static double? ParseNumber(string word)
    {
        if (word.Length == 0 || word.Length > 40) return null;
        var digits = 0;
        var separators = 0;
        foreach (var c in word)
        {
            if (char.IsAsciiDigit(c)) digits++;
            else if (c is '.' or ',') separators++;
            else return null;
        }
        if (digits == 0 || separators > 1) return null;
        return double.Parse(word.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _at;

        public string? Error { get; private set; }

        public int ErrorAt { get; private set; }

        public Token Current => tokens[_at];

        public void Fail(string message)
        {
            if (Error is not null) return;
            Error = message;
            ErrorAt = Current.At;
        }

        // Sum: products joined by + and - (left to right)
        public Node? Expression(int depth)
        {
            var left = Term(depth);
            while (left is not null && Current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                var op = Current.Kind == TokenKind.Plus ? '+' : '-';
                _at++;
                var right = Term(depth);
                if (right is null) return null;
                left = new Binary(op, left, right);
            }
            return left;
        }

        // Product: factors joined by * and /, which bind more tightly
        private Node? Term(int depth)
        {
            var left = Factor(depth);
            while (left is not null && Current.Kind is TokenKind.Times or TokenKind.Divide)
            {
                var op = Current.Kind == TokenKind.Times ? '*' : '/';
                _at++;
                var right = Factor(depth);
                if (right is null) return null;
                left = new Binary(op, left, right);
            }
            return left;
        }

        // Factor: number, field, parenthesis or sign
        private Node? Factor(int depth)
        {
            if (depth > MaxDepth)
            {
                Fail(Tr.T("Zu viele Klammern ineinander."));
                return null;
            }
            var token = Current;
            switch (token.Kind)
            {
                case TokenKind.Number:
                    _at++;
                    return new Number(token.Value);
                case TokenKind.Field:
                    _at++;
                    return new Field(token.Text);
                case TokenKind.Minus or TokenKind.Plus:
                    _at++;
                    var operand = Factor(depth + 1);
                    return operand is null ? null : token.Kind == TokenKind.Minus ? new Negate(operand) : operand;
                case TokenKind.Open:
                    _at++;
                    var inner = Expression(depth + 1);
                    if (inner is null) return null;
                    if (Current.Kind != TokenKind.Close)
                    {
                        Fail(Tr.T("Hier fehlt eine Klammer „)“."));
                        return null;
                    }
                    _at++;
                    return inner;
                case TokenKind.End:
                    Fail(Tr.T("Die Formel endet zu früh — hier fehlt eine Zahl oder ein Feld."));
                    return null;
                default:
                    Fail(Tr.T("Hier fehlt eine Zahl oder ein Feld."));
                    return null;
            }
        }
    }

    private static void Collect(Node node, List<string> fields, HashSet<string> seen)
    {
        switch (node)
        {
            case Field f when seen.Add(f.Name):
                fields.Add(f.Name);
                break;
            case Negate n:
                Collect(n.Operand, fields, seen);
                break;
            case Binary b:
                Collect(b.Left, fields, seen);
                Collect(b.Right, fields, seen);
                break;
        }
    }

    // ------------------------------------------------------------ Evaluation

    /// <summary>
    /// Evaluates the formula with the values of the fields. Division by 0 gives ∞ or NaN; the display then leaves the field
    /// empty.
    /// </summary>
    /// <param name="value">
    /// Supplies the value of a field by full name; empty or unreadable values should be 0, as with the form-script function
    /// <c>AFMakeNumber</c> when the calculation runs in JavaScript.
    /// </param>
    /// <returns>The result.</returns>
    public double Evaluate(Func<string, double> value) => Evaluate(_root, value);

    private static double Evaluate(Node node, Func<string, double> value) => node switch
    {
        Number n => n.Value,
        Field f => value(f.Name),
        Negate n => -Evaluate(n.Operand, value),
        Binary { Operator: '+' } b => Evaluate(b.Left, value) + Evaluate(b.Right, value),
        Binary { Operator: '-' } b => Evaluate(b.Left, value) - Evaluate(b.Right, value),
        Binary { Operator: '*' } b => Evaluate(b.Left, value) * Evaluate(b.Right, value),
        Binary b => Evaluate(b.Left, value) / Evaluate(b.Right, value),
        _ => 0,
    };

    // ------------------------------------------------------------ Writing

    /// <summary>Returns the same formula with one field renamed.</summary>
    /// <param name="oldName">Full name of the field to rename.</param>
    /// <param name="newName">The new full name.</param>
    /// <returns>The new formula.</returns>
    public FieldFormula Renamed(string oldName, string newName) => new(Rename(_root, oldName, newName), Size);

    private static Node Rename(Node node, string oldName, string newName) => node switch
    {
        Field f when f.Name == oldName => new Field(newName),
        Negate n => new Negate(Rename(n.Operand, oldName, newName)),
        Binary b => new Binary(b.Operator, Rename(b.Left, oldName, newName), Rename(b.Right, oldName, newName)),
        _ => node,
    };

    /// <summary>
    /// Builds the calculation script (/AA /C) as common PDF editors do: a comment with the formula, followed by the JavaScript. A "*/"
    /// cannot arise in the comment (operators in names are escaped); it is replaced anyway as a safeguard.
    /// </summary>
    /// <returns>The script.</returns>
    public string Script()
    {
        var notation = Notation.Replace("*/", "*\\/", StringComparison.Ordinal);
        return $"/** BVCALC {notation} EVCALC **/event.value={Write(_root, ScriptWriter)}";
    }

    /// <summary>Escapes a field name for the formula: special characters get a preceding "\", as the notation requires.</summary>
    /// <param name="name">The full field name.</param>
    /// <returns>The escaped name.</returns>
    public static string Escape(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            // White space, operators, parentheses, point and comma (otherwise a number), "\" itself; a digit at the start (otherwise a number)
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '+' or '-' or '*' or '/' or '(' or ')' or '\\' or '.' or ',' or '=' or '%' or '<' or '>'
                    or '^' or ':' or '~' or '?' or '!' or '"' or '\'' or ';' or '{' or '}' or '[' or ']'
                || i == 0 && char.IsAsciiDigit(c))
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private sealed record Writer(Func<string, string> Field, Func<double, string> Number, string Plus, string Minus, string Times, string Divide);

    private static readonly Writer NotationWriter = new(Escape, FormatNumber, " + ", " - ", " * ", " / ");

    // JavaScript: every field through AFMakeNumber (empty → null → 0 in arithmetic), names only as safely escaped string literals
    private static readonly Writer ScriptWriter = new(name => $"AFMakeNumber(getField({FieldScripts.Quote(name)}).value)", FormatNumber, "+", "-", "*", "/");

    private static string FormatNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static int Rank(Node node) => node switch
    {
        Binary { Operator: '+' or '-' } => 1,
        Binary => 2,
        _ => 3,
    };

    // Parentheses only where necessary: around a part that binds more weakly, and to the right of - and / also around one of equal rank
    private static string Write(Node node, Writer w) => node switch
    {
        Number n => w.Number(n.Value),
        Field f => w.Field(f.Name),
        Negate n => "-" + (Rank(n.Operand) < 3 || n.Operand is Negate ? "(" + Write(n.Operand, w) + ")" : Write(n.Operand, w)),
        Binary b => Side(b.Left, Rank(b.Left) < Rank(b), w)
                    + b.Operator switch { '+' => w.Plus, '-' => w.Minus, '*' => w.Times, _ => w.Divide }
                    // A sign on the right always in parentheses: otherwise "a - -b" would become "a--b" in JavaScript (the -- operator)
                    + Side(b.Right, b.Right is Negate || Rank(b.Right) < Rank(b) || Rank(b.Right) == Rank(b) && b.Operator is '-' or '/', w),
        _ => "",
    };

    private static string Side(Node node, bool parenthesize, Writer w) => parenthesize ? "(" + Write(node, w) + ")" : Write(node, w);
}
