namespace Interfold.Contracts;

/// <summary>
/// Persistence backend selection sourced from the <c>OCTOCON_PERSISTENCE</c> env var.
/// The wire string form is bespoke (not <see cref="System.Text.Json.JsonNamingPolicy.SnakeCaseLower"/>-compatible)
/// so parsing lives in <see cref="PersistenceModeExtensions"/>.
/// </summary>
public enum PersistenceMode
{
    InMemory,
    ScyllaPostgres,
}

/// <summary>
/// Wire-string translation for <see cref="PersistenceMode"/>. Kept explicit so a rename to any
/// enum member here does NOT silently shift the env-var / deployment contract.
/// </summary>
public static class PersistenceModeExtensions
{
    /// <summary>The canonical <c>OCTOCON_PERSISTENCE</c> value for the ScyllaDB + Postgres stack.</summary>
    public const string ScyllaPostgresWireValue = "scylla-postgres";

    /// <summary>The canonical <c>OCTOCON_PERSISTENCE</c> value for the in-memory stack.</summary>
    public const string InMemoryWireValue = "inmemory";

    /// <summary>Emits the canonical <c>OCTOCON_PERSISTENCE</c> string form.</summary>
    public static string ToWireValue(this PersistenceMode mode) => mode switch
    {
        PersistenceMode.ScyllaPostgres => ScyllaPostgresWireValue,
        PersistenceMode.InMemory => InMemoryWireValue,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unhandled PersistenceMode."),
    };

    /// <summary>
    /// Parses a raw <c>OCTOCON_PERSISTENCE</c> env-var value. Null / empty / whitespace resolves
    /// to <see cref="PersistenceMode.ScyllaPostgres"/> (the historical default). Unknown values
    /// throw so an operator typo lands at boot rather than as a silent switch to the wrong backend.
    /// </summary>
    public static PersistenceMode Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return PersistenceMode.ScyllaPostgres;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            ScyllaPostgresWireValue => PersistenceMode.ScyllaPostgres,
            InMemoryWireValue => PersistenceMode.InMemory,
            var unknown => throw new InvalidOperationException(
                $"Unsupported OCTOCON_PERSISTENCE value '{unknown}'. Expected: {ScyllaPostgresWireValue} | {InMemoryWireValue}."),
        };
    }
}
