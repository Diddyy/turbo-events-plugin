namespace TurboEvents;

/// <summary>
/// The plugin's settings, under <c>Turbo:Events</c> in the host's configuration, and on the admin
/// panel's Settings page while the plugin is loaded. A change applies at once. Prize sets are kept
/// in the plugin's tables and set on its own pages.
/// </summary>
public sealed class EventsConfig
{
    public const string SECTION_NAME = "Turbo:Events";

    /// <summary>
    /// How long, in minutes, a host waits after starting an event before starting another. 0: no
    /// wait. Staff holding <c>events.cooldown.bypass</c> never wait.
    /// </summary>
    public int HostCooldownMinutes { get; init; } = 30;

    /// <summary>
    /// How long, in minutes, the hotel waits after any event started before the next one is called,
    /// so players are not called away every few minutes. 0: no wait.
    /// </summary>
    public int HotelCooldownMinutes { get; init; } = 10;

    /// <summary>
    /// An event still running this many minutes after it started is ended by the plugin. 0: an event
    /// runs until <c>:endevent</c>.
    /// </summary>
    public int MaxEventMinutes { get; init; } = 120;

    /// <summary>The longest event title, in characters.</summary>
    public int MaxTitleLength { get; init; } = 60;

    /// <summary>
    /// The notification's type: the client looks up <c>notification.&lt;type&gt;</c> in its
    /// external variables (to restyle it) and draws <c>notifications/&lt;type&gt;.png</c> from the
    /// image library when <see cref="AlertImage"/> is empty.
    /// </summary>
    public string AlertType { get; init; } = "events.started";

    /// <summary>
    /// Whether the call to an event is a pop-up with a button to the room (<c>false</c>) or a
    /// bubble in the corner that goes to the room when clicked (<c>true</c>).
    /// </summary>
    public bool AlertAsBubble { get; init; }

    /// <summary>The pop-up's title. Placeholders: <c>{host}</c> <c>{title}</c> <c>{room}</c> <c>{roomId}</c>.</summary>
    public string AlertTitle { get; init; } = "Event: {title}";

    /// <summary>The call's text. Placeholders: <c>{host}</c> <c>{title}</c> <c>{room}</c> <c>{roomId}</c>.</summary>
    public string AlertMessage { get; init; } =
        "{host} is hosting {title} in {room}. Come and join in!";

    /// <summary>The pop-up's button to the room. Placeholders as <see cref="AlertMessage"/>.</summary>
    public string AlertLinkTitle { get; init; } = "Go to {room}";

    /// <summary>
    /// A picture for the call (an image library address such as
    /// <c>${image.library.url}notifications/events.png</c>, or https); empty: the type's own.
    /// </summary>
    public string AlertImage { get; init; } = string.Empty;

    /// <summary>
    /// The prize set <c>:winner</c> pays when the line names none; empty: a line must name one.
    /// </summary>
    public string DefaultPrizeSet { get; init; } = string.Empty;

    /// <summary>Whether <c>:winner</c> needs an event running in the room it is typed in.</summary>
    public bool WinnerNeedsEvent { get; init; } = true;

    /// <summary>Whether a winner must be in the room when <c>:winner</c> is typed.</summary>
    public bool WinnerMustBeInRoom { get; init; } = true;

    /// <summary>
    /// Whether a winner must be online. Off (and <see cref="WinnerMustBeInRoom"/> off): an offline
    /// winner is paid and finds the prize at the next login.
    /// </summary>
    public bool WinnerMustBeOnline { get; init; } = true;

    /// <summary>Whether a host may name themselves the winner.</summary>
    public bool HostMayWin { get; init; }

    /// <summary>
    /// What the room is told of a winner, as a bubble to everyone in it; empty: nothing.
    /// Placeholders: <c>{winner}</c> <c>{prize}</c> <c>{title}</c> <c>{host}</c>.
    /// </summary>
    public string WinnerAnnouncement { get; init; } = "{winner} won {title}: {prize}!";

    /// <summary>The winner bubble's notification type, as <see cref="AlertType"/>.</summary>
    public string WinnerType { get; init; } = "events.winner";

    /// <summary>A picture for the winner bubble, as <see cref="AlertImage"/>; empty: the type's own.</summary>
    public string WinnerImage { get; init; } = string.Empty;

    /// <summary>The most furni one prize set may give, so a typo is not the hotel's economy.</summary>
    public int MaxPrizeFurni { get; init; } = 10;

    /// <summary>The most of one currency one prize set may give.</summary>
    public int MaxPrizeCurrency { get; init; } = 100000;

    /// <summary>How often, in seconds, the rooms of running events are counted for their peak.</summary>
    public int AttendanceSampleSeconds { get; init; } = 30;
}
