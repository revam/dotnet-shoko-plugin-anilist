# Shoko AniList Metadata

Supplies AniList anime metadata to Shoko through the metadata provider
contract, under the `anilist` source (displayed as "AniList"). It replaces the
AniList support that used to ship in the server.

`AnilistSources` registers the source, with a short description, from its
static constructor. `RegisterServices` touches the class, so the source is in
place before the core closes registration after plugin setup. The plugin reads
it as `MetadataSource.AniList`, a C# 14 extension member.

## What it does

- Refreshes an AniList anime when the core asks and writes it into the core's
  stores: the anime and its episodes in the series store, with its titles,
  English description, ratings and vote counts, release status, source
  material, original language, popularity, favorites, air and end dates, the
  adult flag as the restricted flag, its links as resources with their bare
  IDs (the AniList page, MyAnimeList, the trailer, and the sites AniList lists
  where their URLs carry an ID), and its MyAnimeList ID as a cross-source ID
  under `mal`. AniList has no seasons and no episode entity,
  so the episodes sit in no season and keep their packed IDs.
- Tells comics apart where AniList's `source` does not. AniList files Korean
  and Chinese comics as `MANGA` or `COMIC`, so for those the source material
  comes from the country of the comic the anime relates to as its source (or,
  failing that, the anime's own country): Korea gives a manhwa, China or Taiwan
  a manhua, Japan a manga and anywhere else a comic. An adult anime adapted
  from a visual novel or a video game is an eroge. An all-ages anime whose
  roots are an eroge (Fate/stay night, for example) stays a visual novel:
  AniList lists no visual novels and flags no eroge, so the adult flag is the
  only signal it gives. AniDB's source tags carry that fact instead.
- Writes tags and genres (genres as `TagKind.Genre`), studios, and cast and
  crew (on every refresh but a quick one, see below) with genders, birthdays
  (whatever parts AniList knows, so a birthday without a year is kept),
  alternative names, role notes and dub groups, relations to other anime, and
  recommendations as suggestions. AniList serves one recommendation from both
  sides, so what the other stored anime recommend naming this one is merged in
  when an anime is written.
- Keeps what only AniList has in its own database (see
  [Its own database](#its-own-database)): the season and season year, the licensed flag, the mean score, AniList's episode
  count and duration, its own recommendations before the merge, the airing
  schedule's IDs, and where the cover, banner and portraits are. It forgets
  them when the core purges the anime.
- Reads the cast and crew past their first page only while the provider's
  kinds are on: the characters, with the people voicing them, for
  `character`, and the staff for `creator`. AniList serves them 25 to a
  request, so a long show costs many requests, and both kinds start off. The
  first page of each comes with the anime at no extra request. A list that
  fits on it is written as always; a longer one is written only for an anime
  with no credits stored, so a new anime still shows its leading cast and
  crew, and the credits already stored are otherwise left as they are.
- Refreshes one staff member, character or studio on its own when the core
  asks, which it does only for a stub: an anime's refresh writes the ones it
  reads in full, so they never go stale on their own. They are the
  provider's `creator`, `character` and `studio` kinds, and their AniList
  pages are answered there. `studio` starts on: the studios come with the
  anime, so a refresh writes them whatever is turned on.
- Hands the core its images (a cover and a banner per anime, a portrait per
  character or person) and registers AniList's default template URL. Which of
  them are downloaded is set in the core's image settings for the source.
- Searches AniList and looks one anime up by its ID (the stored copy first,
  and AniList only for one not stored).
- Works out which AniList anime an AniDB anime is for the core's automatic
  search. It searches by the anime's titles (or its first prequel's), each
  with and without a sequel suffix and a subtitle, with and without the year
  of the regular broadcast, and has the core's `IMetadataMatchingEngine` judge
  the results on their titles (synonyms and romaji included), start date and
  episode count. AniList keeps every season as an anime of its own, so a title
  found only once its sequel suffix is dropped counts for less unless the
  dates agree too. The three best rated anime get their broadcast times from
  within a month of the anime's run, in one request, and are judged again
  with them, so the engine can line their episodes up with the anime's by air
  date and tell a split cour or a remake apart. AniList gives no episode
  count for an airing anime, so the search reads its next episode instead:
  the episodes aired so far count only when they are more than the AniDB
  anime has, and the first episode's air date stands in for a start AniList
  has not fully dated. Anime of unknown length still tied for the top get
  their whole schedules, aired and upcoming, in one more request, and are
  judged again with their last scheduled episode as their length. A match
  found by the prequel's title that began long before the anime gives way to
  one found by the anime's own title that did not. It hands the core every
  anime it scored, the one taken first and the rest with why they lost, and
  writes nothing: the core links what was taken and matches its episodes, or
  shows the list as a preview.
- Matches episodes with the engine again after each refresh that is not a
  quick one. A re-match leaves alone the episodes linked into another AniList
  anime and the ones the user said have no AniList episode, and matches again
  an episode linked to one AniList no longer lists.
- Linking queues no refresh in the core. The core's link routes fetch the
  anime only when it is not stored yet, has never been refreshed in full (a
  quick fetch does not count), or the request asks for a refresh. An anime
  linked by the automatic search is refreshed by the core's search job.
- Publishes AniList's broadcast times as one channel-less airing schedule per
  anime.
- Serves no endpoints of its own. The core's generic metadata routes serve
  everything under the `anilist` source: `/api/v3/Metadata/anilist/...` for the
  stored anime and episodes, the search, the lookups, the actions and the link
  export and import, `/api/v3/Series/{id}/Metadata/anilist/...` and
  `/api/v3/Episode/{id}/Metadata/anilist/...` for the links of a Shoko series
  or episode, and `/api/v3/Metadata/Provider` for the provider's settings.
- Offers the AniList scheduled actions, for admins under
  `/api/v3/Action/Scheduled`: update everything, with or without the images,
  search for matches, and purge the unused anime or every link. None runs on
  its own until an admin gives it triggers.
- Offers the per-series refresh, image, search and episode-mapping actions.
  Each action asks the core's services for the work.

## Background work

The plugin runs no queue of its own. The core runs the refresh, search, image
and purge jobs for every provider, decides when an anime is due, holds its lock
while it is refreshed, and retries a failed refresh. The plugin keeps its rate
limiter: every request to AniList goes through it, and while it backs off
the plugin's `AnilistSuspensionProvider` (named "AniList") reports a
`RateLimited` suspension (a 429 or a spent quota) or a `ServerErrors` one (its
breaker tripped after a server error), with when it expects to resume, so the
core holds the provider's jobs back.

A failure that passes (a server error, a timeout, an exhausted rate-limit
budget, or AniList out of reach) is thrown as `AnilistUnavailableException`, a
`MetadataProviderUnavailableException` carrying the time left on the pause, so
the core answers a search, lookup or preview that met it with `502` and a
`Retry-After` header. Anything else AniList answers with, such as a rejected
query, is an `AnilistApiException`.

On start the plugin registers its image template, and when no AniList anime is
stored while some are linked, as after an upgrade from a version that kept them
elsewhere, it asks the core to refresh every linked one. Once a day it asks the
core to purge the stored anime nothing links to and nothing refreshed for
longer than the setting below.

## Its own database

The plugin keeps what only AniList has in an Entity Framework Core database of
its own, `AnilistDbContext`, registered from `RegisterServices` with
`AddPluginDbContext<Plugin, AnilistDbContext>("anilist")`. The server owns the
rest: it configures the context (SQLite in WAL mode, at
`data/<plugin-id>/anilist.db3`), applies its migrations while it starts,
before `Ready`, copies the file before each one, includes it in its backups,
and removes it when the plugin is uninstalled with its data. The plugin never
names a provider.

There are three tables: `Anime` and `Episodes` keyed by AniList's ID (an
episode by its packed ID, indexed by its anime), and `Portraits` keyed by
`creator/<id>` or `character/<id>`. Scalars are columns, the season by its
name; an anime's own recommendations are a JSON column mapped as a complex
collection. Dates are kept and read back in UTC. `AnilistStore` opens a
context per call and serializes its writes.

Entity Framework Core is referenced for compiling only
(`ExcludeAssets="runtime;native"`): the server already loads it and SQLite, and
the plugin's output carries neither. The migrations are in
`source/Storage/Migrations`. Add one, from the repository root, with:

```bash
dotnet tool install --global dotnet-ef --version 10.0.12
dotnet ef migrations add <Name> --project source --startup-project design --output-dir Storage/Migrations
```

`design/` exists only for that: it references Entity Framework Core in full and
builds the context for the tool. It is not in the solution and is never
shipped. Keep migrations provider-neutral (no raw SQL), so they can later run on
a server's own database, and never edit one a release has shipped.

## Settings

| Setting | Default | What it does |
|---|---|---|
| Consider Existing Other Links | off | Leave out AniList episodes another anime is linked to when matching. |
| Auto-Search Candidate Count | 5 | How many search results to score when linking automatically. |
| Purge Unlinked After (Days) | 14 | How long an unlinked anime stays stored after its last refresh. 0 keeps it forever. |
| Recommendation Depth | While well rated | How far down the recommendations to read. |
| Image CDN URL | *(none)* | A base URL or `{0}` template for the image CDN. `ANILIST_IMAGE_CDN_URL` sets it too. |
| Rate Limit | 1 per 4 s | `ANILIST_RATE_LIMIT_MAX_REQUESTS_PER_WINDOW` and `ANILIST_RATE_LIMIT_WINDOW_DURATION_MS` set it too. |

Whether the provider answers at all, and whether it links on its own, belongs to
`IMetadataProviderManager`, not to these settings, and which images are
downloaded to the core's image settings for the `anilist` source. There is no
switch here for staff, characters or studios: the provider's `creator` and
`character` kinds, off by default, decide whether a refresh reads the cast and
crew past their first page, and with `studio` whether one is fetched on its
own.

## Building

```bash
dotnet build Shoko.Plugin.Anilist.slnx -c Release
dotnet test tests/Shoko.Plugin.Anilist.Tests.csproj
scripts/pack.sh --zip
```

The abstractions are referenced as a package pinned in `Directory.Build.props`,
with `ExcludeAssets="runtime"`: the server supplies them at runtime. Entity
Framework Core and its SQLite provider are referenced at the server's version
with `ExcludeAssets="runtime;native"`, for the same reason.

## License

MIT. See [LICENSE](LICENSE).
