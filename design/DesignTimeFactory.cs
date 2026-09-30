using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Shoko.Plugin.Anilist.Storage;

namespace Shoko.Plugin.Anilist.Design;

/// <summary>
/// Builds the plugin's context for <c>dotnet ef</c>. The server configures it
/// at runtime; this is only for adding migrations.
/// </summary>
public class AnilistDbContextFactory : IDesignTimeDbContextFactory<AnilistDbContext>
{
    /// <inheritdoc/>
    public AnilistDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<AnilistDbContext>().UseSqlite("Data Source=design-time.db3").Options);
}

/// <summary>
/// The entry point <c>dotnet ef</c> needs. Does nothing.
/// </summary>
public static class Program
{
    /// <summary>
    /// Does nothing.
    /// </summary>
    public static void Main() { }
}
