namespace Interfold.Contracts.Configuration;

using System.ComponentModel.DataAnnotations;
using Interfold.Contracts;

/// <summary>
/// Database and persistence configuration for Scylla, PostgreSQL, and retry logic.
/// Binds from environment variables with OCTOCON_ prefix:
///   - OCTOCON_PERSISTENCE (scylla-postgres or inmemory)
///   - OCTOCON_SCYLLA_KEYSPACE (per-instance region/keyspace; default: nam)
///   - OCTOCON_POSTGRES_CONNECTION
///   - OCTOCON_SINGLE_SCYLLA_INSTANCE
///   - OCTOCON_DB_RETRY_* (retry strategy parameters — wire is ms, in-memory is <see cref="TimeSpan"/>)
///   - OCTOCON_HYDRATION_MAX_CONCURRENCY
///
/// Scylla connection details (contact_points, datacenter, username, password, keyspace)
/// and admin credentials are stored in internal.secrets (PostgreSQL) and read directly
/// by services via ISecretsStore. The keyspace and region are unified:
/// OCTOCON_SCYLLA_KEYSPACE controls both the Scylla session default keyspace and the
/// instance's assigned region for new-account creation and query routing.
/// </summary>
public sealed class PersistenceConfiguration : IValidatableObject
{
    public const string SectionName = "Octocon:Persistence";

    /// <summary>
    /// Persistence backend mode. Bound from <c>OCTOCON_PERSISTENCE</c> via
    /// <see cref="PersistenceModeExtensions.Parse"/>; the wire values are
    /// <c>scylla-postgres</c> and <c>inmemory</c>.
    /// </summary>
    [EnumDataType(typeof(PersistenceMode))]
    public PersistenceMode Mode { get; set; } = PersistenceMode.ScyllaPostgres;

    /// <summary>
    /// Instance region/keyspace identity. Controls both the Scylla session default
    /// keyspace and the region used for new-account creation and query routing.
    /// Values: nam, eur, ocn, eas, sam, sas, gdpr
    /// Default: 'nam'
    /// Env: OCTOCON_SCYLLA_KEYSPACE
    /// </summary>
    [Required, MinLength(1)]
    public string ScyllaKeyspace { get; set; } = "nam";

    /// <summary>
    /// PostgreSQL connection string.
    /// Env: OCTOCON_POSTGRES_CONNECTION
    /// </summary>
    [Required, MinLength(1)]
    public string PostgresConnectionString { get; set; } = "";

    /// <summary>
    /// When true, the migration service only creates the single keyspace specified by
    /// ScyllaKeyspace instead of all regional keyspaces. Useful for dev/single-instance setups.
    /// Default: false
    /// Env: OCTOCON_SINGLE_SCYLLA_INSTANCE
    /// </summary>
    public bool IsSingleScyllaInstance { get; set; } = false;

    /// <summary>
    /// Maximum number of retry attempts for transient database failures.
    /// Default: 3
    /// Env: OCTOCON_DB_RETRY_ATTEMPTS
    /// </summary>
    [Range(1, 100)]
    public int DbRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Initial backoff delay for exponential retry strategy. Wire form is
    /// <c>OCTOCON_DB_RETRY_INITIAL_DELAY_MS</c> (integer milliseconds); the binder
    /// converts to <see cref="TimeSpan"/> so callers don't need to remember the unit.
    /// Default: 100&#160;ms.
    /// </summary>
    public TimeSpan DbRetryInitialDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Maximum backoff delay for exponential retry strategy. Wire form is
    /// <c>OCTOCON_DB_RETRY_MAX_DELAY_MS</c> (integer milliseconds); the binder
    /// converts to <see cref="TimeSpan"/>. Must be greater than or equal to
    /// <see cref="DbRetryInitialDelay"/> — enforced by the <see cref="IValidatableObject"/>
    /// implementation on this type.
    /// Default: 1500&#160;ms.
    /// </summary>
    public TimeSpan DbRetryMaxDelay { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Maximum concurrent friendship profile/fronting hydration tasks per request.
    /// Used to cap fan-out in friendship query paths to avoid unbounded bursts.
    /// Default: 8
    /// Env: OCTOCON_HYDRATION_MAX_CONCURRENCY
    /// </summary>
    [Range(1, 1024)]
    public int HydrationMaxConcurrency { get; set; } = 8;

    /// <summary>
    /// Cross-field validation: <see cref="DbRetryInitialDelay"/> must be positive and
    /// <see cref="DbRetryMaxDelay"/> must be at least as large as the initial delay.
    /// Runs on <see cref="Microsoft.Extensions.Options.OptionsBuilderDataAnnotationsExtensions.ValidateDataAnnotations{TOptions}"/>
    /// once the binder has populated both timespans.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DbRetryInitialDelay <= TimeSpan.Zero)
        {
            yield return new ValidationResult(
                $"{nameof(DbRetryInitialDelay)} must be a positive duration (got {DbRetryInitialDelay}).",
                [nameof(DbRetryInitialDelay)]);
        }
        if (DbRetryMaxDelay < DbRetryInitialDelay)
        {
            yield return new ValidationResult(
                $"{nameof(DbRetryMaxDelay)} ({DbRetryMaxDelay}) must be >= {nameof(DbRetryInitialDelay)} ({DbRetryInitialDelay}).",
                [nameof(DbRetryMaxDelay), nameof(DbRetryInitialDelay)]);
        }
    }
}
