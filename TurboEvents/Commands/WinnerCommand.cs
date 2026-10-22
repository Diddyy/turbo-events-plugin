using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Primitives.Commands;
using Turbo.Primitives.Players.Permissions;
using TurboEvents.Hosting;

namespace TurboEvents.Commands;

/// <summary>
/// <c>:winner &lt;name&gt; [prize set]</c>: names the winner of the event running in the room the
/// line is typed in, pays them the prize set (the default one when the line names none), all of it
/// or none, tells the room in a bubble and records it. A winner who is not in the room or offline
/// is refused, saying which, as the settings ask.
/// </summary>
[Command(
    "winner",
    Description = "Name an event's winner and pay their prize",
    Category = EventCommand.CATEGORY
)]
[RequiresPermission(EventsNodes.WINNER)]
public sealed class WinnerCommand(EventService events) : IOperatorCommand<WinnerCommand.Arguments>
{
    public sealed record Arguments(PlayerTarget Who, string? Prize = null);

    public IReadOnlyDictionary<string, string> DefaultTexts { get; } =
        new Dictionary<string, string>
        {
            [EventService.PAID] = "%0% won %1%.",
            [EventService.NO_EVENT] = "No event is running in this room: start one with :event.",
            [EventService.NO_PRIZE] =
                "Name a prize set: :winner <name> <prize set>. Prize sets: %0%.",
            [EventService.UNKNOWN_PRIZE] = "There is no prize set called %0%. Prize sets: %1%.",
            [EventService.BROKEN_PRIZE] = "The prize set %0% can't be paid: %1%.",
            [EventService.OFFLINE] = "%0% is offline.",
            [EventService.NOT_IN_ROOM] = "%0% is not in this room.",
            [EventService.SELF] = "You can't name yourself the winner.",
            [EventService.NOT_PAID] = "%0% was not paid: %1%",
            [EventService.UNSURE] =
                "%0%'s prize may be paid in part: %1% See the Event winners page.",
            [EventService.NO_ROOM] = "Winners are named from inside the event's room.",
        };

    public async ValueTask<CommandResult> ExecuteAsync(
        IOperatorCommandContext ctx,
        Arguments arguments,
        CancellationToken ct
    )
    {
        if (ctx.Executor is not { PlayerId: { } byId, RoomId: { } roomId })
            return CommandResult.Fail(EventService.NO_ROOM);

        var target = await ctx.ResolveAsync(arguments.Who, ct);

        if (target.Failure is { } failure)
            return failure;

        var winner = target.Players[0];

        return EventCommand.Reply(
            await events.AwardAsync(
                new AwardRequest(
                    byId,
                    ctx.Executor.Name,
                    roomId,
                    [.. ctx.Executor.RoomPlayerIds.Select(x => x.Value)],
                    winner.Id,
                    winner.Name,
                    arguments.Prize
                ),
                ct
            )
        );
    }
}
