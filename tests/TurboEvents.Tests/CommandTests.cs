using System;
using System.Threading.Tasks;
using Turbo.Primitives.Commands;
using Turbo.Testing;
using TurboEvents.Commands;
using TurboEvents.Hosting;
using Xunit;

namespace TurboEvents.Tests;

/// <summary>The commands said in a room, run by the hotel's own operator command runner.</summary>
public sealed class CommandTests
{
    [Fact]
    public async Task Event_calls_everyone_online_who_is_not_in_the_room_already()
    {
        using var host = new TestHost();
        var alice = host.Player(1, room: 10, EventsNodes.EVENT);
        host.Player(3, room: 10);

        await alice.SaysAsync(":event Falling Furni", TestHost.CT);

        alice.Should().HaveHadCommand(CommandOutcome.Completed);
        alice.Should().HaveBeenWhispered("Falling Furni started: 2 players were called to it.");
        Assert.Equal([2, 4], Assert.Single(host.Hotel.Sent).Players);
    }

    [Fact]
    public async Task Event_waits_for_confirm_when_it_reaches_many_and_checks_first()
    {
        using var host = new TestHost();
        var alice = host.Player(1, room: 10, EventsNodes.EVENT);
        host.Kit.Settings.Set("Turbo:Commands:ConfirmAtPlayers", 3);

        await alice.SaysAsync(":event Tag", TestHost.CT);
        alice.Should().HaveHadCommand(CommandOutcome.AwaitingConfirmation);
        alice.Should().HaveBeenWhispered("That calls 3 players to this room. Type :confirm");
        Assert.Empty(host.Hotel.Sent);

        await alice.SaysAsync(":confirm", TestHost.CT);
        alice.Should().HaveBeenWhispered("Tag started: 3 players were called to it.");

        // A line the cooldown refuses is refused before anyone is asked to confirm.
        var bob = host.Player(2, room: 20, EventsNodes.EVENT);
        host.Kit.Settings.Set("Turbo:Commands:ConfirmAtPlayers", 1);
        await bob.SaysAsync(":event Tag", TestHost.CT);
        Assert.StartsWith("An event started a moment ago.", bob.Whispers[^1]);

        // Holding the bypass node, the cooldowns do not apply.
        host.Kit.Settings.Set("Turbo:Commands:ConfirmAtPlayers", 10);
        bob.WithPermissions(EventsNodes.COOLDOWN_BYPASS);
        await bob.SaysAsync(":event Tag", TestHost.CT);
        Assert.StartsWith("Tag started:", bob.Whispers[^1]);
    }

    [Fact]
    public async Task Winner_resolves_the_name_and_pays_who_is_in_the_room()
    {
        using var host = new TestHost();
        host.Prize("small", "credits:25");
        await host.StartAsync();
        var alice = host.Player(1, room: 10, EventsNodes.WINNER);
        host.Player(2, room: 10);

        await alice.SaysAsync(":winner bob small", TestHost.CT);
        Assert.StartsWith("Bob won ", alice.Whispers[^1]);
        Assert.Equal(25, host.Hotel.Balance(2, "credits"));

        await alice.SaysAsync(":winner Dave small", TestHost.CT);
        Assert.Equal("Dave is not in this room.", alice.Whispers[^1]);

        await alice.SaysAsync(":winner Zed small", TestHost.CT);
        Assert.Equal(
            CommandReplyKeys.Defaults[CommandReplyKeys.PLAYER_NOT_FOUND].Replace("%0%", "Zed"),
            alice.Whispers[^1]
        );
    }

    [Fact]
    public async Task Endevent_ends_the_event_in_the_room_it_is_typed_in()
    {
        using var host = new TestHost();
        await host.StartAsync();

        var alice = host.Player(1, room: 20, EventsNodes.END_EVENT);
        await alice.SaysAsync(":endevent", TestHost.CT);
        alice.Should().HaveBeenWhispered("No event is running in this room.");

        var bob = host.Player(2, room: 10, EventsNodes.END_EVENT);
        await bob.SaysAsync(":endevent", TestHost.CT);
        bob.Should().HaveBeenWhispered("Falling Furni ended after");
    }

    [Fact]
    public void Every_reply_a_command_gives_has_a_text()
    {
        using var host = new TestHost();

        Assert.All(
            new (Turbo.Primitives.Commands.ICommand Command, string[] Statuses)[]
            {
                (
                    new EventCommand(host.Events, host.Hotel),
                    [
                        EventService.STARTED,
                        EventService.NO_TITLE,
                        EventService.TITLE_TOO_LONG,
                        EventService.RUNNING,
                        EventService.HOST_COOLDOWN,
                        EventService.HOTEL_COOLDOWN,
                        EventService.NO_ROOM,
                        EventCommand.REACHES,
                    ]
                ),
                (
                    new EndEventCommand(host.Events),
                    [EventService.ENDED, EventService.NONE_RUNNING, EventService.NO_ROOM]
                ),
                (
                    new WinnerCommand(host.Events),
                    [
                        EventService.PAID,
                        EventService.NO_EVENT,
                        EventService.NO_PRIZE,
                        EventService.UNKNOWN_PRIZE,
                        EventService.BROKEN_PRIZE,
                        EventService.OFFLINE,
                        EventService.NOT_IN_ROOM,
                        EventService.SELF,
                        EventService.NOT_PAID,
                        EventService.UNSURE,
                        EventService.NO_ROOM,
                    ]
                ),
            },
            x => Assert.All(x.Statuses, s => Assert.True(x.Command.DefaultTexts.ContainsKey(s), s))
        );
    }
}
