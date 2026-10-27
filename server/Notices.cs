using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Turbo.Primitives.Plugins.Events;
using TurboEvents.Database;

namespace TurboEvents;

/// <summary>
/// Tells other plugins of events started and ended and of winners with a
/// <see cref="PluginNoticeEvent"/> on the host's event system (turbo-cloud's
/// <c>docs/plugin-notices.md</c>): the Discord plugin posts them as <c>plugin:events:event.started</c>,
/// <c>plugin:events:event.ended</c> and <c>plugin:events:event.winner</c>, without this plugin
/// knowing it. Nothing is published until the plugin's start sets <see cref="Publisher"/>, and a
/// publish that throws is logged: the event stands either way. When the host has an admin panel URL
/// (<c>Turbo:Admin:PanelUrl</c>) a notice links to the event's page.
/// </summary>
public sealed class Notices(ILogger<Notices> logger, IConfiguration? configuration = null)
{
    public const string SOURCE = "events";
    public const string STARTED = "event.started";
    public const string ENDED = "event.ended";
    public const string WINNER = "event.winner";

    /// <summary>The host's <c>IEventPublisher.PublishAsync</c> while the plugin runs; null: nothing is published.</summary>
    public Func<PluginNoticeEvent, CancellationToken, Task>? Publisher { get; set; }

    public Task StartedAsync(HostedEventEntity hosted, CancellationToken ct) =>
        PublishAsync(
            new PluginNoticeEvent
            {
                Source = SOURCE,
                Topic = STARTED,
                Title = $"{hosted.HostName} started {hosted.Title}",
                Text =
                    $"In {hosted.RoomName}. {Count(hosted.Called)} players online were called to it.",
                Url = EventPage(hosted.Id),
                Fields =
                [
                    new("Host", hosted.HostName),
                    new("Room", $"{hosted.RoomName} ({Count(hosted.RoomId)})"),
                    new("Called", Count(hosted.Called)),
                ],
                Level = PluginNoticeLevel.Info,
                Key = Count(hosted.Id),
            },
            ct
        );

    public Task EndedAsync(HostedEventEntity hosted, int winners, CancellationToken ct) =>
        PublishAsync(
            new PluginNoticeEvent
            {
                Source = SOURCE,
                Topic = ENDED,
                Title = $"{hosted.Title} ended",
                Text =
                    hosted.EndReason == HostedEventEntity.EXPIRED
                        ? "It ran past the longest an event may run."
                        : $"Ended by {hosted.EndedBy}.",
                Url = EventPage(hosted.Id),
                Fields =
                [
                    new("Host", hosted.HostName),
                    new("Room", $"{hosted.RoomName} ({Count(hosted.RoomId)})"),
                    new("Lasted", Lasted(hosted)),
                    new("Most players", Count(hosted.PeakAttendees)),
                    new("Winners", Count(winners)),
                ],
                Level = PluginNoticeLevel.Info,
                Key = Count(hosted.Id),
            },
            ct
        );

    public Task WinnerAsync(
        WinnerEntity winner,
        string prize,
        string? eventTitle,
        CancellationToken ct
    ) =>
        PublishAsync(
            new PluginNoticeEvent
            {
                Source = SOURCE,
                Topic = WINNER,
                Title = eventTitle is null
                    ? $"{winner.PlayerName} won"
                    : $"{winner.PlayerName} won {eventTitle}",
                Text = $"Paid {prize}.",
                Url = winner.EventId is { } id ? EventPage(id) : null,
                Fields =
                [
                    new("Winner", winner.PlayerName),
                    new("Prize", winner.PrizeName),
                    new("Awarded by", winner.AwardedBy),
                ],
                Level = PluginNoticeLevel.Success,
                Key = Count(winner.Id),
            },
            ct
        );

    /// <summary>Publishes <paramref name="notice"/> cut to the event's limits; never throws.</summary>
    public async Task PublishAsync(PluginNoticeEvent notice, CancellationToken ct)
    {
        if (Publisher is not { } publish)
            return;

        try
        {
            await publish(Fit(notice), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Plugin notice {Topic} {Key} could not be published",
                notice.Topic,
                notice.Key
            );
        }
    }

    /// <summary>The notice within <see cref="PluginNoticeEvent"/>'s limits.</summary>
    internal static PluginNoticeEvent Fit(PluginNoticeEvent notice) =>
        notice with
        {
            Topic = Cut(notice.Topic, PluginNoticeEvent.MAX_TOPIC_LENGTH),
            Title = Cut(notice.Title, PluginNoticeEvent.MAX_TITLE_LENGTH),
            Text = notice.Text is null ? null : Cut(notice.Text, PluginNoticeEvent.MAX_TEXT_LENGTH),
            Fields = notice.Fields.IsDefaultOrEmpty
                ? []
                : [.. notice.Fields.Take(PluginNoticeEvent.MAX_FIELDS)],
        };

    private string? EventPage(long id)
    {
        var panel = configuration?["Turbo:Admin:PanelUrl"]?.TrimEnd('/');

        return string.IsNullOrEmpty(panel) ? null : $"{panel}/plugin/events/event/{Count(id)}";
    }

    private static string Lasted(HostedEventEntity hosted) =>
        hosted.EndedAt is { } end
            ? $"{Count((long)Math.Round((end - hosted.StartedAt).TotalMinutes))} min"
            : "-";

    private static string Count(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
