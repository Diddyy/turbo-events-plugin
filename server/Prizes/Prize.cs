using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using TurboEvents.Database;

namespace TurboEvents.Prizes;

/// <summary>An amount of one of the hotel's currencies, by its name (<c>credits</c>).</summary>
public readonly record struct CurrencyAmount(string Currency, int Amount);

/// <summary>
/// What a prize set pays: amounts of currencies, a badge, and a count of one furni, any of them.
/// Kept as text in the tables (<c>credits:100, duckets:50</c>), so a winner's row says what they
/// were paid even after the set changes.
/// </summary>
public sealed record Prize(
    string Name,
    ImmutableArray<CurrencyAmount> Currencies,
    string? BadgeCode,
    string? FurniName,
    int FurniCount
)
{
    public bool IsEmpty =>
        Currencies.IsDefaultOrEmpty
        && string.IsNullOrEmpty(BadgeCode)
        && (string.IsNullOrEmpty(FurniName) || FurniCount <= 0);

    public static Prize Of(PrizeSetEntity set) =>
        new(
            set.Name,
            ParseCurrencies(set.Currencies, out _),
            Blank(set.BadgeCode),
            Blank(set.FurniName),
            Blank(set.FurniName) is null ? 0 : set.FurniCount
        );

    public static Prize Of(WinnerEntity winner) =>
        new(
            winner.PrizeName,
            ParseCurrencies(winner.Currencies, out _),
            Blank(winner.BadgeCode),
            Blank(winner.FurniName),
            Blank(winner.FurniName) is null ? 0 : winner.FurniCount
        );

    /// <summary>
    /// <c>credits:100, duckets:50</c> (a space or <c>=</c> also separates a name from its amount);
    /// <paramref name="error"/> says what is wrong with the first part that does not read.
    /// </summary>
    public static ImmutableArray<CurrencyAmount> ParseCurrencies(string? text, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(text))
            return [];

        var amounts = ImmutableArray.CreateBuilder<CurrencyAmount>();

        foreach (
            var part in text.Split(
                [',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            var pieces = part.Split(
                [':', '=', ' '],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );

            if (
                pieces.Length != 2
                || !int.TryParse(
                    pieces[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var amount
                )
                || amount <= 0
            )
            {
                error = $"\"{part}\" is not a currency and an amount above 0, as credits:100";

                return [];
            }

            if (
                amounts.Any(x =>
                    string.Equals(x.Currency, pieces[0], StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                error = $"{pieces[0]} is named twice";

                return [];
            }

            amounts.Add(new CurrencyAmount(pieces[0].ToLowerInvariant(), amount));
        }

        return amounts.ToImmutable();
    }

    public static string FormatCurrencies(IEnumerable<CurrencyAmount> amounts) =>
        string.Join(
            ", ",
            amounts.Select(x => $"{x.Currency}:{x.Amount.ToString(CultureInfo.InvariantCulture)}")
        );

    /// <summary>For people: <c>100 credits, the ACH_X badge and 2 throne</c>.</summary>
    public string Describe()
    {
        var parts = Currencies
            .Select(x => $"{x.Amount.ToString(CultureInfo.InvariantCulture)} {x.Currency}")
            .ToList();

        if (BadgeCode is not null)
            parts.Add($"the {BadgeCode} badge");

        if (FurniName is not null && FurniCount > 0)
            parts.Add(
                FurniCount == 1
                    ? FurniName
                    : $"{FurniCount.ToString(CultureInfo.InvariantCulture)} x {FurniName}"
            );

        return parts.Count switch
        {
            0 => "nothing",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
