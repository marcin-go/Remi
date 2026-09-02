using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Remi.Application;
using Remi.Domain;
using Remi.Infrastructure;
using Remi.Web;
using Remi.Web.Components;
using Remi.Web.Components.Pages;
using Xunit;
using DocumentState = Remi.Web.Components.ClipboardImageEvidence.DocumentState;
using PendingDocument = Remi.Web.Components.ClipboardImageEvidence.PendingDocument;

namespace Remi.Tests;

public sealed partial class RegisterComponentTests
{
    [Theory]
    [InlineData("Agreement v1.2", 200, "Agreement v1.2.png")]
    [InlineData("Agreement.png", 200, "Agreement.png")]
    [InlineData("../unsafe", 400, null)]
    [InlineData("unsafe:document", 400, null)]
    public async Task Attachment_endpoint_validates_titles_and_preserves_dots(string title, int status, string? expectedName)
    {
        var root = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var context = CreateContext(attachmentTestDirectory: root);
            var store = context.Services.GetRequiredService<IRemiStore>();
            using var content = new MemoryStream(AuditDocumentBytes(1));
            var file = new FormFile(content, 0, content.Length, "file", "image.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
            var result = await EvidenceUploadEndpoint.HandleAsync("contract", SampleContractId, title, file, store, context.Services.GetRequiredService<ReportingWorkspace>(), CancellationToken.None);
            Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            var evidence = await store.ReadAsync(db => db.Evidence.ToList());
            if (expectedName is null) Assert.Empty(evidence);
            else Assert.Equal(expectedName, Assert.Single(evidence).FileName);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    public static IEnumerable<object[]> AttachmentWorkflows =>
        from workflow in new[] { "contract-register", "contract-edit", "change-register", "change-edit", "invoice-register", "invoice-edit", "submission-register", "submission-edit" }
        from failFirst in new[] { false, true }
        select new object[] { workflow, failFirst };

    [Theory]
    [MemberData(nameof(AttachmentWorkflows))]
    public async Task Every_attachment_form_persists_twenty_documents_and_retries_only_unfinished_files(string workflow, bool failFirst)
    {
        var root = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var context = CreateContext(includeSubmittedReturn: workflow == "submission-edit", includeContractExtension: workflow == "change-edit", includeInvoiceContract: workflow == "invoice-edit", attachmentTestDirectory: root);
            context.JSInterop.SetupVoid("window.scrollTo", _ => true).SetVoidResult();
            var module = context.JSInterop.SetupModule("/clipboard-image-evidence.js");
            module.SetupVoid("attach", _ => true).SetVoidResult();
            module.SetupVoid("dispose", _ => true).SetVoidResult();
            var prepared = new DocumentState { Revision = 1, Documents = Enumerable.Range(1, 20).Select(index => new PendingDocument
            {
                Id = Guid.NewGuid().ToString(), FileName = $"document-{index:00}.png", Title = $"document-{index:00}",
                ContentType = "image/png", FileSizeBytes = 80 * 1024, PreviewUrl = $"blob:test-preview-{index}"
            }).ToList() };
            module.Setup<DocumentState>("prepare", _ => true).SetResult(prepared);
            module.Setup<DocumentState>("getState", _ => true).SetResult(prepared);
            var phase = 0;
            var firstUpload = module.Setup<DocumentState>("archive", _ => phase == 0);
            var retryUpload = module.Setup<DocumentState>("archive", _ => phase == 1);
            var (cut, saveSelector) = OpenAttachmentWorkflow(context, workflow);
            if (workflow == "submission-edit" && failFirst)
                cut.Find("button[aria-label='Remove gca-confirmation.png']").Click();

            // No notification has reached the parent yet. Preparation must discover the browser queue anyway.
            Assert.Empty(cut.FindAll(".clipboard-document-item"));
            Assert.True(cut.Find("input[type='file']").HasAttribute("multiple"));
            Assert.False(cut.Find(saveSelector).HasAttribute("disabled"), cut.Markup);
            var click = cut.Find(saveSelector).ClickAsync(new MouseEventArgs());
            cut.WaitForAssertion(() => Assert.True(module.Invocations["archive"].Count == 1, string.Join(" | ", cut.FindAll(".notice").Select(item => item.TextContent))), TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(() => Assert.Equal(20, cut.FindAll(".clipboard-document-item").Count));
            var invocation = module.Invocations["archive"][0];
            var entityType = Assert.IsType<string>(invocation.Arguments[1]);
            var entityId = Assert.IsType<Guid>(invocation.Arguments[2]);
            var expectedType = workflow.StartsWith("change") ? "contract-change" : workflow.StartsWith("submission") ? "monthly-return" : workflow.Split('-')[0];
            Assert.Equal(expectedType, entityType);
            var store = context.Services.GetRequiredService<IRemiStore>();
            var workspace = context.Services.GetRequiredService<ReportingWorkspace>();
            var originalEntities = await store.ReadAsync(db => (db.Contracts.Count, db.Invoices.Count, db.ContractChanges.Count, db.MonthlyReturns.Count));

            var completed = failFirst ? 7 : 20;
            for (var index = 1; index <= completed; index++) await UploadAuditDocument(index, entityType, entityId, store, workspace);
            var remaining = prepared.Documents.Skip(completed).ToList();
            prepared.Revision = 2;
            prepared.Documents = remaining;
            firstUpload.SetResult(new DocumentState { Revision = 2, Documents = remaining, ArchivedCount = completed, Error = failFirst ? "Simulated upload failure; unfinished documents have been kept." : null });
            await click;

            if (failFirst)
            {
                cut.WaitForAssertion(() => Assert.Equal(13, cut.FindAll(".clipboard-document-item").Count));
                Assert.Contains("Simulated upload failure", cut.Markup);
                if (workflow == "submission-edit")
                    Assert.Empty(cut.FindAll("input[aria-label='Document title for gca-confirmation.png']"));
                phase = 1;
                var retryButton = workflow is "contract-register" or "invoice-register"
                    ? cut.FindAll("button").Single(button => button.TextContent.Trim() == "Retry documents")
                    : cut.Find(saveSelector);
                var retryClick = retryButton.ClickAsync(new MouseEventArgs());
                cut.WaitForAssertion(() => Assert.Equal(2, module.Invocations["archive"].Count), TimeSpan.FromSeconds(5));
                Assert.Equal(entityId, module.Invocations["archive"][1].Arguments[2]);
                for (var index = 8; index <= 20; index++) await UploadAuditDocument(index, entityType, entityId, store, workspace);
                prepared.Revision = 3;
                prepared.Documents = [];
                retryUpload.SetResult(new DocumentState { Revision = 3, ArchivedCount = 13 });
                await retryClick;
                Assert.Equal(originalEntities, await store.ReadAsync(db => (db.Contracts.Count, db.Invoices.Count, db.ContractChanges.Count, db.MonthlyReturns.Count)));
            }

            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".clipboard-document-item")));
            // Reopen the SQLite register and every archived file, not just the component's in-memory state.
            var reopened = new SqliteRemiStore(Path.Combine(root, "test.db"));
            var retained = await reopened.ReadAsync(db => db.Evidence.Where(item => item.OriginalRelativePath.StartsWith($"clipboard/{entityType}/{entityId:D}/document-", StringComparison.Ordinal)).OrderBy(item => item.FileName).ToList());
            Assert.Equal(20, retained.Count);
            var archive = new FileEvidenceArchive(Path.Combine(root, "evidence"));
            for (var index = 1; index <= 20; index++)
            {
                await using var stream = await archive.OpenReadAsync(retained[index - 1]);
                Assert.NotNull(stream);
                using var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes);
                Assert.Equal(AuditDocumentBytes(index), bytes.ToArray());
            }
            if (entityType is "contract" or "contract-change") Assert.True((await workspace.GetContractDetailsAsync(entityType == "contract" ? entityId : SampleContractId))!.Evidence.Count >= 20);
            if (entityType == "invoice") Assert.Equal(20, (await workspace.GetInvoiceDetailsAsync(entityId))!.Evidence.Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static byte[] AuditDocumentBytes(int index) => Enumerable.Repeat((byte)index, 80 * 1024).ToArray();

    private static async Task UploadAuditDocument(int index, string entityType, Guid entityId, IRemiStore store, ReportingWorkspace workspace)
    {
        var bytes = AuditDocumentBytes(index);
        using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", $"document-{index:00}.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
        var result = await EvidenceUploadEndpoint.HandleAsync(entityType, entityId, $"document-{index:00}", file, store, workspace, CancellationToken.None);
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        // Simulate a lost HTTP success response: retrying the same content must not duplicate evidence.
        stream.Position = 0;
        result = await EvidenceUploadEndpoint.HandleAsync(entityType, entityId, $"document-{index:00}", file, store, workspace, CancellationToken.None);
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    private static (IRenderedComponent<IComponent> Cut, string SaveSelector) OpenAttachmentWorkflow(BunitContext context, string workflow)
    {
        if (workflow == "contract-register")
        {
            var cut = context.Render<ContractRegistration>();
            cut.Find("button[role='combobox'][aria-label='Framework']").Click();
            cut.FindAll("#framework-picklist-options [role='option']").Single(option => option.TextContent.Contains("G-Cloud 14")).Click();
            cut.Find("input[autocapitalize='characters']").Input("RM-ATTACHMENTS");
            cut.Find("input[role='combobox'][aria-label='Customer organisation name']").Input("Attachment audit");
            cut.Find("input[role='combobox'][aria-label='Customer Unique Reference Number (URN)']").Input("10000001");
            cut.Find("button[role='combobox'][aria-label='Lot number']").Click();
            cut.FindAll("#lot-number-picklist-options [role='option']").Single(option => option.TextContent.Trim() == "2").Click();
            cut.Find("input[role='combobox'][aria-label='Service Group']").Focus();
            cut.FindAll("#service-group-picklist-options [role='option']").Single(option => option.TextContent.Contains("Information and Communication Technology")).Click();
            cut.Find("input[role='combobox'][aria-label='Digital Marketplace Service ID']").Input("115981361947474");
            cut.FindAll("input[type='date']")[0].Change("2026-07-01");
            cut.FindAll("input[type='date']")[1].Change("2027-06-30");
            cut.Find("input[aria-label='Value excluding VAT, payment position 1']").Input("1200");
            return (cut, "button.invoice-command-save");
        }
        if (workflow is "contract-edit" or "change-register" or "change-edit")
        {
            var cut = context.Render<ContractRecordView>(p => p.Add(component => component.ContractId, SampleContractId));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".contract-tabs button")));
            if (workflow == "contract-edit") { cut.Find(".contract-hero-actions button.secondary").Click(); return (cut, ".contract-hero-actions button.primary"); }
            cut.FindAll(".contract-tabs button").Single(button => button.TextContent.Trim().StartsWith("Changes")).Click();
            if (workflow == "change-edit") cut.Find("tbody button").Click();
            else
            {
                cut.Find(".contract-card-head button").Click();
                cut.Find(".contract-invoice-form input[type='date']").Change("2026-07-15");
                cut.Find(".contract-invoice-form input[type='number']").Change("100");
            }
            return (cut, ".contract-invoice-actions .invoice-command-save");
        }
        if (workflow == "invoice-edit")
        {
            var cut = context.Render<InvoiceRecordView>(p => p.Add(component => component.InvoiceId, SampleInvoiceId));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".contract-hero-actions button.secondary")));
            cut.Find(".contract-hero-actions button.secondary").Click();
            return (cut, ".invoice-overview-actions button.primary");
        }
        if (workflow == "invoice-register")
        {
            var cut = context.Render<InvoiceRegistration>();
            cut.Find("input[role='combobox'][aria-label='Contract']").Input("RM-001");
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button[role='option']")));
            cut.Find("button[role='option']").Click();
            cut.FindAll("label").Single(label => label.TextContent.Trim() == "Invoice or credit note number").QuerySelector("input")!.Input("INV-ATTACHMENTS");
            cut.Find("input[type='date']").Change("2026-07-15");
            cut.Find("input[type='number']").Change("100");
            return (cut, ".invoice-intake-actions .invoice-command-save");
        }
        var report = context.Render<Reporting>(p => p.Add(component => component.FrameworkValue, (int)FrameworkCode.GCloud13).Add(component => component.WorkspaceMonth, "2026-07"));
        report.WaitForAssertion(() => Assert.NotEmpty(report.FindAll("button[data-stage='4']")));
        report.Find("button[data-stage='4']").Click();
        if (workflow == "submission-edit") report.Find(".return-submission-record-actions button").Click();
        report.Find("input[aria-label='Submission date and time']").Input("6 August 2026 14:45 UTC");
        return (report, workflow == "submission-edit" ? ".submission-evidence-save .remi-action--primary" : ".return-submission-capture-actions button");
    }
}
