using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Turbo.Contracts.Plugins.Admin;
using Turbo.Database.Delegates;
using Turbo.Primitives.Plugins.Events;
using TurboEvents.Database;
using TurboEvents.Hosting;
using TurboEvents.Prizes;

namespace TurboEvents.Tests;

internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class Settings(EventsConfig value) : IOptionsMonitor<EventsConfig>
{
    public EventsConfig Value { get; set; } = value;

    public EventsConfig CurrentValue => Value;

    public EventsConfig Get(string? name) => Value;

    public IDisposable? OnChange(Action<EventsConfig, string?> listener) => null;
}

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
/// The plugin's services over an in-memory SQLite database (the real SQL: unique keys,
/// transactions), a fake hotel and a clock the test moves. Players: 1 Alice (staff), 2 Bob,
/// 3 Carol, 4 Dave; room 10 Lobby and room 20 Pool.
/// </summary>
internal sealed class TestHost : IDisposable
{
    public static readonly CancellationToken CT = CancellationToken.None;

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public TestHost(EventsConfig? config = null)
    {
        _connection.Open();
        Settings = new Settings(config ?? new EventsConfig());

        var services = new ServiceCollection();

        services.AddSingleton<TablePrefixProvider>(() => "events_");
        services.AddDbContext<EventsDbContext>(o => o.UseSqlite(_connection));
        Services = services.BuildServiceProvider();

        using (var scope = Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<EventsDbContext>().Database.EnsureCreated();

        Scopes = Services.GetRequiredService<IServiceScopeFactory>();
        Notices = new Notices(NullLogger<Notices>.Instance)
        {
            Publisher = (notice, _) =>
            {
                Published.Add(notice);

                return Task.CompletedTask;
            },
        };
        Payer = new PrizePayer(Scopes, Hotel, Time, NullLogger<PrizePayer>.Instance);
        Events = new EventService(Scopes, Hotel, Settings, Payer, Notices, Time);
        AdminService = new EventsAdmin(Scopes, Events, Payer, Time);

        Hotel.Online.UnionWith([1, 2, 3, 4]);
        Hotel.Rooms[10] = new RoomInfo("Lobby", 3);
        Hotel.Rooms[20] = new RoomInfo("Pool", 1);
    }

    public ManualTime Time { get; } = new();

    public Settings Settings { get; }

    public ServiceProvider Services { get; }

    public IServiceScopeFactory Scopes { get; }

    public FakeHotel Hotel { get; } = new();

    public List<PluginNoticeEvent> Published { get; } = [];

    public Notices Notices { get; }

    public PrizePayer Payer { get; }

    public EventService Events { get; }

    public EventsAdmin AdminService { get; }

    public DateTime Now => Time.Now.UtcDateTime;

    public T Query<T>(Func<EventsDbContext, T> query)
    {
        using var scope = Services.CreateScope();

        return query(scope.ServiceProvider.GetRequiredService<EventsDbContext>());
    }

    public void Change(Action<EventsDbContext> change)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventsDbContext>();

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

    public Admin Admin() => new(AdminService);

    public void Dispose()
    {
        Services.Dispose();
        _connection.Dispose();
    }
}

/// <summary>The admin endpoints as the host maps them, called as the panel would.</summary>
internal sealed class Admin : IPluginAdminBuilder
{
    public Admin(EventsAdmin admin) => admin.Configure(this);

    public Dictionary<
        string,
        (string Node, Func<PluginAdminRequest, CancellationToken, Task<PluginAdminResult>> Handler)
    > Map { get; } = [];

    public List<string> Nodes { get; } = [];

    public List<PluginAdminPage> Pages { get; } = [];

    public string Node(string name, string description)
    {
        Nodes.Add($"plugin.events.{name}");

        return $"plugin.events.{name}";
    }

    void IPluginAdminBuilder.Map(
        string method,
        string pattern,
        string node,
        Func<PluginAdminRequest, CancellationToken, Task<PluginAdminResult>> handler
    ) => Map[$"{method} {pattern}"] = (node, handler);

    public void Page(PluginAdminPage page) => Pages.Add(page);

    public Task<PluginAdminResult> CallAsync(
        string route,
        long id = 0,
        string query = "",
        string? body = null
    ) =>
        Map[route]
            .Handler(
                new PluginAdminRequest(
                    route.Split(' ')[0],
                    route,
                    new Dictionary<string, string> { ["id"] = id.ToString() },
                    query
                        .Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Split('='))
                        .ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1])),
                    body is null ? null : JsonDocument.Parse(body).RootElement,
                    9
                ),
                TestHost.CT
            );

    public static string Json(PluginAdminResult result) => JsonSerializer.Serialize(result.Value);
}
