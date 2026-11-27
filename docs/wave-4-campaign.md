# Wave 4 campaign workflow

## Depart

1. On a disposable save, start with `get_game_context`. Record the home `mapId` and `loadId`. Use `list_world` to identify the destination and the loaded home settlement. Plan home food, medical coverage, and defense while the travelers are away.
2. Read `get_caravan_candidates` with the home map ID. Use `view: "supplies"` for native transfer group IDs and counts. A group can contain several stacks the game treats as interchangeable. IDs and availability can change as stacks move.
3. Call `preview_caravan_draft` with `mapId`, `pawnIds`, `targetId`, and `supplies: [{"id":"<group ID>","count":10}]`. Check `accepted`, the carry limit, and the separate outbound and return estimates. Food estimates use RimWorld's calculators. The draft estimates food on the outbound route and compares that estimate to the predicted round-trip duration when both legs are available. Foraging, temperatures and consumption can change by the time the caravan returns.
4. Send the same selection to `commit_caravan_draft` with `expectedLoadId`, `expectedRevision`, and a unique `requestId`. This accepts the selected roster and cargo after reviewing the estimates. The order rechecks current availability and runs the game's formation checks. The normal result is `packing`, followed by a world caravan when the packing jobs finish. Follow `list_caravans.forming` and use `include: ["inventory"]` to verify the departed party.
5. A repeated request ID with the same arguments returns the original receipt while retained in this load. If the draft becomes stale, preview the current selection again. If native validation declines departure, check `get_events` for the game's reason.

## Travel and return

Use `advance_time` with `untilStopped: true`, `digest: true`, and stops for the applicable home and travel risks. For example:

```json
{
  "untilStopped": true,
  "speed": "Superfast",
  "digest": true,
  "stopOn": ["downed", "threatLetter", "lowFood", "caravanHungry", "caravanImmobilized", "caravanDepartureBlocked", "caravanArrived"],
  "requestId": "expedition-outbound"
}
```

Stops detect changes after the run starts. Read current hunger, immobilization, routes, and packing state before resuming. Night rest is normal. `caravanRouteEnded` also fires when routes are cancelled or the party leaves the world map. When both route-ended and arrival events occur, check the target tile or newly loaded map before recording the outcome. Pass only `caravanArrived` from that pair to stop with the more specific reason.

Read the home map explicitly with `get_colony_status(mapId: ...)` while the party travels. A home dialog can stop travel. Inspect `get_advance` for the interaction, handle it through the normal dialog path, and start another run. Use `preview_caravan_route` with `action: "enter"` and the home object's ID to plan the return. Submit it using `set_caravan_route`. Check the return with `list_caravans`, `list_pawns` on the home map, and `get_colony_status`.

## Reform after an encounter

An attack order uses `set_caravan_route` with `action: "attack"` against an already-hostile settlement. Wait for map generation to finish, then find the encounter `mapId` in `get_game_context`. Preview and commit explicitly on that map. RimWorld blocks reforming while its threats or other restrictions remain. On maps with a retained route, omit `targetId` and let RimWorld restore that route. After reforming, preview any new route from the new caravan ID.

The reform inherits the game's initial supply selection, including carried cargo. Pass a count of zero to remove a native default group. Use the preview to verify what will leave the map. Gravship or shuttle departure confirmations still use the in-game dialog.

## Recorded live verification

On RimWorld 1.6.4871 rev597, the copied Benevolina save sent Willow with 15 packaged meals and three medicine. The draft estimated a 4.47-day round trip. Native packing produced caravan 94. A stale draft and an overweight plan were refused, and replaying the commit created one formation. The home colony stayed readable during travel. A Solar Panels dialog stopped the first run, and answering it let travel resume. The caravan reached Swampston at tick 498427 and Willow returned to the loaded home map at tick 629914. The resulting home read showed zero stored nutrition and loose food remained.

A separate controlled copy placed the already-hostile Squid Bramble on the caravan's next tile. The native attack generated the settlement map and reforming was blocked with active defenders. After controlled clearance, native reform produced caravan 95 with Willow and its cargo. A peaceful meeting paused travel, and the party resumed after the Move on option and returned to home at tick 629553. Squid Bramble remained hostile on the world map.
