namespace Interfold.AppHost;

/// <summary>Parsed image reference (name + tag). Tags only — SHA digests would need a
/// different code path.</summary>
internal readonly record struct ImageRef(string Image, string Tag)
{
    /// <summary>True when the first path segment is a host (<c>ghcr.io/…</c>,
    /// <c>localhost:5000/…</c>). Unprefixed names (<c>interfold-wasm</c>, <c>nginx</c>)
    /// are Docker Hub short names or local-only tags.</summary>
    public bool HasRegistryHost
    {
        get
        {
            var slash = Image.IndexOf('/');
            var first = slash >= 0 ? Image.AsSpan(0, slash) : Image.AsSpan();
            return first.Contains('.') || first.Contains(':');
        }
    }

    /// <summary>Parses <c>ghcr.io/foo/bar:1.2.3</c>-style refs. Missing tag → <c>latest</c>.</summary>
    public static ImageRef Parse(string reference)
    {
        // Tag separator is the LAST colon after the LAST slash — any earlier colon belongs
        // to a registry-hostname port (e.g. localhost:5000/foo:tag).
        var lastSlash = reference.LastIndexOf('/');
        var lastColon = reference.LastIndexOf(':');
        if (lastColon > lastSlash && lastColon > 0)
        {
            return new ImageRef(reference[..lastColon], reference[(lastColon + 1)..]);
        }
        return new ImageRef(reference, "latest");
    }
}
