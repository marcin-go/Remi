using System.Diagnostics;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.DataProtection;
using Remi.Application;
using Remi.Infrastructure;
using Remi.Web;
using Remi.Web.Components;
using Serilog;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Remi is portable: resolve static assets from beside the executable rather than from
    // whichever directory happened to launch the process.
    ContentRootPath = AppContext.BaseDirectory
});
const long MaxDataTransferRequestSize = 4L * 1024 * 1024 * 1024;
var dataPath = builder.Configuration["Remi:DataPath"] ?? RemiDataPaths.DefaultDatabaseFile;
var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(dataPath))
    ?? throw new InvalidOperationException("The Remi data path has no parent directory.");
var openBrowser = bool.TryParse(builder.Configuration["open-browser"], out var shouldOpenBrowser) && shouldOpenBrowser;
var browser = builder.Configuration["browser"];
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
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = MaxDataTransferRequestSize);
builder.Services.AddSingleton<SqliteRemiStore>(_ => new SqliteRemiStore(dataPath));
builder.Services.AddSingleton<IRemiStore>(services => services.GetRequiredService<SqliteRemiStore>());
builder.Services.AddSingleton<IRemiDataTransfer>(services => new RemiDataTransferService(
    dataDirectory,
    dataPath,
    services.GetRequiredService<SqliteRemiStore>()));
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
builder.Services.AddSingleton<RestoreRequestTokenService>();
builder.Services.AddScoped<ReportingPeriodContext>();
builder.Services.AddScoped<ReportingWorkspace>();

var app = builder.Build();

app.UseSerilogRequestLogging();

app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) &&
        string.Equals(context.Request.Path.Value, "/data-transfer/restore", StringComparison.OrdinalIgnoreCase))
    {
        var sizeLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeLimit is { IsReadOnly: false })
        {
            sizeLimit.MaxRequestBodySize = MaxDataTransferRequestSize;
        }
    }

    await next();
});

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

app.MapPost("/evidence/clipboard/{entityType}/{entityId:guid}", async (
    string entityType,
    Guid entityId,
    string? title,
    IFormFile file,
    IRemiStore store,
    ReportingWorkspace workspace,
    CancellationToken cancellationToken) =>
{
    if (file.Length is <= 0 or > 15 * 1024 * 1024)
    {
        return Results.BadRequest("Add a file smaller than 15 MB.");
    }

    var target = await store.ReadAsync(database => entityType.ToLowerInvariant() switch
    {
        "contract" => database.Contracts.Where(item => item.Id == entityId).Select(item => new ClipboardEvidenceTarget(item.Framework, item.ReportMonth, item.SupplierReference)).SingleOrDefault(),
        "invoice" => database.Invoices.Where(item => item.Id == entityId).Select(item => new ClipboardEvidenceTarget(item.Framework, item.ReportMonth, item.SupplierReference)).SingleOrDefault(),
        "contract-change" => (from change in database.ContractChanges
                              join contract in database.Contracts on change.ContractId equals contract.Id
                              where change.Id == entityId
                              select new ClipboardEvidenceTarget(contract.Framework, change.AgreementDate.ToString("yyyy-MM"), contract.SupplierReference)).SingleOrDefault(),
        "monthly-return" => database.MonthlyReturns.Where(item => item.Id == entityId).Select(item => new ClipboardEvidenceTarget(item.Framework, item.ReportMonth, null)).SingleOrDefault(),
        _ => null,
    }, cancellationToken);
    if (target is null)
    {
        return Results.NotFound();
    }

    var extension = Path.GetExtension(file.FileName);
    var fileName = string.IsNullOrWhiteSpace(title)
        ? Path.GetFileName(file.FileName)
        : $"{Path.GetFileNameWithoutExtension(title.Trim())}{extension}";
    await using var content = file.OpenReadStream();
    var archived = await workspace.ArchiveEvidenceAsync(
        string.Equals(entityType, "monthly-return", StringComparison.OrdinalIgnoreCase)
            ? Remi.Domain.EvidenceKind.SubmissionEvidence
            : Remi.Domain.EvidenceKind.SupportingDocument,
        target.Framework,
        target.ReportMonth,
        fileName,
        $"clipboard/{entityType.ToLowerInvariant()}/{entityId:D}/{fileName}",
        file.ContentType,
        target.SupplierReference,
        content,
        cancellationToken);
    return Results.Ok(new { archived });
});

app.MapPost("/data-transfer/backup/prepare", async (
    HttpRequest request,
    IAntiforgery antiforgery,
    IRemiDataTransfer dataTransfer,
    CancellationToken cancellationToken) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(request.HttpContext);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest("The backup form has expired. Reload Settings and create the backup again.");
    }

    var prepared = await dataTransfer.PrepareExportAsync(cancellationToken);
    return Results.Redirect($"/settings?section=data-transfer&backup={prepared.Id:D}");
});

app.MapGet("/data-transfer/backup/{id:guid}", async (
    Guid id,
    HttpResponse response,
    IRemiDataTransfer dataTransfer,
    CancellationToken cancellationToken) =>
{
    var prepared = dataTransfer.GetPreparedExport(id);
    if (prepared is null)
    {
        return Results.NotFound("This prepared backup is no longer available. Create a new backup from Settings.");
    }

    var stream = await dataTransfer.OpenPreparedExportAsync(id, cancellationToken);
    if (stream is null)
    {
        return Results.NotFound("This prepared backup is no longer available. Create a new backup from Settings.");
    }

    return Results.File(stream, "application/zip", prepared.FileName, enableRangeProcessing: true);
});

string RestoreResultLocation(HttpRequest request, string result)
{
    var period = request.Query["period"].ToString();
    var periodQuery = ReportingPeriodContext.IsValidPeriod(period)
        ? $"&period={Uri.EscapeDataString(period)}"
        : string.Empty;
    return $"/settings?section=data-transfer&restore={Uri.EscapeDataString(result)}{periodQuery}";
}

app.MapGet("/data-transfer/restore/token", (RestoreRequestTokenService restoreRequestTokens) =>
    Results.Json(new { requestToken = restoreRequestTokens.Issue() }));

app.MapPost("/data-transfer/restore", async (
    HttpRequest request,
    IRemiDataTransfer dataTransfer,
    RestoreRequestTokenService restoreRequestTokens,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!restoreRequestTokens.TryConsume(request.Headers["X-Remi-Restore-Token"].ToString()))
    {
        logger.LogWarning("Rejected a Remi restore request because its one-time restore token was missing or invalid.");
        return Results.Redirect(RestoreResultLocation(request, "expired"));
    }

    if (!request.HasFormContentType)
    {
        logger.LogWarning("Rejected a Remi restore request because it was not submitted as a form.");
        return Results.Redirect(RestoreResultLocation(request, "missing-file"));
    }

    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync(cancellationToken);
    }
    catch (BadHttpRequestException exception)
    {
        logger.LogWarning(exception, "Rejected a Remi restore request because its upload could not be read.");
        var result = exception.StatusCode == StatusCodes.Status413PayloadTooLarge
            ? "package-too-large"
            : "invalid-upload";
        return Results.Redirect(RestoreResultLocation(request, result));
    }

    var confirmsReplacement = string.Equals(form["confirmDestructiveRestore"], "on", StringComparison.OrdinalIgnoreCase);
    var confirmsPackage = string.Equals(form["confirmBackupPackage"], "on", StringComparison.OrdinalIgnoreCase);
    if (!confirmsReplacement || !confirmsPackage || !string.Equals(form["replacementPhrase"], "REPLACE", StringComparison.Ordinal))
    {
        logger.LogWarning("Rejected a Remi restore request because its destructive-action confirmation was incomplete.");
        return Results.Redirect(RestoreResultLocation(request, "not-confirmed"));
    }

    var package = form.Files.GetFile("package");
    if (package is null || package.Length <= 0 || !string.Equals(Path.GetExtension(package.FileName), ".zip", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogWarning("Rejected a Remi restore request because it did not include a non-empty ZIP package.");
        return Results.Redirect(RestoreResultLocation(request, "missing-file"));
    }

    try
    {
        await using var packageStream = package.OpenReadStream();
        await dataTransfer.ImportAsync(packageStream, cancellationToken);
        return Results.Redirect(RestoreResultLocation(request, "complete"));
    }
    catch (InvalidDataException exception)
    {
        logger.LogWarning(exception, "Rejected an invalid Remi restore package.");
        return Results.Redirect(RestoreResultLocation(request, "invalid-package"));
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Remi restore did not complete.");
        return Results.Redirect(RestoreResultLocation(request, "failed"));
    }
});

app.MapGet("/reports/card/{frameworkCode:int}/{reportingMonth}", async (
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

app.MapGet("/", () => Results.Redirect("/home"));

app.MapStaticAssets();

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
        if (string.Equals(browser, "edge", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo("msedge.exe", address) { UseShellExecute = true });
                return;
            }
            catch (Exception exception)
            {
                app.Logger.LogWarning(exception, "Microsoft Edge could not be started; opening Remi in the default browser instead.");
            }
        }

        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    }
});

app.Run();

sealed record ClipboardEvidenceTarget(Remi.Domain.FrameworkCode Framework, string ReportMonth, string? SupplierReference);
