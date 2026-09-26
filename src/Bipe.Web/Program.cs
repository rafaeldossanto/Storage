using System.Globalization;
using Bipe.Infrastructure;
using Bipe.Infrastructure.Persistence;
using Bipe.Web.Components;
using Microsoft.AspNetCore.Localization;

var builder = WebApplication.CreateBuilder(args);

// The shop floor reads Portuguese, the code does not. Pinning the culture once here is
// what makes Money render as "R$ 8,99" and dates as dd/MM/yyyy everywhere, without a
// single formatting decision leaking into the domain.
var storeCulture = new CultureInfo("pt-BR");
CultureInfo.DefaultThreadCurrentCulture = storeCulture;
CultureInfo.DefaultThreadCurrentUICulture = storeCulture;

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Every user-facing string comes from a resource file, never from a literal in a
// component. Today there is one language; the day a customer asks for another, it is a
// new .resx rather than a rewrite.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

builder.Services.AddBipePersistence(ResolveDataDirectory(builder));

var app = builder.Build();

app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(storeCulture),
    SupportedCultures = [storeCulture],
    SupportedUICultures = [storeCulture],
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// The store has to be usable the moment the shop opens: prepare the file before the
// first request instead of failing on the first sale of the day.
await app.Services.GetRequiredService<SqliteStore>().PrepareAsync();

await app.RunAsync();

static string ResolveDataDirectory(WebApplicationBuilder builder)
{
    var configured = builder.Configuration["Bipe:DataDirectory"];

    if (!string.IsNullOrWhiteSpace(configured))
    {
        return Path.GetFullPath(configured, builder.Environment.ContentRootPath);
    }

    // Shared machine data, not per-user: the app runs as a Windows service and the
    // counter, the manager and the service account all read the same database.
    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Bipe");
}
