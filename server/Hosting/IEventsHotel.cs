using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TurboEvents.Hosting;

/// <summary>A room as the plugin needs it: its name, and how many players are in it now.</summary>
public readonly record struct RoomInfo(string Name, int Population);

/// <summary>
/// What the plugin asks of the hotel, through the host's own services (<see cref="HostHotel"/>):
/// who is online, a room's name and population, notifications, and the grants a prize pays. It
/// never writes core's tables.
/// </summary>
public interface IEventsHotel
{
    IReadOnlyCollection<int> OnlinePlayerIds();

    Task<RoomInfo?> RoomAsync(int roomId, CancellationToken ct);

    /// <summary>
    /// The client's own notification (<c>NotificationDialog</c>) to each player: a pop-up, or a
    /// bubble when <paramref name="parameters"/> say <c>display: BUBBLE</c>.
    /// </summary>
    Task NotifyAsync(
        IReadOnlyCollection<int> playerIds,
        string type,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct
    );

    bool CurrencyExists(string name);

    IReadOnlyCollection<string> CurrencyNames();

    bool FurniExists(string name);

    /// <summary>
    /// Credits <paramref name="amount"/> of <paramref name="currency"/> as a reward (the client's
    /// corner bubble), at most once per <paramref name="operationId"/> and <paramref name="seq"/>.
    /// </summary>
    Task<bool> CreditAsync(
        int playerId,
        string currency,
        int amount,
        string operationId,
        int seq,
        int actorId,
        long winnerId,
        CancellationToken ct
    );

    /// <summary>Takes back what <see cref="CreditAsync"/> paid under the same operation and seq, at most once.</summary>
    Task<bool> TakeBackAsync(
        int playerId,
        string currency,
        int amount,
        string operationId,
        int seq,
        int actorId,
        long winnerId,
        CancellationToken ct
    );

    Task<bool> HasBadgeAsync(int playerId, string code, CancellationToken ct);

    Task<bool> GiveBadgeAsync(int playerId, string code, CancellationToken ct);

    Task<bool> RemoveBadgeAsync(int playerId, string code, CancellationToken ct);

    /// <summary>One furni of the definition named <paramref name="furniName"/> into the player's inventory.</summary>
    Task<bool> GrantFurniAsync(
        int playerId,
        string furniName,
        string operationId,
        int seq,
        int actorId,
        long winnerId,
        CancellationToken ct
    );
}
