using FormaWire.Web.Components;
using FormaWire.Web.Hosting;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
var settings = WebSettings.Read(builder.Configuration);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<AssetVersioner>();
builder.Services.AddSingleton<CircuitCapacity>();
builder.Services.AddScoped<CircuitHandler, CapacityCircuitHandler>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
{
    options.DetailedErrors = builder.Environment.IsDevelopment();
    options.DisconnectedCircuitMaxRetained = 20;
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(2);
}).AddHubOptions(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaximumReceiveMessageSize = settings.MaxUploadBytes;
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Keep the framework's loopback-only trusted proxies/networks. nginx is local.
    options.ForwardLimit = 1;
});

var app = builder.Build();
if (!app.Services.GetRequiredService<AssetVersioner>().ValidateWebRoot() && !app.Environment.IsDevelopment())
{
    Environment.ExitCode = 1;
    await app.DisposeAsync();
    return;
}
if (settings.PathBase.Length > 0)
    app.UsePathBase(settings.PathBase);
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/error", createScopeForErrors: true);

app.Use(async (context, next) =>
{
    // UsePathBase alone also accepts unprefixed URLs. Do not expose a second copy
    // of the app at / when configured beneath a website path.
    if (settings.PathBase.Length > 0 && context.Request.PathBase != settings.PathBase)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    if (!context.Request.Path.HasValue)
    {
        context.Response.Redirect(settings.BaseHref + context.Request.QueryString);
        return;
    }
    if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/" &&
        context.RequestServices.GetRequiredService<CircuitCapacity>().IsFull)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Redirect(settings.BaseHref + "busy");
        return;
    }
    await next();
});
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        // Revalidate assets even when opened through an older, unversioned page.
        if (context.File.Name is "indaloop-tema.css" or "app.css" or "editor.js" or "hosting.js" or "favicon.svg")
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
});
// Explicit routing ensures endpoint selection happens after UsePathBase.
app.UseRouting();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
