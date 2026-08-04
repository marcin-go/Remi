# Invoice reporting workflow

This describes the workflow Remi must support for an accounting invoice. It replaces the monthly free-text staging process while retaining its review value.

1. Receive the invoice from the accounting department.
2. Record the accounting facts needed for MI: invoice or credit note number, date, ex-VAT value and any framework-specific fields that differ from the contract.
3. Select the existing contract in Remi. Confirm its supplier reference/designation and the MI fields captured at contract intake before using them for the invoice.
4. Record the invoice against the reporting month. Remi derives the reported-invoice count and value for the contract instead of maintaining a free-text paid-invoice count.
5. Review the generated reporting card. It lists the contract and invoice fields in the exact order of the framework spreadsheet and can be downloaded as text for review.
6. At month end, use the reviewed reporting card to generate the approved framework workbook, then upload that workbook and record the submission reference.

## Product implications

- An invoice is linked to an existing contract through the supplier reference within the same framework; contract MI data should be copied into the invoice staging form, not retyped.
- The reporting card is a generated, filterable view of the structured records for one framework/month. It replaces `MI Reporting Information Card.txt` as the working staging record.
- The number of invoices Remi shows is a **reported invoice count**, not a payment-status assertion. Payment confirmation would require a separate accounting-status integration or field.
