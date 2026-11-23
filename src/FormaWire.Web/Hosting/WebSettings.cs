using System.Text.RegularExpressions;

namespace FormaWire.Web.Hosting;

public sealed class WebSettings
{
    public string PathBase { get; init; } = "";
    public int MaxUploadMB { get; init; } = 20;
    public int MaxConcurrentCircuits { get; init; } = 30;
    public string SiteUrl { get; init; } = "https://indaloop.ai";
    public long MaxUploadBytes => MaxUploadMB * 1024L * 1024L;
    public string BaseHref => PathBase + "/";
    public string SiteHost => new Uri(SiteUrl).Host;
    // The server version's legal documents live on the website.
    public string TermsUrl => SiteUrl + "/formawire-termini";
    public string PrivacyUrl => SiteUrl + "/privacy";
    /// <summary>True when drawings are processed in the browser and never sent anywhere.</summary>
    public bool ProcessesInBrowser => false;

    public static WebSettings Read(IConfiguration configuration)
    {
        var path = (configuration["PathBase"] ?? "").TrimEnd('/');
        if (!Regex.IsMatch(path, @"^(?:/[a-zA-Z0-9_-]+)*$"))
            throw new InvalidOperationException("PathBase must be empty or a path such as /formawire.");
        var upload = configuration.GetValue("MaxUploadMB", 20);
        var circuits = configuration.GetValue("MaxConcurrentCircuits", 30);
        if (upload < 1 || upload > 1024)
            throw new InvalidOperationException("MaxUploadMB must be between 1 and 1024.");
        if (circuits < 1)
            throw new InvalidOperationException("MaxConcurrentCircuits must be positive.");
        var site = (configuration["SiteUrl"] ?? "https://indaloop.ai").TrimEnd('/');
        if (!Uri.TryCreate(site, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("SiteUrl must be an absolute HTTP(S) base URL without a query or fragment.");
        return new() { PathBase = path, MaxUploadMB = upload, MaxConcurrentCircuits = circuits, SiteUrl = site };
    }
}
