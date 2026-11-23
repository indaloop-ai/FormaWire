namespace FormaWire.Web.Hosting;

/// <summary>
/// The settings the shared editor reads. The browser build has no server to configure, so
/// they are fixed here. The upload limit still protects the browser's memory.
/// </summary>
public sealed class WebSettings
{
    public int MaxUploadMB { get; init; } = 20;
    public string SiteUrl { get; init; } = "https://indaloop.ai";
    public long MaxUploadBytes => MaxUploadMB * 1024L * 1024L;
    public string SiteHost => new Uri(SiteUrl).Host;
    // Static pages next to index.html (wwwroot/termini.html, wwwroot/privacy.html): readable
    // without starting the editor. Cloudflare Pages also serves them as /termini and /privacy.
    public string TermsUrl => "termini.html";
    public string PrivacyUrl => "privacy.html";
    /// <summary>True when drawings are processed in the browser and never sent anywhere.</summary>
    public bool ProcessesInBrowser => true;
}
