# Fixtures

Hand-written in the shape of AniList's GraphQL responses to the queries in
`AnilistApiClient`. They cover the fields the plugin reads, including the awkward
cases: a relation and a recommendation to a manga, a deleted recommendation, a
character nobody voices, a person credited twice, and dates with missing parts.

The `media-source-*` files hold only the fields that decide the source material:
a manhwa and a manhua adaptation, an adult visual novel, and a Korean anime with
no relation to its comic.

`airing-schedules-page.json` answers the batched `airingSchedules` query the
automatic search sends for its best candidates: one anime's first three slots,
another's numbered on from a first cour, and a slot with no time.
