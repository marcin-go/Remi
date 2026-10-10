namespace Remi.Domain;

public enum AgreementBasis { SignedDocument, EmailConfirmation, Verbal }
public enum ContractTermKind { FixedTerm, OpenEnded, AnnualRolling }

public sealed record NonFrameworkContractTerms(
    AgreementBasis AgreementBasis,
    DateOnly AgreementDate,
    string AgreementNotes,
    ContractTermKind TermKind,
    decimal InitialAnnualChargeExVat,
    bool UsesCpi,
    decimal AdditionalPercentage,
    string CpiReference,
    DateOnly? NoticeReceivedDate = null,
    DateOnly? TerminationDate = null);

/// <summary>An immutable, reviewed annual charge, never a forecast CPI rate.</summary>
public sealed record NonFrameworkAnnualReview(
    Guid Id, Guid ContractId, int ContractYear, DateOnly EffectiveDate,
    decimal PreviousAnnualChargeExVat, decimal CpiPercentage, decimal AdditionalPercentage,
    decimal AnnualChargeExVat, string CpiReference, DateTimeOffset CreatedAtUtc);

public static class NonFrameworkContractRules
{
    public const int NoticeDays = 90;

    public static string? Validate(ContractRecord contract)
    {
        var terms = contract.NonFrameworkTerms;
        if (terms is null) return "Record the agreement basis, term and annual charge.";
        if (!Enum.IsDefined(terms.AgreementBasis) || !Enum.IsDefined(terms.TermKind)) return "Choose a valid agreement basis and contract term.";
        if (contract.StartDate is null) return "A start date is required to calculate contract anniversaries.";
        if (terms.AgreementBasis == AgreementBasis.Verbal && string.IsNullOrWhiteSpace(terms.AgreementNotes)) return "Record the parties and terms of the verbal agreement in the agreement notes.";
        if (terms.TermKind == ContractTermKind.FixedTerm && contract.EndDate is null) return "A fixed-term contract needs an end date.";
        if (terms.TermKind != ContractTermKind.FixedTerm && contract.EndDate is not null) return "Use a termination date for open-ended or rolling contracts, rather than an original end date.";
        if (contract.EndDate < contract.StartDate) return "The end date cannot precede the start date.";
        if (terms.InitialAnnualChargeExVat <= 0 || decimal.Round(terms.InitialAnnualChargeExVat, 2) != terms.InitialAnnualChargeExVat) return "Enter a positive annual charge with at most two decimal places.";
        if (terms.AdditionalPercentage < 0) return "The additional annual percentage cannot be negative.";
        if (terms.UsesCpi && string.IsNullOrWhiteSpace(terms.CpiReference)) return "Record which CPI measure and reference period the agreement uses.";
        if (!terms.UsesCpi && terms.AdditionalPercentage != 0) return "Enable annual CPI increases to record CPI plus an additional percentage.";
        if (terms.TerminationDate < contract.StartDate || terms.NoticeReceivedDate > terms.TerminationDate) return "Termination cannot precede the contract start or the notice received date.";
        if (terms.TerminationDate is not null && terms.NoticeReceivedDate is null) return "Record the notice received date for a termination.";
        if (terms.NoticeReceivedDate is not null && terms.TerminationDate is null) return "Record the effective termination date with the notice.";
        if (terms.TermKind == ContractTermKind.FixedTerm && terms.TerminationDate > contract.EndDate) return "The termination date cannot be after the fixed contract end.";
        if (terms.TermKind == ContractTermKind.AnnualRolling && terms.TerminationDate is { } end)
        {
            var years = end.Year - contract.StartDate.Value.Year;
            if (years < 1 || contract.StartDate.Value.AddYears(years) != end) return "A rolling contract terminates on an anniversary of its start date.";
            if (terms.NoticeReceivedDate > end.AddDays(-NoticeDays)) return "The customer must give at least 90 calendar days' notice before that anniversary. Choose the next eligible anniversary.";
        }
        return null;
    }

    public static DateOnly? EffectiveEnd(ContractRecord contract) => contract.NonFrameworkTerms?.TerminationDate ?? contract.EndDate;

    public static int ContractYear(ContractRecord contract, DateOnly asAt)
    {
        if (contract.StartDate is not { } start || asAt < start) return 1;
        var years = asAt.Year - start.Year;
        if (start.AddYears(years) > asAt) years--;
        return years + 1;
    }

    public static DateOnly? NextAnniversary(ContractRecord contract, DateOnly asAt)
    {
        if (contract.StartDate is not { } start) return null;
        var years = Math.Max(1, asAt.Year - start.Year);
        if (start.Year + years > 9999) return null;
        var next = start.AddYears(years);
        if (next < asAt) { if (start.Year + years + 1 > 9999) return null; next = start.AddYears(++years); }
        return EffectiveEnd(contract) is { } end && next >= end ? null : next;
    }

    public static DateOnly? NextNoticeDeadline(ContractRecord contract, DateOnly asAt) =>
        contract.NonFrameworkTerms is { TermKind: ContractTermKind.AnnualRolling, NoticeReceivedDate: null }
            && NextAnniversary(contract, asAt) is { } next ? next.AddDays(-NoticeDays) : null;

    public static decimal AnnualCharge(ContractRecord contract, IEnumerable<NonFrameworkAnnualReview> reviews, DateOnly asAt) =>
        reviews.Where(item => item.ContractId == contract.Id && item.EffectiveDate <= asAt
            && (EffectiveEnd(contract) is not { } end || item.EffectiveDate < end))
            .OrderByDescending(item => item.EffectiveDate).Select(item => (decimal?)item.AnnualChargeExVat).FirstOrDefault()
        ?? contract.NonFrameworkTerms!.InitialAnnualChargeExVat;

    public static decimal CalculateUplift(decimal previous, decimal cpi, decimal additional) =>
        decimal.Round(previous * (1 + (cpi + additional) / 100), 2, MidpointRounding.AwayFromZero);
}
