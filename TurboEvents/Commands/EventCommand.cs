using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Primitives.Commands;
using Turbo.Primitives.Players;
using Turbo.Primitives.Players.Permissions;
using TurboEvents.Hosting;

namespace TurboEvents.Commands;

/// <summary>
/// <c>:event &lt;title&gt;</c>: starts an event in the room the line is typed in and calls everyone
/// online and elsewhere to it with the client's own notification (a pop-up with a button to the
/// room, or a bubble). Held to the host's and the hotel's cooldowns unless the host holds
/// <c>events.cooldown.bypass</c>; a call reaching <c>Turbo:Commands:ConfirmAtPlayers</c> players
/// waits for <c>:confirm</c>, as core's own broadcasts do.
/// </summary>
[Command(
    "event",
    Description = "Start an event in this room and call the hotel to it",
    Category = CATEGORY
)]
[RequiresPermission(EventsNodes.EVENT)]
public sealed class EventCommand(EventService events, IEventsHotel hotel)
    : IOperatorCommand<EventCommand.Arguments>
{
    public const string CATEGORY = "Events";
    public const string REACHES = "reaches";

    public sealed record Arguments(RestOfLine Title);

    public IReadOnlyDictionary<string, string> DefaultTexts { get; } =
        new Dictionary<string, string>
        {
            [EventService.STARTED] = "%0% started: %1% players were called to it.",
            [EventService.NO_TITLE] = "Name the event: :event <title>.",
            [EventService.TITLE_TOO_LONG] = "An event's title is at most %0% characters.",
            [EventService.RUNNING] =
                "%0% (hosted by %1%) is running in this room. End it with :endevent first.",
            [EventService.HOST_COOLDOWN] = "You can start another event in %0% minutes.",
            [EventService.HOTEL_COOLDOWN] =
                "An event started a moment ago. The next can start in %0% minutes.",
            [EventService.NO_ROOM] = "Events are started from inside a room.",
            [REACHES] = "That calls %0% players to this room.",
        };

    public async ValueTask<CommandResult> ExecuteAsync(
        IOperatorCommandContext ctx,
        Arguments arguments,
        CancellationToken ct
    )
    {
        if (ctx.Executor is not { PlayerId: { } hostId, RoomId: { } roomId })
            return CommandResult.Fail(EventService.NO_ROOM);

        var request = new StartRequest(
            hostId,
            ctx.Executor.Name,
            roomId,
            arguments.Title.Text,
            await ctx.Executor.HasAsync(EventsNodes.COOLDOWN_BYPASS, ct)
        );

        if (await events.CheckStartAsync(request, ct) is { } refusal)
            return Reply(refusal);

        // Everyone online but who is in the room already, as it was when the line was typed.
        var inRoom = ctx.Executor.RoomPlayerIds.ToHashSet();
        var called = ctx.SnapshotRecipients(
            nameof(EventCommand),
            hotel.OnlinePlayerIds().Select(x => (PlayerId)x).Where(x => !inRoom.Contains(x))
        );

        if (ctx.ShouldConfirm(called.Count))
            return CommandResult.Confirm(
                REACHES,
                called.Count.ToString(CultureInfo.InvariantCulture)
            );

        return Reply(await events.StartAsync(request, [.. called.Select(x => x.Value)], ct));
    }

    internal static CommandResult Reply(EventsReply reply) =>
        reply.IsFailure
            ? CommandResult.Fail(reply.Status, reply.Parameters)
            : CommandResult.Done(reply.Status, reply.Parameters);
}
