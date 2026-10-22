using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TurboEvents.Database;
using TurboEvents.Hosting;

namespace TurboEvents.Prizes;

/// <summary>
/// Pays a winner's prize all or none, and once, from the row written before anything was paid
/// (<see cref="WinnerEntity"/>). The currencies go first, as rewards under the winner's own wallet
/// operation (paid once however often it is tried); then the badge; then the furni, which nothing
/// can take back, so it goes last. A step that fails takes back what the payout gave and fails it.
/// Each step is saved as it is done, so a payout a restart broke off is finished when the plugin
/// starts again (<see cref="ResumeAsync"/>): the wallet and the badge pay nothing twice, and a
/// payout broken off while granting furni is marked unsure for staff to check.
/// </summary>
public sealed class PrizePayer(
    IServiceScopeFactory scopes,
    IEventsHotel hotel,
    TimeProvider time,
    ILogger<PrizePayer> logger
)
{
    /// <summary>The first furni grant's seq in the prize's operation; the currencies count up from 0.</summary>
    public const int FURNI_SEQ = 1000;

    /// <summary>Pays the winner's prize as far as it goes, and answers the row as it ends.</summary>
    public async Task<WinnerEntity> PayAsync(long winnerId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
        var winner = await db.Winners.FirstAsync(x => x.Id == winnerId, ct).ConfigureAwait(false);

        if (winner.Status != WinnerEntity.PENDING)
            return winner;

        try
        {
            await PayStepsAsync(db, winner, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Prize of winner {WinnerId} could not be paid", winner.Id);

            winner.Error = Cut($"Broke off: {ex.Message}");

            // Furni has no receipt: one granted as the call broke may be there or not.
            if (winner.FurniStarted && winner.FurniGiven < winner.FurniCount)
                winner.Status = WinnerEntity.UNSURE;

            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return winner;
    }

    /// <summary>
    /// Finishes the payouts a restart broke off: each pending row is paid on, but one broken off
    /// while granting furni is marked unsure. Answers how many were finished.
    /// </summary>
    public async Task<int> ResumeAsync(CancellationToken ct)
    {
        List<long> pending;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
            var rows = await db
                .Winners.Where(x => x.Status == WinnerEntity.PENDING)
                .OrderBy(x => x.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in rows.Where(x => x.FurniStarted && x.FurniGiven < x.FurniCount))
            {
                row.Status = WinnerEntity.UNSURE;
                row.Error = Cut(
                    $"The server stopped while furni was given ({row.FurniGiven} of {row.FurniCount} saved): check the winner's inventory."
                );
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            pending = [.. rows.Where(x => x.Status == WinnerEntity.PENDING).Select(x => x.Id)];
        }

        var finished = 0;

        foreach (var id in pending)
            if ((await PayAsync(id, ct).ConfigureAwait(false)).Status == WinnerEntity.PAID)
                finished++;

        if (pending.Count > 0)
            logger.LogInformation(
                "Finished {Finished} of {Pending} prize payouts a restart broke off",
                finished,
                pending.Count
            );

        return finished;
    }

    private async Task PayStepsAsync(EventsDbContext db, WinnerEntity winner, CancellationToken ct)
    {
        var prize = Prize.Of(winner);
        var paid = new List<(CurrencyAmount Amount, int Seq)>();

        if (!winner.CurrenciesPaid)
        {
            for (var seq = 0; seq < prize.Currencies.Length; seq++)
            {
                var amount = prize.Currencies[seq];

                if (
                    !await hotel
                        .CreditAsync(
                            winner.PlayerId,
                            amount.Currency,
                            amount.Amount,
                            winner.OperationId,
                            seq,
                            winner.AwardedById,
                            winner.Id,
                            ct
                        )
                        .ConfigureAwait(false)
                )
                {
                    await FailAsync(
                            db,
                            winner,
                            prize,
                            paid,
                            $"{amount.Amount} {amount.Currency} could not be paid",
                            ct
                        )
                        .ConfigureAwait(false);

                    return;
                }

                paid.Add((amount, seq));
            }

            winner.CurrenciesPaid = true;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else
            paid.AddRange(prize.Currencies.Select((x, i) => (x, i)));

        if (!winner.BadgeDone && prize.BadgeCode is { } badge)
        {
            if (!await hotel.HasBadgeAsync(winner.PlayerId, badge, ct).ConfigureAwait(false))
            {
                if (!await hotel.GiveBadgeAsync(winner.PlayerId, badge, ct).ConfigureAwait(false))
                {
                    await FailAsync(
                            db,
                            winner,
                            prize,
                            paid,
                            $"The {badge} badge could not be given",
                            ct
                        )
                        .ConfigureAwait(false);

                    return;
                }

                winner.BadgeGiven = true;
            }

            winner.BadgeDone = true;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        if (prize.FurniName is { } furni && winner.FurniGiven < prize.FurniCount)
        {
            winner.FurniStarted = true;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            while (winner.FurniGiven < prize.FurniCount)
            {
                if (
                    !await hotel
                        .GrantFurniAsync(
                            winner.PlayerId,
                            furni,
                            winner.OperationId,
                            FURNI_SEQ + winner.FurniGiven,
                            winner.AwardedById,
                            winner.Id,
                            ct
                        )
                        .ConfigureAwait(false)
                )
                {
                    if (winner.FurniGiven == 0)
                    {
                        await FailAsync(db, winner, prize, paid, $"{furni} could not be given", ct)
                            .ConfigureAwait(false);

                        return;
                    }

                    // Furni already given cannot be taken back: the rest stands, for staff to settle.
                    winner.Status = WinnerEntity.UNSURE;
                    winner.Error = Cut(
                        $"{furni} could not be given after {winner.FurniGiven} of {prize.FurniCount}"
                    );
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);

                    return;
                }

                winner.FurniGiven++;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        winner.Status = WinnerEntity.PAID;
        winner.PaidAt = time.GetUtcNow().UtcDateTime;
        winner.Error = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Takes back the currencies and the badge this payout gave, and fails it.</summary>
    private async Task FailAsync(
        EventsDbContext db,
        WinnerEntity winner,
        Prize prize,
        IReadOnlyList<(CurrencyAmount Amount, int Seq)> paid,
        string reason,
        CancellationToken ct
    )
    {
        var kept = new List<string>();

        foreach (var (amount, seq) in paid)
            if (
                !await hotel
                    .TakeBackAsync(
                        winner.PlayerId,
                        amount.Currency,
                        amount.Amount,
                        winner.OperationId,
                        seq,
                        winner.AwardedById,
                        winner.Id,
                        ct
                    )
                    .ConfigureAwait(false)
            )
                kept.Add($"{amount.Amount} {amount.Currency}");

        if (
            winner.BadgeGiven
            && prize.BadgeCode is { } badge
            && !await hotel.RemoveBadgeAsync(winner.PlayerId, badge, ct).ConfigureAwait(false)
        )
            kept.Add($"the {badge} badge");

        winner.Status = kept.Count == 0 ? WinnerEntity.FAILED : WinnerEntity.UNSURE;
        winner.Error = Cut(
            kept.Count == 0
                ? $"{reason}: nothing was paid."
                : $"{reason}, and {string.Join(", ", kept)} could not be taken back."
        );
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static string Cut(string value) =>
        value.Length <= WinnerEntity.MAX_ERROR ? value : value[..WinnerEntity.MAX_ERROR];
}
