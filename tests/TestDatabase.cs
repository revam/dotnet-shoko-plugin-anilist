using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shoko.Plugin.Anilist.Storage;

namespace Shoko.Plugin.Anilist.Tests;

/// <summary>
/// The plugin's database in memory, migrated the way the server migrates it,
/// for as long as the instance lives.
/// </summary>
internal sealed class TestDatabase : IDbContextFactory<AnilistDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private readonly DbContextOptions<AnilistDbContext> _options;

    public TestDatabase()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AnilistDbContext>().UseSqlite(_connection).Options;
        using var context = CreateDbContext();
        context.Database.Migrate();
    }

    public AnilistDbContext CreateDbContext()
        => new(_options);

    public void Dispose()
        => _connection.Dispose();
}
