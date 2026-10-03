using System.Text.Json;
using System.Text.RegularExpressions;
using Interfold.Settings.Contracts.Configuration;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>Loose read of the Firebase console web block. <c>vapidKey</c> is absent there.</summary>
internal sealed class FirebaseWebSnippetFields
{
    public string? ApiKey { get; set; }
    public string? AuthDomain { get; set; }
    public string? ProjectId { get; set; }
    public string? StorageBucket { get; set; }
    public string? MessagingSenderId { get; set; }
    public string? AppId { get; set; }
    public string? VapidKey { get; set; }

    internal FirebaseWebClientConfig WithVapid(string vapidKey) => new(
        Require(ApiKey, "apiKey"),
        Require(AuthDomain, "authDomain"),
        Require(ProjectId, "projectId"),
        StorageBucket,
        Require(MessagingSenderId, "messagingSenderId"),
        Require(AppId, "appId"),
        vapidKey);

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Firebase web config is missing {name}.")
            : value;
}

/// <summary>Reads the web SDK block the Firebase console shows, and writes the JSON file the phase ingests.</summary>
internal static partial class FirebaseWebSnippet
{
    internal const string SavedFileName = "firebase-web-config.json";

    // The console block is a JS object (`const firebaseConfig = { apiKey: "..." };`), not JSON.
    [GeneratedRegex(@"(?<=[{,]\s*)([A-Za-z_][A-Za-z0-9_]*)(?=\s*:)", RegexOptions.CultureInvariant)]
    private static partial Regex BareKey();

    [GeneratedRegex(@",(\s*})")]
    private static partial Regex TrailingComma();

    internal static bool TryRead(string text, out FirebaseWebSnippetFields fields)
    {
        try
        {
            fields = Read(text);
            return true;
        }
        catch (InvalidDataException)
        {
            fields = null!;
            return false;
        }
    }

    internal static FirebaseWebSnippetFields Read(string text)
    {
        // Import lines use `{` before the config object, so the slice starts after `firebaseConfig`.
        var from = 0;
        var named = text.IndexOf("firebaseConfig", StringComparison.Ordinal);
        if (named >= 0)
            from = named + "firebaseConfig".Length;

        var start = text.IndexOf('{', from);
        if (start < 0)
        {
            throw new InvalidDataException(
                "This file is not the firebaseConfig block. Copy only const firebaseConfig = { ... }; and leave out the import lines.");
        }

        var depth = 0;
        var end = -1;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    end = i;
                    break;
                }
            }
        }

        if (end < 0)
        {
            throw new InvalidDataException(
                "This file is not the firebaseConfig block. Copy only const firebaseConfig = { ... }; and leave out the import lines.");
        }

        var json = TrailingComma().Replace(BareKey().Replace(text[start..(end + 1)], "\"$1\""), "$1");
        try
        {
            var fields = JsonSerializer.Deserialize(json, FirebaseCamelCaseReadContext.Default.FirebaseWebSnippetFields)
                ?? throw new InvalidDataException("Firebase web config is empty.");
            if (string.IsNullOrWhiteSpace(fields.ApiKey) || string.IsNullOrWhiteSpace(fields.AppId))
            {
                throw new InvalidDataException(
                    "This file is not the firebaseConfig block. Copy it from Project settings, Your apps, Web, then save that block as a file.");
            }

            return fields;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "This file is not the firebaseConfig block. Copy it from Project settings, Your apps, Web, then save that block as a file.",
                ex);
        }
    }

    internal static string Save(string directory, FirebaseWebClientConfig config)
    {
        var path = Path.Combine(directory, SavedFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(config, FirebaseCamelCaseWriteContext.Default.FirebaseWebClientConfig));
        return Path.GetFullPath(path);
    }
}
