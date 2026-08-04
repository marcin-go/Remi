namespace Remi.Domain;

public static class ReportingRules
{
    public static IReadOnlyList<ValidationFinding> Validate(RemiDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var findings = new List<ValidationFinding>();
        ValidateContracts(database.Contracts, findings);
        ValidateInvoices(database, findings);
        return findings;
    }

    private static void ValidateContracts(
        IEnumerable<ContractRecord> contracts,
        ICollection<ValidationFinding> findings)
    {
        foreach (var contract in contracts)
        {
            if (string.IsNullOrWhiteSpace(contract.SupplierReference))
            {
                findings.Add(Error("MissingSupplierReference", "A contract must have a supplier reference number.", "Contract", contract.Id));
            }

            if (string.IsNullOrWhiteSpace(contract.CustomerName))
            {
                findings.Add(Error("MissingCustomerName", $"{contract.SupplierReference}: a customer name is required.", "Contract", contract.Id));
            }

            if (contract.StartDate is not null && contract.EndDate is not null && contract.EndDate < contract.StartDate)
            {
                findings.Add(Error(
                    "ContractEndBeforeStart",
                    $"{contract.SupplierReference}: the contract end date is earlier than the start date.",
                    "Contract",
                    contract.Id));
            }

            if (contract.TotalContractValueExVat <= 0)
            {
                findings.Add(Error(
                    "InvalidContractValue",
                    $"{contract.SupplierReference}: the total contract value must be greater than zero.",
                    "Contract",
                    contract.Id));
            }
        }

        foreach (var duplicate in contracts
                     .GroupBy(contract => (contract.Framework, Reference: NormaliseReference(contract.SupplierReference)))
                     .Where(group => group.Count() > 1))
        {
            findings.Add(Error(
                "DuplicateContractReference",
                $"{Frameworks.Get(duplicate.Key.Framework).DisplayName}: {duplicate.Key.Reference} appears in more than one imported contract.",
                "Contract"));
        }
    }

    private static void ValidateInvoices(RemiDatabase database, ICollection<ValidationFinding> findings)
    {
        var contracts = database.Contracts
            .Select(contract => (contract.Framework, Reference: NormaliseReference(contract.SupplierReference)))
            .ToHashSet();

        foreach (var invoice in database.Invoices)
        {
            if (!contracts.Contains((invoice.Framework, NormaliseReference(invoice.SupplierReference))))
            {
                findings.Add(Error(
                    "InvoiceContractNotFound",
                    $"{invoice.SupplierReference}, invoice {invoice.InvoiceNumber}: no matching contract was imported for this framework.",
                    "Invoice",
                    invoice.Id));
            }

            if (invoice.TotalCostExVat == 0)
            {
                findings.Add(new ValidationFinding(
                    FindingSeverity.Warning,
                    "ZeroValueInvoice",
                    $"{invoice.SupplierReference}, invoice {invoice.InvoiceNumber}: the reported invoice value is zero.",
                    "Invoice",
                    invoice.Id));
            }
        }

        foreach (var duplicate in database.Invoices
                     .GroupBy(invoice => (
                         invoice.Framework,
                         Reference: NormaliseReference(invoice.SupplierReference),
                         invoice.InvoiceNumber,
                         invoice.InvoiceDate,
                         invoice.TotalCostExVat))
                     .Where(group => group.Count() > 1))
        {
            findings.Add(Error(
                "DuplicateInvoice",
                $"{Frameworks.Get(duplicate.Key.Framework).DisplayName}: invoice {duplicate.Key.InvoiceNumber} for {duplicate.Key.Reference} has been imported more than once.",
                "Invoice"));
        }
    }

    public static string NormaliseReference(string? reference) =>
        (reference ?? string.Empty).Trim().ToUpperInvariant();

    private static ValidationFinding Error(string code, string message, string entityType, Guid? entityId = null) =>
        new(FindingSeverity.Error, code, message, entityType, entityId);
}
