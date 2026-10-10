using System.Globalization;
using System.Text;
using Remi.Domain;

namespace Remi.Application;

public sealed record InvoicePaymentReport(DateOnly AsAtDate, IReadOnlyList<InvoiceRecord> Invoices)
{
    public IReadOnlyList<InvoiceRecord> Filter(string? status, string? search = null, FrameworkCode? framework = null) => Invoices
        .Where(invoice => (framework is null || invoice.Framework == framework)
            && (string.IsNullOrWhiteSpace(search) || invoice.InvoiceNumber.Contains(search, StringComparison.OrdinalIgnoreCase)
                || invoice.CustomerName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || invoice.SupplierReference.Contains(search, StringComparison.OrdinalIgnoreCase))
            && (status switch
            {
                "unpaid" => invoice.PaymentStatus == InvoicePaymentStatus.Unpaid,
                "overdue" => invoice.IsOverdue(AsAtDate),
                "unknown" => invoice.PaymentStatus == InvoicePaymentStatus.Unknown,
                "paid" => invoice.PaymentStatus == InvoicePaymentStatus.Paid,
                "assumed" => invoice.IsPaymentDateAssumed,
                _ => true,
            }))
        .OrderBy(invoice => invoice.DueDate is null).ThenBy(invoice => invoice.DueDate)
        .ThenBy(invoice => invoice.CustomerName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(invoice => invoice.InvoiceNumber, StringComparer.OrdinalIgnoreCase).ToList();

    public byte[] ExportCsv(string? status, string? search = null, FrameworkCode? framework = null)
    {
        var text = new StringBuilder("As at,Customer,Contract,Framework,Invoice,Invoice date,Due date,Payment status,Date paid,Assumed date,Days overdue,Value ex VAT GBP\r\n");
        foreach (var invoice in Filter(status, search, framework))
        {
            var values = new[] { AsAtDate.ToString("yyyy-MM-dd"), invoice.CustomerName, invoice.SupplierReference,
                Frameworks.Get(invoice.Framework).DisplayName, invoice.InvoiceNumber, invoice.InvoiceDate?.ToString("yyyy-MM-dd") ?? "",
                invoice.DueDate?.ToString("yyyy-MM-dd") ?? "", invoice.PaymentStatus.ToString(), invoice.DatePaid?.ToString("yyyy-MM-dd") ?? "",
                invoice.IsPaymentDateAssumed ? "Yes" : "No", invoice.DaysOverdue(AsAtDate).ToString(CultureInfo.InvariantCulture),
                invoice.TotalCostExVat.ToString("0.00", CultureInfo.InvariantCulture) };
            text.AppendLine(string.Join(",", values.Select(CsvCell)));
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text.ToString())).ToArray();
    }

    private static string CsvCell(string value)
    {
        // Keep user-entered references and names as text when opened in a spreadsheet.
        if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+')
            || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')
            || value.StartsWith('\t') || value.StartsWith('\r') || value.StartsWith('\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

public sealed partial class ReportingWorkspace
{
    public Task<InvoicePaymentReport> GetInvoicePaymentReportAsync(CancellationToken cancellationToken = default) =>
        store.ReadAsync(database => new InvoicePaymentReport(Today(), database.Invoices.Where(item => item.TotalCostExVat > 0).ToList()), cancellationToken);

    public Task<ReturnActionResult> UpdateInvoicePaymentAsync(Guid invoiceId, InvoicePaymentStatus status, DateOnly? datePaid,
        string? actor = null, CancellationToken cancellationToken = default) => store.UpdateAsync(database =>
    {
        var invoice = database.Invoices.SingleOrDefault(item => item.Id == invoiceId);
        if (invoice is null) return new ReturnActionResult(false, "The selected invoice no longer exists.", []);
        if (!Enum.IsDefined(status)) return new ReturnActionResult(false, "Choose a valid payment status.", []);
        if (status == InvoicePaymentStatus.Paid && datePaid is null)
            return new ReturnActionResult(false, "Enter the date paid before marking the invoice paid.", []);
        if (status == InvoicePaymentStatus.Paid && (datePaid > Today() || datePaid < invoice.InvoiceDate))
            return new ReturnActionResult(false, "The date paid must be on or after the invoice date and cannot be in the future.", []);
        var updated = invoice with { PaymentStatus = status, DatePaid = status == InvoicePaymentStatus.Paid ? datePaid : null, IsPaymentDateAssumed = false };
        if (updated == invoice) return new ReturnActionResult(true, "Payment details are already up to date.", []);
        database.Invoices[database.Invoices.IndexOf(invoice)] = updated;
        RecordAudit(database, timeProvider.GetUtcNow(), "InvoicePaymentUpdated", "Invoice", invoiceId,
            $"Invoice {invoice.InvoiceNumber}: payment status {invoice.PaymentStatus} → {status}; date paid {invoice.DatePaid:yyyy-MM-dd} → {updated.DatePaid:yyyy-MM-dd}.",
            invoice.IsPaymentDateAssumed ? "Reviewed and replaced the initial assumed payment date." : null, actor);
        return new ReturnActionResult(true, "Payment details saved.", []);
    }, cancellationToken);
}
