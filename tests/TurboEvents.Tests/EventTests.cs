using System;
using System.Linq;
using System.Threading.Tasks;
using TurboEvents.Database;
using TurboEvents.Hosting;
using Xunit;

namespace TurboEvents.Tests;

/// <summary>Starting and ending events: the cooldowns, the call to the hotel, the peak and the notices.</summary>
public sealed class EventTests
{
    [Fact]
    public async Task An_event_calls_the_hotel_to_its_room_with_a_popup_whose_button_goes_there()
    {
        using var host = new TestHost();

        var reply = await host.StartAsync(title: "Falling Furni");

        Assert.Equal(EventService.STARTED, reply.Status);
        Assert.Equal(["Falling Furni", "3"], reply.Parameters);

        var (players, type, parameters) = Assert.Single(host.Hotel.Sent);
        Assert.Equal([2, 3, 4], players);
        Assert.Equal("events.started", type);
        // NotificationPopup: caption, message, and the "action" button for an event: link.
        Assert.Equal("Event: Falling Furni", parameters["title"]);
        Assert.Equal(
            "Alice is hosting Falling Furni in Lobby. Come and join in!",
            parameters["message"]
        );
        Assert.Equal("Go to Lobby", parameters["linkTitle"]);
        Assert.Equal("event:navigator/goto/10", parameters["linkUrl"]);
        Assert.False(parameters.ContainsKey("display"));

        var hosted = host.Query(db => db.Events.Single());
        Assert.Equal(
            (10, "Lobby", 1, "Alice", 3, 3),
            (
                hosted.RoomId,
                hosted.RoomName,
                hosted.HostId,
                hosted.HostName,
                hosted.Called,
                hosted.PeakAttendees
            )
        );
        Assert.Null(hosted.EndedAt);

        var notice = Assert.Single(host.Published);
        Assert.Equal(("events", "event.started"), (notice.Source, notice.Topic));
        Assert.Equal("Alice started Falling Furni", notice.Title);
        Assert.Equal(hosted.Id.ToString(), notice.Key);
    }

    [Fact]
    public async Task A_bubble_is_sent_instead_when_the_hotel_asks_for_one()
    {
        using var host = new TestHost(
            new EventsConfig { AlertAsBubble = true, AlertImage = "${image.library.url}x.png" }
        );

        await host.StartAsync();

        var parameters = Assert.Single(host.Hotel.Sent).Parameters;
        Assert.Equal("BUBBLE", parameters["display"]);
        Assert.Equal("${image.library.url}x.png", parameters["image"]);
        Assert.Equal("event:navigator/goto/10", parameters["linkUrl"]);
    }

    [Fact]
    public async Task A_host_waits_out_their_cooldown_and_the_hotel_its_own()
    {
        using var host = new TestHost(
            new EventsConfig { HostCooldownMinutes = 30, HotelCooldownMinutes = 10 }
        );

        Assert.Equal(EventService.STARTED, (await host.StartAsync(host: 1, room: 10)).Status);
        await host.Events.EndAsync(10, "Alice", TestHost.CT);

        // Another host, another room: the hotel's cooldown holds them.
        host.Time.Advance(TimeSpan.FromMinutes(4));
        var hotel = await host.StartAsync(host: 2, room: 20);
        Assert.Equal((EventService.HOTEL_COOLDOWN, true), (hotel.Status, hotel.IsFailure));
        Assert.Equal(["6"], hotel.Parameters);

        // Past the hotel's cooldown, the first host is still held by their own.
        host.Time.Advance(TimeSpan.FromMinutes(7));
        var own = await host.StartAsync(host: 1, room: 20);
        Assert.Equal(EventService.HOST_COOLDOWN, own.Status);
        Assert.Equal(["19"], own.Parameters);
        Assert.Equal(EventService.STARTED, (await host.StartAsync(host: 2, room: 20)).Status);

        // The bypass node skips both.
        Assert.Equal(
            EventService.STARTED,
            (await host.StartAsync(host: 1, room: 10, bypass: true)).Status
        );

        // A refused start calls nobody and records nothing.
        Assert.Equal(3, host.Hotel.Sent.Count);
        Assert.Equal(3, host.Query(db => db.Events.Count()));
    }

    [Fact]
    public async Task The_cooldowns_hold_across_a_restart()
    {
        using var host = new TestHost(new EventsConfig { HostCooldownMinutes = 30 });

        await host.StartAsync(host: 1, room: 10);
        await host.Events.EndAsync(10, "Alice", TestHost.CT);

        // A new service over the same tables, as after a restart.
        var restarted = new EventService(
            host.Scopes,
            host.Hotel,
            host.Settings,
            host.Payer,
            host.Notices,
            host.Time
        );
        host.Time.Advance(TimeSpan.FromMinutes(20));

        var refused = await restarted.CheckStartAsync(
            new StartRequest(1, "Alice", 20, "Again", false),
            TestHost.CT
        );
        Assert.Equal(EventService.HOST_COOLDOWN, refused?.Status);
    }

    [Fact]
    public async Task A_room_runs_one_event_and_a_title_is_needed_and_kept_short()
    {
        using var host = new TestHost(
            new EventsConfig
            {
                HostCooldownMinutes = 0,
                HotelCooldownMinutes = 0,
                MaxTitleLength = 10,
            }
        );

        Assert.Equal(EventService.NO_TITLE, (await host.StartAsync(title: "  ")).Status);
        var tooLong = await host.StartAsync(title: "Much too long a title");
        Assert.Equal((EventService.TITLE_TOO_LONG, "10"), (tooLong.Status, tooLong.Parameters[0]));

        await host.StartAsync(host: 1, title: "Tag");
        var busy = await host.StartAsync(host: 2, title: "Race");
        Assert.Equal(EventService.RUNNING, busy.Status);
        Assert.Equal(["Tag", "Alice"], busy.Parameters);
        Assert.Equal(EventService.NO_ROOM, (await host.StartAsync(room: 99)).Status);
    }

    [Fact]
    public async Task Ending_an_event_records_it_with_its_peak_and_tells_other_plugins()
    {
        using var host = new TestHost();

        await host.StartAsync();
        Assert.Equal(
            EventService.NONE_RUNNING,
            (await host.Events.EndAsync(20, "Alice", TestHost.CT)).Status
        );

        host.Hotel.Rooms[10] = new RoomInfo("Lobby", 9);
        await host.Events.TickAsync(TestHost.CT);
        host.Hotel.Rooms[10] = new RoomInfo("Lobby", 5);
        host.Time.Advance(TimeSpan.FromMinutes(25));

        var ended = await host.Events.EndAsync(10, "Bob", TestHost.CT);

        Assert.Equal(EventService.ENDED, ended.Status);
        Assert.Equal(["Falling Furni", "25", "0", "9"], ended.Parameters);

        var hosted = host.Query(db => db.Events.Single());
        Assert.Equal(
            (HostedEventEntity.ENDED, "Bob", 9),
            (hosted.EndReason, hosted.EndedBy, hosted.PeakAttendees)
        );
        Assert.Equal(host.Now, hosted.EndedAt);

        var notice = host.Published.Last();
        Assert.Equal("event.ended", notice.Topic);
        Assert.Contains(notice.Fields, x => x is { Name: "Most players", Value: "9" });
        Assert.Equal(
            EventService.NONE_RUNNING,
            (await host.Events.EndAsync(10, "Bob", TestHost.CT)).Status
        );
    }

    [Fact]
    public async Task An_event_left_running_past_the_longest_is_ended_by_the_plugin()
    {
        using var host = new TestHost(new EventsConfig { MaxEventMinutes = 60 });

        await host.StartAsync();
        host.Time.Advance(TimeSpan.FromMinutes(59));
        await host.Events.TickAsync(TestHost.CT);
        Assert.Null(host.Query(db => db.Events.Single()).EndedAt);

        host.Time.Advance(TimeSpan.FromMinutes(2));
        await host.Events.TickAsync(TestHost.CT);

        var hosted = host.Query(db => db.Events.Single());
        Assert.Equal(HostedEventEntity.EXPIRED, hosted.EndReason);
        Assert.Null(hosted.EndedBy);
        Assert.Equal("event.ended", host.Published.Last().Topic);
    }

    [Fact]
    public async Task A_notice_that_cannot_be_published_does_not_stop_the_event()
    {
        using var host = new TestHost();

        host.Notices.Publisher = (_, _) => throw new InvalidOperationException("no relay");

        Assert.Equal(EventService.STARTED, (await host.StartAsync()).Status);
        Assert.Single(host.Hotel.Sent);
    }
}
