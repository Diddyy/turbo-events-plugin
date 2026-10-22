using System.Collections.Generic;
using Turbo.Primitives.Players.Permissions;

namespace TurboEvents;

/// <summary>The plugin's command nodes. No one holds them until the hotel grants them (or <c>*</c>).</summary>
public sealed class EventsNodes : IPermissionNodeSource
{
    public const string EVENT = "events.command.event";
    public const string END_EVENT = "events.command.endevent";
    public const string WINNER = "events.command.winner";
    public const string COOLDOWN_BYPASS = "events.cooldown.bypass";

    public string? Prefix => "events";

    public IEnumerable<PermissionNodeDefinition> Nodes =>
        [
            new(EVENT, "Start an event in a room and call the hotel to it with :event"),
            new(END_EVENT, "End the event running in a room with :endevent"),
            new(WINNER, "Name an event's winner and pay their prize with :winner"),
            new(COOLDOWN_BYPASS, "Start events without waiting for the host or hotel cooldown"),
        ];

    public IEnumerable<PermissionMetaDefinition> MetaKeys => [];
}
