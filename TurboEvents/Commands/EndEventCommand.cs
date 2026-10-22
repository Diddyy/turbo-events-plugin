using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Primitives.Commands;
using Turbo.Primitives.Players.Permissions;
using TurboEvents.Hosting;

namespace TurboEvents.Commands;

/// <summary><c>:endevent</c>: ends the event running in the room the line is typed in, whoever hosts it.</summary>
[Command(
    "endevent",
    Description = "End the event running in this room",
    Category = EventCommand.CATEGORY
)]
[RequiresPermission(EventsNodes.END_EVENT)]
public sealed class EndEventCommand(EventService events) : IOperatorCommand<NoArguments>
{
    public IReadOnlyDictionary<string, string> DefaultTexts { get; } =
        new Dictionary<string, string>
        {
            [EventService.ENDED] =
                "%0% ended after %1% minutes, with %2% winners and at most %3% players.",
            [EventService.NONE_RUNNING] = "No event is running in this room.",
            [EventService.NO_ROOM] = "Events are ended from inside their room.",
        };

    public async ValueTask<CommandResult> ExecuteAsync(
        IOperatorCommandContext ctx,
        NoArguments arguments,
        CancellationToken ct
    ) =>
        ctx.Executor.RoomId is not { } roomId
            ? CommandResult.Fail(EventService.NO_ROOM)
            : EventCommand.Reply(await events.EndAsync(roomId, ctx.Executor.Name, ct));
}
