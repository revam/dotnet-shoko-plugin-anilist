using System;
using Microsoft.EntityFrameworkCore;

namespace Shoko.Plugin.Anilist.Storage;

/// <summary>
/// The plugin's own database: what only AniList has, one table per kind of
/// record. The server configures it and applies its migrations while it
/// starts.
/// </summary>
/// <remarks>
/// Scalars are columns, the season by its name. An anime's own
/// recommendations are a JSON column, as a complex collection. Dates are
/// kept and read back in UTC. Nothing here names a provider, so the
/// migrations stay provider-neutral.
/// </remarks>
/// <param name="options">The options the server built.</param>
public class AnilistDbContext(DbContextOptions<AnilistDbContext> options) : DbContext(options)
{
    /// <summary>
    /// The name the database is registered under, and its file name.
    /// </summary>
    public const string DatabaseName = "anilist";

    /// <summary>What the plugin keeps of each anime.</summary>
    public DbSet<AnilistStoredAnime> Anime => Set<AnilistStoredAnime>();

    /// <summary>What the plugin keeps of each episode.</summary>
    public DbSet<AnilistStoredEpisode> Episodes => Set<AnilistStoredEpisode>();

    /// <summary>The portraits of people and characters.</summary>
    public DbSet<AnilistStoredPortrait> Portraits => Set<AnilistStoredPortrait>();

    /// <inheritdoc/>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AnilistStoredAnime>(anime =>
        {
            anime.ToTable("Anime");
            anime.HasKey(row => row.ID);
            anime.Property(row => row.ID).ValueGeneratedNever();
            anime.Property(row => row.Season).HasConversion<string>().HasMaxLength(16);
            anime.Property(row => row.Color).HasMaxLength(16);
            anime.ComplexCollection(row => row.Recommendations, recommendation => recommendation.ToJson());
        });

        modelBuilder.Entity<AnilistStoredEpisode>(episode =>
        {
            episode.ToTable("Episodes");
            episode.HasKey(row => row.ID);
            episode.Property(row => row.ID).ValueGeneratedNever();
            episode.HasIndex(row => row.AnimeID);
        });

        modelBuilder.Entity<AnilistStoredPortrait>(portrait =>
        {
            portrait.ToTable("Portraits");
            portrait.HasKey(row => row.Key);
            portrait.Property(row => row.Key).HasMaxLength(128);
        });
    }
}
