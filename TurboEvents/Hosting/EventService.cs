using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TurboEvents.Database;
using TurboEvents.Prizes;

namespace TurboEvents.Hosting;

/// <summary>Who starts an event, where, and whether the cooldowns hold them.</summary>
public sealed record StartRequest(
    int HostId,
    string HostName,
    int RoomId,
    string Title,
    bool BypassCooldown
);

/// <summary>Who names a winner, where, who was in the room then, and the prize set named (null: the default).</summary>
public sealed record AwardRequest(
    int ById,
    string ByName,
    int RoomId,
    IReadOnlyCollection<int> RoomPlayerIds,
    int WinnerId,
    string WinnerName,
    string? PrizeName
);

/// <summary>
/// Hosting an event: starting one in a room (the cooldowns, the call to the hotel, the notice),
/// ending it, naming its winners and paying them (<see cref="PrizePayer"/>), and keeping each
/// running event's peak. A room runs one event at a time. The commands are thin shells over this.
/// </summary>
public sealed class EventService(
    IServiceScopeFactory scopes,
    IEventsHotel hotel,
    IOptionsMonitor<EventsConfig> options,
    PrizePayer payer,
    Notices notices,
    TimeProvider time
)
{
    public const string ROOM_LINK = "event:navigator/goto/";

    // Statuses: the commands' texts (command.<name>.<status>).
    public const string STARTED = "started";
    public const string NO_TITLE = "no_title";
    public const string TITLE_TOO_LONG = "title_too_long";
    public const string RUNNING = "running";
    public const string HOST_COOLDOWN = "host_cooldown";
    public const string HOTEL_COOLDOWN = "hotel_cooldown";
    public const string NO_ROOM = "no_room";
    public const string ENDED = "ended";
    public const string NONE_RUNNING = "none_running";
    public const string PAID = "paid";
    public const string NO_EVENT = "no_event";
    public const string NO_PRIZE = "no_prize";
    public const string UNKNOWN_PRIZE = "unknown_prize";
    public const string BROKEN_PRIZE = "broken_prize";
    public const string OFFLINE = "offline";
    public const string NOT_IN_ROOM = "not_in_room";
    public const string SELF = "self";
    public const string NOT_PAID = "not_paid";
    public const string UNSURE = "unsure";

    // One start or end at a time, so two hosts cannot both start an event in one room.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private EventsConfig Config => options.CurrentValue;

    /// <summary>The prize set a <c>:winner</c> line naming none pays; empty: none.</summary>
    public string DefaultPrizeSet => Config.DefaultPrizeSet.Trim();

    /// <summary>Why <paramref name="request"/> cannot start an event now; null: it can.</summary>
    public async Task<EventsReply?> CheckStartAsync(StartRequest request, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();

        return await RefusalAsync(Db(scope), request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the event, calls <paramref name="called"/> to its room, and tells other plugins.
    /// The checks of <see cref="CheckStartAsync"/> are made again first.
    /// </summary>
    public async Task<EventsReply> StartAsync(
        StartRequest request,
        IReadOnlyCollection<int> called,
        CancellationToken ct
    )
    {
        var room = await hotel.RoomAsync(request.RoomId, ct).ConfigureAwait(false);

        if (room is null)
            return EventsReply.Fail(NO_ROOM);

        HostedEventEntity hosted;

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = Db(scope);

            if (await RefusalAsync(db, request, ct).ConfigureAwait(false) is { } refusal)
                return refusal;

            hosted = new HostedEventEntity
            {
                RoomId = request.RoomId,
                RoomName = Cut(room.Value.Name, HostedEventEntity.MAX_NAME),
                HostId = request.HostId,
                HostName = Cut(request.HostName, HostedEventEntity.MAX_NAME),
                Title = request.Title.Trim(),
                StartedAt = Now,
                Called = called.Count,
                PeakAttendees = room.Value.Population,
            };
            db.Events.Add(hosted);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        await hotel
            .NotifyAsync(called, Config.AlertType, AlertParameters(hosted), ct)
            .ConfigureAwait(false);
        await notices.StartedAsync(hosted, ct).ConfigureAwait(false);

        return EventsReply.Done(STARTED, hosted.Title, Count(called.Count));
    }

    /// <summary>What the client's notification shows: a pop-up with a button to the room, or a bubble that goes there.</summary>
    internal Dictionary<string, string> AlertParameters(HostedEventEntity hosted)
    {
        var config = Config;
        string Fill(string template) =>
            template
                .Replace("{host}", hosted.HostName, StringComparison.Ordinal)
                .Replace("{title}", hosted.Title, StringComparison.Ordinal)
                .Replace("{room}", hosted.RoomName, StringComparison.Ordinal)
                .Replace("{roomId}", Count(hosted.RoomId), StringComparison.Ordinal);

        var parameters = new Dictionary<string, string>
        {
            ["title"] = Fill(config.AlertTitle),
            ["message"] = Fill(config.AlertMessage),
            ["linkTitle"] = Fill(config.AlertLinkTitle),
            ["linkUrl"] = ROOM_LINK + Count(hosted.RoomId),
        };

        if (config.AlertAsBubble)
            parameters["display"] = "BUBBLE";

        if (!string.IsNullOrWhiteSpace(config.AlertImage))
            parameters["image"] = config.AlertImage.Trim();

        return parameters;
    }

    /// <summary>Ends the event running in <paramref name="roomId"/>.</summary>
    public async Task<EventsReply> EndAsync(int roomId, string endedBy, CancellationToken ct)
    {
        var ended = await EndWhereAsync(
                x => x.RoomId == roomId,
                HostedEventEntity.ENDED,
                endedBy,
                ct
            )
            .ConfigureAwait(false);

        if (ended.Count == 0)
            return EventsReply.Fail(NONE_RUNNING);

        var (hosted, winners) = ended[0];

        return EventsReply.Done(
            ENDED,
            hosted.Title,
            Count((long)Math.Round((hosted.EndedAt!.Value - hosted.StartedAt).TotalMinutes)),
            Count(winners),
            Count(hosted.PeakAttendees)
        );
    }

    /// <summary>Ends the event of this id from the panel; false when it is not running.</summary>
    public async Task<bool> EndByIdAsync(long id, string endedBy, CancellationToken ct) =>
        (
            await EndWhereAsync(x => x.Id == id, HostedEventEntity.ENDED, endedBy, ct)
                .ConfigureAwait(false)
        ).Count > 0;

    private async Task<List<(HostedEventEntity Event, int Winners)>> EndWhereAsync(
        System.Linq.Expressions.Expression<Func<HostedEventEntity, bool>> which,
        string reason,
        string? endedBy,
        CancellationToken ct
    )
    {
        var ended = new List<(HostedEventEntity, int)>();

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = Db(scope);
            var running = await db
                .Events.Where(x => x.EndedAt == null)
                .Where(which)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var hosted in running)
            {
                if (await hotel.RoomAsync(hosted.RoomId, ct).ConfigureAwait(false) is { } room)
                    hosted.PeakAttendees = Math.Max(hosted.PeakAttendees, room.Population);

                hosted.EndedAt = Now;
                hosted.EndReason = reason;
                hosted.EndedBy = endedBy is null ? null : Cut(endedBy, HostedEventEntity.MAX_NAME);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            foreach (var hosted in running)
                ended.Add(
                    (
                        hosted,
                        await db
                            .Winners.CountAsync(
                                x => x.EventId == hosted.Id && x.Status == WinnerEntity.PAID,
                                ct
                            )
                            .ConfigureAwait(false)
                    )
                );
        }
        finally
        {
            _gate.Release();
        }

        foreach (var (hosted, winners) in ended)
            await notices.EndedAsync(hosted, winners, ct).ConfigureAwait(false);

        return ended;
    }

    /// <summary>
    /// Counts the rooms of running events for their peak, and ends those that ran past
    /// <c>MaxEventMinutes</c>. Run every <c>AttendanceSampleSeconds</c>.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var max = Config.MaxEventMinutes;

        if (max > 0)
        {
            var oldest = Now.AddMinutes(-max);

            await EndWhereAsync(x => x.StartedAt <= oldest, HostedEventEntity.EXPIRED, null, ct)
                .ConfigureAwait(false);
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = Db(scope);
        var running = await db
            .Events.Where(x => x.EndedAt == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var hosted in running)
            if (
                await hotel.RoomAsync(hosted.RoomId, ct).ConfigureAwait(false) is { } room
                && room.Population > hosted.PeakAttendees
            )
                hosted.PeakAttendees = room.Population;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Names a winner and pays them: the checks (an event running, the winner where they must be,
    /// the prize set), then the row, then the payout, then the room is told and other plugins.
    /// </summary>
    public async Task<EventsReply> AwardAsync(AwardRequest request, CancellationToken ct)
    {
        var config = Config;
        HostedEventEntity? hosted;
        PrizeSetEntity? set;
        long winnerId;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = Db(scope);

            hosted = await db
                .Events.AsNoTracking()
                .FirstOrDefaultAsync(x => x.RoomId == request.RoomId && x.EndedAt == null, ct)
                .ConfigureAwait(false);

            if (hosted is null && config.WinnerNeedsEvent)
                return EventsReply.Fail(NO_EVENT);

            if (request.WinnerId == request.ById && !config.HostMayWin)
                return EventsReply.Fail(SELF);

            var inRoom = request.RoomPlayerIds.Contains(request.WinnerId);

            if (!inRoom && (config.WinnerMustBeInRoom || config.WinnerMustBeOnline))
            {
                var online = hotel.OnlinePlayerIds().Contains(request.WinnerId);

                if (!online)
                    return EventsReply.Fail(OFFLINE, request.WinnerName);

                if (config.WinnerMustBeInRoom)
                    return EventsReply.Fail(NOT_IN_ROOM, request.WinnerName);
            }

            var name = string.IsNullOrWhiteSpace(request.PrizeName)
                ? config.DefaultPrizeSet.Trim()
                : request.PrizeName.Trim();
            var names = await db
                .PrizeSets.OrderBy(x => x.Name)
                .Select(x => x.Name)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var list = names.Count == 0 ? "none yet" : string.Join(", ", names);

            if (name.Length == 0)
                return EventsReply.Fail(NO_PRIZE, list);

            set = await db
                .PrizeSets.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Name == name, ct)
                .ConfigureAwait(false);

            if (set is null)
                return EventsReply.Fail(UNKNOWN_PRIZE, name, list);

            if (PrizeProblem(set) is { } problem)
                return EventsReply.Fail(BROKEN_PRIZE, set.Name, problem);

            var winner = new WinnerEntity
            {
                EventId = hosted?.Id,
                RoomId = request.RoomId,
                PlayerId = request.WinnerId,
                PlayerName = Cut(request.WinnerName, HostedEventEntity.MAX_NAME),
                AwardedById = request.ById,
                AwardedBy = Cut(request.ByName, HostedEventEntity.MAX_NAME),
                PrizeName = set.Name,
                Currencies = Prize.FormatCurrencies(Prize.Of(set).Currencies),
                BadgeCode = Prize.Of(set).BadgeCode,
                FurniName = Prize.Of(set).FurniName,
                FurniCount = Prize.Of(set).FurniCount,
                OperationId = Guid.CreateVersion7().ToString("N"),
                CreatedAt = Now,
            };
            db.Winners.Add(winner);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            winnerId = winner.Id;
        }

        var paid = await payer.PayAsync(winnerId, ct).ConfigureAwait(false);
        var prize = Prize.Of(paid);

        if (paid.Status == WinnerEntity.UNSURE)
            return EventsReply.Fail(UNSURE, paid.PlayerName, paid.Error ?? "");

        if (paid.Status != WinnerEntity.PAID)
            return EventsReply.Fail(NOT_PAID, paid.PlayerName, paid.Error ?? "");

        await AnnounceAsync(request, hosted, paid, prize, ct).ConfigureAwait(false);
        await notices.WinnerAsync(paid, prize.Describe(), hosted?.Title, ct).ConfigureAwait(false);

        return EventsReply.Done(PAID, paid.PlayerName, prize.Describe());
    }

    /// <summary>The room's bubble of the winner, to everyone in it when the line was typed.</summary>
    private Task AnnounceAsync(
        AwardRequest request,
        HostedEventEntity? hosted,
        WinnerEntity winner,
        Prize prize,
        CancellationToken ct
    )
    {
        var config = Config;

        if (string.IsNullOrWhiteSpace(config.WinnerAnnouncement))
            return Task.CompletedTask;

        var message = config
            .WinnerAnnouncement.Replace("{winner}", winner.PlayerName, StringComparison.Ordinal)
            .Replace("{prize}", prize.Describe(), StringComparison.Ordinal)
            .Replace("{title}", hosted?.Title ?? "the event", StringComparison.Ordinal)
            .Replace("{host}", hosted?.HostName ?? request.ByName, StringComparison.Ordinal);
        var parameters = new Dictionary<string, string>
        {
            ["display"] = "BUBBLE",
            ["message"] = message,
        };

        if (!string.IsNullOrWhiteSpace(config.WinnerImage))
            parameters["image"] = config.WinnerImage.Trim();

        return hotel.NotifyAsync(
            [.. request.RoomPlayerIds.Append(request.WinnerId).Distinct()],
            config.WinnerType,
            parameters,
            ct
        );
    }

    /// <summary>What stops a prize set paying in this hotel now; null: nothing.</summary>
    public string? PrizeProblem(PrizeSetEntity set)
    {
        var config = Config;
        var currencies = Prize.ParseCurrencies(set.Currencies, out var error);

        if (error is not null)
            return error;

        var prize = Prize.Of(set);

        if (prize.IsEmpty)
            return "it pays nothing";

        foreach (var amount in currencies)
        {
            if (!hotel.CurrencyExists(amount.Currency))
                return $"there is no currency called {amount.Currency} (there are: {string.Join(", ", hotel.CurrencyNames())})";

            if (amount.Amount > config.MaxPrizeCurrency)
                return $"{amount.Amount} {amount.Currency} is over MaxPrizeCurrency ({config.MaxPrizeCurrency})";
        }

        if (prize.FurniName is { } furni)
        {
            if (!hotel.FurniExists(furni))
                return $"there is no furni called {furni}";

            if (prize.FurniCount < 1 || prize.FurniCount > config.MaxPrizeFurni)
                return $"the furni count must be 1 to {config.MaxPrizeFurni}";
        }

        return null;
    }

    private async Task<EventsReply?> RefusalAsync(
        EventsDbContext db,
        StartRequest request,
        CancellationToken ct
    )
    {
        var config = Config;
        var title = request.Title.Trim();

        if (title.Length == 0)
            return EventsReply.Fail(NO_TITLE);

        if (title.Length > Math.Min(config.MaxTitleLength, HostedEventEntity.MAX_TITLE))
            return EventsReply.Fail(
                TITLE_TOO_LONG,
                Count(Math.Min(config.MaxTitleLength, HostedEventEntity.MAX_TITLE))
            );

        var running = await db
            .Events.AsNoTracking()
            .FirstOrDefaultAsync(x => x.RoomId == request.RoomId && x.EndedAt == null, ct)
            .ConfigureAwait(false);

        if (running is not null)
            return EventsReply.Fail(RUNNING, running.Title, running.HostName);

        if (request.BypassCooldown)
            return null;

        var now = Now;

        if (config.HostCooldownMinutes > 0)
        {
            var since = now.AddMinutes(-config.HostCooldownMinutes);
            var last = await db
                .Events.Where(x => x.HostId == request.HostId && x.StartedAt > since)
                .OrderByDescending(x => x.StartedAt)
                .Select(x => (DateTime?)x.StartedAt)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (last is { } at)
                return EventsReply.Fail(
                    HOST_COOLDOWN,
                    MinutesLeft(at, config.HostCooldownMinutes, now)
                );
        }

        if (config.HotelCooldownMinutes > 0)
        {
            var since = now.AddMinutes(-config.HotelCooldownMinutes);
            var last = await db
                .Events.Where(x => x.StartedAt > since)
                .OrderByDescending(x => x.StartedAt)
                .Select(x => (DateTime?)x.StartedAt)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (last is { } at)
                return EventsReply.Fail(
                    HOTEL_COOLDOWN,
                    MinutesLeft(at, config.HotelCooldownMinutes, now)
                );
        }

        return null;
    }

    /// <summary>Whole minutes left, rounded up, so a wait is never said to be 0.</summary>
    private static string MinutesLeft(DateTime startedAt, int cooldown, DateTime now) =>
        Count((long)Math.Ceiling((startedAt.AddMinutes(cooldown) - now).TotalMinutes));

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static EventsDbContext Db(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<EventsDbContext>();

    private static string Count(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
