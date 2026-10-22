using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TurboEvents.Database;
using Xunit;

namespace TurboEvents.Tests;

/// <summary>The admin pages' endpoints: prize sets, events, winners and the leaderboard.</summary>
public sealed class AdminTests
{
    [Fact]
    public async Task A_prize_set_is_kept_only_when_it_can_pay_in_this_hotel()
    {
        using var host = new TestHost();
        var admin = host.Admin();

        Assert.Equal(["plugin.events.view", "plugin.events.manage"], admin.Nodes);
        Assert.Equal(
            ["events", "event", "winners", "winner", "leaderboard", "prizes", "prize"],
            admin.Pages.Select(x => x.Id)
        );

        async Task<int> Add(string body) =>
            (await admin.CallAsync("POST prizes", body: body)).Status;

        Assert.Equal(400, await Add("""{"name":"no spaces","currencies":"credits:5"}"""));
        Assert.Equal(400, await Add("""{"name":"empty"}"""));
        Assert.Equal(400, await Add("""{"name":"bad","currencies":"pixels:5"}"""));
        Assert.Equal(400, await Add("""{"name":"bad","currencies":"credits:-5"}"""));
        Assert.Equal(400, await Add("""{"name":"bad","furni":"no_such_furni"}"""));
        Assert.Equal(400, await Add("""{"name":"bad","furni":"throne","furniCount":50}"""));

        var added = await admin.CallAsync(
            "POST prizes",
            body: """{"name":"big","currencies":"credits=500; diamonds 5","badge":"EVT01","furni":"throne","furniCount":"1"}"""
        );
        Assert.Equal(200, added.Status);
        Assert.Equal(400, await Add("""{"name":"big","currencies":"credits:5"}"""));

        var set = host.Query(db => db.PrizeSets.Single());
        Assert.Equal(
            ("credits:500, diamonds:5", "EVT01", "throne", 1),
            (set.Currencies, set.BadgeCode, set.FurniName, set.FurniCount)
        );

        var list = Admin.Json(await admin.CallAsync("GET prizes"));
        Assert.Contains("500 credits, 5 diamonds, the EVT01 badge and throne", list);

        Assert.Equal(
            200,
            (
                await admin.CallAsync(
                    "POST prizes/{id}",
                    set.Id,
                    body: """{"currencies":"credits:50"}"""
                )
            ).Status
        );
        var changed = host.Query(db => db.PrizeSets.Single());
        Assert.Equal(
            ("credits:50", (string?)null, (string?)null, 0),
            (changed.Currencies, changed.BadgeCode, changed.FurniName, changed.FurniCount)
        );

        Assert.Equal(204, (await admin.CallAsync("DELETE prizes/{id}", set.Id)).Status);
        Assert.Empty(host.Query(db => db.PrizeSets.ToList()));
    }

    [Fact]
    public async Task The_events_page_lists_events_with_their_winners_and_ends_a_running_one()
    {
        using var host = new TestHost();
        var admin = host.Admin();

        host.Prize("small", "credits:10");
        await host.StartAsync(title: "Tag");
        await host.AwardAsync(2, "small");

        var id = host.Query(db => db.Events.Single().Id);
        var events = Admin.Json(await admin.CallAsync("GET events", query: "state=running"));
        Assert.Contains("\"title\":\"Tag\"", events);
        Assert.Contains("\"winners\":1", events);
        Assert.Contains(
            "\"running\":true",
            Admin.Json(await admin.CallAsync("GET events/{id}", id))
        );
        Assert.Contains(
            "\"player\":\"Bob\"",
            Admin.Json(await admin.CallAsync("GET events/{id}/winners", id))
        );

        Assert.Equal(200, (await admin.CallAsync("POST events/{id}/end", id)).Status);
        Assert.Equal(400, (await admin.CallAsync("POST events/{id}/end", id)).Status);
        Assert.Contains(
            "\"total\":0",
            Admin.Json(await admin.CallAsync("GET events", query: "state=running"))
        );
        Assert.Equal("panel (player 9)", host.Query(db => db.Events.Single().EndedBy));
    }

    [Fact]
    public async Task An_unsure_payout_is_settled_and_a_pending_one_finished_from_the_panel()
    {
        using var host = new TestHost();
        var admin = host.Admin();

        host.Prize("small", "credits:10", furni: "throne", count: 2);
        await host.StartAsync();
        host.Hotel.ThrowFurniAt = 2;
        await host.AwardAsync(2, "small");

        var unsure = host.Query(db => db.Winners.Single());
        Assert.Contains(
            "1 of 2 throne",
            Admin.Json(await admin.CallAsync("GET winners/{id}", unsure.Id))
        );
        Assert.Equal(400, (await admin.CallAsync("POST winners/{id}/pay", unsure.Id)).Status);
        Assert.Equal(200, (await admin.CallAsync("POST winners/{id}/settled", unsure.Id)).Status);
        Assert.Equal(WinnerEntity.PAID, host.Query(db => db.Winners.Single()).Status);

        host.Change(db =>
            db.Winners.Add(
                new WinnerEntity
                {
                    PlayerId = 3,
                    PlayerName = "Carol",
                    PrizeName = "small",
                    Currencies = "credits:10",
                    OperationId = "op-pending",
                    CreatedAt = host.Now,
                }
            )
        );
        var pending = host.Query(db => db.Winners.Single(x => x.PlayerId == 3).Id);
        var finished = await admin.CallAsync("POST winners/{id}/pay", pending);
        Assert.Contains("\"status\":\"paid\"", Admin.Json(finished));
        Assert.Equal(10, host.Hotel.Balance(3, "credits"));
    }

    [Fact]
    public async Task The_leaderboard_counts_paid_wins_in_the_week_the_month_or_ever()
    {
        using var host = new TestHost();
        var admin = host.Admin();

        void Win(int player, int daysAgo, string status = WinnerEntity.PAID) =>
            host.Change(db =>
                db.Winners.Add(
                    new WinnerEntity
                    {
                        PlayerId = player,
                        PlayerName = TestHost.Name(player),
                        PrizeName = "small",
                        OperationId = Guid.NewGuid().ToString("N"),
                        Status = status,
                        CreatedAt = host.Now.AddDays(-daysAgo),
                    }
                )
            );

        Win(2, 1);
        Win(2, 2);
        Win(3, 3);
        Win(3, 10);
        Win(3, 20);
        Win(4, 40);
        Win(4, 50);
        Win(4, 60);
        Win(4, 70);
        Win(2, 1, WinnerEntity.FAILED);

        async Task<string[]> Players(string period)
        {
            var json = Admin.Json(
                await admin.CallAsync("GET leaderboard", query: $"period={period}")
            );

            return
            [
                .. JsonDocument
                    .Parse(json)
                    .RootElement.GetProperty("items")
                    .EnumerateArray()
                    .Select(x =>
                        $"{x.GetProperty("player").GetString()}:{x.GetProperty("wins").GetInt32()}"
                    ),
            ];
        }

        Assert.Equal(["Bob:2", "Carol:1"], await Players("week"));
        Assert.Equal(["Carol:3", "Bob:2"], await Players("month"));
        Assert.Equal(["Dave:4", "Carol:3", "Bob:2"], await Players("all"));
    }
}
