using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Turbo.Contracts.Plugins.Admin;
using TurboEvents.Database;
using TurboEvents.Hosting;
using TurboEvents.Prizes;

namespace TurboEvents;

/// <summary>
/// The Events pages of the admin panel: the events hosted (running and ended, with their winners;
/// a running one can be ended), the winners (a payout a restart broke off can be finished, an unsure
/// one marked settled), the leaderboard (this week, this month, all time) and the prize sets (add,
/// change, remove). The settings (cooldowns, the call's texts, the default prize set) are on the
/// panel's Settings page under <c>Turbo:Events</c>. Seeing them needs <c>plugin.events.view</c>;
/// changing anything <c>plugin.events.manage</c>. No one holds them until a hotel grants them.
/// </summary>
public sealed partial class EventsAdmin(
    IServiceScopeFactory scopes,
    EventService events,
    PrizePayer payer,
    TimeProvider time
) : IPluginAdmin
{
    public const int PAGE_SIZE = 25;

    public void Configure(IPluginAdminBuilder admin)
    {
        var view = admin.Node("view", "See the events hosted, their winners and the prize sets");
        var manage = admin.Node(
            "manage",
            "Change the prize sets, end events and settle winners' payouts"
        );

        admin.Map("GET", "stats", view, (_, ct) => StatsAsync(ct));
        admin.Map("GET", "events", view, EventsAsync);
        admin.Map("GET", "events/{id}", view, EventAsync);
        admin.Map("GET", "events/{id}/winners", view, EventWinnersAsync);
        admin.Map("POST", "events/{id}/end", manage, EndAsync);
        admin.Map("GET", "winners", view, WinnersAsync);
        admin.Map("GET", "winners/{id}", view, WinnerAsync);
        admin.Map("POST", "winners/{id}/pay", manage, FinishPayoutAsync);
        admin.Map("POST", "winners/{id}/settled", manage, SettleAsync);
        admin.Map("GET", "leaderboard", view, LeaderboardAsync);
        admin.Map("GET", "prizes", view, PrizesAsync);
        admin.Map("POST", "prizes", manage, AddPrizeAsync);
        admin.Map("GET", "prizes/{id}", view, PrizeAsync);
        admin.Map("POST", "prizes/{id}", manage, EditPrizeAsync);
        admin.Map("DELETE", "prizes/{id}", manage, RemovePrizeAsync);

        admin.Page(
            new PluginAdminPage(
                "events",
                "Events",
                view,
                [
                    new PluginAdminBlock("stats", Endpoint: "stats"),
                    new PluginAdminBlock(
                        "table",
                        Title: "Events hosted",
                        Endpoint: "events",
                        Columns:
                        [
                            new PluginAdminColumn("title", "Event"),
                            new PluginAdminColumn("host", "Host"),
                            new PluginAdminColumn("room", "Room"),
                            new PluginAdminColumn("startedAt", "Started", "time"),
                            new PluginAdminColumn("minutes", "Minutes", "number"),
                            new PluginAdminColumn("peak", "Most players", "number"),
                            new PluginAdminColumn("winners", "Winners", "number"),
                            new PluginAdminColumn("state", "State"),
                        ],
                        Filters:
                        [
                            new PluginAdminField(
                                "state",
                                "State",
                                "select",
                                Options: ["", "running", "ended"]
                            ),
                            new PluginAdminField("q", "Event or host"),
                        ],
                        ItemPage: "event"
                    ),
                    new PluginAdminBlock(
                        "text",
                        Title: "How it works",
                        Text: "Staff holding **events.command.event** start an event with **:event <title>** in its room: everyone online elsewhere gets the client's notification with a button to the room. **:winner <name> [prize set]** pays a prize set (Prize sets page) all or none, tells the room and records it; **:endevent** ends it.\n\n"
                            + "The cooldowns, the call's texts, the default prize set and where a winner must be are on the Settings page, under **Turbo:Events**. Each start, end and winner is a plugin notice: the Discord plugin posts them as **plugin:events:event.started**, **event.ended** and **event.winner** once routed."
                    ),
                ],
                "Events hosted in the hotel, and their winners."
            )
        );
        admin.Page(
            new PluginAdminPage(
                "event",
                "Event",
                view,
                [
                    new PluginAdminBlock(
                        "detail",
                        Endpoint: "events/{id}",
                        Columns:
                        [
                            new PluginAdminColumn("title", "Event"),
                            new PluginAdminColumn("host", "Host"),
                            new PluginAdminColumn("room", "Room"),
                            new PluginAdminColumn("roomId", "Room id", "number"),
                            new PluginAdminColumn("startedAt", "Started", "time"),
                            new PluginAdminColumn("endedAt", "Ended", "time"),
                            new PluginAdminColumn("ended", "How it ended"),
                            new PluginAdminColumn("called", "Players called", "number"),
                            new PluginAdminColumn("peak", "Most players", "number"),
                        ]
                    ),
                    new PluginAdminBlock(
                        "actions",
                        Actions:
                        [
                            new PluginAdminAction(
                                "End it",
                                "POST",
                                "events/{id}/end",
                                "End this event now?",
                                Node: manage,
                                When: "running=true"
                            ),
                        ]
                    ),
                    new PluginAdminBlock(
                        "table",
                        Title: "Winners",
                        Endpoint: "events/{id}/winners",
                        Columns: WinnerColumns,
                        ItemPage: "winner"
                    ),
                ],
                Nav: false
            )
        );
        admin.Page(
            new PluginAdminPage(
                "winners",
                "Event winners",
                view,
                [
                    new PluginAdminBlock(
                        "table",
                        Endpoint: "winners",
                        Columns: WinnerColumns,
                        Filters:
                        [
                            new PluginAdminField(
                                "status",
                                "Payout",
                                "select",
                                Options:
                                [
                                    "",
                                    WinnerEntity.PAID,
                                    WinnerEntity.PENDING,
                                    WinnerEntity.UNSURE,
                                    WinnerEntity.FAILED,
                                ]
                            ),
                            new PluginAdminField("q", "Player"),
                        ],
                        ItemPage: "winner"
                    ),
                ],
                "Every winner named, what they were paid, and payouts to check."
            )
        );
        admin.Page(
            new PluginAdminPage(
                "winner",
                "Event winner",
                view,
                [
                    new PluginAdminBlock(
                        "detail",
                        Endpoint: "winners/{id}",
                        Columns:
                        [
                            new PluginAdminColumn("player", "Winner"),
                            new PluginAdminColumn("playerId", "Player id", "number"),
                            new PluginAdminColumn("event", "Event"),
                            new PluginAdminColumn("prizeName", "Prize set"),
                            new PluginAdminColumn("prize", "Prize"),
                            new PluginAdminColumn("by", "Named by"),
                            new PluginAdminColumn("status", "Payout", "badge"),
                            new PluginAdminColumn("paid", "What was given"),
                            new PluginAdminColumn("createdAt", "Named", "time"),
                            new PluginAdminColumn("paidAt", "Paid", "time"),
                            new PluginAdminColumn("error", "Problem"),
                        ]
                    ),
                    new PluginAdminBlock(
                        "actions",
                        Actions:
                        [
                            new PluginAdminAction(
                                "Finish the payout",
                                "POST",
                                "winners/{id}/pay",
                                "Pay what is left of this prize? What was paid is not paid again.",
                                Node: manage,
                                When: "status=pending"
                            ),
                            new PluginAdminAction(
                                "Mark settled",
                                "POST",
                                "winners/{id}/settled",
                                "Checked the winner's inventory and settled it by hand?",
                                Node: manage,
                                When: "status=unsure"
                            ),
                        ]
                    ),
                ],
                Nav: false
            )
        );
        admin.Page(
            new PluginAdminPage(
                "leaderboard",
                "Event leaderboard",
                view,
                [
                    new PluginAdminBlock(
                        "table",
                        Endpoint: "leaderboard",
                        Columns:
                        [
                            new PluginAdminColumn("rank", "#", "number"),
                            new PluginAdminColumn("player", "Player"),
                            new PluginAdminColumn("wins", "Wins", "number"),
                            new PluginAdminColumn("lastWin", "Last win", "time"),
                        ],
                        Filters:
                        [
                            new PluginAdminField(
                                "period",
                                "Period",
                                "select",
                                Options: ["week", "month", "all"]
                            ),
                        ]
                    ),
                ],
                "Who won the most events: the last 7 days, the last 30 days, or ever. Paid wins only."
            )
        );
        admin.Page(
            new PluginAdminPage(
                "prizes",
                "Event prize sets",
                view,
                [
                    new PluginAdminBlock(
                        "table",
                        Endpoint: "prizes",
                        Columns:
                        [
                            new PluginAdminColumn("name", "Name"),
                            new PluginAdminColumn("prize", "Pays"),
                            new PluginAdminColumn("isDefault", "Default", "bool"),
                            new PluginAdminColumn("problem", "Problem"),
                            new PluginAdminColumn("updatedAt", "Changed", "time"),
                        ],
                        ItemPage: "prize"
                    ),
                    new PluginAdminBlock(
                        "form",
                        Title: "Add a prize set",
                        Text: "Any of currencies, a badge and furni. :winner <name> <set> pays it; the default set (Settings, Turbo:Events:DefaultPrizeSet) when the line names none.",
                        Fields: PrizeFields(true),
                        Submit: new PluginAdminAction("Add", "POST", "prizes", Node: manage)
                    ),
                ],
                "What :winner pays."
            )
        );
        admin.Page(
            new PluginAdminPage(
                "prize",
                "Event prize set",
                view,
                [
                    new PluginAdminBlock(
                        "detail",
                        Endpoint: "prizes/{id}",
                        Columns:
                        [
                            new PluginAdminColumn("name", "Name"),
                            new PluginAdminColumn("prize", "Pays"),
                            new PluginAdminColumn("currencies", "Currencies"),
                            new PluginAdminColumn("badge", "Badge"),
                            new PluginAdminColumn("furni", "Furni"),
                            new PluginAdminColumn("furniCount", "Furni count", "number"),
                            new PluginAdminColumn("isDefault", "Default", "bool"),
                            new PluginAdminColumn("problem", "Problem"),
                            new PluginAdminColumn("wins", "Paid out", "number"),
                        ]
                    ),
                    new PluginAdminBlock(
                        "actions",
                        Actions:
                        [
                            new PluginAdminAction(
                                "Remove",
                                "DELETE",
                                "prizes/{id}",
                                "Remove this prize set? Winners keep what it paid them.",
                                Danger: true,
                                Node: manage,
                                Then: "back"
                            ),
                        ]
                    ),
                    new PluginAdminBlock(
                        "form",
                        Title: "Change",
                        Text: "Every field is saved as it is sent: an empty one pays none of that.",
                        Fields: PrizeFields(false),
                        Submit: new PluginAdminAction("Save", "POST", "prizes/{id}", Node: manage)
                    ),
                ],
                Nav: false
            )
        );
    }

    private static readonly PluginAdminColumn[] WinnerColumns =
    [
        new("player", "Winner"),
        new("prize", "Prize"),
        new("event", "Event"),
        new("by", "Named by"),
        new("status", "Payout", "badge"),
        new("createdAt", "When", "time"),
    ];

    private static List<PluginAdminField> PrizeFields(bool adding)
    {
        var fields = new List<PluginAdminField>();

        if (adding)
            fields.Add(
                new PluginAdminField(
                    "name",
                    "Name",
                    Required: true,
                    Pattern: NAME_PATTERN,
                    Hint: "Typed after :winner <player>, e.g. small"
                )
            );

        fields.Add(
            new PluginAdminField(
                "currencies",
                "Currencies",
                Hint: "The hotel's currency names and amounts: credits:100, duckets:50"
            )
        );
        fields.Add(new PluginAdminField("badge", "Badge code", Hint: "e.g. ACH_Event1"));
        fields.Add(
            new PluginAdminField("furni", "Furni", Hint: "A furni definition's name, as :giveitem")
        );
        fields.Add(new PluginAdminField("furniCount", "Furni count", "number", Min: 0, Max: 100));

        return fields;
    }

    public const string NAME_PATTERN = "[A-Za-z0-9_.-]{1,32}";

    [GeneratedRegex("^" + NAME_PATTERN + "$")]
    private static partial Regex NameRegex();

    private async Task<T> WithDbAsync<T>(Func<EventsDbContext, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();

        return await work(scope.ServiceProvider.GetRequiredService<EventsDbContext>())
            .ConfigureAwait(false);
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private Task<PluginAdminResult> StatsAsync(CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            var week = Now.AddDays(-7);
            var running = await db
                .Events.CountAsync(x => x.EndedAt == null, ct)
                .ConfigureAwait(false);
            var hosted = await db
                .Events.CountAsync(x => x.StartedAt >= week, ct)
                .ConfigureAwait(false);
            var winners = await db
                .Winners.CountAsync(x => x.Status == WinnerEntity.PAID && x.CreatedAt >= week, ct)
                .ConfigureAwait(false);
            var check = await db
                .Winners.CountAsync(
                    x => x.Status == WinnerEntity.UNSURE || x.Status == WinnerEntity.PENDING,
                    ct
                )
                .ConfigureAwait(false);

            return PluginAdminResult.Ok(
                new object[]
                {
                    new { label = "Running now", value = running },
                    new { label = "Events (7 days)", value = hosted },
                    new { label = "Winners paid (7 days)", value = winners },
                    new { label = "Payouts to check", value = check },
                }
            );
        });

    // Events

    private Task<PluginAdminResult> EventsAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            var query = db.Events.AsNoTracking();

            switch (request.Query.GetValueOrDefault("state"))
            {
                case "running":
                    query = query.Where(x => x.EndedAt == null);
                    break;
                case "ended":
                    query = query.Where(x => x.EndedAt != null);
                    break;
            }

            if (request.Query.GetValueOrDefault("q")?.Trim() is { Length: > 0 } q)
                query = query.Where(x => x.Title.Contains(q) || x.HostName.Contains(q));

            var total = await query.CountAsync(ct).ConfigureAwait(false);
            var page = Page(request);
            var rows = await query
                .OrderByDescending(x => x.Id)
                .Skip((page - 1) * PAGE_SIZE)
                .Take(PAGE_SIZE)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var ids = rows.Select(x => (long?)x.Id).ToList();
            var winners = await db
                .Winners.Where(x => ids.Contains(x.EventId) && x.Status == WinnerEntity.PAID)
                .GroupBy(x => x.EventId)
                .Select(x => new { Id = x.Key, Count = x.Count() })
                .ToDictionaryAsync(x => x.Id!.Value, x => x.Count, ct)
                .ConfigureAwait(false);

            return PluginAdminResult.Ok(
                new
                {
                    items = rows.Select(x => new
                    {
                        id = x.Id,
                        title = x.Title,
                        host = x.HostName,
                        room = x.RoomName,
                        startedAt = x.StartedAt,
                        minutes = Minutes(x),
                        peak = x.PeakAttendees,
                        winners = winners.GetValueOrDefault(x.Id),
                        state = x.EndedAt is null ? "running" : x.EndReason ?? "ended",
                    }),
                    total,
                    pageSize = PAGE_SIZE,
                }
            );
        });

    private Task<PluginAdminResult> EventAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            if (await FindAsync(db.Events, request, ct).ConfigureAwait(false) is not { } x)
                return PluginAdminResult.NotFound("No such event.");

            return PluginAdminResult.Ok(
                new
                {
                    id = x.Id,
                    title = x.Title,
                    host = x.HostName,
                    room = x.RoomName,
                    roomId = x.RoomId,
                    startedAt = x.StartedAt,
                    endedAt = x.EndedAt,
                    ended = x.EndedAt is null ? "running"
                    : x.EndReason == HostedEventEntity.EXPIRED ? "ran past the longest an event may"
                    : $"by {x.EndedBy}",
                    called = x.Called,
                    peak = x.PeakAttendees,
                    running = x.EndedAt is null,
                }
            );
        });

    private Task<PluginAdminResult> EventWinnersAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            if (!long.TryParse(request.Route.GetValueOrDefault("id"), out var id))
                return PluginAdminResult.NotFound("No such event.");

            var rows = await db
                .Winners.AsNoTracking()
                .Where(x => x.EventId == id)
                .OrderBy(x => x.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var title = await db
                .Events.Where(x => x.Id == id)
                .Select(x => x.Title)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            return PluginAdminResult.Ok(
                new
                {
                    items = rows.Select(x => WinnerRow(x, title)),
                    total = rows.Count,
                    pageSize = Math.Max(rows.Count, PAGE_SIZE),
                }
            );
        });

    private async Task<PluginAdminResult> EndAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        long.TryParse(request.Route.GetValueOrDefault("id"), out var id)
        && await events
            .EndByIdAsync(id, $"panel (player {request.PlayerId})", ct)
            .ConfigureAwait(false)
            ? PluginAdminResult.Ok(new { id, ended = true })
            : PluginAdminResult.BadRequest("That event is not running.");

    // Winners

    private Task<PluginAdminResult> WinnersAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            var query = db.Winners.AsNoTracking();

            if (request.Query.GetValueOrDefault("status") is { Length: > 0 } status)
                query = query.Where(x => x.Status == status);

            if (request.Query.GetValueOrDefault("q")?.Trim() is { Length: > 0 } q)
                query = query.Where(x => x.PlayerName.Contains(q));

            var total = await query.CountAsync(ct).ConfigureAwait(false);
            var page = Page(request);
            var rows = await query
                .OrderByDescending(x => x.Id)
                .Skip((page - 1) * PAGE_SIZE)
                .Take(PAGE_SIZE)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var titles = await TitlesAsync(db, rows, ct).ConfigureAwait(false);

            return PluginAdminResult.Ok(
                new
                {
                    items = rows.Select(x =>
                        WinnerRow(x, x.EventId is { } e ? titles.GetValueOrDefault(e) : null)
                    ),
                    total,
                    pageSize = PAGE_SIZE,
                }
            );
        });

    private static object WinnerRow(WinnerEntity x, string? title) =>
        new
        {
            id = x.Id,
            player = x.PlayerName,
            prize = $"{x.PrizeName}: {Prize.Of(x).Describe()}",
            @event = title ?? "-",
            by = x.AwardedBy,
            status = x.Status,
            createdAt = x.CreatedAt,
        };

    private static async Task<Dictionary<long, string>> TitlesAsync(
        EventsDbContext db,
        IEnumerable<WinnerEntity> rows,
        CancellationToken ct
    )
    {
        var ids = rows.Where(x => x.EventId != null)
            .Select(x => x.EventId!.Value)
            .Distinct()
            .ToList();

        return await db
            .Events.Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Title, ct)
            .ConfigureAwait(false);
    }

    private Task<PluginAdminResult> WinnerAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            if (await FindAsync(db.Winners, request, ct).ConfigureAwait(false) is not { } x)
                return PluginAdminResult.NotFound("No such winner.");

            var title = x.EventId is { } e
                ? await db
                    .Events.Where(h => h.Id == e)
                    .Select(h => h.Title)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false)
                : null;
            var prize = Prize.Of(x);
            var given = new List<string>();

            if (x.CurrenciesPaid && !prize.Currencies.IsDefaultOrEmpty)
                given.Add(Prize.FormatCurrencies(prize.Currencies));

            if (x.BadgeDone && prize.BadgeCode is { } badge)
                given.Add(x.BadgeGiven ? $"badge {badge}" : $"badge {badge} (had it)");

            if (x.FurniGiven > 0)
                given.Add($"{x.FurniGiven} of {x.FurniCount} {x.FurniName}");

            return PluginAdminResult.Ok(
                new
                {
                    id = x.Id,
                    player = x.PlayerName,
                    playerId = x.PlayerId,
                    @event = title ?? "-",
                    prizeName = x.PrizeName,
                    prize = prize.Describe(),
                    by = x.AwardedBy,
                    status = x.Status,
                    paid = x.Status == WinnerEntity.FAILED ? "nothing (taken back)"
                    : given.Count == 0 ? "nothing yet"
                    : string.Join(", ", given),
                    createdAt = x.CreatedAt,
                    paidAt = x.PaidAt,
                    error = x.Error,
                }
            );
        });

    private async Task<PluginAdminResult> FinishPayoutAsync(
        PluginAdminRequest request,
        CancellationToken ct
    )
    {
        if (!long.TryParse(request.Route.GetValueOrDefault("id"), out var id))
            return PluginAdminResult.NotFound("No such winner.");

        var exists = await WithDbAsync(db =>
                db.Winners.Where(x => x.Id == id).Select(x => x.Status).FirstOrDefaultAsync(ct)
            )
            .ConfigureAwait(false);

        if (exists is null)
            return PluginAdminResult.NotFound("No such winner.");

        if (exists != WinnerEntity.PENDING)
            return PluginAdminResult.BadRequest("Only a pending payout is finished.");

        var paid = await payer.PayAsync(id, ct).ConfigureAwait(false);

        return PluginAdminResult.Ok(
            new
            {
                id,
                status = paid.Status,
                error = paid.Error,
            }
        );
    }

    private Task<PluginAdminResult> SettleAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            if (
                await FindAsync(db.Winners, request, ct, tracked: true).ConfigureAwait(false)
                is not { } x
            )
                return PluginAdminResult.NotFound("No such winner.");

            if (x.Status != WinnerEntity.UNSURE)
                return PluginAdminResult.BadRequest("Only an unsure payout is settled.");

            x.Status = WinnerEntity.PAID;
            x.PaidAt ??= Now;
            x.Error = Cut(
                $"Settled by hand on the panel (player {request.PlayerId}). {x.Error}",
                WinnerEntity.MAX_ERROR
            );
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return PluginAdminResult.Ok(new { id = x.Id, status = x.Status });
        });

    // Leaderboard

    private Task<PluginAdminResult> LeaderboardAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            var rows = await TopAsync(
                    db,
                    request.Query.GetValueOrDefault("period") ?? "week",
                    Now,
                    ct
                )
                .ConfigureAwait(false);
            var page = Page(request);

            return PluginAdminResult.Ok(
                new
                {
                    items = rows.Skip((page - 1) * PAGE_SIZE)
                        .Take(PAGE_SIZE)
                        .Select(
                            (x, i) =>
                                new
                                {
                                    id = x.PlayerId,
                                    rank = (page - 1) * PAGE_SIZE + i + 1,
                                    player = x.Name,
                                    wins = x.Wins,
                                    lastWin = x.LastWin,
                                }
                        ),
                    total = rows.Count,
                    pageSize = PAGE_SIZE,
                }
            );
        });

    /// <summary>One leaderboard row: a player, their paid wins in the period and their last.</summary>
    internal sealed record LeaderRow(int PlayerId, string Name, int Wins, DateTime LastWin);

    /// <summary>
    /// Paid wins by player since the period's start (<c>week</c>: 7 days, <c>month</c>: 30 days,
    /// <c>all</c>), most first, then whoever got there first.
    /// </summary>
    internal static async Task<List<LeaderRow>> TopAsync(
        EventsDbContext db,
        string period,
        DateTime now,
        CancellationToken ct
    )
    {
        var since = period switch
        {
            "all" => DateTime.MinValue,
            "month" => now.AddDays(-30),
            _ => now.AddDays(-7),
        };
        var rows = await db
            .Winners.Where(x => x.Status == WinnerEntity.PAID && x.CreatedAt >= since)
            .GroupBy(x => x.PlayerId)
            .Select(x => new
            {
                PlayerId = x.Key,
                Wins = x.Count(),
                LastWin = x.Max(w => w.CreatedAt),
                LastId = x.Max(w => w.Id),
            })
            .OrderByDescending(x => x.Wins)
            .ThenBy(x => x.LastWin)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        // The name they won under last: a player renamed since shows as they are now known.
        var lastIds = rows.Select(x => x.LastId).ToList();
        var names = await db
            .Winners.Where(x => lastIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.PlayerName, ct)
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(x => new LeaderRow(
                x.PlayerId,
                names.GetValueOrDefault(x.LastId, $"#{x.PlayerId}"),
                x.Wins,
                x.LastWin
            )),
        ];
    }

    // Prize sets

    private Task<PluginAdminResult> PrizesAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            var rows = await db
                .PrizeSets.AsNoTracking()
                .OrderBy(x => x.Name)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var fallback = DefaultSet;

            return PluginAdminResult.Ok(
                new
                {
                    items = rows.Select(x => new
                    {
                        id = x.Id,
                        name = x.Name,
                        prize = Prize.Of(x).Describe(),
                        isDefault = string.Equals(
                            x.Name,
                            fallback,
                            StringComparison.OrdinalIgnoreCase
                        ),
                        problem = events.PrizeProblem(x) ?? "",
                        updatedAt = x.UpdatedAt,
                    }),
                    total = rows.Count,
                    pageSize = Math.Max(rows.Count, PAGE_SIZE),
                }
            );
        });

    private Task<PluginAdminResult> PrizeAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            if (await FindAsync(db.PrizeSets, request, ct).ConfigureAwait(false) is not { } x)
                return PluginAdminResult.NotFound("No such prize set.");

            var wins = await db
                .Winners.CountAsync(w => w.PrizeName == x.Name && w.Status == WinnerEntity.PAID, ct)
                .ConfigureAwait(false);

            return PluginAdminResult.Ok(
                new
                {
                    id = x.Id,
                    name = x.Name,
                    prize = Prize.Of(x).Describe(),
                    currencies = x.Currencies,
                    badge = x.BadgeCode ?? "",
                    furni = x.FurniName ?? "",
                    furniCount = x.FurniCount,
                    isDefault = string.Equals(
                        x.Name,
                        DefaultSet,
                        StringComparison.OrdinalIgnoreCase
                    ),
                    problem = events.PrizeProblem(x) ?? "",
                    wins,
                }
            );
        });

    private Task<PluginAdminResult> AddPrizeAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            var name = Text(request, "name")?.Trim() ?? "";

            if (!NameRegex().IsMatch(name))
                return PluginAdminResult.BadRequest(
                    "A name is 1 to 32 letters, digits, dots, dashes and underscores."
                );

            if (await db.PrizeSets.AnyAsync(x => x.Name == name, ct).ConfigureAwait(false))
                return PluginAdminResult.BadRequest($"There is a prize set called {name} already.");

            var set = new PrizeSetEntity
            {
                Name = name,
                CreatedAt = Now,
                UpdatedAt = Now,
            };

            if (Apply(set, request) is { } problem)
                return PluginAdminResult.BadRequest(problem);

            db.PrizeSets.Add(set);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return PluginAdminResult.Ok(new { id = set.Id, name = set.Name });
        });

    private Task<PluginAdminResult> EditPrizeAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            if (
                await FindAsync(db.PrizeSets, request, ct, tracked: true).ConfigureAwait(false)
                is not { } set
            )
                return PluginAdminResult.NotFound("No such prize set.");

            if (Apply(set, request) is { } problem)
                return PluginAdminResult.BadRequest(problem);

            set.UpdatedAt = Now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return PluginAdminResult.Ok(new { id = set.Id, name = set.Name });
        });

    private Task<PluginAdminResult> RemovePrizeAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            if (
                await FindAsync(db.PrizeSets, request, ct, tracked: true).ConfigureAwait(false)
                is not { } set
            )
                return PluginAdminResult.NotFound("No such prize set.");

            db.PrizeSets.Remove(set);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return PluginAdminResult.NoContent();
        });

    /// <summary>The form's fields onto <paramref name="set"/>; what stops it paying, or null.</summary>
    private string? Apply(PrizeSetEntity set, PluginAdminRequest request)
    {
        var currencies = Prize.ParseCurrencies(Text(request, "currencies"), out var error);

        if (error is not null)
            return error;

        var badge = Text(request, "badge")?.Trim();
        var furni = Text(request, "furni")?.Trim();
        var count = Number(request, "furniCount") ?? (string.IsNullOrEmpty(furni) ? 0 : 1);

        if (badge?.Length > PrizeSetEntity.MAX_BADGE)
            return $"A badge code is at most {PrizeSetEntity.MAX_BADGE} characters.";

        if (furni?.Length > PrizeSetEntity.MAX_FURNI)
            return $"A furni name is at most {PrizeSetEntity.MAX_FURNI} characters.";

        set.Currencies = Prize.FormatCurrencies(currencies);
        set.BadgeCode = string.IsNullOrEmpty(badge) ? null : badge;
        set.FurniName = string.IsNullOrEmpty(furni) ? null : furni;
        set.FurniCount = set.FurniName is null ? 0 : (int)Math.Clamp(count, 0, int.MaxValue);

        if (set.Currencies.Length > PrizeSetEntity.MAX_CURRENCIES)
            return "Too many currencies.";

        return events.PrizeProblem(set);
    }

    private string DefaultSet => events.DefaultPrizeSet;

    private static async Task<T?> FindAsync<T>(
        DbSet<T> set,
        PluginAdminRequest request,
        CancellationToken ct,
        bool tracked = false
    )
        where T : class
    {
        if (!long.TryParse(request.Route.GetValueOrDefault("id"), out var id))
            return null;

        var entity = await set.FindAsync([KeyOf<T>(id)], ct).ConfigureAwait(false);

        if (entity is not null && !tracked)
            set.Entry(entity).State = EntityState.Detached;

        return entity;
    }

    private static object KeyOf<T>(long id) =>
        typeof(T) == typeof(PrizeSetEntity) ? (object)(int)id : id;

    private static int Minutes(HostedEventEntity x) =>
        x.EndedAt is { } end ? (int)Math.Round((end - x.StartedAt).TotalMinutes) : 0;

    private static int Page(PluginAdminRequest request) =>
        int.TryParse(request.Query.GetValueOrDefault("page"), out var page) && page > 0 ? page : 1;

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? Text(PluginAdminRequest request, string name) =>
        request.Body is { ValueKind: JsonValueKind.Object } body
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Number(PluginAdminRequest request, string name) =>
        request.Body is { ValueKind: JsonValueKind.Object } body
        && body.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var n) => n,
                JsonValueKind.String
                    when long.TryParse(
                        value.GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var n
                    ) => n,
                _ => null,
            }
            : null;
}
