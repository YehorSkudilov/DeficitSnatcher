using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using DeficitSnatcher.Api;
using DeficitSnatcher.Api.Auth;
using DeficitSnatcher.Api.Buying;
using DeficitSnatcher.Api.Data;
using DeficitSnatcher.Api.Notifications;
using DeficitSnatcher.Api.Settings;
using DeficitSnatcher.Api.Tracking;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// `dotnet DeficitSnatcher.Api.dll install-browser`: installs the headless Chromium the checkouts and Browser-kind
// sites use (the Dockerfile runs it; run it once locally too).
if (args is ["install-browser", ..])
    return Microsoft.Playwright.Program.Main(["install", .. args.Skip(1), "chromium"]);

// Settings from .env next to the project, the same file docker-compose uses (see .env.example).
DotEnv.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"));

var builder = WebApplication.CreateBuilder(args);

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

// The web (DeficitSnatcher.Web) calls the API from its own site: its origin(s), from Cors__AllowedOrigins (comma-separated).
var corsOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(o => o.TrimEnd('/')).ToArray();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddSingleton<SecretBox>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddHttpClient(nameof(Notifier), c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<BrowserPool>();
// Timeouts are per request, from settings (PageFetcher).
builder.Services.AddHttpClient<PageFetcher>(c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(PageFetcher.CreateHandler)
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<Buyer>();
builder.Services.AddSingleton<CheckService>();
builder.Services.AddSingleton<Tracker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Tracker>());

builder.Services.AddProblemDetails();
builder.Services.AddControllers(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// Behind the reverse proxy: trust X-Forwarded-* from loopback plus ReverseProxy:KnownNetworks (comma-separated CIDRs),
// so rate limiting sees the client's address.
var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
foreach (var network in (app.Configuration["ReverseProxy:KnownNetworks"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
app.UseForwardedHeaders(forwarded);

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    await Seed.RunAsync(db);
}

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler();

app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
{
    try
    {
        return await db.Database.CanConnectAsync(ct) ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();

app.Run();
return 0;
