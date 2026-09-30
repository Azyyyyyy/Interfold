using Microsoft.Data.Sqlite;

namespace Interfold.StackMove.UnitTests;

public sealed class GuardAndReportTests
{
    [Test]
    public async Task MultiRegionSourceIsRefused()
    {
        await Assert.That(() => RegionGuard.EnsureSingleRegion(["nam", "eur"]))
            .ThrowsExactly<MoveRefusedException>();
    }

    [Test]
    public async Task SingleRegionSourceIsAllowed()
    {
        RegionGuard.EnsureSingleRegion(["nam", "nam"]);
        RegionGuard.EnsureSingleRegion([]);
        await Task.CompletedTask;
    }

    [Test]
    public async Task EmptyTargetIsAllowed()
    {
        PopulationGuard.EnsureEmpty(0, "SQLite database");
        await Task.CompletedTask;
    }

    [Test]
    public async Task SqliteTargetCountsColumnsItCannotStore()
    {
        var snapshot = new StackSnapshot
        {
            Accounts =
            [
                new AccountRow
                {
                    SystemId = "a",
                    GoogleId = "g",
                    LinkToken = "tok",
                    DiscordSettings = new DiscordSettingsRow(),
                    CreatedAtUnixMs = 1,
                    UpdatedAtUnixMs = 1,
                },
            ],
            Alters =
            [
                new AlterRow
                {
                    SystemId = "a",
                    Id = 1,
                    Name = "A",
                    ExtraImages = ["img"],
                    DiscordProxies = ["proxy"],
                },
            ],
        };

        var lossy = LossyScan.Evaluate(snapshot, StackKind.Sqlite);
        await Assert.That(lossy.GoogleIds).IsEqualTo(1);
        await Assert.That(lossy.ExtraImages).IsEqualTo(1);
        await Assert.That(lossy.DiscordProxies).IsEqualTo(1);
        await Assert.That(lossy.DiscordSettings).IsEqualTo(1);
        await Assert.That(lossy.LinkTokens).IsEqualTo(0);
    }

    [Test]
    public async Task CqlTargetCountsLinkTokensOnly()
    {
        var snapshot = new StackSnapshot
        {
            Accounts =
            [
                new AccountRow
                {
                    SystemId = "a",
                    GoogleId = "g",
                    LinkToken = "tok",
                    CreatedAtUnixMs = 1,
                    UpdatedAtUnixMs = 1,
                },
            ],
        };

        var lossy = LossyScan.Evaluate(snapshot, StackKind.Scylla);
        await Assert.That(lossy.LinkTokens).IsEqualTo(1);
        await Assert.That(lossy.GoogleIds).IsEqualTo(0);

        var cassandra = LossyScan.Evaluate(snapshot, StackKind.Cassandra);
        await Assert.That(cassandra.LinkTokens).IsEqualTo(1);
    }

    [Test]
    public async Task LossyMoveRefusesWithoutTheFlag()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"interfold-stack-move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "interfold.db");
        try
        {
            await new SqliteStackStore(path).WriteAsync(new StackSnapshot
            {
                Accounts =
                [
                    new AccountRow
                    {
                        SystemId = "abcdefg",
                        LinkToken = "tok",
                        CreatedAtUnixMs = 1,
                        UpdatedAtUnixMs = 1,
                    },
                ],
            }, [], CancellationToken.None);

            var writer = new StringWriter();
            var code = await StackMover.ExecuteAsync(new MoveRequest
            {
                From = StackKind.Sqlite,
                To = StackKind.Scylla,
                SourceSqlitePath = path,
                TargetCql = new CqlEndpoint("Host=localhost;Database=interfold", "127.0.0.1", 9042, null, null, "nam", "nam"),
            }, writer, CancellationToken.None);

            await Assert.That(code).IsEqualTo(1);
            await Assert.That(writer.ToString()).Contains("link_token");
            await Assert.That(writer.ToString()).Contains("--allow-lossy");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task SecretsDropConnectivityAndKeepOauthOnlyForSqlite()
    {
        var secrets = new[]
        {
            Secret("encryption:pepper"),
            Secret("auth:jwt_es256_private_pem"),
            Secret("postgres:admin_password"),
            Secret("scylla:contact_points"),
            Secret("oauth:discord:client_secret"),
            Secret("firebase:client:web"),
        };

        var toSqlite = SecretPolicy.Select(secrets, StackKind.Sqlite).Select(secret => secret.Key).ToArray();
        var toScylla = SecretPolicy.Select(secrets, StackKind.Scylla).Select(secret => secret.Key).ToArray();

        await Assert.That(toSqlite).Contains("encryption:pepper");
        await Assert.That(toSqlite).Contains("oauth:discord:client_secret");
        await Assert.That(toSqlite).Contains("firebase:client:web");
        await Assert.That(toSqlite.Contains("postgres:admin_password")).IsFalse();
        await Assert.That(toSqlite.Contains("scylla:contact_points")).IsFalse();
        await Assert.That(toScylla).Contains("encryption:pepper");
        await Assert.That(toScylla.Contains("oauth:discord:client_secret")).IsFalse();
    }

    [Test]
    public async Task SnippetsNameTheTargetEngine()
    {
        var sqlite = BootstrapReport.Snippet(new MoveRequest { From = StackKind.Scylla, To = StackKind.Sqlite });
        var scylla = BootstrapReport.Snippet(new MoveRequest
        {
            From = StackKind.Sqlite,
            To = StackKind.Scylla,
            TargetCql = Endpoint(),
            Bootstrap = new BootstrapPeek { PostgresDatabase = "interfold", ClusterName = "InterfoldCluster", Keyspace = "nam" },
        });
        var cassandra = BootstrapReport.Snippet(new MoveRequest
        {
            From = StackKind.Sqlite,
            To = StackKind.Cassandra,
            TargetCql = Endpoint(),
        });

        await Assert.That(sqlite).Contains("\"persistence\": \"sqlite\"");
        await Assert.That(scylla).Contains("\"persistence\": \"scylla-postgres\"");
        await Assert.That(scylla).Contains("\"backend\": \"scylla-single\"");
        await Assert.That(scylla).Contains("\"keyspace\": \"nam\"");
        await Assert.That(scylla).Contains("\"database\": \"interfold\"");
        await Assert.That(cassandra).Contains("\"backend\": \"cassandra\"");
    }

    [Test]
    public async Task ReportSaysTheBootstrapFileWasNotModified()
    {
        var text = BootstrapReport.Render(
            new MoveRequest { From = StackKind.Scylla, To = StackKind.Sqlite },
            new MoveCounts(1, 0, 0, 0, 0, 0, 0, 0, 0, 1),
            LossyCounts.None,
            wrote: true);

        await Assert.That(text).Contains("interfold.bootstrap.json was not modified");
        await Assert.That(text).Contains("pepper");
    }

    [Test]
    public async Task ShortFlagsFillTheSingleCqlSide()
    {
        var request = MoveRequestFactory.Create(new MoveCliValues
        {
            From = "sqlite",
            To = "scylla",
            Sqlite = "data/interfold.db",
            Postgres = "Host=localhost;Database=interfold",
            ContactPoints = "127.0.0.1",
            Keyspace = "eur",
        });

        await Assert.That(request.From).IsEqualTo(StackKind.Sqlite);
        await Assert.That(request.To).IsEqualTo(StackKind.Scylla);
        await Assert.That(request.SourceSqlitePath).IsEqualTo("data/interfold.db");
        await Assert.That(request.TargetCql!.Keyspace).IsEqualTo("eur");
        await Assert.That(request.TargetCql.LocalDatacenter).IsEqualTo("eur");
        await Assert.That(request.SourceCql).IsNull();
    }

    [Test]
    public async Task ScyllaToCassandraRequiresBothEndpoints()
    {
        await Assert.That(() => MoveRequestFactory.Create(new MoveCliValues
        {
            From = "scylla",
            To = "cassandra",
            Postgres = "Host=localhost;Database=interfold",
            ContactPoints = "127.0.0.1",
        })).ThrowsExactly<MoveRefusedException>();

        var request = MoveRequestFactory.Create(new MoveCliValues
        {
            From = "scylla",
            To = "cassandra",
            SourcePostgres = "Host=localhost;Database=old",
            TargetPostgres = "Host=localhost;Database=new",
            SourceContactPoints = "10.0.0.1",
            TargetContactPoints = "10.0.0.2",
            SourceKeyspace = "nam",
            TargetKeyspace = "nam",
        });

        await Assert.That(request.SourceCql!.ContactPoints).IsEqualTo("10.0.0.1");
        await Assert.That(request.TargetCql!.ContactPoints).IsEqualTo("10.0.0.2");
    }

    [Test]
    public async Task SameStackIsRefused()
    {
        var writer = new StringWriter();
        await Assert.That(async () => await StackMover.ExecuteAsync(new MoveRequest
        {
            From = StackKind.Scylla,
            To = StackKind.Scylla,
        }, writer, CancellationToken.None)).ThrowsExactly<MoveRefusedException>();
    }

    [Test]
    public async Task SecretsFileSuppliesPasswordsAndIsNotWritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"interfold-stack-move-{Guid.NewGuid():N}");
        var secretsDir = Path.Combine(directory, "secrets");
        Directory.CreateDirectory(secretsDir);
        var config = Path.Combine(directory, "interfold.bootstrap.json");
        var secretsPath = Path.Combine(secretsDir, "secrets.json");
        var secretsJson = """
            {
              "postgresUser": "interfold",
              "postgresAdminPassword": "secret-admin",
              "scyllaUser": "interfold",
              "scyllaPassword": "secret-cql"
            }
            """;
        await File.WriteAllTextAsync(config, $$"""
            {
              "deployment": { "outputDir": "{{directory.Replace("\\", "\\\\")}}" },
              "datastores": { "postgres": { "database": "interfold" }, "cql": { "keyspace": "nam" } }
            }
            """);
        await File.WriteAllTextAsync(secretsPath, secretsJson);
        try
        {
            var request = MoveRequestFactory.Create(new MoveCliValues
            {
                From = "sqlite",
                To = "scylla",
                BootstrapPath = config,
                Sqlite = "data/interfold.db",
            });

            await Assert.That(request.TargetCql!.PostgresConnectionString).Contains("Username=interfold_admin");
            await Assert.That(request.TargetCql.PostgresConnectionString).Contains("Password=secret-admin");
            await Assert.That(request.TargetCql.PostgresConnectionString).Contains("Database=interfold");
            await Assert.That(request.TargetCql.Username).IsEqualTo("interfold");
            await Assert.That(request.TargetCql.Password).IsEqualTo("secret-cql");
            await Assert.That(request.TargetCql.ContactPoints).IsEqualTo("127.0.0.1");
            await Assert.That(request.SecretsPath).IsEqualTo(secretsPath);
            await Assert.That(await File.ReadAllTextAsync(secretsPath)).IsEqualTo(secretsJson);
            await Assert.That(await File.ReadAllTextAsync(config)).Contains("outputDir");

            var report = BootstrapReport.Render(request, new MoveCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0), LossyCounts.None, wrote: false);
            await Assert.That(report).Contains($"{secretsPath} was not modified");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task ConnectionFlagsOverrideTheSecretsFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"interfold-stack-move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var secretsPath = Path.Combine(directory, "secrets.json");
        await File.WriteAllTextAsync(secretsPath, """
            {
              "postgresUser": "interfold",
              "postgresAdminPassword": "secret-admin",
              "scyllaUser": "from-file",
              "scyllaPassword": "secret-cql"
            }
            """);
        try
        {
            var request = MoveRequestFactory.Create(new MoveCliValues
            {
                From = "sqlite",
                To = "cassandra",
                SecretsPath = secretsPath,
                Sqlite = "data/interfold.db",
                Postgres = "Host=db.example;Username=explicit;Password=explicit-pw;Database=custom",
                ContactPoints = "10.1.1.1",
                Username = "explicit-cql",
                Password = "explicit-cql-pw",
            });

            await Assert.That(request.TargetCql!.PostgresConnectionString).Contains("Username=explicit");
            await Assert.That(request.TargetCql.ContactPoints).IsEqualTo("10.1.1.1");
            await Assert.That(request.TargetCql.Username).IsEqualTo("explicit-cql");
            await Assert.That(request.TargetCql.Password).IsEqualTo("explicit-cql-pw");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task SecretsFileFillsOnlyTheSourceWhenBothSidesAreCql()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"interfold-stack-move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var secretsPath = Path.Combine(directory, "secrets.json");
        await File.WriteAllTextAsync(secretsPath, """
            {
              "postgresAdminPassword": "secret-admin",
              "scyllaPassword": "secret-cql"
            }
            """);
        try
        {
            await Assert.That(() => MoveRequestFactory.Create(new MoveCliValues
            {
                From = "scylla",
                To = "cassandra",
                SecretsPath = secretsPath,
            })).ThrowsExactly<MoveRefusedException>();

            var request = MoveRequestFactory.Create(new MoveCliValues
            {
                From = "scylla",
                To = "cassandra",
                SecretsPath = secretsPath,
                TargetPostgres = "Host=db.example;Database=new",
                TargetContactPoints = "10.0.0.2",
                TargetPassword = "target-cql",
            });

            await Assert.That(request.SourceCql!.Password).IsEqualTo("secret-cql");
            await Assert.That(request.SourceCql.PostgresConnectionString).Contains("Password=secret-admin");
            await Assert.That(request.TargetCql!.ContactPoints).IsEqualTo("10.0.0.2");
            await Assert.That(request.TargetCql.Password).IsEqualTo("target-cql");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task BootstrapPeekSuppliesTheSqlitePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"interfold-stack-move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "interfold.bootstrap.json");
        await File.WriteAllTextAsync(config, """
            {
              "deployment": { "outputDir": "./deploy" },
              "datastores": {
                "persistence": "scylla-postgres",
                "postgres": { "database": "interfold" },
                "cql": { "backend": "cassandra", "keyspace": "nam", "clusterName": "InterfoldCluster" }
              },
              "api": { "storage": { "avatarStorageRoot": "/avatars" } }
            }
            """);
        try
        {
            var peek = BootstrapPeek.Load(config);
            await Assert.That(peek.CqlBackend).IsEqualTo("cassandra");
            await Assert.That(peek.Keyspace).IsEqualTo("nam");
            await Assert.That(peek.AvatarStorageRoot).IsEqualTo("/avatars");
            await Assert.That(peek.SqlitePath).Contains(Path.Combine("data", "sqlite", "interfold.db"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SecretRow Secret(string key) => new()
    {
        Key = key,
        Value = "v",
        CreatedAtUnixMs = 1,
        UpdatedAtUnixMs = 1,
    };

    private static CqlEndpoint Endpoint() =>
        new("Host=localhost;Username=u;Password=p;Database=interfold", "127.0.0.1", 9042, null, null, "nam", "nam");
}
