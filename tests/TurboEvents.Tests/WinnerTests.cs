using System;
using System.Linq;
using System.Threading.Tasks;
using TurboEvents.Database;
using TurboEvents.Hosting;
using Xunit;

namespace TurboEvents.Tests;

/// <summary>Naming winners: the checks, the payout all or none and once, the record and the leaderboard.</summary>
public sealed class WinnerTests
{
    private static async Task<TestHost> RunningAsync(EventsConfig? config = null)
    {
        var host = new TestHost(config);

        host.Prize("small", "credits:100, duckets:50", "EVT01", "throne", 2);
        await host.StartAsync();

        return host;
    }

    [Fact]
    public async Task A_winner_is_paid_the_whole_prize_once_and_the_room_is_told()
    {
        using var host = await RunningAsync();

        var reply = await host.AwardAsync(2, "small");

        Assert.Equal((EventService.PAID, false), (reply.Status, reply.IsFailure));
        Assert.Equal(
            ["Bob", "100 credits, 50 duckets, the EVT01 badge and 2 x throne"],
            reply.Parameters
        );
        Assert.Equal(100, host.Hotel.Balance(2, "credits"));
        Assert.Equal(50, host.Hotel.Balance(2, "duckets"));
        Assert.Contains("EVT01", host.Hotel.Badges[2]);
        Assert.Equal(2, host.Hotel.Inventory.Count(x => x == (2, "throne")));

        var winner = host.Query(db => db.Winners.Single());
        Assert.Equal(WinnerEntity.PAID, winner.Status);
        Assert.Equal(
            (2, "Bob", "Alice", "small"),
            (winner.PlayerId, winner.PlayerName, winner.AwardedBy, winner.PrizeName)
        );
        Assert.Equal(host.Query(db => db.Events.Single().Id), winner.EventId);

        // The room's bubble, to everyone in it.
        var (players, type, parameters) = host.Hotel.Sent.Last();
        Assert.Equal([1, 2, 3], players);
        Assert.Equal("events.winner", type);
        Assert.Equal("BUBBLE", parameters["display"]);
        Assert.Equal(
            "Bob won Falling Furni: 100 credits, 50 duckets, the EVT01 badge and 2 x throne!",
            parameters["message"]
        );

        var notice = host.Published.Last();
        Assert.Equal(("event.winner", "Bob won Falling Furni"), (notice.Topic, notice.Title));

        // Paying the row again pays nothing more.
        await host.Payer.PayAsync(winner.Id, TestHost.CT);
        Assert.Equal(100, host.Hotel.Balance(2, "credits"));
        Assert.Equal(2, host.Hotel.Inventory.Count);
    }

    [Fact]
    public async Task A_winner_who_is_offline_or_elsewhere_is_refused_saying_which()
    {
        using var host = await RunningAsync();

        host.Hotel.Online.Remove(4);
        var offline = await host.AwardAsync(4, "small");
        Assert.Equal((EventService.OFFLINE, "Dave"), (offline.Status, offline.Parameters[0]));

        host.Hotel.Online.Add(4);
        var elsewhere = await host.AwardAsync(4, "small");
        Assert.Equal(
            (EventService.NOT_IN_ROOM, "Dave"),
            (elsewhere.Status, elsewhere.Parameters[0])
        );

        Assert.Equal(EventService.SELF, (await host.AwardAsync(1, "small")).Status);
        Assert.Empty(host.Query(db => db.Winners.ToList()));
        Assert.Empty(host.Hotel.Credited);
    }

    [Fact]
    public async Task The_hotel_may_pay_a_winner_who_is_elsewhere_or_offline()
    {
        using var host = await RunningAsync(
            new EventsConfig { WinnerMustBeInRoom = false, WinnerMustBeOnline = false }
        );

        host.Hotel.Online.Remove(4);

        Assert.Equal(EventService.PAID, (await host.AwardAsync(4, "small")).Status);
        Assert.Equal(100, host.Hotel.Balance(4, "credits"));
    }

    [Fact]
    public async Task A_winner_needs_an_event_and_a_prize_set_that_can_pay()
    {
        using var host = new TestHost();

        host.Prize("small", "credits:100");
        host.Prize("broken", "pixels:5");

        Assert.Equal(EventService.NO_EVENT, (await host.AwardAsync(2, "small")).Status);

        await host.StartAsync();

        var none = await host.AwardAsync(2);
        Assert.Equal((EventService.NO_PRIZE, "broken, small"), (none.Status, none.Parameters[0]));
        var unknown = await host.AwardAsync(2, "huge");
        Assert.Equal(EventService.UNKNOWN_PRIZE, unknown.Status);
        var broken = await host.AwardAsync(2, "broken");
        Assert.Equal(EventService.BROKEN_PRIZE, broken.Status);
        Assert.Contains("no currency called pixels", broken.Parameters[1]);

        // The default set, when the line names none.
        host.Configure(new EventsConfig { DefaultPrizeSet = "small" });
        Assert.Equal(EventService.PAID, (await host.AwardAsync(2)).Status);
    }

    [Fact]
    public async Task A_currency_that_cannot_be_paid_takes_back_the_ones_that_were()
    {
        using var host = await RunningAsync();

        host.Hotel.FailCurrency = "duckets";

        var reply = await host.AwardAsync(2, "small");

        Assert.Equal((EventService.NOT_PAID, true), (reply.Status, reply.IsFailure));
        Assert.Equal(0, host.Hotel.Balance(2, "credits"));
        Assert.Single(host.Hotel.TakenBack);
        Assert.False(host.Hotel.Badges.ContainsKey(2));
        Assert.Empty(host.Hotel.Inventory);

        var winner = host.Query(db => db.Winners.Single());
        Assert.Equal(WinnerEntity.FAILED, winner.Status);
        Assert.Equal("50 duckets could not be paid: nothing was paid.", winner.Error);
        // No bubble, no notice for a prize not paid.
        Assert.Single(host.Hotel.Sent);
        Assert.DoesNotContain(host.Published, x => x.Topic == "event.winner");
    }

    [Fact]
    public async Task Furni_that_cannot_be_given_takes_back_the_currencies_and_the_badge()
    {
        using var host = await RunningAsync();

        host.Hotel.FailFurniAt = 1;

        Assert.Equal(EventService.NOT_PAID, (await host.AwardAsync(2, "small")).Status);
        Assert.Equal(0, host.Hotel.Balance(2, "credits"));
        Assert.Equal(0, host.Hotel.Balance(2, "duckets"));
        Assert.Empty(host.Hotel.Badges[2]);
        Assert.Equal(WinnerEntity.FAILED, host.Query(db => db.Winners.Single()).Status);
    }

    [Fact]
    public async Task A_badge_the_winner_had_is_not_taken_from_them_when_a_payout_fails()
    {
        using var host = await RunningAsync();

        host.Hotel.Badges[2] = ["EVT01"];
        host.Hotel.FailFurniAt = 1;

        await host.AwardAsync(2, "small");

        Assert.Contains("EVT01", host.Hotel.Badges[2]);
    }

    [Fact]
    public async Task A_payout_a_restart_broke_off_is_finished_once()
    {
        using var host = await RunningAsync();

        // The row and the credits were written, then the server stopped before the step was saved.
        host.Change(db =>
            db.Winners.Add(
                new WinnerEntity
                {
                    RoomId = 10,
                    PlayerId = 3,
                    PlayerName = "Carol",
                    AwardedById = 1,
                    AwardedBy = "Alice",
                    PrizeName = "small",
                    Currencies = "credits:100, duckets:50",
                    BadgeCode = "EVT01",
                    FurniName = "throne",
                    FurniCount = 2,
                    OperationId = "op-restart",
                    CreatedAt = host.Now,
                }
            )
        );
        await host.Hotel.CreditAsync(3, "credits", 100, "op-restart", 0, 1, 1, TestHost.CT);

        Assert.Equal(1, await host.Payer.ResumeAsync(TestHost.CT));

        Assert.Equal(100, host.Hotel.Balance(3, "credits"));
        Assert.Equal(50, host.Hotel.Balance(3, "duckets"));
        Assert.Equal(2, host.Hotel.Inventory.Count(x => x.Player == 3));
        Assert.Equal(WinnerEntity.PAID, host.Query(db => db.Winners.Single()).Status);

        // Started again: nothing is pending, nothing is paid twice.
        Assert.Equal(0, await host.Payer.ResumeAsync(TestHost.CT));
        Assert.Equal(100, host.Hotel.Balance(3, "credits"));
    }

    [Fact]
    public async Task Broken_off_payouts_are_finished_only_once_the_host_has_started()
    {
        using var host = new TestHost();

        host.Change(db =>
            db.Winners.Add(
                new WinnerEntity
                {
                    PlayerId = 3,
                    PlayerName = "Carol",
                    PrizeName = "small",
                    Currencies = "credits:100",
                    OperationId = "op-boot",
                    CreatedAt = host.Now,
                }
            )
        );
        // A plugin loads before the host has read its currencies (live: 0.75 s before).
        host.Hotel.Currencies.Clear();

        using var started = new System.Threading.CancellationTokenSource();
        using var stop = new System.Threading.CancellationTokenSource();
        var run = EventsPlugin.RunAsync(
            started.Token,
            host.Payer,
            host.Events,
            host.Settings,
            host.Time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            stop.Token
        );

        await Task.Yield();
        Assert.Equal(WinnerEntity.PENDING, host.Query(db => db.Winners.Single()).Status);

        host.Hotel.Currencies.UnionWith(["credits", "duckets"]);
        await started.CancelAsync();

        for (
            var i = 0;
            i < 200 && host.Query(db => db.Winners.Single()).Status == WinnerEntity.PENDING;
            i++
        )
            await Task.Delay(10);

        Assert.Equal(WinnerEntity.PAID, host.Query(db => db.Winners.Single()).Status);
        Assert.Equal(100, host.Hotel.Balance(3, "credits"));

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task A_payout_broken_off_while_giving_furni_is_unsure_and_never_given_twice()
    {
        using var host = await RunningAsync();

        host.Hotel.ThrowFurniAt = 2;

        var reply = await host.AwardAsync(2, "small");

        Assert.Equal(EventService.UNSURE, reply.Status);
        Assert.Equal(2, host.Hotel.Inventory.Count);

        var winner = host.Query(db => db.Winners.Single());
        Assert.Equal((WinnerEntity.UNSURE, 1), (winner.Status, winner.FurniGiven));

        // A restart does not pay it again.
        Assert.Equal(0, await host.Payer.ResumeAsync(TestHost.CT));
        Assert.Equal(2, host.Hotel.Inventory.Count);

        // A pending row whose furni had begun is unsure after a restart, and is not paid on.
        host.Change(db =>
        {
            var row = db.Winners.Single();
            row.Status = WinnerEntity.PENDING;
        });
        Assert.Equal(0, await host.Payer.ResumeAsync(TestHost.CT));
        Assert.Equal(WinnerEntity.UNSURE, host.Query(db => db.Winners.Single()).Status);
        Assert.Equal(2, host.Hotel.Inventory.Count);
    }

    [Fact]
    public async Task Paid_winners_count_on_the_events_page_and_its_end()
    {
        using var host = await RunningAsync();

        await host.AwardAsync(2, "small");
        host.Hotel.FailCurrency = "credits";
        await host.AwardAsync(3, "small");

        var ended = await host.Events.EndAsync(10, "Alice", TestHost.CT);
        Assert.Equal("1", ended.Parameters[2]);
    }
}
