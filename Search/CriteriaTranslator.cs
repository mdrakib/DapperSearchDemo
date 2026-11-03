using System.Globalization;
using System.Text;

namespace DapperSearchDemo.Search;

/// <summary>
/// Translates a Dynamic-LINQ style criteria string (the format eDC stores in tenant configuration and
/// passes to <c>queryable.Where(criteria, propertyTranslator, db)</c>) straight into a SQL predicate.
///
/// Supported subset:
///   comparisons      ==  =  !=  &lt;&gt;  &gt;  &gt;=  &lt;  &lt;=     (null-aware, C# semantics)
///   logic            &amp;&amp;  ||  !  and  or  not  ( )
///   literals         "text"  123  1.5  true  false  null
///   string methods   .Contains("x")  .StartsWith("x")  .EndsWith("x")  .ToUpper()  .ToLower()
///   collections      .Any()  .Any(pred)  .All(pred)  .Count()  .Count(pred)  .Where(pred)  (chainable)
///   navigation       Consolidation.TransportMode        (single related row)
///
/// Inside a collection lambda, identifiers resolve against the element, as in Dynamic LINQ:
///   Containers.Where(ContainerType == "40HC").Count() &gt; 1
///
/// Not supported here (but used by eDC criteria today): Sum/Min/Max/Average, FirstOrDefault().X,
/// collections of scalars with implicit `it` (OrderNumberList.Any(Contains("x"))), arithmetic, casts,
/// DateTime functions, ternaries, virtual properties (CustomFields.X). Each one is more parser + SQL work.
/// </summary>
public sealed class CriteriaTranslator
{
    private readonly SqlFilterBuilder _builder;
    private List<Token> _tokens = new();
    private int _pos;
    private string _text = "";

    public CriteriaTranslator(SqlFilterBuilder builder) => _builder = builder;

    public string Translate(SearchMap map, string alias, string criteria)
    {
        _text = criteria;
        _tokens = Tokenize(criteria);
        _pos = 0;
        var result = ParseOr(new Scope(map, alias));
        if (Peek.Kind != TokenKind.End) throw Error($"Unexpected '{Peek.Text}'");
        return AsPredicate(result);
    }

    // ---------------------------------------------------------------- values produced while parsing

    private abstract record Val;
    /// <summary>A scalar SQL expression (column, sub-select, parameter).</summary>
    private sealed record Scalar(string Sql, bool IsNullLiteral = false) : Val;
    /// <summary>A SQL boolean predicate.</summary>
    private sealed record Predicate(string Sql) : Val;
    /// <summary>A set of rows (collection) with the map that describes its columns.</summary>
    private sealed record RowSet(string SourceSql, SearchMap Element, string Name) : Val;
    /// <summary>A single related row (navigation).</summary>
    private sealed record Row(string SourceSql, SearchMap Target, string Name) : Val;

    private sealed record Scope(SearchMap Map, string Alias);

    // ---------------------------------------------------------------- grammar

    private Val ParseOr(Scope scope)
    {
        var left = ParseAnd(scope);
        while (Accept("||") || AcceptWord("or"))
            left = new Predicate($"({AsPredicate(left)} OR {AsPredicate(ParseAnd(scope))})");
        return left;
    }

    private Val ParseAnd(Scope scope)
    {
        var left = ParseUnary(scope);
        while (Accept("&&") || AcceptWord("and"))
            left = new Predicate($"({AsPredicate(left)} AND {AsPredicate(ParseUnary(scope))})");
        return left;
    }

    private Val ParseUnary(Scope scope)
    {
        if (Accept("!") || AcceptWord("not"))
            return new Predicate($"NOT {AsPredicate(ParseUnary(scope))}");
        return ParseComparison(scope);
    }

    private Val ParseComparison(Scope scope)
    {
        var left = ParsePrimary(scope);
        var op = Peek.Text;
        if (Peek.Kind != TokenKind.Operator || op is not ("==" or "=" or "!=" or "<>" or ">" or ">=" or "<" or "<="))
            return left;
        Next();
        var right = ParsePrimary(scope);
        return Compare(AsScalar(left), op, AsScalar(right));
    }

    private Val ParsePrimary(Scope scope)
    {
        var t = Next();
        switch (t.Kind)
        {
            case TokenKind.String:
                return new Scalar(_builder.AddParameter(t.Text));
            case TokenKind.Number:
                return new Scalar(_builder.AddParameter(
                    t.Text.Contains('.') ? decimal.Parse(t.Text, CultureInfo.InvariantCulture)
                                         : long.Parse(t.Text, CultureInfo.InvariantCulture)));
            case TokenKind.Operator when t.Text == "(":
                var inner = ParseOr(scope);
                Expect(")");
                return inner;
            case TokenKind.Identifier when t.Text is "null":
                return new Scalar("NULL", IsNullLiteral: true);
            case TokenKind.Identifier when t.Text is "true" or "false":
                return new Scalar(t.Text == "true" ? "1" : "0");
            case TokenKind.Identifier:
                return ParseMemberChain(ResolveMember(scope.Map, scope.Alias, t), scope);
            default:
                throw Error($"Unexpected '{t.Text}'", t);
        }
    }

    /// <summary>Handles ".Member" and ".Method(...)" after a value.</summary>
    private Val ParseMemberChain(Val current, Scope scope)
    {
        while (Accept("."))
        {
            var name = Next();
            if (name.Kind != TokenKind.Identifier) throw Error("Member name expected", name);

            if (Peek.Text == "(")
            {
                current = CallMethod(current, name);
                continue;
            }

            current = current switch
            {
                Row row => MemberOfRow(row, name),
                _ => throw Error($"'{name.Text}' cannot be accessed on this value", name),
            };
        }
        return current;
    }

    private Val CallMethod(Val target, Token method)
    {
        Expect("(");
        Val result;
        switch (target, method.Text)
        {
            // ---- string methods
            case (Scalar s, "Contains"):
                result = Like(s, ArgString(), "%", "%"); break;
            case (Scalar s, "StartsWith"):
                result = Like(s, ArgString(), "", "%"); break;
            case (Scalar s, "EndsWith"):
                result = Like(s, ArgString(), "%", ""); break;
            case (Scalar s, "ToUpper"):
                result = new Scalar($"UPPER({s.Sql})"); break;
            case (Scalar s, "ToLower"):
                result = new Scalar($"LOWER({s.Sql})"); break;

            // ---- collection methods (each one wraps the row set in a sub-query)
            case (RowSet set, "Where"):
            {
                var a = _builder.NextAlias("w");
                var pred = AsPredicate(ParseOr(new Scope(set.Element, a)));
                result = set with { SourceSql = $"SELECT {a}.* FROM ({set.SourceSql}) AS {a} WHERE {pred}" };
                break;
            }
            case (RowSet set, "Any"):
            {
                var a = _builder.NextAlias("a");
                result = Peek.Text == ")"
                    ? new Predicate($"EXISTS (SELECT 1 FROM ({set.SourceSql}) AS {a})")
                    : new Predicate($"EXISTS (SELECT 1 FROM ({set.SourceSql}) AS {a} WHERE {AsPredicate(ParseOr(new Scope(set.Element, a)))})");
                break;
            }
            case (RowSet set, "All"):
            {
                // C# All(): an element whose predicate is NULL in SQL (e.g. NULL column) counts as "false",
                // so use CASE instead of NOT(pred), which would silently skip those rows.
                var a = _builder.NextAlias("a");
                var pred = AsPredicate(ParseOr(new Scope(set.Element, a)));
                result = new Predicate($"NOT EXISTS (SELECT 1 FROM ({set.SourceSql}) AS {a} WHERE CASE WHEN {pred} THEN 1 ELSE 0 END = 0)");
                break;
            }
            case (RowSet set, "Count"):
            {
                var a = _builder.NextAlias("n");
                result = Peek.Text == ")"
                    ? new Scalar($"(SELECT COUNT(*) FROM ({set.SourceSql}) AS {a})")
                    : new Scalar($"(SELECT COUNT(*) FROM ({set.SourceSql}) AS {a} WHERE {AsPredicate(ParseOr(new Scope(set.Element, a)))})");
                break;
            }
            default:
                throw Error($"Method '{method.Text}' is not supported on {Describe(target)}", method);
        }
        Expect(")");
        return result;
    }

    /// <summary>Resolves an identifier against the whitelist of the current scope.</summary>
    private Val ResolveMember(SearchMap map, string alias, Token name)
    {
        if (map.TryGetScalar(name.Text, out var scalar)) return new Scalar(scalar.Sql(alias));
        if (map.TryGetCollection(name.Text, out var coll)) return new RowSet(coll.Source(alias), coll.ElementMap, coll.Name);
        if (map.TryGetReference(name.Text, out var reference)) return new Row(reference.Source(alias), reference.Target(), reference.Name);
        throw Error($"'{name.Text}' is not a searchable field of {map.EntityName}", name);
    }

    /// <summary>Consolidation.TransportMode / Consolidation.Shipments / Consolidation.Agent.Code ...</summary>
    private Val MemberOfRow(Row row, Token name)
    {
        var a = _builder.NextAlias("r");
        return ResolveMember(row.Target, a, name) switch
        {
            Scalar s => new Scalar($"(SELECT TOP 1 {s.Sql} FROM ({row.SourceSql}) AS {a})"),
            RowSet set => set with { SourceSql = $"SELECT x_{a}.* FROM ({row.SourceSql}) AS {a} CROSS APPLY ({set.SourceSql}) AS x_{a}" },
            Row next => next with { SourceSql = $"SELECT TOP 1 x_{a}.* FROM ({row.SourceSql}) AS {a} CROSS APPLY ({next.SourceSql}) AS x_{a}" },
            _ => throw Error($"Unsupported member '{name.Text}'", name),
        };
    }

    // ---------------------------------------------------------------- SQL helpers

    private Predicate Compare(Scalar l, string op, Scalar r)
    {
        // C# / EF semantics: x == null -> IS NULL, x != "A" also matches NULL.
        if (r.IsNullLiteral || l.IsNullLiteral)
        {
            var col = r.IsNullLiteral ? l : r;
            return op switch
            {
                "==" or "=" => new Predicate($"{col.Sql} IS NULL"),
                "!=" or "<>" => new Predicate($"{col.Sql} IS NOT NULL"),
                _ => throw Error($"Operator '{op}' cannot be used with null"),
            };
        }
        return op switch
        {
            "==" or "=" => new Predicate($"{l.Sql} = {r.Sql}"),
            "!=" or "<>" => new Predicate($"({l.Sql} <> {r.Sql} OR {l.Sql} IS NULL OR {r.Sql} IS NULL)"),
            _ => new Predicate($"{l.Sql} {op} {r.Sql}"),
        };
    }

    private Predicate Like(Scalar s, string value, string prefix, string suffix) =>
        new($"{s.Sql} LIKE {_builder.AddParameter(prefix + SqlFilterBuilder.EscapeLike(value) + suffix)} ESCAPE '\\'");

    private string ArgString()
    {
        var t = Next();
        return t.Kind == TokenKind.String ? t.Text : throw Error("String literal expected", t);
    }

    private string AsPredicate(Val v) => v switch
    {
        Predicate p => p.Sql,
        _ => throw Error($"Expected a true/false condition but found {Describe(v)}"),
    };

    private Scalar AsScalar(Val v) => v switch
    {
        Scalar s => s,
        _ => throw Error($"Expected a value but found {Describe(v)}"),
    };

    private static string Describe(Val v) => v switch
    {
        RowSet r => $"collection '{r.Name}' (use Any/All/Count/Where)",
        Row r => $"related entity '{r.Name}' (access one of its fields)",
        Predicate => "a condition",
        _ => "a value",
    };

    // ---------------------------------------------------------------- tokenizer

    private enum TokenKind { Identifier, String, Number, Operator, End }
    private sealed record Token(TokenKind Kind, string Text, int Position);

    private Token Peek => _tokens[_pos];
    private Token Next() => _tokens[Math.Min(_pos++, _tokens.Count - 1)];
    private bool Accept(string op) { if (Peek.Kind == TokenKind.Operator && Peek.Text == op) { _pos++; return true; } return false; }
    private bool AcceptWord(string w) { if (Peek.Kind == TokenKind.Identifier && Peek.Text.Equals(w, StringComparison.OrdinalIgnoreCase)) { _pos++; return true; } return false; }
    private void Expect(string op) { if (!Accept(op)) throw Error($"'{op}' expected"); }

    private CriteriaException Error(string message, Token? at = null)
    {
        var pos = (at ?? Peek).Position;
        return new CriteriaException($"{message} at position {pos}: {_text}\n{new string(' ', pos + message.Length + $" at position {pos}: ".Length)}^");
    }

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            var start = i;

            if (char.IsLetter(c) || c == '_')
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                tokens.Add(new(TokenKind.Identifier, s[start..i], start));
            }
            else if (char.IsDigit(c))
            {
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                tokens.Add(new(TokenKind.Number, s[start..i], start));
            }
            else if (c is '"' or '\'')
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != c)
                {
                    if (s[i] == '\\' && i + 1 < s.Length) i++;
                    sb.Append(s[i++]);
                }
                if (i >= s.Length) throw new CriteriaException($"Unterminated string at position {start}: {s}");
                i++;
                tokens.Add(new(TokenKind.String, sb.ToString(), start));
            }
            else
            {
                var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (two is "==" or "!=" or "<>" or ">=" or "<=" or "&&" or "||") { tokens.Add(new(TokenKind.Operator, two, start)); i += 2; }
                else if ("=<>!().,".Contains(c)) { tokens.Add(new(TokenKind.Operator, c.ToString(), start)); i++; }
                else throw new CriteriaException($"Unexpected character '{c}' at position {i}: {s}");
            }
        }
        tokens.Add(new(TokenKind.End, "<end>", s.Length));
        return tokens;
    }
}

public sealed class CriteriaException(string message) : Exception(message);
