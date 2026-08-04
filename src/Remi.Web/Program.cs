using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Remi.Application;
using Remi.Infrastructure;
using Remi.Web.Components;

var builder = WebApplication.CreateBuilder(args);
var dataPath = builder.Configuration["Remi:DataPath"] ?? RemiDataPaths.DefaultDataFile;
var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(dataPath))
    ?? throw new InvalidOperationException("The Remi data path has no parent directory.");
var openBrowser = bool.TryParse(builder.Configuration["open-browser"], out var shouldOpenBrowser) && shouldOpenBrowser;
Directory.CreateDirectory(dataDirectory);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "protection-keys")))
    .SetApplicationName("Remi");
builder.Services.AddSingleton<IRemiStore>(_ => new JsonFileRemiStore(dataPath));
builder.Services.AddSingleton<IEvidenceArchive>(_ => new FileEvidenceArchive(RemiDataPaths.EvidenceDirectoryFor(dataPath)));
builder.Services.AddSingleton<IWorkbookImporter, XlsxMiWorkbookImporter>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ReportingWorkspace>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.UseStaticFiles();

app.MapGet("/evidence/{id:guid}", async (
    Guid id,
    IRemiStore store,
    IEvidenceArchive archive,
    CancellationToken cancellationToken) =>
{
    var evidence = await store.ReadAsync(
        database => database.Evidence.SingleOrDefault(item => item.Id == id),
        cancellationToken);
    if (evidence is null)
    {
        return Results.NotFound();
    }

    var stream = await archive.OpenReadAsync(evidence, cancellationToken);
    return stream is null
        ? Results.NotFound()
        : Results.File(stream, evidence.ContentType, enableRangeProcessing: true);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("Remi data file: {DataPath}", Path.GetFullPath(dataPath));
    if (!openBrowser)
    {
        return;
    }

    var address = app.Urls.FirstOrDefault(url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
    if (address is not null)
    {
        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    }
});

app.Run();
