namespace TurboEvents;

/// <summary>
/// What the plugin answers a command with: a status its texts answer
/// (<c>command.&lt;name&gt;.&lt;status&gt;</c>, with <c>%0%</c>, <c>%1%</c>...) and whether it was
/// refused. The commands turn it into the host's <c>CommandResult</c>.
/// </summary>
public sealed record EventsReply(string Status, bool IsFailure, params string[] Parameters)
{
    public static EventsReply Done(string status, params string[] parameters) =>
        new(status, false, parameters);

    public static EventsReply Fail(string status, params string[] parameters) =>
        new(status, true, parameters);
}
