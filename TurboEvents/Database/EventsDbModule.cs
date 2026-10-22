using System;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Plugins;
using Turbo.Database.Migrations;

namespace TurboEvents.Database;

public class EventsDbModule : IPluginDbModule
{
    public Task MigrateAsync(IServiceProvider sp, CancellationToken ct) =>
        MigrationHelper.MigrateAsync<EventsDbContext>(sp, ct);

    public Task UninstallAsync(IServiceProvider sp, CancellationToken ct) =>
        MigrationHelper.UninstallAsync<EventsDbContext>(sp, ct);
}
