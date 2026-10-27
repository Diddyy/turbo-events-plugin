using Microsoft.EntityFrameworkCore;
using Turbo.Database.Context;
using Turbo.Database.Delegates;

namespace TurboEvents.Database;

/// <summary>
/// The plugin's tables, under its prefix (<c>events_</c>): the events hosted, the prize sets and
/// the winners. No foreign keys into core: players and rooms are kept by id and name.
/// </summary>
public class EventsDbContext(DbContextOptions<EventsDbContext> options, TablePrefixProvider prefix)
    : PluginDbContextBase<EventsDbContext>(options, prefix)
{
    public DbSet<HostedEventEntity> Events => Set<HostedEventEntity>();

    public DbSet<PrizeSetEntity> PrizeSets => Set<PrizeSetEntity>();

    public DbSet<WinnerEntity> Winners => Set<WinnerEntity>();
}
