using Microsoft.Data.Sqlite;
using Remi.Domain;

namespace Remi.Infrastructure;

public sealed partial class SqliteRemiStore
{
    private static async Task<Dictionary<Guid, NonFrameworkContractTerms>> LoadNonFrameworkTermsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT contract_id, agreement_basis, agreement_date, agreement_notes, term_kind, initial_annual_charge_ex_vat, uses_cpi, additional_percentage, cpi_reference, notice_received_date, termination_date FROM non_framework_contract_terms;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var terms = new Dictionary<Guid, NonFrameworkContractTerms>();
        while (await reader.ReadAsync(cancellationToken))
            terms.Add(Guid.Parse(reader.GetString(0)), new((AgreementBasis)reader.GetInt32(1), DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd"),
                reader.GetString(3), (ContractTermKind)reader.GetInt32(4), Number(reader.GetString(5)), reader.GetInt32(6) != 0,
                Number(reader.GetString(7)), reader.GetString(8), NullableDate(reader, 9), NullableDate(reader, 10)));
        return terms;
    }

    private static async Task<List<NonFrameworkAnnualReview>> LoadNonFrameworkReviewsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT id, contract_id, contract_year, effective_date, previous_annual_charge_ex_vat, cpi_percentage, additional_percentage, annual_charge_ex_vat, cpi_reference, created_at_utc FROM non_framework_annual_reviews ORDER BY contract_id, contract_year;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var reviews = new List<NonFrameworkAnnualReview>();
        while (await reader.ReadAsync(cancellationToken))
            reviews.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt32(2),
                DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd"), Number(reader.GetString(4)), Number(reader.GetString(5)),
                Number(reader.GetString(6)), Number(reader.GetString(7)), reader.GetString(8), Timestamp(reader.GetString(9))));
        return reviews;
    }

    private static async Task SaveNonFrameworkAsync(SqliteConnection connection, SqliteTransaction transaction, RemiDatabase database, CancellationToken cancellationToken)
    {
        foreach (var contract in database.Contracts.Where(item => item.NonFrameworkTerms is not null))
        {
            var terms = contract.NonFrameworkTerms!;
            await using var command = CreateCommand(connection, transaction, "INSERT INTO non_framework_contract_terms (contract_id, agreement_basis, agreement_date, agreement_notes, term_kind, initial_annual_charge_ex_vat, uses_cpi, additional_percentage, cpi_reference, notice_received_date, termination_date) VALUES ($contract, $basis, $date, $notes, $term, $charge, $cpi, $additional, $reference, $notice, $termination);");
            AddParameter(command, "$contract", contract.Id.ToString("D"));
            AddParameter(command, "$basis", (int)terms.AgreementBasis);
            AddParameter(command, "$date", Date(terms.AgreementDate));
            AddParameter(command, "$notes", terms.AgreementNotes);
            AddParameter(command, "$term", (int)terms.TermKind);
            AddParameter(command, "$charge", Number(terms.InitialAnnualChargeExVat));
            AddParameter(command, "$cpi", terms.UsesCpi ? 1 : 0);
            AddParameter(command, "$additional", Number(terms.AdditionalPercentage));
            AddParameter(command, "$reference", terms.CpiReference);
            AddParameter(command, "$notice", Date(terms.NoticeReceivedDate));
            AddParameter(command, "$termination", Date(terms.TerminationDate));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var review in database.NonFrameworkAnnualReviews)
        {
            await using var command = CreateCommand(connection, transaction, "INSERT INTO non_framework_annual_reviews (id, contract_id, contract_year, effective_date, previous_annual_charge_ex_vat, cpi_percentage, additional_percentage, annual_charge_ex_vat, cpi_reference, created_at_utc) VALUES ($id, $contract, $year, $effective, $previous, $cpi, $additional, $charge, $reference, $created);");
            AddParameter(command, "$id", review.Id.ToString("D"));
            AddParameter(command, "$contract", review.ContractId.ToString("D"));
            AddParameter(command, "$year", review.ContractYear);
            AddParameter(command, "$effective", Date(review.EffectiveDate));
            AddParameter(command, "$previous", Number(review.PreviousAnnualChargeExVat));
            AddParameter(command, "$cpi", Number(review.CpiPercentage));
            AddParameter(command, "$additional", Number(review.AdditionalPercentage));
            AddParameter(command, "$charge", Number(review.AnnualChargeExVat));
            AddParameter(command, "$reference", review.CpiReference);
            AddParameter(command, "$created", Timestamp(review.CreatedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
