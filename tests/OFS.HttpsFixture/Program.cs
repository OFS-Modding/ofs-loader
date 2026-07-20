using Microsoft.AspNetCore.Server.Kestrel.Core;

if (args.Length != 4 ||
    !int.TryParse(args[3], out var port) ||
    port is < 1024 or > 65535)
{
    Console.Error.WriteLine("Usage: OFS.HttpsFixture <root> <certificate.pfx> <password> <port>");
    return 2;
}

var root = Path.GetFullPath(args[0]);
var certificate = Path.GetFullPath(args[1]);
if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
if (!File.Exists(certificate)) throw new FileNotFoundException("TLS certificate not found.", certificate);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.ListenLocalhost(port, listen =>
    {
        listen.Protocols = HttpProtocols.Http1AndHttp2;
        listen.UseHttps(certificate, args[2]);
    });
});

var app = builder.Build();
app.MapGet("/health", () => Results.Text("ready", "text/plain"));
app.MapGet("/{**relativePath}", (string? relativePath) =>
{
    if (string.IsNullOrWhiteSpace(relativePath)) return Results.NotFound();
    var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    var prefix = root.EndsWith(Path.DirectorySeparatorChar)
        ? root
        : root + Path.DirectorySeparatorChar;
    if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
    {
        return Results.NotFound();
    }
    var contentType = Path.GetExtension(candidate).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".ofmod" => "application/octet-stream",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream",
    };
    return Results.File(candidate, contentType, enableRangeProcessing: false);
});

Console.WriteLine($"READY https://localhost:{port}");
await app.RunAsync();
return 0;
