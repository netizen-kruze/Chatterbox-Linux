# VRChat output-log format (observed)

The facts `PresenceWatcher` is written against, taken from real
`output_log_*.txt` files (VRChat's LocalLow/VRChat/VRChat folder — under Proton it lives inside the game's prefix in the Steam library; see VrchatLogLocator)
(shapes verified 2026-08-28; player names anonymized).

## File behavior

- One `output_log_<datetime>.txt` per game session; the newest by write
  time is the live one. Files persist after VRChat exits — log content
  alone can NOT tell you the game is running.
- Lines are UTF-8, appended continuously while the game runs.
- VRChat's in-game **Settings → Debug → Logging** can be switched off. The
  game still creates the session's file, but nothing is written to it
  (observed 2026-09-14 on Windows: 0 bytes after 70 minutes of play; the
  Proton build is the same game). The watcher reports this as
  `VrchatLogHealth.Empty` once VRChat has been seen running for 30 s with
  no timestamped line — `NoFolder` when the folder itself was never found —
  and the Players header tells the user how to fix it.

## Line shape

```
2026.08.27 00:14:12 Debug      -  [Behaviour] Entering Room: Treehouse in the Shade
2026.08.27 00:14:12 Debug      -  [Behaviour] Joining wrld_551c30cb-…:31757~region(us)
2026.08.27 00:14:22 Debug      -  [Behaviour] OnPlayerJoined Some Player Name (usr_4b22f1ca-…)
2026.08.27 00:33:01 Debug      -  [Behaviour] OnPlayerLeftRoom
```

- Prefix: `yyyy.MM.dd HH:mm:ss` then a level word (`Debug`, `Log`, …),
  padding, `-  `, then the message.
- Instance events carry a `[Behaviour]` marker. World scripts can print
  arbitrary text, so presence events must require that marker.

## Events that matter for presence

| Event | Shape after `[Behaviour]` | Notes |
|---|---|---|
| player joined | `OnPlayerJoined <display name> (usr_…)` | display names may contain spaces and parentheses; the `(id)` suffix is a single trailing token of `[A-Za-z0-9_-]` and is **absent in some historical formats** |
| player left | `OnPlayerLeft <display name> (usr_…)` | same identity shape. **`OnPlayerLeftRoom` also exists** — a bare marker with no name; a parser keying on the `OnPlayerLeft` prefix without requiring the following space would misread it |
| self left room | `OnLeftRoom` | bare marker |
| joining a world | `Joining wrld_<id>:<instance>~…` | the full location is one whitespace-free token; the world id is the part before `:` |
| room name | `Entering Room: <world display name>` | rest of line |
| instance closed | `Instance closed: wrld_…` | also observed without the `[Behaviour]` marker |
| connection lost | contains `Lost connection to realtime network` | no leave events follow; the roster is dead |

## Duplicates & ordering

- The same `OnPlayerJoined` can repeat for one player in one session;
  dedupe on identity.
- A room change (`Joining wrld_` / `Entering Room:`) resets the roster —
  players of the new room re-announce.
- A player who joined with a uid may be logged leaving without one (and
  vice versa); leave-matching needs a display-name fallback.
