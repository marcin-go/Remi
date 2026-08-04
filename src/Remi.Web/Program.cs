using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Remi.Application;
using Remi.Infrastructure;
using Remi.Web;
using Remi.Web.Components;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var dataPath = builder.Configuration["Remi:DataPath"] ?? RemiDataPaths.DefaultDatabaseFile;
var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(dataPath))
    ?? throw new InvalidOperationException("The Remi data path has no parent directory.");
var openBrowser = bool.TryParse(builder.Configuration["open-browser"], out var shouldOpenBrowser) && shouldOpenBrowser;
Directory.CreateDirectory(dataDirectory);

// A portable local app normally runs without permission to create or write Windows Event Log sources.
builder.Logging.ClearProviders();
builder.Host.UseSerilog((_, _, loggerConfiguration) => loggerConfiguration
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(dataDirectory, "logs", "remi-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "protection-keys")))
    .SetApplicationName("Remi");
builder.Services.AddSingleton<IRemiStore>(_ => new SqliteRemiStore(dataPath));
builder.Services.AddSingleton<IEvidenceArchive>(_ => new FileEvidenceArchive(RemiDataPaths.EvidenceDirectoryFor(dataPath)));
builder.Services.AddHttpClient("GcaCustomerUrnSource", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Remi/1.0");
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton<ICustomerUrnDirectory>(services => new GcaCustomerUrnDirectory(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("GcaCustomerUrnSource"),
    services.GetRequiredService<IEvidenceArchive>(),
    RemiDataPaths.CustomerUrnDirectoryIndexFileFor(dataPath)));
builder.Services.AddSingleton<IWorkbookImporter, XlsxMiWorkbookImporter>();
builder.Services.AddSingleton<IMiWorkbookExporter, XlsxMiWorkbookExporter>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MigrationRunner>();
builder.Services.AddSingleton<Remi.Web.BrowserFolderStaging>();
builder.Services.AddScoped<ReportingPeriodContext>();
builder.Services.AddScoped<ReportingWorkspace>();

var app = builder.Build();

app.UseSerilogRequestLogging();

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

    if (string.Equals(evidence.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Redirect($"/evidence/{id}/view");
    }

    var stream = await archive.OpenReadAsync(evidence, cancellationToken);
    return stream is null
        ? Results.NotFound()
        : Results.File(stream, evidence.ContentType, fileDownloadName: evidence.FileName, enableRangeProcessing: true);
});

app.MapGet("/evidence/{id:guid}/content", async (
    Guid id,
    IRemiStore store,
    IEvidenceArchive archive,
    CancellationToken cancellationToken) =>
{
    var evidence = await store.ReadAsync(
        database => database.Evidence.SingleOrDefault(item => item.Id == id),
        cancellationToken);
    if (evidence is null || !string.Equals(evidence.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound();
    }

    var stream = await archive.OpenReadAsync(evidence, cancellationToken);
    return stream is null
        ? Results.NotFound()
        : Results.File(stream, evidence.ContentType, enableRangeProcessing: true);
});

app.MapGet("/evidence/{id:guid}/download", async (
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
        : Results.File(stream, evidence.ContentType, fileDownloadName: evidence.FileName, enableRangeProcessing: true);
});

app.MapGet("/reporting-card/{frameworkCode:int}/{reportingMonth}", async (
    int frameworkCode,
    string reportingMonth,
    ReportingWorkspace workspace,
    CancellationToken cancellationToken) =>
{
    if (!Enum.IsDefined(typeof(Remi.Domain.FrameworkCode), frameworkCode) ||
        !DateOnly.TryParseExact($"{reportingMonth}-01", "yyyy-MM-dd", out _))
    {
        return Results.NotFound();
    }

    var text = await workspace.GetReportingCardTextAsync((Remi.Domain.FrameworkCode)frameworkCode, reportingMonth, cancellationToken);
    return Results.Text(text, "text/plain; charset=utf-8");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("Remi SQLite database: {DataPath}", Path.GetFullPath(dataPath));
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
