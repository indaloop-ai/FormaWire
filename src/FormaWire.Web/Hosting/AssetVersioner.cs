using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace FormaWire.Web.Hosting;

public sealed class AssetVersioner(IWebHostEnvironment environment, ILogger<AssetVersioner> logger)
{
    public static IReadOnlyList<string> RequiredFiles { get; } =
        Array.AsReadOnly(new[] { "app.css", "editor.js", "hosting.js", "favicon.ico" });

    // GetOrAdd may run its factory more than once. Only the winning Lazy is
    // evaluated, so concurrent requests hash (or warn about) a file just once.
    private readonly ConcurrentDictionary<string, Lazy<string>> _versions = new(StringComparer.Ordinal);

    public string GetUrl(string path) => _versions.GetOrAdd(path,
        key => new Lazy<string>(() => Version(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private string Version(string path)
    {
        try
        {
            var file = environment.WebRootFileProvider.GetFileInfo(path);
            if (!file.Exists || file.IsDirectory)
            {
                logger.LogWarning("Static asset {AssetPath} is missing. Using its unversioned URL until restart.", path);
                return path;
            }
            using var stream = file.CreateReadStream();
            return $"{path}?v={Convert.ToHexString(SHA256.HashData(stream))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            // A file can disappear or become unreadable between the lookup and open.
            logger.LogWarning(ex, "Cannot read static asset {AssetPath}. Using its unversioned URL until restart.", path);
            return path;
        }
    }

    public bool ValidateWebRoot()
    {
        var webRoot = environment.WebRootPath;
        var expectedRoot = string.IsNullOrWhiteSpace(webRoot)
            ? Path.Combine(environment.ContentRootPath, "wwwroot") : webRoot;
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(webRoot) || !Directory.Exists(webRoot))
            missing.Add($"wwwroot directory ({expectedRoot}; WebRootPath: {webRoot ?? "not configured"})");
        missing.AddRange(RequiredFiles.Where(file => !File.Exists(Path.Combine(expectedRoot, file)))
            .Select(file => $"wwwroot/{file}"));
        if (missing.Count == 0) return true;

        logger.Log(environment.IsDevelopment() ? LogLevel.Warning : LogLevel.Critical,
            "FormaWire static asset validation failed. Content root: {ContentRoot}. Missing: {MissingFiles}. " +
            "Deploy the complete publish output, including wwwroot, and check service-user read permissions. {Action}",
            environment.ContentRootPath, string.Join(", ", missing),
            environment.IsDevelopment() ? "Continuing in Development." : "Startup aborted (exit code 1).");
        return false;
    }
}
