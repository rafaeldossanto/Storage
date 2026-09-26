using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Storage.Api.Auth;
using Storage.Api.Endpoints;
using Storage.Api.Errors;
using Storage.Api.Tenancy;
using Storage.Application;
using Storage.Application.Abstractions;
using Storage.Application.Accounts;
using Storage.Domain.Accounts;
using Storage.Infrastructure;
using Storage.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// `dotnet build` runs this file through GetDocument.Insider to write the OpenAPI contract.
// That run only builds the host to read the endpoints - it never serves a request nor
// touches the database - so it must not fail for lack of a connection string or a key.
var generatingOpenApiDocument =
    Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

var mongo = builder.Configuration.GetSection("Mongo");

builder.Services.AddStoragePersistence(
    connectionString: mongo["ConnectionString"]
        ?? (generatingOpenApiDocument
            ? "mongodb://unused-while-generating-the-openapi-document"
            : throw new InvalidOperationException("Mongo:ConnectionString is not configured.")),
    databaseName: mongo["Database"] ?? "storage");

builder.Services.AddStorageApplication();
builder.Services.AddHttpContextAccessor();

// Every shop-scoped read takes the shop from the signed-in user's token. There is no
// fallback shop anywhere, in any environment.
builder.Services.AddScoped<ClaimsTenantContext>();
builder.Services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<ClaimsTenantContext>());
builder.Services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<ClaimsTenantContext>());

var authSettings = AuthSettings.From(
    builder.Configuration,
    allowEphemeralKey: builder.Environment.IsDevelopment() || generatingOpenApiDocument,
    secureCookies: !builder.Environment.IsDevelopment());

builder.Services.AddSingleton(authSettings);
builder.Services.AddSingleton(new SessionPolicy(authSettings.RefreshTokenLifetime));
builder.Services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Claims keep the names they were issued with ("tenant_id", "role"), instead of
        // being renamed to long WS-Federation URIs.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = JwtAccessTokenIssuer.ValidationParameters(authSettings);

        // The same problem shape as every other refusal, so the front end handles 401 and
        // 403 the way it handles everything else: by code.
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                await ProblemExceptionHandler.WriteAsync(
                    context.HttpContext, StatusCodes.Status401Unauthorized, ProblemExceptionHandler.UnauthenticatedCode);
            },
            OnForbidden = context => ProblemExceptionHandler.WriteAsync(
                context.HttpContext, StatusCodes.Status403Forbidden, ProblemExceptionHandler.ForbiddenCode),
        };
    });

builder.Services.AddAuthorizationBuilder()
    // Secure by default: an endpoint someone forgets to protect still requires a signed-in
    // user. Only routes that opt out with AllowAnonymous are open.
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(TeamEndpoints.OwnerPolicy, policy => policy.RequireRole(nameof(UserRole.Owner)));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Ten attempts a minute per address on the auth routes: plenty for a person, useless
    // for a script. Behind a reverse proxy this needs forwarded headers configured, or
    // every client shares the proxy's address (task 23, deploy).
    options.AddPolicy(AuthEndpoints.RateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Enums travel by name ("Unit", not 0), the same rule as in the database.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

    // Numbers are numbers. The web default also accepts "899" as text, which types every
    // amount in the contract as integer-or-string and pushes that ambiguity into the
    // front end's generated client.
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();

// The contract the front end generates its typed client from.
builder.Services.AddOpenApi();

// The front end is a separate application with its own origin. Only the origins listed in
// configuration may call the API from a browser, and they may send the refresh cookie.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapAuthEndpoints();
app.MapTeamEndpoints();
app.MapCategoryEndpoints();
app.MapProductEndpoints();
app.MapStockEndpoints();
app.MapReceivingEndpoints();

// Idempotent: creating an index that already exists is a no-op, so every boot guarantees
// the unique indexes (barcode per shop, e-mail per platform) and the session TTL are in
// place. Skipped while the build writes the OpenAPI contract, with no database to talk to.
if (!generatingOpenApiDocument)
{
    await app.Services.GetRequiredService<MongoStorageContext>().EnsureIndexesAsync();
}

await app.RunAsync();
