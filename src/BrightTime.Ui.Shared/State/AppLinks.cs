namespace BrightTime.Ui.Shared.State;

/// <summary>
/// Links the operating system hands the app (D35's <c>/app/*</c> paths), queued until the UI is
/// ready to open them. The host pushes; the layout opens.
/// </summary>
public sealed class AppLinks
{
    private readonly Lock _lock = new();
    private string? _pending;

    /// <summary>Raised when a link arrives, so an open layout can go to it at once.</summary>
    public event EventHandler? Arrived;

    /// <summary>Takes a link the system opened the app with. Only this site's <c>/app/</c> paths are accepted.</summary>
    public bool Open(Uri link)
    {
        ArgumentNullException.ThrowIfNull(link);
        string path = link.IsAbsoluteUri ? link.PathAndQuery + link.Fragment : link.OriginalString;
        if (!path.StartsWith("/app/", StringComparison.Ordinal))
        {
            return false;
        }

        lock (_lock)
        {
            _pending = path;
        }

        Arrived?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The link to open, once; null when there is none.</summary>
    public string? Take()
    {
        lock (_lock)
        {
            string? path = _pending;
            _pending = null;
            return path;
        }
    }
}

/// <summary>
/// A consent link's token, held while the guardian signs in: the sign-in screen loses the
/// address it came from, fragment and all. Memory only, never stored.
/// </summary>
public sealed class PendingConsent
{
    public Guid? LinkId { get; private set; }

    public string? Token { get; private set; }

    public void Hold(Guid linkId, string token)
    {
        LinkId = linkId;
        Token = token;
    }

    public string? TokenFor(Guid linkId) => LinkId == linkId ? Token : null;

    public void Clear()
    {
        LinkId = null;
        Token = null;
    }
}
