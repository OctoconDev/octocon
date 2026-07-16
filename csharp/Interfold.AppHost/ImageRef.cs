namespace Interfold.AppHostGraph;

/// <summary>
/// A parsed container image reference (image name + tag). Tags only - SHA digests would
/// need a different code path.
/// </summary>
internal readonly record struct ImageRef(string Image, string Tag)
{
    /// <summary>
    /// Parses a reference like <c>ghcr.io/foo/bar:1.2.3</c> or <c>interfold-api:test</c>.
    /// References without a tag default to <c>latest</c>.
    /// </summary>
    public static ImageRef Parse(string reference)
    {
        // Find the LAST colon that isn't inside the registry-port portion (e.g. localhost:5000/foo:tag).
        // The tag separator is the last colon after the last slash, since any colon before the last slash
        // belongs to the registry hostname.
        var lastSlash = reference.LastIndexOf('/');
        var lastColon = reference.LastIndexOf(':');
        if (lastColon > lastSlash && lastColon > 0)
        {
            return new ImageRef(reference[..lastColon], reference[(lastColon + 1)..]);
        }
        // No tag specified - default to "latest".
        return new ImageRef(reference, "latest");
    }
}
