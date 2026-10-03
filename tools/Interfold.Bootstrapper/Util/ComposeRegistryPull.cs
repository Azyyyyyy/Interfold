namespace Interfold.Bootstrapper.Util;

internal static class ComposeRegistryPull
{
    internal static IReadOnlyList<string> ServicesToPull(string composeText)
    {
        var services = new List<string>();
        string? service = null;
        string? image = null;
        var never = false;

        void Flush()
        {
            if (service is not null && image is not null && !never && HasRegistryHost(image))
                services.Add(service);
            service = null;
            image = null;
            never = false;
        }

        foreach (var raw in composeText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
                continue;

            if (!line.StartsWith(' ') && line.EndsWith(':'))
            {
                Flush();
                continue;
            }

            if (IsServiceKey(line))
            {
                Flush();
                service = line.Trim()[..^1];
                continue;
            }

            if (service is null)
                continue;

            var trimmed = line.Trim();
            if (trimmed.StartsWith("image:", StringComparison.Ordinal))
                image = Unquote(trimmed["image:".Length..].Trim());
            else if (trimmed.StartsWith("pull_policy:", StringComparison.Ordinal)
                     && trimmed.EndsWith("never", StringComparison.Ordinal))
                never = true;
        }

        Flush();
        return services;
    }

    // see ImageRef.HasRegistryHost — a dotted or port-qualified first segment is a registry.
    internal static bool HasRegistryHost(string imageReference)
    {
        var name = ImageName(imageReference);
        var slash = name.IndexOf('/');
        var first = slash >= 0 ? name[..slash] : name;
        return first.Contains('.') || first.Contains(':');
    }

    private static bool IsServiceKey(string line) =>
        line.StartsWith("  ", StringComparison.Ordinal)
        && (line.Length < 3 || line[2] != ' ')
        && line.EndsWith(':')
        && line.IndexOf(':') == line.Length - 1;

    private static string ImageName(string reference)
    {
        var lastSlash = reference.LastIndexOf('/');
        var lastColon = reference.LastIndexOf(':');
        return lastColon > lastSlash && lastColon > 0 ? reference[..lastColon] : reference;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }
}
