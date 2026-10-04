namespace Interfold.DatabaseBootstrap;

/// <summary>Doubles apostrophes in SQL string literals. The bootstrapper's password alphabet excludes the
/// apostrophe, so this is defence-in-depth for operator-supplied values.</summary>
public static class SqlEscape
{
    public static string Literal(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
