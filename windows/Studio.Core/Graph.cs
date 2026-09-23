using System.Globalization;

namespace Lernheft.Studio;

/// <summary>
/// Rechnet Funktionsterme aus, die man eintippt: „x^2", „0,5x²-2x+1", „sin(x)".
/// Erlaubt sind + - * / ^, Klammern, sin cos tan sqrt abs ln log exp, pi und e.
/// Dieselbe Rechnung steckt in der iPad-App.
/// </summary>
public static class MathExpression
{
    public class ParseException(string message) : Exception(message);

    public static Func<double, double?> Compile(string text)
    {
        var cleaned = text.ToLowerInvariant();
        foreach (var (from, to) in new[]
                 {
                     ("f(x)=", ""), ("y=", ""), ("f(x) =", ""), ("y =", ""),
                     ("²", "^2"), ("³", "^3"), ("·", "*"), ("×", "*"), ("÷", "/"),
                     ("−", "-"), ("–", "-"), ("√", "sqrt"), ("π", "pi"), (",", ".")
                 })
        {
            cleaned = cleaned.Replace(from, to);
        }
        cleaned = cleaned.Replace(" ", "");
        if (cleaned.Length == 0) throw new ParseException("Tipp einen Funktionsterm ein, z. B. x^2 - 2x + 1");

        var parser = new Parser(Tokenize(cleaned));
        var node = parser.ParseExpression();
        if (!parser.AtEnd) throw new ParseException($"Das versteh ich nicht: „{parser.Rest}“");
        return x =>
        {
            var value = node.Value(x);
            return double.IsFinite(value) ? value : null;
        };
    }

    public static bool TryCompile(string text, out Func<double, double?>? function, out string? error)
    {
        try
        {
            function = Compile(text);
            error = null;
            return true;
        }
        catch (ParseException exception)
        {
            function = null;
            error = exception.Message;
            return false;
        }
    }

    private enum Kind { Number, Variable, Identifier, Symbol }

    private readonly record struct Token(Kind Kind, double Number = 0, string Name = "", char Symbol = '\0');

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (char.IsDigit(character) || character == '.')
            {
                var start = index;
                while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.')) index++;
                var literal = text[start..index];
                if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    throw new ParseException($"Das versteh ich nicht: „{literal}“");
                tokens.Add(new Token(Kind.Number, value));
                continue;
            }
            if (char.IsLetter(character))
            {
                var start = index;
                while (index < text.Length && char.IsLetter(text[index])) index++;
                var name = text[start..index];
                // „2xsin(x)" oder „xx" soll wie 2·x·sin(x) rechnen.
                while (name.Length > 1 && name.StartsWith('x') && !IsKnown(name))
                {
                    tokens.Add(new Token(Kind.Variable));
                    name = name[1..];
                }
                tokens.Add(name == "x" ? new Token(Kind.Variable) : new Token(Kind.Identifier, Name: name));
                continue;
            }
            if ("+-*/^()".Contains(character))
            {
                tokens.Add(new Token(Kind.Symbol, Symbol: character));
                index++;
                continue;
            }
            throw new ParseException($"Das versteh ich nicht: „{character}“");
        }
        return tokens;
    }

    private static readonly string[] Functions = { "sin", "cos", "tan", "sqrt", "wurzel", "abs", "ln", "log", "exp" };

    private static bool IsKnown(string name) => name is "pi" or "e" || Functions.Contains(name);

    private abstract record Node
    {
        public abstract double Value(double x);
    }

    private sealed record Constant(double Number) : Node
    {
        public override double Value(double x) => Number;
    }

    private sealed record Variable : Node
    {
        public override double Value(double x) => x;
    }

    private sealed record Unary(string Name, Node Inner) : Node
    {
        public override double Value(double x)
        {
            var v = Inner.Value(x);
            return Name switch
            {
                "neg" => -v,
                "sin" => Math.Sin(v),
                "cos" => Math.Cos(v),
                "tan" => Math.Tan(v),
                "sqrt" or "wurzel" => v < 0 ? double.NaN : Math.Sqrt(v),
                "abs" => Math.Abs(v),
                "ln" => v <= 0 ? double.NaN : Math.Log(v),
                "log" => v <= 0 ? double.NaN : Math.Log10(v),
                "exp" => Math.Exp(v),
                _ => double.NaN
            };
        }
    }

    private sealed record Binary(char Operation, Node Left, Node Right) : Node
    {
        public override double Value(double x)
        {
            var a = Left.Value(x);
            var b = Right.Value(x);
            return Operation switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                '/' => b == 0 ? double.NaN : a / b,
                '^' => a < 0 && b != Math.Round(b) ? double.NaN : Math.Pow(a, b),
                _ => double.NaN
            };
        }
    }

    private class Parser(List<Token> tokens)
    {
        private int _index;

        public bool AtEnd => _index >= tokens.Count;

        public string Rest
        {
            get
            {
                if (AtEnd) return "";
                var token = tokens[_index];
                return token.Kind switch
                {
                    Kind.Number => token.Number.ToString(CultureInfo.InvariantCulture),
                    Kind.Variable => "x",
                    Kind.Identifier => token.Name,
                    _ => token.Symbol.ToString()
                };
            }
        }

        private Token? Peek() => AtEnd ? null : tokens[_index];

        private bool IsSymbol(char symbol) => Peek() is { Kind: Kind.Symbol } token && token.Symbol == symbol;

        public Node ParseExpression()
        {
            var node = ParseTerm();
            while (IsSymbol('+') || IsSymbol('-'))
            {
                var operation = tokens[_index++].Symbol;
                node = new Binary(operation, node, ParseTerm());
            }
            return node;
        }

        private Node ParseTerm()
        {
            var node = ParsePower();
            while (Peek() is { } token)
            {
                if (token.Kind == Kind.Symbol && token.Symbol is '*' or '/')
                {
                    _index++;
                    node = new Binary(token.Symbol, node, ParsePower());
                    continue;
                }
                // Verstecktes Malzeichen: 2x, 3(x+1), 2sin(x)
                var startsValue = token.Kind is Kind.Number or Kind.Variable or Kind.Identifier
                                  || (token.Kind == Kind.Symbol && token.Symbol == '(');
                if (!startsValue) break;
                node = new Binary('*', node, ParsePower());
            }
            return node;
        }

        private Node ParsePower()
        {
            var basis = ParseUnary();
            if (IsSymbol('^'))
            {
                _index++;
                return new Binary('^', basis, ParsePowerExponent());
            }
            return basis;
        }

        /// <summary>Im Exponenten darf ein Vorzeichen stehen: 2^-x.</summary>
        private Node ParsePowerExponent()
        {
            if (IsSymbol('-'))
            {
                _index++;
                return new Unary("neg", ParsePowerExponent());
            }
            return ParsePower();
        }

        private Node ParseUnary()
        {
            if (IsSymbol('-'))
            {
                _index++;
                // -x^2 ist -(x^2), wie in der Schule.
                return new Unary("neg", ParsePower());
            }
            if (IsSymbol('+'))
            {
                _index++;
                return ParsePower();
            }
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            if (Peek() is not { } token) throw new ParseException("Der Term hört mittendrin auf.");
            switch (token.Kind)
            {
                case Kind.Number:
                    _index++;
                    return new Constant(token.Number);
                case Kind.Variable:
                    _index++;
                    return new Variable();
                case Kind.Identifier:
                    _index++;
                    if (token.Name == "pi") return new Constant(Math.PI);
                    if (token.Name == "e") return new Constant(Math.E);
                    if (!Functions.Contains(token.Name)) throw new ParseException($"Das versteh ich nicht: „{token.Name}“");
                    return new Unary(token.Name, ParsePrimary());
                case Kind.Symbol when token.Symbol == '(':
                    _index++;
                    var inner = ParseExpression();
                    if (!IsSymbol(')')) throw new ParseException("Da fehlt eine Klammer „)“.");
                    _index++;
                    return inner;
                default:
                    throw new ParseException($"Das versteh ich nicht: „{token.Symbol}“");
            }
        }
    }
}

/// <summary>
/// Baut aus einer Funktion echte Striche: Koordinatensystem mit Pfeilen, Skalenstriche,
/// Zahlen an den Achsen (als Textfelder) und den Graphen selbst.
/// </summary>
public static class GraphBuilder
{
    public record Settings(
        double XMin = -5, double XMax = 5, double YMin = -5, double YMax = 5,
        double Unit = 32, bool DrawAxes = true, bool DrawLabels = true,
        string Color = "#1A1F2B", double LineWidth = 2.4);

    public record Label(double X, double Y, double Width, string Text);

    public record Result(List<InkStroke> Strokes, List<Label> Labels, double Width, double Height);

    private const string AxisColor = "#1A1F2B";

    /// <summary><paramref name="originX"/>/<paramref name="originY"/> ist der Nullpunkt auf der Seite.</summary>
    public static Result Build(Func<double, double?>? function, Settings settings, double originX, double originY)
    {
        var strokes = new List<InkStroke>();
        var labels = new List<Label>();
        var unit = settings.Unit;
        var width = (settings.XMax - settings.XMin) * unit;
        var height = (settings.YMax - settings.YMin) * unit;

        (double X, double Y) Point(double x, double y) => (originX + x * unit, originY - y * unit);

        InkStroke Line(string color, double lineWidth, string kind, params (double X, double Y)[] points)
        {
            var stroke = new InkStroke { Id = InkIds.New(), Color = color, Width = lineWidth, Kind = kind };
            foreach (var (x, y) in points) stroke.Add(x, y, 0.8);
            return stroke;
        }

        if (settings.DrawAxes)
        {
            var left = Point(settings.XMin, 0);
            var right = Point(settings.XMax, 0);
            strokes.Add(Line(AxisColor, 1.8, "shape", left, right));
            strokes.Add(Line(AxisColor, 1.8, "shape", (right.X - 9, right.Y - 6), right, (right.X - 9, right.Y + 6)));

            var bottom = Point(0, settings.YMin);
            var top = Point(0, settings.YMax);
            strokes.Add(Line(AxisColor, 1.8, "shape", bottom, top));
            strokes.Add(Line(AxisColor, 1.8, "shape", (top.X - 6, top.Y + 9), top, (top.X + 6, top.Y + 9)));

            var step = Math.Max(1, (int)Math.Round(Math.Max(settings.XMax - settings.XMin, settings.YMax - settings.YMin) / 12));
            for (var value = (int)Math.Round(settings.XMin); value <= (int)Math.Round(settings.XMax); value += step)
            {
                if (value == 0) continue;
                var at = Point(value, 0);
                strokes.Add(Line(AxisColor, 1.5, "shape", (at.X, at.Y - 4), (at.X, at.Y + 4)));
                if (settings.DrawLabels) labels.Add(new Label(at.X - 10, at.Y + 5, 34, value.ToString(CultureInfo.InvariantCulture)));
            }
            for (var value = (int)Math.Round(settings.YMin); value <= (int)Math.Round(settings.YMax); value += step)
            {
                if (value == 0) continue;
                var at = Point(0, value);
                strokes.Add(Line(AxisColor, 1.5, "shape", (at.X - 4, at.Y), (at.X + 4, at.Y)));
                if (settings.DrawLabels) labels.Add(new Label(at.X - 32, at.Y - 11, 28, value.ToString(CultureInfo.InvariantCulture)));
            }
        }

        if (function is not null)
        {
            // In feinen Schritten abtasten; bei Lücken und Sprüngen neu ansetzen.
            var steps = Math.Max(2, (int)width);
            var segment = new InkStroke { Id = InkIds.New(), Color = settings.Color, Width = settings.LineWidth, Kind = "pen" };
            double? lastY = null;
            for (var index = 0; index <= steps; index++)
            {
                var x = settings.XMin + (settings.XMax - settings.XMin) * index / steps;
                var y = function(x);
                var inside = y is double value && value >= settings.YMin - 0.5 && value <= settings.YMax + 0.5;
                var jump = inside && lastY is double previous && Math.Abs(y!.Value - previous) > (settings.YMax - settings.YMin) * 0.6;
                if (!inside || jump)
                {
                    if (segment.Count >= 2) strokes.Add(segment);
                    segment = new InkStroke { Id = InkIds.New(), Color = settings.Color, Width = settings.LineWidth, Kind = "pen" };
                    lastY = inside ? y : null;
                    if (!inside) continue;
                }
                var (px, py) = Point(x, y!.Value);
                segment.Add(px, py, 0.9);
                lastY = y;
            }
            if (segment.Count >= 2) strokes.Add(segment);
        }
        return new Result(strokes, labels, width, height);
    }
}
