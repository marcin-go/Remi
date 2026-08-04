# Contract reporting workflow

This describes the real-world workflow Remi must support when a new reportable framework contract is received. It is the basis for the next contract-intake delivery.

1. Receive the signed contract.
2. Open the contract and identify the information required by the relevant framework's MI report.
3. Preserve the extracted evidence in separate, easy-to-reference files. These are normally contract-details and pricing images or PDFs, so the information can be checked later without reopening and searching the signed contract.
4. Create the contract record that replaces the Ledger entry. Record the customer, contract designation or supplier reference, contract duration, and the annual payment amounts. Where a year has more than one chargeable position, retain separate line items; year one commonly has separate data-migration, training and annual software-licence charges.
5. Prepare the reporting information in the exact field order and structure required by the framework spreadsheet. The existing `MI Reporting Information Card.txt` is the current manual staging format and should become a generated, reviewable Remi reporting card.

## Product implications

- Contract intake begins with the original signed contract and its extracted supporting evidence, not only an MI workbook import.
- The evidence archive must keep the signed contract, contract-details files and pricing files together with the contract.
- A structured annual charge schedule is needed. Values should be stored ex VAT and support multiple line items per contract year.
- Fully or partially invoiced is a calculated view from the charge schedule and reported invoices, not free-text ledger status.
- Remi should create a reviewable MI reporting card before the official framework workbook is completed and submitted.
