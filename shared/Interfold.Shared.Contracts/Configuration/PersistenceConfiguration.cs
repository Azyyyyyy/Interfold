namespace Interfold.Shared.Contracts.Configuration;

using System.ComponentModel.DataAnnotations;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration.Validation;

/// <summary>Persistence + retry configuration. Bound from OCTOCON_ env vars.
/// <c>ScyllaKeyspace</c> unifies session default keyspace and region routing;
/// Scylla connection secrets live in the secrets store.</summary>
public sealed class PersistenceConfiguration : IValidatableObject
{
    public const string SectionName = "Octocon:Persistence";

    [EnumDataType(typeof(PersistenceMode))]
    public PersistenceMode Mode { get; set; } = PersistenceMode.ScyllaPostgres;

    /// <summary>Region/keyspace identity — nam|eur|ocn|eas|sam|sas|gdpr.
    /// Env: OCTOCON_SCYLLA_KEYSPACE.</summary>
    public Enums.ScyllaKeyspace ScyllaKeyspace { get; set; } = Enums.ScyllaKeyspace.Nam;

    /// <summary>Env: OCTOCON_POSTGRES_CONNECTION. Required when
    /// <see cref="Mode"/> is <see cref="PersistenceMode.ScyllaPostgres"/>.</summary>
    public string PostgresConnectionString { get; set; } = "";

    /// <summary>Env: OCTOCON_SQLITE_CONNECTION. Required when
    /// <see cref="Mode"/> is <see cref="PersistenceMode.Sqlite"/>
    /// (e.g. <c>Data Source=/var/lib/interfold/interfold.db</c>).</summary>
    public string SqliteConnectionString { get; set; } = "";

    /// <summary>True → migration service creates only the ScyllaKeyspace keyspace.
    /// Env: OCTOCON_SINGLE_SCYLLA_INSTANCE.</summary>
    public bool IsSingleScyllaInstance { get; set; } = false;

    /// <summary>Env: OCTOCON_DB_RETRY_ATTEMPTS.</summary>
    [Range(ConfigurationBounds.DbRetryAttemptsMin, ConfigurationBounds.DbRetryAttemptsMax)]
    public int DbRetryAttempts { get; set; } = 3;

    /// <summary>Env: OCTOCON_DB_RETRY_INITIAL_DELAY_MS (binder converts ms → TimeSpan).</summary>
    public TimeSpan DbRetryInitialDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Env: OCTOCON_DB_RETRY_MAX_DELAY_MS. Must be ≥ DbRetryInitialDelay.</summary>
    public TimeSpan DbRetryMaxDelay { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Env: OCTOCON_HYDRATION_MAX_CONCURRENCY. Caps friendship-query fan-out.</summary>
    [Range(ConfigurationBounds.HydrationMaxConcurrencyMin, ConfigurationBounds.HydrationMaxConcurrencyMax)]
    public int HydrationMaxConcurrency { get; set; } = 8;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Mode == PersistenceMode.ScyllaPostgres && string.IsNullOrWhiteSpace(PostgresConnectionString))
        {
            yield return new ValidationResult(
                $"{nameof(PostgresConnectionString)} is required when {nameof(Mode)} is {PersistenceMode.ScyllaPostgres}.",
                [nameof(PostgresConnectionString)]);
        }

        if (Mode == PersistenceMode.Sqlite && string.IsNullOrWhiteSpace(SqliteConnectionString))
        {
            yield return new ValidationResult(
                $"{nameof(SqliteConnectionString)} is required when {nameof(Mode)} is {PersistenceMode.Sqlite}.",
                [nameof(SqliteConnectionString)]);
        }

        var initialMs = DbRetryInitialDelay.TotalMilliseconds;
        if (initialMs < ConfigurationBounds.DbRetryInitialDelayMsMin
            || initialMs > ConfigurationBounds.DbRetryInitialDelayMsMax)
        {
            yield return new ValidationResult(
                $"{nameof(DbRetryInitialDelay)}={initialMs}ms is outside the allowed " +
                $"[{ConfigurationBounds.DbRetryInitialDelayMsMin}..{ConfigurationBounds.DbRetryInitialDelayMsMax}]ms range.",
                [nameof(DbRetryInitialDelay)]);
        }

        var maxMs = DbRetryMaxDelay.TotalMilliseconds;
        if (maxMs < ConfigurationBounds.DbRetryMaxDelayMsMin
            || maxMs > ConfigurationBounds.DbRetryMaxDelayMsMax)
        {
            yield return new ValidationResult(
                $"{nameof(DbRetryMaxDelay)}={maxMs}ms is outside the allowed " +
                $"[{ConfigurationBounds.DbRetryMaxDelayMsMin}..{ConfigurationBounds.DbRetryMaxDelayMsMax}]ms range.",
                [nameof(DbRetryMaxDelay)]);
        }

        if (DbRetryMaxDelay < DbRetryInitialDelay)
        {
            yield return new ValidationResult(
                $"{nameof(DbRetryMaxDelay)} ({DbRetryMaxDelay}) must be >= {nameof(DbRetryInitialDelay)} ({DbRetryInitialDelay}).",
                [nameof(DbRetryMaxDelay), nameof(DbRetryInitialDelay)]);
        }
    }
}
