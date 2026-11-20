using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using System.Net;
using DeficitSnatcher.Api;
using DeficitSnatcher.Api.Auth;
using DeficitSnatcher.Api.Buying;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Diagnostics;
using DeficitSnatcher.Api.Notifications;
using DeficitSnatcher.Api.Settings;
using DeficitSnatcher.Api.Tracking;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// `dotnet DeficitSnatcher.Api.dll install-browser`: installs the Chromium the checkouts and Browser-kind
// sites use (the Dockerfile runs it; run it once locally too).
if (args is ["install-browser", ..])
    return Microsoft.Playwright.Program.Main(["install", .. args.Skip(1), "chromium"]);

// Settings from .env: next to the project, or the repository's (the one docker-compose uses; see .env.example).
DotEnv.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"));
DotEnv.Load(Path.Combine(Directory.GetCurrentDirectory(), "..", ".env"));

var builder = WebApplication.CreateBuilder(args);
var diagnosticsLogs = new DiagnosticsLogBuffer();
builder.Logging.AddProvider(diagnosticsLogs);

var auth = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
auth.SigningKey = builder.Configuration["Jwt:SigningKey"] ?? "";
auth.Validate();
builder.Services.AddSingleton(auth);

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(
    builder.Configuration.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.")));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = auth.Issuer,
            ValidAudience = auth.Audience,
            IssuerSigningKey = auth.Key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
        };
    });
// Everything needs the owner signed in, unless it says [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimiting:AuthPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
});

// In Docker the site's nginx passes /api through, so it's one origin and CORS isn't needed. Only for `npm run dev` against
// a separately run API: its origin(s), from Cors__AllowedOrigins (comma-separated).
var corsOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(o => o.TrimEnd('/')).ToArray();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddSingleton<SecretBox>();
builder.Services.AddSingleton(diagnosticsLogs);
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddHttpClient(nameof(Notifier), c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<Backoff>();
builder.Services.AddSingleton<DeficitSnatcher.Api.Alarms.AlarmService>();
builder.Services.AddSingleton<BrowserPool>();
builder.Services.AddSingleton<BrowserSessions>();
// Timeouts are per request, from settings (PageFetcher).
builder.Services.AddHttpClient<PageFetcher>(c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(PageFetcher.CreateHandler)
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
// The AI (ChatGPT), off while OpenAI__ApiKey is unset: sets pages up, goes through checkouts, fixes broken ones.
builder.Services.AddHttpClient<DeficitSnatcher.Api.Ai.OpenAiClient>(c => c.Timeout = TimeSpan.FromMinutes(8));
builder.Services.AddSingleton<DeficitSnatcher.Api.Ai.CheckoutAgent>();
builder.Services.AddSingleton<DeficitSnatcher.Api.Ai.PageSetup>();
builder.Services.AddSingleton<ScanFollowUp>();
builder.Services.AddSingleton<DeficitSnatcher.Api.Ai.RestockForecaster>();
builder.Services.AddSingleton<Buyer>();
builder.Services.AddSingleton<CheckService>();
builder.Services.AddSingleton<Tracker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Tracker>());
// The Deploy tab (Deploy/DeployController.cs).
builder.Services.AddSingleton(builder.Configuration.GetSection("Deploy").Get<DeficitSnatcher.Api.Deploy.DeployOptions>()
    ?? new DeficitSnatcher.Api.Deploy.DeployOptions());
builder.Services.AddSingleton<DeficitSnatcher.Api.Deploy.ServerVersion>();
builder.Services.AddHttpClient(DeficitSnatcher.Api.Deploy.DeployController.ClientName, DeficitSnatcher.Api.Deploy.DeployController.Configure);

builder.Services.AddProblemDetails();
builder.Services.AddControllers(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// Behind the reverse proxy: trust X-Forwarded-* from loopback plus ReverseProxy:KnownNetworks (comma-separated CIDRs),
// so rate limiting sees the client's address.
// The site's nginx and the server's reverse proxy are both in front: read past every trusted hop.
var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, ForwardLimit = null };
foreach (var network in (app.Configuration["ReverseProxy:KnownNetworks"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
app.UseForwardedHeaders(forwarded);

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // The database may still be starting (or restarting): wait for it a while rather than crash on the first try.
    var startLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            db.Database.Migrate();
            break;
        }
        catch (Exception e) when (attempt < 30 && e is Npgsql.NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
        {
            startLog.LogWarning("Database not reachable yet (try {Attempt} of 30): {Error}", attempt, e.Message);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
    await Seed.RunAsync(db);
}

// A checkout still "running" was cut off by a restart (a deploy, for one): it didn't finish, so it's failed.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Purchases.Where(p => p.Status == PurchaseStatus.Running).ExecuteUpdateAsync(u => u
        .SetProperty(p => p.Status, p => p.DryRun ? PurchaseStatus.Failed : PurchaseStatus.Interrupted)
        .SetProperty(p => p.Message, p => p.DryRun
            ? "Interrupted: the server restarted during the dry run."
            : "Interrupted: the server restarted during the checkout. Check the store for an order, then mark this as not bought if there's none.")
        .SetProperty(p => p.FinishedAt, DateTimeOffset.UtcNow));
}

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler();

app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();
DeficitSnatcher.Api.Alarms.AlarmEndpoints.Map(app);
app.MapGet("/api/health", async (AppDbContext db, CancellationToken ct) =>
{
    try
    {
        return await db.Database.CanConnectAsync(ct) ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous().DisableRateLimiting();

app.MapGet("/api/diagnostics/logs", (int? limit, DiagnosticsLogBuffer logs) =>
    Results.Ok(new { entries = logs.Recent(limit ?? 1000) }));

// Camoufox is a separate container. Accept its forwarded stdout/stderr only from loopback/private container networks;
// the browser container has no Docker socket or API credentials. All other diagnostics routes use normal signed-in auth.
app.MapPost("/internal/diagnostics/camoufox-log", (HttpContext context, CamoufoxLogLine input, DiagnosticsLogBuffer logs) =>
{
    if (!IsPrivateAddress(context.Connection.RemoteIpAddress))
        return Results.NotFound();
    var line = input.Line?.TrimEnd();
    if (string.IsNullOrWhiteSpace(line))
        return Results.NoContent();
    logs.AddCamoufox(line);
    return Results.NoContent();
}).AllowAnonymous().DisableRateLimiting().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(8192));

app.Run();
return 0;

static bool IsPrivateAddress(IPAddress? address)
{
    if (address == null)
        return false;
    if (address.IsIPv4MappedToIPv6)
        address = address.MapToIPv4();
    if (IPAddress.IsLoopback(address))
        return true;
    var bytes = address.GetAddressBytes();
    if (bytes.Length == 4)
        return bytes[0] == 10
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] == 169 && bytes[1] == 254;
    return bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc;
}

public sealed record CamoufoxLogLine(string? Line);
