using System.Text.Json.Serialization;
using Storage.Api.Endpoints;
using Storage.Api.Errors;
using Storage.Api.Tenancy;
using Storage.Application;
using Storage.Application.Abstractions;
using Storage.Infrastructure;
using Storage.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var mongo = builder.Configuration.GetSection("Mongo");

builder.Services.AddStoragePersistence(
    connectionString: mongo["ConnectionString"]
        ?? throw new InvalidOperationException("Mongo:ConnectionString is not configured."),
    databaseName: mongo["Database"] ?? "storage");

builder.Services.AddStorageApplication();
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

// Enums travel by name ("Unit", not 0), the same rule as in the database.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();

// The contract the front end generates its typed client from.
builder.Services.AddOpenApi();

// The front end is a separate application with its own origin. Only the origins listed in
// configuration may call the API from a browser.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapCategoryEndpoints();
app.MapProductEndpoints();

// Idempotent: creating an index that already exists is a no-op, so every boot guarantees
// the unique barcode index and the path index are in place.
await app.Services.GetRequiredService<MongoStorageContext>().EnsureIndexesAsync();

await app.RunAsync();
