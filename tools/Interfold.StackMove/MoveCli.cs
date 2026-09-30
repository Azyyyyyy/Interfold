using System.CommandLine;

namespace Interfold.StackMove;

internal static class MoveCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var fromOpt = Option("--from", "Source stack: sqlite, scylla, or cassandra.");
        var toOpt = Option("--to", "Target stack: sqlite, scylla, or cassandra.");
        var bootstrapOpt = Option("--bootstrap", "Read-only path to interfold.bootstrap.json. Supplies default paths, the printed snippet, and, when present, deploy/secrets/secrets.json. Neither file is modified.");
        var secretsOpt = Option("--secrets", "Read-only path to secrets.json. Supplies the Postgres admin password and the CQL password. Overrides the file next to --bootstrap. The file is not modified.");
        var dryRunOpt = new Option<bool>("--dry-run")
        {
            Description = "Print counts and the bootstrap snippet without writing.",
        };
        var allowLossyOpt = new Option<bool>("--allow-lossy")
        {
            Description = "Continue when the target cannot store google_id, extra_images, discord_proxies, discord_settings, or link tokens.",
        };
        var sqliteOpt = Option("--sqlite", "SQLite database file. Used when exactly one side is sqlite.");
        var postgresOpt = Option("--postgres", "Postgres connection string. Used when exactly one side is Scylla or Cassandra.");
        var contactOpt = Option("--cql-contact-points", "CQL contact points. Used when exactly one side is Scylla or Cassandra.");
        var portOpt = new Option<int>("--cql-port")
        {
            Description = "CQL port. Defaults to 9042.",
            DefaultValueFactory = _ => 9042,
        };
        var userOpt = Option("--cql-username", "CQL username.");
        var passwordOpt = Option("--cql-password", "CQL password.");
        var keyspaceOpt = Option("--cql-keyspace", "CQL keyspace. Defaults to the bootstrap file, then nam.");
        var dcOpt = Option("--cql-local-dc", "CQL local datacenter. Defaults to the keyspace name.");
        var sourceSqliteOpt = Option("--source-sqlite", "Source SQLite database file.");
        var targetSqliteOpt = Option("--target-sqlite", "Target SQLite database file.");
        var sourcePostgresOpt = Option("--source-postgres", "Source Postgres connection string.");
        var targetPostgresOpt = Option("--target-postgres", "Target Postgres connection string.");
        var sourceContactOpt = Option("--source-cql-contact-points", "Source CQL contact points.");
        var targetContactOpt = Option("--target-cql-contact-points", "Target CQL contact points.");
        var sourcePortOpt = new Option<int?>("--source-cql-port") { Description = "Source CQL port." };
        var targetPortOpt = new Option<int?>("--target-cql-port") { Description = "Target CQL port." };
        var sourceUserOpt = Option("--source-cql-username", "Source CQL username.");
        var targetUserOpt = Option("--target-cql-username", "Target CQL username.");
        var sourcePasswordOpt = Option("--source-cql-password", "Source CQL password.");
        var targetPasswordOpt = Option("--target-cql-password", "Target CQL password.");
        var sourceKeyspaceOpt = Option("--source-cql-keyspace", "Source CQL keyspace.");
        var targetKeyspaceOpt = Option("--target-cql-keyspace", "Target CQL keyspace.");
        var sourceDcOpt = Option("--source-cql-local-dc", "Source CQL local datacenter.");
        var targetDcOpt = Option("--target-cql-local-dc", "Target CQL local datacenter.");

        var command = new Command("move", "Copy a deployment between sqlite, scylla, and cassandra, then print the bootstrap edits.")
        {
            fromOpt, toOpt, bootstrapOpt, secretsOpt, dryRunOpt, allowLossyOpt,
            sqliteOpt, postgresOpt, contactOpt, portOpt, userOpt, passwordOpt, keyspaceOpt, dcOpt,
            sourceSqliteOpt, targetSqliteOpt,
            sourcePostgresOpt, targetPostgresOpt,
            sourceContactOpt, targetContactOpt,
            sourcePortOpt, targetPortOpt,
            sourceUserOpt, targetUserOpt,
            sourcePasswordOpt, targetPasswordOpt,
            sourceKeyspaceOpt, targetKeyspaceOpt,
            sourceDcOpt, targetDcOpt,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            try
            {
                var request = MoveRequestFactory.Create(new MoveCliValues
                {
                    From = parse.GetValue(fromOpt),
                    To = parse.GetValue(toOpt),
                    BootstrapPath = parse.GetValue(bootstrapOpt),
                    SecretsPath = parse.GetValue(secretsOpt),
                    DryRun = parse.GetValue(dryRunOpt),
                    AllowLossy = parse.GetValue(allowLossyOpt),
                    Sqlite = parse.GetValue(sqliteOpt),
                    Postgres = parse.GetValue(postgresOpt),
                    ContactPoints = parse.GetValue(contactOpt),
                    Port = parse.GetValue(portOpt),
                    Username = parse.GetValue(userOpt),
                    Password = parse.GetValue(passwordOpt),
                    Keyspace = parse.GetValue(keyspaceOpt),
                    LocalDatacenter = parse.GetValue(dcOpt),
                    SourceSqlite = parse.GetValue(sourceSqliteOpt),
                    TargetSqlite = parse.GetValue(targetSqliteOpt),
                    SourcePostgres = parse.GetValue(sourcePostgresOpt),
                    TargetPostgres = parse.GetValue(targetPostgresOpt),
                    SourceContactPoints = parse.GetValue(sourceContactOpt),
                    TargetContactPoints = parse.GetValue(targetContactOpt),
                    SourcePort = parse.GetValue(sourcePortOpt),
                    TargetPort = parse.GetValue(targetPortOpt),
                    SourceUsername = parse.GetValue(sourceUserOpt),
                    TargetUsername = parse.GetValue(targetUserOpt),
                    SourcePassword = parse.GetValue(sourcePasswordOpt),
                    TargetPassword = parse.GetValue(targetPasswordOpt),
                    SourceKeyspace = parse.GetValue(sourceKeyspaceOpt),
                    TargetKeyspace = parse.GetValue(targetKeyspaceOpt),
                    SourceLocalDatacenter = parse.GetValue(sourceDcOpt),
                    TargetLocalDatacenter = parse.GetValue(targetDcOpt),
                });
                return await StackMover.ExecuteAsync(request, Console.Out, cancellationToken).ConfigureAwait(false);
            }
            catch (MoveRefusedException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        });

        var root = new RootCommand("Move an Interfold deployment between database stacks without editing the bootstrap file.")
        {
            command,
        };
        return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
    }

    private static Option<string?> Option(string name, string description) => new(name) { Description = description };
}

internal sealed class MoveCliValues
{
    public string? From { get; init; }
    public string? To { get; init; }
    public string? BootstrapPath { get; init; }
    public string? SecretsPath { get; init; }
    public bool DryRun { get; init; }
    public bool AllowLossy { get; init; }
    public string? Sqlite { get; init; }
    public string? Postgres { get; init; }
    public string? ContactPoints { get; init; }
    public int Port { get; init; } = 9042;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Keyspace { get; init; }
    public string? LocalDatacenter { get; init; }
    public string? SourceSqlite { get; init; }
    public string? TargetSqlite { get; init; }
    public string? SourcePostgres { get; init; }
    public string? TargetPostgres { get; init; }
    public string? SourceContactPoints { get; init; }
    public string? TargetContactPoints { get; init; }
    public int? SourcePort { get; init; }
    public int? TargetPort { get; init; }
    public string? SourceUsername { get; init; }
    public string? TargetUsername { get; init; }
    public string? SourcePassword { get; init; }
    public string? TargetPassword { get; init; }
    public string? SourceKeyspace { get; init; }
    public string? TargetKeyspace { get; init; }
    public string? SourceLocalDatacenter { get; init; }
    public string? TargetLocalDatacenter { get; init; }
}

internal static class MoveRequestFactory
{
    public static MoveRequest Create(MoveCliValues values)
    {
        if (!StackKindParser.TryParse(values.From, out var from) ||
            !StackKindParser.TryParse(values.To, out var to))
        {
            throw new MoveRefusedException("Pass --from and --to. Each is sqlite, scylla, or cassandra.");
        }

        var bootstrap = string.IsNullOrWhiteSpace(values.BootstrapPath)
            ? null
            : BootstrapPeek.Load(values.BootstrapPath);
        var secrets = DeploymentSecrets.LoadOptional(values.SecretsPath, bootstrap?.OutputDir);
        var keyspaceDefault = First(values.Keyspace, bootstrap?.Keyspace, "nam")!;
        var bothCql = StackKindParser.IsCql(from) && StackKindParser.IsCql(to);

        string? sourceSqlite = values.SourceSqlite;
        string? targetSqlite = values.TargetSqlite;
        if (from == StackKind.Sqlite)
        {
            sourceSqlite ??= bothCql ? null : values.Sqlite ?? bootstrap?.SqlitePath;
        }

        if (to == StackKind.Sqlite)
        {
            targetSqlite ??= values.TargetSqlite ?? (from == StackKind.Sqlite ? null : values.Sqlite ?? bootstrap?.SqlitePath);
        }

        return new MoveRequest
        {
            From = from,
            To = to,
            DryRun = values.DryRun,
            AllowLossy = values.AllowLossy,
            SourceSqlitePath = sourceSqlite,
            TargetSqlitePath = targetSqlite,
            SourceCql = StackKindParser.IsCql(from)
                ? BuildEndpoint(values, source: true, bothCql, keyspaceDefault, bootstrap, secrets)
                : null,
            TargetCql = StackKindParser.IsCql(to)
                ? BuildEndpoint(values, source: false, bothCql, keyspaceDefault, bootstrap, secrets)
                : null,
            Bootstrap = bootstrap,
            SecretsPath = secrets?.FilePath,
        };
    }

    private static CqlEndpoint BuildEndpoint(
        MoveCliValues values,
        bool source,
        bool bothCql,
        string keyspaceDefault,
        BootstrapPeek? bootstrap,
        DeploymentSecrets? secrets)
    {
        var useSecrets = secrets is not null && (!bothCql || source);
        var postgres = source
            ? First(values.SourcePostgres, bothCql ? null : values.Postgres)
            : First(values.TargetPostgres, bothCql ? null : values.Postgres);
        var contacts = source
            ? First(values.SourceContactPoints, bothCql ? null : values.ContactPoints)
            : First(values.TargetContactPoints, bothCql ? null : values.ContactPoints);
        if (string.IsNullOrWhiteSpace(postgres) && useSecrets)
        {
            postgres = secrets!.PostgresAdminConnectionString(bootstrap?.PostgresDatabase);
        }

        if (string.IsNullOrWhiteSpace(contacts) && useSecrets)
        {
            contacts = "127.0.0.1";
        }

        if (string.IsNullOrWhiteSpace(postgres) || string.IsNullOrWhiteSpace(contacts))
        {
            var side = source ? "source" : "target";
            var hint = bothCql
                ? $"Pass --{side}-postgres and --{side}-cql-contact-points."
                : "Pass --postgres and --cql-contact-points, or --secrets pointing at secrets.json.";
            throw new MoveRefusedException($"The {side} cluster needs a Postgres connection string and CQL contact points. {hint}");
        }

        var keyspace = First(
            source ? values.SourceKeyspace : values.TargetKeyspace,
            values.Keyspace,
            keyspaceDefault)!;
        var port = (source ? values.SourcePort : values.TargetPort) ?? values.Port;
        if (port == 0)
        {
            port = 9042;
        }

        var datacenter = First(
            source ? values.SourceLocalDatacenter : values.TargetLocalDatacenter,
            values.LocalDatacenter,
            keyspace)!;

        var username = First(
            source ? values.SourceUsername : values.TargetUsername,
            bothCql ? null : values.Username,
            useSecrets ? secrets!.ScyllaUser : null);
        var password = First(
            source ? values.SourcePassword : values.TargetPassword,
            bothCql ? null : values.Password,
            useSecrets ? secrets!.ScyllaPassword : null);
        if (useSecrets && string.IsNullOrWhiteSpace(password))
        {
            throw new MoveRefusedException($"{secrets!.FilePath} has no scyllaPassword.");
        }

        return new CqlEndpoint(
            postgres,
            contacts,
            port,
            username,
            password,
            keyspace,
            datacenter);
    }

    private static string? First(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
