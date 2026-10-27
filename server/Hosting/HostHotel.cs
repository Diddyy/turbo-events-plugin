using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Turbo.Primitives.Furniture.Providers;
using Turbo.Primitives.Messages.Outgoing.Notifications;
using Turbo.Primitives.Movements;
using Turbo.Primitives.Networking;
using Turbo.Primitives.Orleans;
using Turbo.Primitives.Players;
using Turbo.Primitives.Players.Providers;
using Turbo.Primitives.Players.Wallet;

namespace TurboEvents.Hosting;

/// <summary>
/// <see cref="IEventsHotel"/> over the host's services: the session gateway, the room, wallet,
/// badge and inventory grains, and the currency and furni definitions. Every grant goes through
/// the grain that owns it, under the movement cause <see cref="PRIZE_CAUSE"/> with the winner's id
/// as its ref, so an audit reads a prize as one.
/// </summary>
public sealed class HostHotel(
    IGrainFactory grains,
    ISessionGateway sessions,
    ICurrencyTypeProvider currencies,
    IFurnitureDefinitionProvider furni
) : IEventsHotel
{
    public const string PRIZE_CAUSE = "events.prize";

    public IReadOnlyCollection<int> OnlinePlayerIds() =>
        [.. sessions.GetOnlinePlayerIds().Select(x => x.Value)];

    public async Task<RoomInfo?> RoomAsync(int roomId, CancellationToken ct)
    {
        var summary = await grains.GetRoomGrain(roomId).GetSummaryAsync(ct).ConfigureAwait(false);

        return summary.RoomId.Value <= 0 ? null : new RoomInfo(summary.Name, summary.Population);
    }

    public Task NotifyAsync(
        IReadOnlyCollection<int> playerIds,
        string type,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct
    ) =>
        Task.WhenAll(
            playerIds.Select(id =>
                grains.TrySendComposerToPlayerAsync(
                    id,
                    new NotificationDialogMessageComposer
                    {
                        NotificationType = type,
                        Parameters = parameters.ToImmutableDictionary(),
                    },
                    ct
                )
            )
        );

    public bool CurrencyExists(string name) => currencies.TryGetCurrencyKindByName(name, out _);

    public IReadOnlyCollection<string> CurrencyNames() => currencies.GetEnabledCurrencyNames();

    public bool FurniExists(string name) => furni.TryGetDefinitionByName(name) is not null;

    public async Task<bool> CreditAsync(
        int playerId,
        string currency,
        int amount,
        string operationId,
        int seq,
        int actorId,
        long winnerId,
        CancellationToken ct
    ) =>
        currencies.TryGetCurrencyKindByName(currency, out var kind)
        && await grains
            .GetPlayerWalletGrain(playerId)
            .CreditRewardAsync(kind, amount, Cause(operationId, seq, actorId, winnerId), ct)
            .ConfigureAwait(false);

    public async Task<bool> TakeBackAsync(
        int playerId,
        string currency,
        int amount,
        string operationId,
        int seq,
        int actorId,
        long winnerId,
        CancellationToken ct
    )
    {
        if (!currencies.TryGetCurrencyKindByName(currency, out var kind))
            return false;

        var result = await grains
            .GetPlayerWalletGrain(playerId)
            .TryDebitAsync(
                [new WalletDebitRequest { CurrencyKind = kind, Amount = amount }],
                Cause(operationId, seq, actorId, winnerId),
                ct
            )
            .ConfigureAwait(false);

        return result.Succeeded;
    }

    public Task<bool> HasBadgeAsync(int playerId, string code, CancellationToken ct) =>
        grains.GetPlayerBadgeGrain(playerId).HasBadgeAsync(code, ct);

    public Task<bool> GiveBadgeAsync(int playerId, string code, CancellationToken ct) =>
        grains.GetPlayerBadgeGrain(playerId).GiveBadgeAsync(code, ct);

    public Task<bool> RemoveBadgeAsync(int playerId, string code, CancellationToken ct) =>
        grains.GetPlayerBadgeGrain(playerId).RemoveBadgeAsync(code, ct);

    public async Task<bool> GrantFurniAsync(
        int playerId,
        string furniName,
        string operationId,
        int seq,
        int actorId,
        long winnerId,
        CancellationToken ct
    )
    {
        if (furni.TryGetDefinitionByName(furniName) is not { } definition)
            return false;

        return await grains
            .GetInventoryGrain(playerId)
            .GrantFurnitureAsync(
                definition.Id,
                null,
                Cause(operationId, seq, actorId, winnerId),
                ct
            )
            .ConfigureAwait(false)
            is not null;
    }

    /// <summary>
    /// The prize's operation, the same on every try, so the wallet pays it once. Each grant has a
    /// seq of its own, so no two of the prize's movements share one.
    /// </summary>
    private static MovementCause Cause(string operationId, int seq, int actorId, long winnerId) =>
        new MovementCause(
            operationId,
            PRIZE_CAUSE,
            winnerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            actorId
        ).StartingAt(seq);
}
