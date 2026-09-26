using Storage.Api.Tenancy;
using Storage.Application.Abstractions;
using Storage.Infrastructure;
using Storage.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var mongo = builder.Configuration.GetSection("Mongo");

builder.Services.AddStoragePersistence(
    connectionString: mongo["ConnectionString"]
        ?? throw new InvalidOperationException("Mongo:ConnectionString is not configured."),
    databaseName: mongo["Database"] ?? "storage");

builder.Services.AddHttpContextAccessor();

if (builder.Environment.IsDevelopment())
{
    // Until login exists, development runs as one fixed shop. This branch is the only
    // place the fixed tenant can be registered, and it is unreachable outside Development.
    var developmentTenant = Guid.Parse(
        builder.Configuration["Storage:DevelopmentTenantId"]
        ?? throw new InvalidOperationException("Storage:DevelopmentTenantId is not configured."));

    builder.Services.AddScoped<ITenantContext>(_ => new DevelopmentTenantContext(developmentTenant));
}
else
{
    builder.Services.AddScoped<ITenantContext, ClaimsTenantContext>();
}

// The front end is a separate application with its own origin. Only the origins listed in
// configuration may call the API from a browser.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Idempotent: creating an index that already exists is a no-op, so every boot guarantees
// the unique barcode index and the path index are in place.
await app.Services.GetRequiredService<MongoStorageContext>().EnsureIndexesAsync();

await app.RunAsync();
