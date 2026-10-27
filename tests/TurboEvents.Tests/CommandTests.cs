using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Primitives.Commands;
using Turbo.Primitives.Players;
using Turbo.Primitives.Rooms;
using TurboEvents.Commands;
using TurboEvents.Hosting;
using Xunit;

namespace TurboEvents.Tests;

/// <summary>A player typing in a room, as core's runner hands a line to an operator command.</summary>
internal sealed class FakeExecutor(
    int player,
    string name,
    int room,
    int[] inRoom,
    params string[] nodes
) : IOperatorExecutor
{
    public PlayerId? PlayerId => player;

    public string Name => name;

    public RoomId? RoomId => room;

    public IReadOnlyList<PlayerId> RoomPlayerIds => [.. inRoom.Select(x => (PlayerId)x)];

    public Task<bool> HasAsync(string node, CancellationToken ct) =>
        Task.FromResult(nodes.Contains(node));

    public Task ReplyAsync(string text, CancellationToken ct) => Task.CompletedTask;

    public Task NoticeAsync(IReadOnlyList<string> lines, CancellationToken ct) =>
        Task.CompletedTask;
}

internal sealed class FakeContext(
    IOperatorExecutor executor,
    int confirmAt = 10,
    bool confirmed = false
) : IOperatorCommandContext
{
    public IOperatorExecutor Executor => executor;

    public string ArgumentText => "";

    public bool IsConfirmed => confirmed;

    public Task NotifyAsync(
        PlayerId playerId,
        string textKey,
        string defaultText,
        IReadOnlyList<string> parameters,
        CancellationToken ct
    ) => Task.CompletedTask;

    public bool ShouldConfirm(int players) => !confirmed && players >= confirmAt;

    public IReadOnlyList<PlayerId> SnapshotRecipients(
        string scope,
        IEnumerable<PlayerId> recipients
    ) => [.. recipients];

    public Task<CommandBatchResult> ExecuteBatchAsync(
        IReadOnlyList<ResolvedPlayer> players,
        int unitsPerPlayer,
        Func<ResolvedPlayer, CancellationToken, ValueTask<bool>> operation,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<TargetSelection> ResolveAsync(PlayerTarget target, CancellationToken ct) =>
        Task.FromResult(
            target.Text switch
            {
                "Bob" => TargetSelection.Of(new ResolvedPlayer(2, "Bob")),
                "Dave" => TargetSelection.Of(new ResolvedPlayer(4, "Dave")),
                _ => TargetSelection.Refused(CommandResult.Fail("not_found", target.Text)),
            }
        );

    public Task<TargetSelection> SelectAsync(PlayerTarget target, CancellationToken ct) =>
        ResolveAsync(target, ct);
}

/// <summary>The commands as core's runner calls them: the line's room and players, confirmation, replies.</summary>
public sealed class CommandTests
{
    [Fact]
    public async Task Event_calls_everyone_online_who_is_not_in_the_room_already()
    {
        using var host = new TestHost();
        var command = new EventCommand(host.Events, host.Hotel);

        var result = await command.ExecuteAsync(
            new FakeContext(new FakeExecutor(1, "Alice", 10, [1, 3])),
            new EventCommand.Arguments(new RestOfLine("Falling Furni")),
            TestHost.CT
        );

        Assert.Equal((EventService.STARTED, false), (result.Status, result.IsFailure));
        Assert.Equal([2, 4], Assert.Single(host.Hotel.Sent).Players);
    }

    [Fact]
    public async Task Event_waits_for_confirm_when_it_reaches_many_and_checks_first()
    {
        using var host = new TestHost();
        var command = new EventCommand(host.Events, host.Hotel);
        var arguments = new EventCommand.Arguments(new RestOfLine("Tag"));
        var alice = new FakeExecutor(1, "Alice", 10, [1]);

        var waiting = await command.ExecuteAsync(
            new FakeContext(alice, confirmAt: 3),
            arguments,
            TestHost.CT
        );
        Assert.True(waiting.NeedsConfirmation);
        Assert.Equal((EventCommand.REACHES, "3"), (waiting.Status, waiting.Parameters[0]));
        Assert.Empty(host.Hotel.Sent);

        var confirmed = await command.ExecuteAsync(
            new FakeContext(alice, confirmAt: 3, confirmed: true),
            arguments,
            TestHost.CT
        );
        Assert.Equal(EventService.STARTED, confirmed.Status);

        // A line the cooldown refuses is refused before anyone is asked to confirm.
        var bob = new FakeExecutor(2, "Bob", 20, [2]);
        var refused = await command.ExecuteAsync(
            new FakeContext(bob, confirmAt: 1),
            arguments,
            TestHost.CT
        );
        Assert.Equal(
            (EventService.HOTEL_COOLDOWN, false),
            (refused.Status, refused.NeedsConfirmation)
        );

        // Holding the bypass node, the cooldowns do not apply.
        var staff = new FakeExecutor(2, "Bob", 20, [2], "events.cooldown.bypass");
        Assert.Equal(
            EventService.STARTED,
            (await command.ExecuteAsync(new FakeContext(staff), arguments, TestHost.CT)).Status
        );
    }

    [Fact]
    public async Task Winner_resolves_the_name_and_pays_who_is_in_the_room()
    {
        using var host = new TestHost();
        host.Prize("small", "credits:25");
        await host.StartAsync();
        var command = new WinnerCommand(host.Events);
        var alice = new FakeContext(new FakeExecutor(1, "Alice", 10, [1, 2]));

        var paid = await command.ExecuteAsync(
            alice,
            new WinnerCommand.Arguments(new PlayerTarget("Bob"), "small"),
            TestHost.CT
        );
        Assert.Equal((EventService.PAID, "Bob"), (paid.Status, paid.Parameters[0]));
        Assert.Equal(25, host.Hotel.Balance(2, "credits"));

        var elsewhere = await command.ExecuteAsync(
            alice,
            new WinnerCommand.Arguments(new PlayerTarget("Dave"), "small"),
            TestHost.CT
        );
        Assert.Equal((EventService.NOT_IN_ROOM, true), (elsewhere.Status, elsewhere.IsFailure));

        var nobody = await command.ExecuteAsync(
            alice,
            new WinnerCommand.Arguments(new PlayerTarget("Zed"), "small"),
            TestHost.CT
        );
        Assert.Equal("not_found", nobody.Status);
    }

    [Fact]
    public async Task Endevent_ends_the_event_in_the_room_it_is_typed_in()
    {
        using var host = new TestHost();
        await host.StartAsync();
        var command = new EndEventCommand(host.Events);

        var other = await command.ExecuteAsync(
            new FakeContext(new FakeExecutor(1, "Alice", 20, [1])),
            new NoArguments(),
            TestHost.CT
        );
        Assert.Equal(EventService.NONE_RUNNING, other.Status);

        var ended = await command.ExecuteAsync(
            new FakeContext(new FakeExecutor(2, "Bob", 10, [2])),
            new NoArguments(),
            TestHost.CT
        );
        Assert.Equal(EventService.ENDED, ended.Status);
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
