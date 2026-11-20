namespace FormaWire.Web.Hosting;

/// <summary>
/// The settings the shared editor reads. The browser build has no server to configure, so
/// they are fixed here. The upload limit still protects the browser's memory.
/// </summary>
public sealed class WebSettings
{
    public int MaxUploadMB { get; init; } = 20;
    public string SiteUrl { get; init; } = "https://madnetai.com";
    public long MaxUploadBytes => MaxUploadMB * 1024L * 1024L;
}
