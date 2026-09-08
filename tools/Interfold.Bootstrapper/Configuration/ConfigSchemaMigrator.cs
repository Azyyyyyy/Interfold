using System.Text.Json;
using System.Text.Json.Nodes;
using Interfold.Bootstrapper.Cli;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>Automatic <c>interfold.bootstrap.json</c> schema upgrades (V1 → streamlined V2).</summary>
internal static class ConfigSchemaMigrator
{
    private const int V1DefaultApiHttp = 5000;
    private const int V1DefaultApiHttps = 5001;
    private const int V1DefaultWebHttps = 8081;

    internal sealed record MigrationResult(
        string Json,
        bool DidMigrate,
        string? OriginalJson,
        V1EndpointSnapshot? V1Snapshot);

    internal static MigrationResult MigrateIfNeeded(string json, string configPath, PhaseLogger logger)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new MigrationResult(json, false, null, null);
        }

        var version = DetectVersion(doc.RootElement);
        if (version >= BootstrapConfig.CurrentSchemaVersion)
        {
            RejectInvalidV2Keys(doc.RootElement, configPath);
            return new MigrationResult(json, false, null, null);
        }

        var v1Snapshot = CaptureV1Snapshot(doc.RootElement);
        var migratedRoot = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException($"Failed to parse {configPath} for migration.");
        migratedRoot = MigrateChain(migratedRoot, version, logger);
        var migratedJson = Serialize(migratedRoot);
        return new MigrationResult(migratedJson, true, json, v1Snapshot);
    }

    internal static async Task PersistMigratedAsync(
        string originalJson,
        string migratedJson,
        string configPath,
        CancellationToken ct)
    {
        var backupPath = configPath + ".bak.v1";
        if (!File.Exists(backupPath))
        {
            await File.WriteAllTextAsync(backupPath, originalJson, ct).ConfigureAwait(false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(configPath))!);
        await File.WriteAllTextAsync(configPath, migratedJson, ct).ConfigureAwait(false);
    }

    internal static int DetectVersion(JsonElement root)
    {
        if (root.TryGetProperty("schemaVersion", out var versionProp)
            && versionProp.ValueKind == JsonValueKind.Number
            && versionProp.TryGetInt32(out var version))
        {
            return version;
        }

        return 1;
    }

    private static JsonObject MigrateChain(JsonObject root, int fromVersion, PhaseLogger logger)
    {
        var current = fromVersion;
        while (current < BootstrapConfig.CurrentSchemaVersion)
        {
            root = current switch
            {
                1 => MigrateV1ToV2(root, logger),
                _ => throw new InvalidOperationException(
                    $"Unsupported bootstrap config schemaVersion {current} in {nameof(MigrateChain)}."),
            };
            current++;
        }

        return root;
    }

    private static JsonObject MigrateV1ToV2(JsonObject root, PhaseLogger logger)
    {
        var deployment = root["deployment"] as JsonObject ?? new JsonObject();
        var includeWeb = ReadBool(deployment, "includeWeb");
        var webHttps = ReadBool(deployment, "webHttps");
        deployment["includeWeb"] = includeWeb || webHttps;
        deployment.Remove("webHttps");
        root["deployment"] = deployment;

        ApplyStreamlinedV2Layout(root);
        root["schemaVersion"] = BootstrapConfig.CurrentSchemaVersion;
        logger.Info("    config: applying v1→v2 transforms (streamlined layout)");
        return root;
    }

    private static void ApplyStreamlinedV2Layout(JsonObject root)
    {
        var deployment = EnsureObject(root, "deployment");
        var edge = EnsureObject(root, "edge");
        var api = EnsureObject(root, "api");
        var datastores = EnsureObject(root, "datastores");
        var postgres = EnsureObject(datastores, "postgres");
        var cql = EnsureObject(datastores, "cql");

        if (deployment.TryGetPropertyValue("hosts", out var hostsNode))
        {
            edge["hosts"] = hostsNode?.DeepClone();
            deployment.Remove("hosts");
        }

        MoveCertFields(deployment, edge);

        if (deployment.TryGetPropertyValue("includeWeb", out var includeWeb))
        {
            deployment["includeWeb"] = includeWeb?.DeepClone();
        }
        else if (edge.TryGetPropertyValue("includeWeb", out var edgeIncludeWeb))
        {
            deployment["includeWeb"] = edgeIncludeWeb?.DeepClone();
            edge.Remove("includeWeb");
        }

        HoistAutostartServer(root, deployment);

        var nestedEdge = deployment["edge"] as JsonObject;
        if (nestedEdge is not null)
        {
            CopyEdgeFields(nestedEdge, edge);
            deployment.Remove("edge");
        }

        NormalizeEdgeRouting(edge);
        NormalizeEdgePorts(root, edge);

        if (root.TryGetPropertyValue("certificates", out var topCerts) && topCerts is JsonObject certsObj)
        {
            var edgeCerts = EnsureObject(edge, "certificates");
            foreach (var (key, value) in certsObj)
            {
                edgeCerts[key] = value?.DeepClone();
            }

            root.Remove("certificates");
        }

        MoveBackupUpdate(root, deployment);
        HoistDatastoreFields(root, postgres, cql);
        HoistApiFields(root, api);

        root["deployment"] = deployment;
        root["edge"] = edge;
        root["api"] = api;
        root["datastores"] = datastores;

        root.Remove("ports");
        root.Remove("databaseMode");
        root.Remove("postgresDatabase");
        root.Remove("clusterName");
        root.Remove("scyllaKeyspace");
        root.Remove("apiImage");
        root.Remove("apiRuntime");
        root.Remove("persistence");
        root.Remove("cluster");
        root.Remove("socket");
        root.Remove("oauth");
        root.Remove("storage");
        root.Remove("firebase");
        root.Remove("backup");
        root.Remove("update");
    }

    private static void MoveCertFields(JsonObject deployment, JsonObject edge)
    {
        var certs = EnsureObject(edge, "certificates");
        foreach (var key in new[] { "rootCaName", "certYears", "trustStoreInstall" })
        {
            if (deployment.TryGetPropertyValue(key, out var value))
            {
                certs[key] = value?.DeepClone();
                deployment.Remove(key);
            }
        }
    }

    private static void HoistAutostartServer(JsonObject root, JsonObject deployment)
    {
        if (deployment.TryGetPropertyValue("backup", out var backupNode) && backupNode is JsonObject backup)
        {
            if (backup.TryGetPropertyValue("autostartServer", out var autostart))
            {
                deployment["autostartServer"] = autostart?.DeepClone();
                backup.Remove("autostartServer");
            }
        }

        if (root.TryGetPropertyValue("backup", out var rootBackup) && rootBackup is JsonObject rootBackupObj)
        {
            if (rootBackupObj.TryGetPropertyValue("autostartServer", out var autostart))
            {
                deployment["autostartServer"] = autostart?.DeepClone();
                rootBackupObj.Remove("autostartServer");
            }
        }
    }

    private static void CopyEdgeFields(JsonObject source, JsonObject target)
    {
        foreach (var (key, value) in source)
        {
            if (key is "routing" or "apiHost" or "webHost" or "tlsMode" or "cloudflare")
            {
                if (key is "apiHost" or "webHost")
                {
                    var routing = EnsureObject(target, "routing");
                    routing[key] = value?.DeepClone();
                }
                else if (key == "routing" && value is JsonValue routingValue)
                {
                    var routing = EnsureObject(target, "routing");
                    routing["mode"] = routingValue.DeepClone();
                }
                else if (key == "routing" && value is JsonObject routingObj)
                {
                    target["routing"] = routingObj.DeepClone();
                }
                else
                {
                    target[key] = value?.DeepClone();
                }
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private static void NormalizeEdgeRouting(JsonObject edge)
    {
        var routing = EnsureObject(edge, "routing");
        if (routing.TryGetPropertyValue("routing", out var nested))
        {
            routing["mode"] = nested?.DeepClone();
            routing.Remove("routing");
        }

        if (edge.TryGetPropertyValue("routing", out var flatRouting) && flatRouting is JsonValue modeValue)
        {
            routing["mode"] = modeValue.DeepClone();
            edge.Remove("routing");
            edge["routing"] = routing;
        }
        else if (edge.TryGetPropertyValue("apiHost", out var apiHost))
        {
            routing["apiHost"] = apiHost?.DeepClone();
            edge.Remove("apiHost");
        }

        if (edge.TryGetPropertyValue("webHost", out var webHost))
        {
            routing = EnsureObject(edge, "routing");
            routing["webHost"] = webHost?.DeepClone();
            edge.Remove("webHost");
        }
    }

    private static void NormalizeEdgePorts(JsonObject root, JsonObject edge)
    {
        var ports = EnsureObject(edge, "ports");
        if (root.TryGetPropertyValue("ports", out var rootPorts) && rootPorts is JsonObject rootPortsObj)
        {
            if (rootPortsObj.TryGetPropertyValue("edgeHttp", out var http))
            {
                ports["http"] = http?.DeepClone();
            }

            if (rootPortsObj.TryGetPropertyValue("edgeHttps", out var https))
            {
                ports["https"] = https?.DeepClone();
            }
        }

        if (isV1Ports(root))
        {
            var v1RootPortsObj = root["ports"] as JsonObject;
            if (v1RootPortsObj?.TryGetPropertyValue("apiHttp", out var apiHttp) == true)
            {
                ports["http"] = apiHttp?.DeepClone();
            }

            if (v1RootPortsObj?.TryGetPropertyValue("apiHttps", out var apiHttps) == true)
            {
                ports["https"] = apiHttps?.DeepClone();
            }
        }

        edge["ports"] = ports;
    }

    private static bool isV1Ports(JsonObject root)
    {
        if (root["ports"] is not JsonObject ports)
        {
            return false;
        }

        return ports.ContainsKey("apiHttp") || ports.ContainsKey("apiHttps");
    }

    private static void MoveBackupUpdate(JsonObject root, JsonObject deployment)
    {
        if (root.TryGetPropertyValue("backup", out var backup) && backup is JsonObject backupObj)
        {
            deployment["backup"] = backupObj.DeepClone();
            root.Remove("backup");
        }
        else if (!deployment.ContainsKey("backup"))
        {
            deployment["backup"] = new JsonObject
            {
                ["enabled"] = false,
                ["schedule"] = "daily",
                ["retainCount"] = 14,
                ["directory"] = "",
            };
        }

        if (root.TryGetPropertyValue("update", out var update) && update is JsonObject updateObj)
        {
            deployment["update"] = updateObj.DeepClone();
            root.Remove("update");
        }
        else if (!deployment.ContainsKey("update"))
        {
            deployment["update"] = new JsonObject
            {
                ["enabled"] = false,
                ["healthCheckTimeoutSeconds"] = 180,
                ["autoRestoreOnFailure"] = false,
                ["recreateOnUpdate"] = true,
                ["services"] = new JsonArray(),
            };
        }
    }

    private static void HoistDatastoreFields(JsonObject root, JsonObject postgres, JsonObject cql)
    {
        if (root.TryGetPropertyValue("postgresDatabase", out var db))
        {
            postgres["database"] = db?.DeepClone();
        }

        if (root.TryGetPropertyValue("databaseMode", out var mode))
        {
            cql["backend"] = MapDatabaseModeToBackend(mode);
        }
        else if (root.TryGetPropertyValue("scyllaMode", out var scyllaMode))
        {
            cql["backend"] = MapDatabaseModeToBackend(scyllaMode);
        }

        if (root.TryGetPropertyValue("clusterName", out var clusterName))
        {
            cql["clusterName"] = clusterName?.DeepClone();
        }

        if (root.TryGetPropertyValue("scyllaKeyspace", out var keyspace))
        {
            cql["keyspace"] = keyspace?.DeepClone();
        }
    }

    private static JsonNode MapDatabaseModeToBackend(JsonNode? modeNode)
    {
        var wire = modeNode?.GetValue<string>() ?? "single";
        return wire.Trim().ToLowerInvariant() switch
        {
            "single" => "scylla-single",
            "multi" => "scylla-multi",
            "cassandra" => "cassandra",
            "scylla-single" => "scylla-single",
            "scylla-multi" => "scylla-multi",
            _ => "scylla-single",
        };
    }

    private static void HoistApiFields(JsonObject root, JsonObject api)
    {
        if (root.TryGetPropertyValue("apiImage", out var image))
        {
            api["image"] = image?.DeepClone();
        }

        var oauth = EnsureObject(api, "oauth");
        if (root.TryGetPropertyValue("oauth", out var rootOauth) && rootOauth is JsonObject rootOauthObj)
        {
            foreach (var (key, value) in rootOauthObj)
            {
                oauth[key] = value?.DeepClone();
            }
        }

        if (root.TryGetPropertyValue("apiRuntime", out var runtime) && runtime is JsonObject runtimeObj)
        {
            foreach (var (key, value) in runtimeObj)
            {
                if (key is "callbackBaseUrl" or "jwtAuthority" or "jwtAudience")
                {
                    oauth[key] = value?.DeepClone();
                }
                else if (key == "corsAllowedOrigins")
                {
                    api["corsAllowedOrigins"] = value?.DeepClone();
                }
            }
        }

        if (api.TryGetPropertyValue("callbackBaseUrl", out var cb))
        {
            oauth["callbackBaseUrl"] = cb?.DeepClone();
            api.Remove("callbackBaseUrl");
        }

        if (api.TryGetPropertyValue("jwtAuthority", out var jwtAuth))
        {
            oauth["jwtAuthority"] = jwtAuth?.DeepClone();
            api.Remove("jwtAuthority");
        }

        if (api.TryGetPropertyValue("jwtAudience", out var jwtAud))
        {
            oauth["jwtAudience"] = jwtAud?.DeepClone();
            api.Remove("jwtAudience");
        }

        api["oauth"] = oauth;

        if (root.TryGetPropertyValue("persistence", out var persistence) && persistence is JsonObject persistenceObj)
        {
            api["resilience"] = persistenceObj.DeepClone();
        }

        if (root.TryGetPropertyValue("socket", out var socket) && socket is JsonObject socketObj
            && socketObj.TryGetPropertyValue("batchBytesThreshold", out var threshold))
        {
            api["batchBytesThreshold"] = threshold?.DeepClone();
        }

        if (root.TryGetPropertyValue("cluster", out var cluster) && cluster is JsonObject clusterObj
            && clusterObj.TryGetPropertyValue("nodeGroup", out var nodeGroup))
        {
            api["nodeGroup"] = nodeGroup?.DeepClone();
        }
        else if (api.TryGetPropertyValue("node", out var node) && node is JsonObject nodeObj
                 && nodeObj.TryGetPropertyValue("group", out var group))
        {
            api["nodeGroup"] = group?.DeepClone();
            api.Remove("node");
        }

        if (root.TryGetPropertyValue("storage", out var storage))
        {
            api["storage"] = storage?.DeepClone();
        }

        if (root.TryGetPropertyValue("firebase", out var firebase))
        {
            api["firebase"] = firebase?.DeepClone();
        }
    }

    internal static V1EndpointSnapshot? CaptureV1Snapshot(JsonElement root)
    {
        if (!root.TryGetProperty("deployment", out var deployment)
            || deployment.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var includeWeb = deployment.TryGetProperty("includeWeb", out var iw)
            && iw.ValueKind == JsonValueKind.True;
        var webHttps = deployment.TryGetProperty("webHttps", out var wh)
            && wh.ValueKind == JsonValueKind.True;
        var webEnabled = includeWeb || webHttps;

        var apiHttps = V1DefaultApiHttps;
        var webHttpsPort = V1DefaultWebHttps;
        if (root.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Object)
        {
            if (ports.TryGetProperty("apiHttps", out var ah) && ah.TryGetInt32(out var apiHttpsValue))
            {
                apiHttps = apiHttpsValue;
            }

            if (ports.TryGetProperty("webHttps", out var wHp) && wHp.TryGetInt32(out var webHttpsValue))
            {
                webHttpsPort = webHttpsValue;
            }
        }

        var hosts = new List<string>();
        if (deployment.TryGetProperty("hosts", out var hostsEl) && hostsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var host in hostsEl.EnumerateArray())
            {
                if (host.ValueKind == JsonValueKind.String)
                {
                    var value = host.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        hosts.Add(value);
                    }
                }
            }
        }

        return new V1EndpointSnapshot(webEnabled, apiHttps, webHttpsPort, hosts);
    }

    internal static void RejectInvalidV2Keys(JsonElement root, string configPath)
    {
        if (!root.TryGetProperty("deployment", out var deployment)
            || deployment.ValueKind != JsonValueKind.Object
            || !deployment.TryGetProperty("edge", out var edge)
            || edge.ValueKind != JsonValueKind.Object
            || !edge.TryGetProperty("enabled", out var enabledProp))
        {
            return;
        }

        if (enabledProp.ValueKind == JsonValueKind.False)
        {
            throw new InvalidOperationException(
                $"{configPath}: deployment.edge.enabled=false was removed — edge-nginx is always " +
                "emitted. Use edge.tlsMode=none for plaintext HTTP on edge.ports.http.");
        }

        throw new InvalidOperationException(
            $"{configPath}: deployment.edge.enabled was removed — edge-nginx is always emitted. " +
            "Delete the enabled property; use edge.tlsMode to select none / privateCa.");
    }

    private static JsonObject EnsureObject(JsonObject parent, string name)
    {
        if (parent.TryGetPropertyValue(name, out var existing) && existing is JsonObject obj)
        {
            return obj;
        }

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static string Serialize(JsonObject root)
        => root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

    private static bool ReadBool(JsonObject obj, string name)
        => obj.TryGetPropertyValue(name, out var node)
           && node is JsonValue value
           && value.TryGetValue<bool>(out var parsed)
           && parsed;
}
