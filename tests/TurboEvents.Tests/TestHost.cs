using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Turbo.Contracts.Plugins.Admin;
using Turbo.Primitives.Plugins.Events;
using Turbo.Testing;
using TurboEvents.Database;
using TurboEvents.Hosting;
using TurboEvents.Prizes;

namespace TurboEvents.Tests;

/// <summary>
/// A hotel as the plugin sees it: who is online, rooms, the notifications sent, and wallets,
/// badges and inventories that keep what was granted. The wallet pays an operation and seq once,
/// as core's does, and takes back a credit once.
/// </summary>
internal sealed class FakeHotel : IEventsHotel
{
    public HashSet<int> Online { get; } = [];

    public Dictionary<int, RoomInfo> Rooms { get; } = [];

    public List<(int[] Players, string Type, Dictionary<string, string> Parameters)> Sent { get; } =
    [];

    public HashSet<string> Currencies { get; } = ["credits", "duckets", "diamonds"];

    public HashSet<string> Furni { get; } = ["throne", "rare_dragonlamp*1"];

    public Dictionary<(int Player, string Currency), int> Balances { get; } = [];

    public HashSet<(int Player, string Operation, int Seq)> Credited { get; } = [];

    public HashSet<(int Player, string Operation, int Seq)> TakenBack { get; } = [];

    public Dictionary<int, HashSet<string>> Badges { get; } = [];

    public List<(int Player, string Furni)> Inventory { get; } = [];

    /// <summary>A currency whose credit fails (as a wallet refusing it would).</summary>
    public string? FailCurrency { get; set; }

    /// <summary>The furni grant that fails, counted from 1; 0: none.</summary>
    public int FailFurniAt { get; set; }

    /// <summary>The furni grant that throws (a server stopping), counted from 1; 0: none.</summary>
    public int ThrowFurniAt { get; set; }

    public bool FailBadge { get; set; }

    private int _furniCalls;

    public IReadOnlyCollection<int> OnlinePlayerIds() => [.. Online];

    public Task<RoomInfo?> RoomAsync(int roomId, CancellationToken ct) =>
        Task.FromResult(Rooms.TryGetValue(roomId, out var room) ? room : (RoomInfo?)null);

    public Task NotifyAsync(
        IReadOnlyCollection<int> playerIds,
        string type,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct
    )
    {
        Sent.Add(([.. playerIds], type, parameters.ToDictionary()));

        return Task.CompletedTask;
    }

    public bool CurrencyExists(string name) => Currencies.Contains(name);

    public IReadOnlyCollection<string> CurrencyNames() => [.. Currencies];

    public bool FurniExists(string name) => Furni.Contains(name);

    public int Balance(int player, string currency) =>
        Balances.GetValueOrDefault((player, currency));

    public Task<bool> CreditAsync(
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
        if (currency == FailCurrency || !Currencies.Contains(currency))
            return Task.FromResult(false);

        if (Credited.Add((playerId, operationId, seq)))
            Balances[(playerId, currency)] = Balance(playerId, currency) + amount;

        return Task.FromResult(true);
    }

    public Task<bool> TakeBackAsync(
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
        if (TakenBack.Add((playerId, operationId, seq)))
            Balances[(playerId, currency)] = Balance(playerId, currency) - amount;

        return Task.FromResult(true);
    }

    public Task<bool> HasBadgeAsync(int playerId, string code, CancellationToken ct) =>
        Task.FromResult(Badges.TryGetValue(playerId, out var held) && held.Contains(code));

    public Task<bool> GiveBadgeAsync(int playerId, string code, CancellationToken ct)
    {
        if (FailBadge)
            return Task.FromResult(false);

        if (!Badges.TryGetValue(playerId, out var held))
            Badges[playerId] = held = [];

        return Task.FromResult(held.Add(code));
    }

    public Task<bool> RemoveBadgeAsync(int playerId, string code, CancellationToken ct) =>
        Task.FromResult(Badges.TryGetValue(playerId, out var held) && held.Remove(code));

    public Task<bool> GrantFurniAsync(
        int playerId,
        string furniName,
        string operationId,
        int seq,
        int actorId,
        long winnerId,
        CancellationToken ct
    )
    {
        _furniCalls++;

        if (_furniCalls == ThrowFurniAt)
        {
            // Granted, then the call broke before the plugin heard.
            Inventory.Add((playerId, furniName));

            throw new InvalidOperationException("The server is stopping");
        }

        if (_furniCalls == FailFurniAt || !Furni.Contains(furniName))
            return Task.FromResult(false);

        Inventory.Add((playerId, furniName));

        return Task.FromResult(true);
    }
}

/// <summary>
/// The plugin loaded in a test hotel (turbo-cloud docs/plugin-testing.md): compiled from its
/// sources as a hotel compiles it, its tables in an in-memory SQLite database, its settings the
/// hotel's, its clock the hotel's, which the test moves. The hotel it reaches through
/// <see cref="IEventsHotel"/> is <see cref="FakeHotel"/>. Players: 1 Alice (staff), 2 Bob,
/// 3 Carol, 4 Dave; room 10 Lobby and room 20 Pool.
/// </summary>
internal sealed class TestHost : IDisposable
{
    public static readonly CancellationToken CT = CancellationToken.None;

    public TestHost(EventsConfig? config = null)
    {
        // The plugin's own room count runs while it is loaded: not during a test.
        Kit.Settings.Set($"{EventsConfig.SECTION_NAME}:AttendanceSampleSeconds", 3600);
        Configure(config ?? new EventsConfig());
        Kit.Replace<IEventsHotel>(Hotel);
        Plugin = Kit.LoadAsync<EventsPlugin>(ct: CT).GetAwaiter().GetResult();

        Hotel.Online.UnionWith([1, 2, 3, 4]);
        Hotel.Rooms[10] = new RoomInfo("Lobby", 3);
        Hotel.Rooms[20] = new RoomInfo("Pool", 1);
    }

    public TestHotel Kit { get; } = TestHotel.Create(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));

    public TestPlugin Plugin { get; }

    public ManualClock Time => Kit.Clock;

    public IOptionsMonitor<EventsConfig> Settings => Plugin.Service<IOptionsMonitor<EventsConfig>>();

    public IServiceScopeFactory Scopes => Plugin.Service<IServiceScopeFactory>();

    public FakeHotel Hotel { get; } = new();

    /// <summary>The notices the plugin published, in order.</summary>
    public IReadOnlyList<PluginNoticeEvent> Published => Kit.Events.Of<PluginNoticeEvent>();

    public Notices Notices => Plugin.Service<Notices>();

    public PrizePayer Payer => Plugin.Service<PrizePayer>();

    public EventService Events => Plugin.Service<EventService>();

    public DateTime Now => Time.GetUtcNow().UtcDateTime;

    /// <summary>The hotel's settings for the plugin become <paramref name="config"/>'s, as a save in the panel does.</summary>
    public void Configure(EventsConfig config)
    {
        foreach (var property in typeof(EventsConfig).GetProperties())
            Kit.Settings.Set($"{EventsConfig.SECTION_NAME}:{property.Name}", property.GetValue(config));
    }

    public T Query<T>(Func<EventsDbContext, T> query)
    {
        using var db = Plugin.Db<EventsDbContext>();

        return query(db);
    }

    public void Change(Action<EventsDbContext> change)
    {
        using var db = Plugin.Db<EventsDbContext>();

        change(db);
        db.SaveChanges();
    }

    public void Prize(
        string name,
        string currencies = "",
        string? badge = null,
        string? furni = null,
        int count = 0
    ) =>
        Change(db =>
            db.PrizeSets.Add(
                new PrizeSetEntity
                {
                    Name = name,
                    Currencies = currencies,
                    BadgeCode = badge,
                    FurniName = furni,
                    FurniCount = count,
                    CreatedAt = Now,
                    UpdatedAt = Now,
                }
            )
        );

    public Task<EventsReply> StartAsync(
        int host = 1,
        int room = 10,
        string title = "Falling Furni",
        bool bypass = false,
        params int[] called
    ) =>
        Events.StartAsync(
            new StartRequest(host, Name(host), room, title, bypass),
            called.Length == 0 ? [2, 3, 4] : called,
            CT
        );

    public Task<EventsReply> AwardAsync(
        int winner,
        string? prize = null,
        int by = 1,
        int room = 10,
        int[]? inRoom = null
    ) =>
        Events.AwardAsync(
            new AwardRequest(by, Name(by), room, inRoom ?? [1, 2, 3], winner, Name(winner), prize),
            CT
        );

    public static string Name(int player) =>
        player switch
        {
            1 => "Alice",
            2 => "Bob",
            3 => "Carol",
            4 => "Dave",
            _ => $"#{player}",
        };

    public Admin Admin() => new(Kit);

    public void Dispose() => Kit.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

/// <summary>The admin endpoints, called as the panel would: <c>GET prizes</c>, <c>DELETE prizes/{id}</c>.</summary>
internal sealed class Admin(TestHotel kit)
{
    // The panel's staff member: player 9, beside the hotel's players 1-4.
    private readonly TestPlayer _staff = Staff(kit);

    public IReadOnlyList<string> Nodes => kit.Admin.Nodes;

    public IReadOnlyList<PluginAdminPage> Pages => kit.Admin.Pages;

    public Task<PluginAdminResult> CallAsync(
        string route,
        long id = 0,
        string query = "",
        string? body = null
    )
    {
        var method = route.Split(' ')[0];
        var path = route[(method.Length + 1)..].Replace("{id}", id.ToString());

        return kit.Admin.CallAsync(
            method,
            $"events/{path}{(query.Length > 0 ? "?" + query : "")}",
            body is null ? null : JsonDocument.Parse(body).RootElement,
            _staff,
            TestHost.CT
        );
    }

    private static TestPlayer Staff(TestHotel kit)
    {
        kit.Fake.Players.NextId = 9;

        return kit.Player("Panel").AsStaff();
    }

    public static string Json(PluginAdminResult result) => JsonSerializer.Serialize(result.Value);
}
