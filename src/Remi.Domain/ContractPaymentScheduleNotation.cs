using System.Globalization;
using System.Text.RegularExpressions;

namespace Remi.Domain;

/// <summary>
/// A payment position expressed in the concise notation formerly used in the MI Reporting Ledger.
/// A parenthesised group represents several chargeable positions in the same contract year.
/// </summary>
public sealed record ContractPaymentPosition(
    int ContractYear,
    int PositionInYear,
    int PositionsInYear,
    bool IsOptionalExtension,
    decimal ValueExVat,
    string SourceText,
    bool HasUnresolvedUplift);

public sealed record ContractPaymentSchedule(
    string Notation,
    int BaseTermYears,
    int OptionalExtensionYears,
    IReadOnlyList<ContractPaymentPosition> Positions);

public sealed record ContractPaymentScheduleParseResult(
    ContractPaymentSchedule? Schedule,
    string? Error);

/// <summary>
/// Parses plans such as <c>3+1 years; (17 750 + 1 700 + 850) + 17 750 + 17 750 + 17 750 GBP</c>.
/// </summary>
public static partial class ContractPaymentScheduleNotation
{
    public static ContractPaymentScheduleParseResult Parse(string notation)
    {
        if (string.IsNullOrWhiteSpace(notation))
        {
            return new(null, "Enter a contract term and payment plan, for example '3+1 years; 17 750 + 17 750 + 17 750 + 17 750 GBP'.");
        }

        var normalised = notation.Trim();
        var separator = normalised.IndexOf(';');
        if (separator < 1)
        {
            return new(null, "The payment plan must separate the term and payments with a semicolon.");
        }

        var termMatch = TermPattern().Match(normalised[..separator]);
        if (!termMatch.Success)
        {
            return new(null, "The contract term must be expressed as years, for example '3 years' or '3+1 years'.");
        }

        var termParts = termMatch.Groups["years"].Value
            .Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!termParts.All(value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var years) && years > 0))
        {
            return new(null, "Each term component must be a positive whole number of years.");
        }

        var baseTermYears = int.Parse(termParts[0], CultureInfo.InvariantCulture);
        var optionalExtensionYears = termParts.Skip(1).Sum(value => int.Parse(value, CultureInfo.InvariantCulture));
        var paymentText = normalised[(separator + 1)..].Trim();
        if (paymentText.EndsWith("GBP", StringComparison.OrdinalIgnoreCase))
        {
            paymentText = paymentText[..^3].Trim();
        }

        var annualGroups = SplitTopLevelGroups(paymentText, out var groupingError);
        if (groupingError is not null)
        {
            return new(null, groupingError);
        }

        if (annualGroups.Count == 0)
        {
            return new(null, "At least one payment amount is required.");
        }

        var positions = new List<ContractPaymentPosition>();
        for (var yearIndex = 0; yearIndex < annualGroups.Count; yearIndex++)
        {
            var components = SplitComponents(annualGroups[yearIndex], out var componentError);
            if (componentError is not null)
            {
                return new(null, $"Contract year {yearIndex + 1}: {componentError}");
            }

            for (var positionIndex = 0; positionIndex < components.Count; positionIndex++)
            {
                var component = components[positionIndex];
                var amountMatch = AmountPattern().Match(component);
                if (!amountMatch.Success || !TryReadAmount(amountMatch.Groups["amount"].Value, out var amount) || amount <= 0)
                {
                    return new(null, $"Contract year {yearIndex + 1} contains an invalid payment amount: '{component}'.");
                }

                positions.Add(new ContractPaymentPosition(
                    yearIndex + 1,
                    positionIndex + 1,
                    components.Count,
                    yearIndex + 1 > baseTermYears,
                    amount,
                    component.Trim(),
                    component.Contains("up", StringComparison.OrdinalIgnoreCase)));
            }
        }

        return new(new ContractPaymentSchedule(normalised, baseTermYears, optionalExtensionYears, positions), null);
    }

    private static List<string> SplitTopLevelGroups(string value, out string? error)
    {
        error = null;
        var groups = new List<string>();
        var start = 0;
        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth < 0)
                    {
                        error = "The payment plan contains a closing parenthesis without a matching opening parenthesis.";
                        return [];
                    }

                    break;
                case '+' when depth == 0:
                    groups.Add(value[start..index].Trim());
                    start = index + 1;
                    break;
            }
        }

        if (depth != 0)
        {
            error = "The payment plan contains an unclosed parenthesised payment group.";
            return [];
        }

        groups.Add(value[start..].Trim());
        if (groups.Any(string.IsNullOrWhiteSpace))
        {
            error = "Each '+' in the payment plan must have an amount on both sides.";
            return [];
        }

        return groups;
    }

    private static List<string> SplitComponents(string group, out string? error)
    {
        error = null;
        var trimmed = group.Trim();
        if (trimmed.StartsWith('(') && trimmed.EndsWith(')'))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        if (trimmed.Contains('(') || trimmed.Contains(')'))
        {
            error = "Payment groups can only contain amounts joined with '+'.";
            return [];
        }

        var components = trimmed.Split('+', StringSplitOptions.TrimEntries).ToList();
        if (components.Any(string.IsNullOrWhiteSpace))
        {
            error = "Each payment position must contain an amount.";
            return [];
        }

        return components;
    }

    private static bool TryReadAmount(string value, out decimal amount)
    {
        var normalised = value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(normalised, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    [GeneratedRegex(@"(?<years>\d+(?:\s*\+\s*\d+)*)\s*years?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TermPattern();

    [GeneratedRegex(@"(?<amount>\d[\d\s,]*(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex AmountPattern();
}
