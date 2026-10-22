using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Turbo.Contracts.Plugins;
using Turbo.Contracts.Plugins.Admin;
using Turbo.Contracts.Plugins.Exports;
using Turbo.Database.Extensions;
using Turbo.Events;
using Turbo.Primitives.Furniture.Providers;
using Turbo.Primitives.Networking;
using Turbo.Primitives.Players.Providers;
using TurboEvents.Database;
using TurboEvents.Hosting;
using TurboEvents.Prizes;

namespace TurboEvents;

/// <summary>
/// Events staff host by hand, kept: <c>:event</c> starts one in a room and calls the hotel to it
/// with the client's own notification, <c>:winner</c> pays a prize set all or none and records it,
/// <c>:endevent</c> ends it. The history, prize sets and a winners' leaderboard are on the admin
/// panel's Events pages, and each start, end and winner is a <c>PluginNoticeEvent</c> another
/// plugin (Discord) may post.
/// </summary>
public sealed class EventsPlugin : ITurboPlugin
{
    private CancellationTokenSource? _stop;
    private Task? _ticker;
    private Notices? _notices;

    public string Key => "events";

    public string Version => "1.0.0";

    public void ConfigureServices(IServiceCollection services, PluginManifest manifest)
    {
        services.AddPluginDatabaseContext<EventsDbContext, EventsDbModule>();
        // Bound as a host module binds its options: the panel lists them, and saves reach the monitor.
        services.AddOptions<EventsConfig>().BindConfiguration(EventsConfig.SECTION_NAME);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IEventsHotel>(sp => new HostHotel(
            Host<IGrainFactory>(sp),
            Host<ISessionGateway>(sp),
            Host<ICurrencyTypeProvider>(sp),
            Host<IFurnitureDefinitionProvider>(sp)
        ));
        services.AddSingleton<Notices>();
        services.AddSingleton<PrizePayer>();
        services.AddSingleton<EventService>();
        services.AddSingleton<IPluginAdmin, EventsAdmin>();
    }

    private static T Host<T>(IServiceProvider sp)
        where T : notnull => sp.GetRequiredService<IHostServices>().GetRequiredService<T>();

    public Task StartAsync(IServiceProvider services, CancellationToken ct)
    {
        _notices = services.GetRequiredService<Notices>();
        _notices.Publisher = Host<EventSystem>(services).PublishAsync;

        _stop = new CancellationTokenSource();
        _ticker = RunAsync(
            Host<IHostApplicationLifetime>(services).ApplicationStarted,
            services.GetRequiredService<PrizePayer>(),
            services.GetRequiredService<EventService>(),
            services.GetRequiredService<IOptionsMonitor<EventsConfig>>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<EventsPlugin>>(),
            _stop.Token
        );

        return Task.CompletedTask;
    }

    /// <summary>
    /// Once the host has started (its currencies and furni loaded, which a payout needs; at once on a
    /// reload), finishes the payouts a restart broke off, then counts the rooms of running events
    /// and ends those that ran too long, every <c>AttendanceSampleSeconds</c>.
    /// </summary>
    internal static async Task RunAsync(
        CancellationToken started,
        PrizePayer payer,
        EventService events,
        IOptionsMonitor<EventsConfig> options,
        TimeProvider time,
        ILogger logger,
        CancellationToken ct
    )
    {
        try
        {
            var ready = new TaskCompletionSource();

            await using (started.Register(() => ready.TrySetResult()))
                await ready.Task.WaitAsync(ct).ConfigureAwait(false);

            await payer.ResumeAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Events could not finish the prize payouts a restart broke off");
        }

        await TickAsync(events, options, time, logger, ct).ConfigureAwait(false);
    }

    /// <summary>Counts the rooms of running events and ends those that ran too long, every <c>AttendanceSampleSeconds</c>.</summary>
    private static async Task TickAsync(
        EventService events,
        IOptionsMonitor<EventsConfig> options,
        TimeProvider time,
        ILogger logger,
        CancellationToken ct
    )
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                        TimeSpan.FromSeconds(
                            Math.Max(5, options.CurrentValue.AttendanceSampleSeconds)
                        ),
                        time,
                        ct
                    )
                    .ConfigureAwait(false);
                await events.TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Events could not count the rooms of running events");
            }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_stop is not null)
            await _stop.CancelAsync().ConfigureAwait(false);

        if (_ticker is not null)
            await _ticker.ConfigureAwait(false);

        _stop?.Dispose();
        _stop = null;
        _ticker = null;

        if (_notices is not null)
            _notices.Publisher = null;

        _notices = null;
    }

    public Task BindExportsAsync(IExportBinder binder, IServiceProvider services) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
