using Moq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Abstractions.Metadata.Stub;

namespace Shoko.Plugin.Anilist.Tests;

internal sealed class FakeTag(MetadataTagData data, MetadataEntryTagData? entry = null) : ITag
{
    public MetadataGuid ID => data.ID;

    public MetadataTagData Data => data;

    public string Name => data.Name;

    public string Overview => data.Overview;

    public TagKind Kind => data.Kind;

    public string? Category => data.Category;

    public bool IsSpoiler => entry?.IsSpoiler ?? data.IsSpoiler;

    public bool IsRestricted => data.IsRestricted;

    public int? Weight => entry?.Weight;
}

internal sealed class FakeTagStore : IMetadataTagStore
{
    private readonly Dictionary<MetadataGuid, MetadataTagData> _tags = [];

    public Dictionary<MetadataGuid, List<MetadataEntryTagData>> Entries { get; } = [];

    public ITag? GetTag(MetadataGuid id)
        => _tags.TryGetValue(id, out var tag) ? new FakeTag(tag) : null;

    public IReadOnlyList<ITag> GetAllTags(MetadataSource source, TagKind? kind = null)
        => [.. _tags.Values.Where(tag => tag.ID.Source == source && (kind is null || tag.Kind == kind)).Select(tag => new FakeTag(tag))];

    public IReadOnlyList<ITag> GetTags(MetadataGuid entry)
        => Entries.TryGetValue(entry, out var tags) ? [.. tags.Select(tag => new FakeTag(_tags[tag.TagID], tag))] : [];

    public IReadOnlyList<MetadataGuid> GetEntriesWithTag(MetadataGuid tag)
        => [.. Entries.Where(pair => pair.Value.Any(entry => entry.TagID == tag)).Select(pair => pair.Key)];

    public void SaveTags(IEnumerable<MetadataTagData> tags)
    {
        foreach (var tag in tags)
            _tags[tag.ID] = tag;
    }

    public void SetTags(MetadataGuid entry, IEnumerable<MetadataEntryTagData> tags)
    {
        var list = tags.ToList();
        if (list.FirstOrDefault(tag => !_tags.ContainsKey(tag.TagID)) is { } missing)
            throw new ArgumentException($"Tag {missing.TagID} is not stored.", nameof(tags));

        Entries[entry] = list;
    }

    public int RemoveTags(MetadataGuid entry)
        => Entries.Remove(entry, out var removed) ? removed.Count : 0;
}

internal sealed class FakeStudio(MetadataStudioData data, StudioType type) : IStudio
{
    public MetadataGuid ID => data.ID;

    public string Name => data.Name;

    public string? OriginalName => data.OriginalName;

    public string? CountryOfOrigin => data.CountryOfOrigin;

    public DateTime? LastRefreshedAt => null;

    public StudioType StudioType { get; } = type;

    public IEnumerable<IMovie> MovieWorks => [];

    public IEnumerable<ISeries> SeriesWorks => [];

    public IEnumerable<IMetadata> Works => [];
}

internal sealed class FakeStudioStore : IMetadataStudioStore
{
    private readonly Dictionary<MetadataGuid, MetadataStudioData> _studios = [];

    public Dictionary<MetadataGuid, List<MetadataEntryStudioData>> Entries { get; } = [];

    public IStudio? GetStudio(MetadataGuid id)
        => _studios.TryGetValue(id, out var studio) ? new FakeStudio(studio, StudioType.None) : null;

    public IReadOnlyList<IStudio> GetStudios(MetadataGuid entry)
        => Entries.TryGetValue(entry, out var studios) ? [.. studios.Select(studio => new FakeStudio(_studios[studio.StudioID], studio.Type))] : [];

    public IReadOnlyList<MetadataGuid> GetEntriesForStudio(MetadataGuid studio)
        => [.. Entries.Where(pair => pair.Value.Any(entry => entry.StudioID == studio)).Select(pair => pair.Key)];

    public INetwork? GetNetwork(MetadataGuid id) => null;

    public IReadOnlyList<INetwork> GetNetworks(MetadataGuid entry) => [];

    public IReadOnlyList<MetadataGuid> GetEntriesForNetwork(MetadataGuid network) => [];

    public void SaveStudios(IEnumerable<MetadataStudioData> studios)
    {
        foreach (var studio in studios)
            _studios[studio.ID] = studio;
    }

    public void SetStudios(MetadataGuid entry, IEnumerable<MetadataEntryStudioData> studios)
    {
        var list = studios.ToList();
        if (list.FirstOrDefault(studio => !_studios.ContainsKey(studio.StudioID)) is { } missing)
            throw new ArgumentException($"Studio {missing.StudioID} is not stored.", nameof(studios));

        Entries[entry] = list;
    }

    public int RemoveStudios(MetadataGuid entry)
        => Entries.Remove(entry, out var removed) ? removed.Count : 0;

    public void SaveNetworks(IEnumerable<MetadataNetworkData> networks) { }

    public void SetNetworks(MetadataGuid entry, IEnumerable<MetadataGuid> networks) { }

    public void SetNetworks(MetadataGuid entry, IEnumerable<MetadataEntryNetworkData> networks) { }

    public int RemoveNetworks(MetadataGuid entry) => 0;

    public IReadOnlyList<MetadataGuid> RemoveOrphaned(MetadataSource source, DateTime orphanedBefore) => [];
}

internal sealed class FakeCharacter(MetadataCharacterData data) : ICharacter
{
    public MetadataGuid ID => data.ID;

    public MetadataCharacterData Data => data;

    public string Name => data.Name;

    public string? OriginalName => data.OriginalName;

    public CharacterType Type => data.Type;

    public IReadOnlyList<ITitle> AlternativeNames => [];

    public PersonGender Gender => data.Gender;

    public FuzzyDateOnly? BirthDay => data.BirthDay;

    public IText? DefaultOverview => data.Overview is { } overview ? new TextStub { Source = data.ID.Source, Language = TitleLanguage.English, LanguageCode = "en", Value = overview } : null;

    public IText? PreferredOverview => DefaultOverview;

    public IReadOnlyList<IText> Overviews => DefaultOverview is { } overview ? [overview] : [];

    public DateTime LastUpdatedAt => DateTime.UnixEpoch;

    public DateTime CreatedAt => DateTime.UnixEpoch;

    public DateTime? LastRefreshedAt => null;

    public IReadOnlyList<Resource> Resources => data.Resources;

    public IEnumerable<ICast<IEpisode>> EpisodeCastRoles => [];

    public IEnumerable<ICast<IMovie>> MovieCastRoles => [];

    public IEnumerable<ICast<ISeries>> SeriesCastRoles => [];
}

internal sealed class FakeCreator(MetadataCreatorData data) : ICreator
{
    public MetadataGuid ID => data.ID;

    public MetadataCreatorData Data => data;

    public string Name => data.Name;

    public string? OriginalName => data.OriginalName;

    public CreatorType Type => data.Type;

    public IReadOnlyList<ITitle> AlternativeNames => [];

    public PersonGender Gender => data.Gender;

    public FuzzyDateOnly? BirthDay => data.BirthDay;

    public FuzzyDateOnly? DeathDay => data.DeathDay;

    public string? PlaceOfBirth => data.PlaceOfBirth;

    public bool IsRestricted => data.IsRestricted;

    public IText? DefaultOverview => data.Overview is { } overview ? new TextStub { Source = data.ID.Source, Language = TitleLanguage.English, LanguageCode = "en", Value = overview } : null;

    public IText? PreferredOverview => DefaultOverview;

    public IReadOnlyList<IText> Overviews => DefaultOverview is { } overview ? [overview] : [];

    public DateTime LastUpdatedAt => DateTime.UnixEpoch;

    public DateTime CreatedAt => DateTime.UnixEpoch;

    public DateTime? LastRefreshedAt => null;

    public IReadOnlyList<Resource> Resources => data.Resources;

    public IEnumerable<ICast<IEpisode>> EpisodeCastRoles => [];

    public IEnumerable<ICast<IMovie>> MovieCastRoles => [];

    public IEnumerable<ICast<ISeries>> SeriesCastRoles => [];

    public IEnumerable<ICrew<IEpisode>> EpisodeCrewRoles => [];

    public IEnumerable<ICrew<IMovie>> MovieCrewRoles => [];

    public IEnumerable<ICrew<ISeries>> SeriesCrewRoles => [];
}

internal sealed class FakeCast(MetadataGuid entry, MetadataCastData data, FakeCharacter? character, FakeCreator? creator) : ICast
{
    public MetadataSource Source => entry.Source;

    public MetadataCastData Data => data;

    public MetadataGuid? CreatorID => data.CreatorID;

    public MetadataGuid? CharacterID => data.CharacterID;

    public MetadataGuid ParentID => entry;

    public string Name => data.Name;

    public string? OriginalName => character?.OriginalName;

    public string? Description => character?.Data.Overview;

    public string? DubGroup => data.DubGroup;

    public CastRoleType RoleType => data.RoleType;

    public TitleLanguage Language => TitleLanguage.Unknown;

    public string LanguageCode => data.LanguageCode ?? "unk";

    public IMetadata? Parent => null;

    public ICharacter? Character => character;

    public ICreator? Creator => creator;
}

internal sealed class FakeCrew(MetadataGuid entry, MetadataCrewData data, FakeCreator? creator) : ICrew
{
    public MetadataSource Source => entry.Source;

    public MetadataCrewData Data => data;

    public MetadataGuid CreatorID => data.CreatorID;

    public MetadataGuid ParentID => entry;

    public string Name => data.Name;

    public CrewRoleType RoleType => data.RoleType;

    public TitleLanguage Language => TitleLanguage.Unknown;

    public string LanguageCode => data.LanguageCode ?? "unk";

    public IMetadata? Parent => null;

    public ICreator? Creator => creator;
}

internal sealed class FakePeopleStore : IMetadataPeopleStore
{
    private readonly Dictionary<MetadataGuid, FakeCharacter> _characters = [];

    private readonly Dictionary<MetadataGuid, FakeCreator> _creators = [];

    public Dictionary<MetadataGuid, List<MetadataCastData>> Cast { get; } = [];

    public Dictionary<MetadataGuid, List<MetadataCrewData>> Crew { get; } = [];

    public IReadOnlyCollection<FakeCharacter> Characters => _characters.Values;

    public IReadOnlyCollection<FakeCreator> Creators => _creators.Values;

    public ICreator? GetCreator(MetadataGuid id) => _creators.GetValueOrDefault(id);

    public ICharacter? GetCharacter(MetadataGuid id) => _characters.GetValueOrDefault(id);

    public IReadOnlyList<ICast> GetCast(MetadataGuid entry)
        => Cast.TryGetValue(entry, out var cast)
            ? [.. cast.Select(credit => new FakeCast(
                entry,
                credit,
                credit.CharacterID is { } characterID ? _characters.GetValueOrDefault(characterID) : null,
                credit.CreatorID is { } creatorID ? _creators.GetValueOrDefault(creatorID) : null))]
            : [];

    public IReadOnlyList<ICrew> GetCrew(MetadataGuid entry)
        => Crew.TryGetValue(entry, out var crew)
            ? [.. crew.Select(credit => new FakeCrew(entry, credit, _creators.GetValueOrDefault(credit.CreatorID)))]
            : [];

    public void SaveCreators(IEnumerable<MetadataCreatorData> creators)
    {
        foreach (var creator in creators)
            _creators[creator.ID] = new(creator);
    }

    public void SaveCharacters(IEnumerable<MetadataCharacterData> characters)
    {
        foreach (var character in characters)
            _characters[character.ID] = new(character);
    }

    public int SetCast(MetadataGuid entry, IEnumerable<MetadataCastData> cast)
    {
        var list = cast.ToList();
        if (list.Any(credit => (credit.CharacterID is { } id && !_characters.ContainsKey(id)) || (credit.CreatorID is { } creatorID && !_creators.ContainsKey(creatorID))))
            throw new ArgumentException("A credit names someone who is not stored.", nameof(cast));

        Cast[entry] = list;
        return list.Count;
    }

    public int SetCrew(MetadataGuid entry, IEnumerable<MetadataCrewData> crew)
    {
        var list = crew.ToList();
        if (list.Any(credit => !_creators.ContainsKey(credit.CreatorID)))
            throw new ArgumentException("A credit names someone who is not stored.", nameof(crew));

        Crew[entry] = list;
        return list.Count;
    }

    public int RemoveCast(MetadataGuid entry)
        => Cast.Remove(entry, out var removed) ? removed.Count : 0;

    public int RemoveCrew(MetadataGuid entry)
        => Crew.Remove(entry, out var removed) ? removed.Count : 0;

    public IReadOnlyList<MetadataGuid> RemoveOrphaned(MetadataSource source, DateTime orphanedBefore)
    {
        var credited = Cast.Values.SelectMany(cast => cast.SelectMany(credit => new[] { credit.CharacterID, credit.CreatorID }))
            .Concat(Crew.Values.SelectMany(crew => crew.Select(credit => (MetadataGuid?)credit.CreatorID)))
            .OfType<MetadataGuid>()
            .ToHashSet();
        var removed = _characters.Keys.Concat(_creators.Keys).Where(id => !credited.Contains(id)).ToList();
        foreach (var id in removed)
        {
            _characters.Remove(id);
            _creators.Remove(id);
        }

        return removed;
    }
}

internal sealed class FakeRelationStore : IMetadataRelationStore
{
    public Dictionary<MetadataGuid, List<MetadataRelationData>> Entries { get; } = [];

    public IReadOnlyList<IRelatedMetadata<TBase, TRelated>> GetRelations<TBase, TRelated>(MetadataGuid entry)
        where TBase : IMetadata
        where TRelated : IMetadata
        => [];

    public void SetRelations(MetadataGuid entry, IEnumerable<MetadataRelationData> relations)
        => Entries[entry] = [.. relations];

    public int RemoveRelations(MetadataGuid entry)
        => Entries.Remove(entry, out var removed) ? removed.Count : 0;
}

internal sealed class FakeSuggestion(MetadataGuid entry, MetadataSuggestionData data) : ISuggestedMetadata<ISeries, ISeries>
{
    public MetadataGuid BaseID => entry;

    public MetadataGuid SuggestedID => data.SuggestedID;

    public ISeries? Base => null;

    public ISeries? Suggested => null;

    IMetadata? ISuggestedMetadata.Base => null;

    IMetadata? ISuggestedMetadata.Suggested => null;

    public SuggestionKind Kind => data.Kind;

    public int? Order => data.Order;

    public double? ApprovalRating => data.ApprovalRating;

    public int? ApprovalVotes => data.ApprovalVotes;

    public int? Votes => data.Votes;

    public int? Score => data.Score;

    public MetadataSource Source => entry.Source;

    public bool Equals(ISuggestedMetadata? other)
        => other is not null && other.BaseID == BaseID && other.SuggestedID == SuggestedID && other.Source == Source;

    public override bool Equals(object? obj) => obj is ISuggestedMetadata other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(BaseID, SuggestedID);
}

internal sealed class FakeSuggestionStore : IMetadataSuggestionStore
{
    public Dictionary<MetadataGuid, List<MetadataSuggestionData>> Entries { get; } = [];

    public IReadOnlyList<ISuggestedMetadata<TBase, TSuggested>> GetSuggestions<TBase, TSuggested>(MetadataGuid entry)
        where TBase : IMetadata
        where TSuggested : IMetadata
        => Entries.TryGetValue(entry, out var suggestions)
            ? [.. suggestions.Select(suggestion => new FakeSuggestion(entry, suggestion)).OfType<ISuggestedMetadata<TBase, TSuggested>>()]
            : [];

    public IReadOnlyList<ISuggestedMetadata<TBase, TSuggested>> GetSuggestedBy<TBase, TSuggested>(MetadataGuid entry)
        where TBase : IMetadata
        where TSuggested : IMetadata
        => [.. Entries
            .SelectMany(pair => pair.Value.Where(suggestion => suggestion.SuggestedID == entry).Select(suggestion => new FakeSuggestion(pair.Key, suggestion)))
            .OfType<ISuggestedMetadata<TBase, TSuggested>>()];

    public void SetSuggestions(MetadataGuid entry, IEnumerable<MetadataSuggestionData> suggestions)
        => Entries[entry] = [.. suggestions];

    public int RemoveSuggestions(MetadataGuid entry)
        => Entries.Remove(entry, out var removed) ? removed.Count : 0;
}

/// <summary>
/// An in-memory stand-in for the core's series store, which reads a saved
/// series back through mocks carrying what the other fakes hold for it.
/// </summary>
internal sealed class FakeSeriesStore(FakeTagStore tags, FakeStudioStore studios, FakePeopleStore people) : IMetadataSeriesStore
{
    public Dictionary<MetadataGuid, MetadataSeriesData> Series { get; } = [];

    public int Saves { get; private set; }

    public ISeries? GetSeries(MetadataGuid id)
        => Series.TryGetValue(id, out var data) ? Read(data) : null;

    public ISeason? GetSeason(MetadataGuid id) => null;

    public IEpisode? GetEpisode(MetadataGuid id)
        => Series.Values.SelectMany(series => series.Episodes.Select(episode => (Series: series, Episode: episode)))
            .Where(pair => pair.Episode.ID == id)
            .Select(pair => ReadEpisode(pair.Series, pair.Episode))
            .FirstOrDefault();

    public IReadOnlyList<ISeries> GetAllSeries(MetadataSource source)
        => [.. Series.Values.Where(series => series.ID.Source == source).Select(Read)];

    public IReadOnlyList<ISeason> GetAllSeasons(MetadataSource source) => [];

    public IReadOnlyList<IEpisode> GetAllEpisodes(MetadataSource source)
        => [.. Series.Values.Where(series => series.ID.Source == source).SelectMany(series => series.Episodes.Select(episode => ReadEpisode(series, episode)))];

    public int SaveSeries(MetadataSeriesData series)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (series.Episodes.Any(episode => episode.ID.Source != series.ID.Source))
            throw new ArgumentException("An episode is on another source.", nameof(series));

        Saves++;
        Series[series.ID] = series;
        return 1;
    }

    public int RemoveSeries(MetadataGuid id)
        => Series.Remove(id) ? 1 : 0;

    private ISeries Read(MetadataSeriesData data)
    {
        var series = new Mock<ISeries>();
        var titles = data.Titles;
        var mainTitle = titles.FirstOrDefault(title => title.Type is TitleType.Main) ?? titles.FirstOrDefault()
            ?? new TitleStub { Source = data.ID.Source, Language = TitleLanguage.Unknown, LanguageCode = "unk", Value = data.ID.ID, Type = TitleType.Main };
        series.SetupGet(s => s.ID).Returns(data.ID);
        series.SetupGet(s => s.Source).Returns(data.ID.Source);
        series.SetupGet(s => s.EntityType).Returns(data.ID.EntityType);
        series.SetupGet(s => s.Title).Returns(mainTitle.Value);
        series.SetupGet(s => s.DefaultTitle).Returns(mainTitle);
        series.SetupGet(s => s.PreferredTitle).Returns(mainTitle);
        series.SetupGet(s => s.Titles).Returns(titles);
        series.SetupGet(s => s.Overviews).Returns(data.Overviews);
        series.SetupGet(s => s.DefaultOverview).Returns(data.Overviews.FirstOrDefault());
        series.SetupGet(s => s.PreferredOverview).Returns(data.Overviews.FirstOrDefault());
        series.SetupGet(s => s.Type).Returns(data.Type);
        series.SetupGet(s => s.AirDate).Returns(data.AirDate);
        series.SetupGet(s => s.EndDate).Returns(data.EndDate);
        series.SetupGet(s => s.Rating).Returns(data.Rating);
        series.SetupGet(s => s.RatingVotes).Returns(data.RatingVotes);
        series.SetupGet(s => s.Restricted).Returns(data.Restricted);
        series.SetupGet(s => s.ReleaseStatus).Returns(data.ReleaseStatus);
        series.SetupGet(s => s.SourceMaterial).Returns(data.SourceMaterial);
        series.SetupGet(s => s.OriginalLanguageCode).Returns(data.OriginalLanguageCode);
        series.SetupGet(s => s.Popularity).Returns(data.Popularity);
        series.SetupGet(s => s.FavoriteCount).Returns(data.FavoriteCount);
        series.SetupGet(s => s.Resources).Returns(data.Resources);
        series.SetupGet(s => s.Episodes).Returns(() => [.. data.Episodes.Select(episode => ReadEpisode(data, episode))]);
        series.SetupGet(s => s.EpisodeCounts).Returns(new EpisodeCounts { Episodes = data.Episodes.Count });
        series.SetupGet(s => s.Tags).Returns(() => tags.GetTags(data.ID));
        series.SetupGet(s => s.Studios).Returns(() => studios.GetStudios(data.ID));
        series.SetupGet(s => s.Cast).Returns(() => people.GetCast(data.ID));
        series.SetupGet(s => s.Crew).Returns(() => people.GetCrew(data.ID));
        series.SetupGet(s => s.ShokoSeries).Returns([]);
        return series.Object;
    }

    private static IEpisode ReadEpisode(MetadataSeriesData series, MetadataEpisodeData data)
    {
        var episode = new Mock<IEpisode>();
        episode.SetupGet(e => e.ID).Returns(data.ID);
        episode.SetupGet(e => e.Source).Returns(data.ID.Source);
        episode.SetupGet(e => e.EntityType).Returns(data.ID.EntityType);
        episode.SetupGet(e => e.SeriesID).Returns(series.ID);
        episode.SetupGet(e => e.SeasonID).Returns(data.SeasonID);
        episode.SetupGet(e => e.SeasonNumber).Returns(data.SeasonNumber);
        episode.SetupGet(e => e.EpisodeNumber).Returns(data.EpisodeNumber);
        episode.SetupGet(e => e.Type).Returns(data.Type);
        episode.SetupGet(e => e.Runtime).Returns(data.Runtime);
        episode.SetupGet(e => e.AirDateWithTime).Returns(data.AirDateWithTime);
        episode.SetupGet(e => e.AirDate).Returns(data.AirDate ?? (data.AirDateWithTime is { } airedAt ? DateOnly.FromDateTime(airedAt) : null));
        episode.SetupGet(e => e.Titles).Returns(data.Titles);
        episode.SetupGet(e => e.Title).Returns(data.Titles.FirstOrDefault()?.Value ?? string.Empty);
        return episode.Object;
    }
}
