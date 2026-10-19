# LOCKED — Prototype Architecture & MVP Roadmap

## What's in this folder

| File | What it is |
|---|---|
| `index.html` | The playable browser game (singleplayer vs bots). Double-click to open it in Chrome, Edge, Firefox or Safari. |
| `three.min.js` | The 3D library the game uses (three.js r128), so the game also works offline. |
| `three-LICENSE.txt` | three.js MIT licence. |
| `*.cs` (8 files) | Unity C# scripts for the multiplayer version. Copy them into `Assets/Scripts/` of a Unity project. |
| `README.md` | This file: architecture, Unity setup and roadmap. |

Browser controls: WASD move · Shift sprint · C crouch · Space jump · L lock · hold K to free a teammate · click or drag to look · P or Esc to pause · M to mute.
Keep `index.html` and `three.min.js` together in the same folder.

Engine: **Unity 6 (or 2022.3 LTS)** · Netcode: **Netcode for GameObjects 1.8+ / 2.x** · Transport: Unity Transport + Relay/Lobby (UGS)

---

## 1. System architecture

```
┌──────────────────────────── PRESENTATION (client only) ────────────────────────────┐
│ HUD (phase timer, lock count) · MapPingUI · RescueRing · Skin picker · Lobby UI   │
│ Cinemachine 3rd-person camera · Animator (reads IsCrouched / IsClimbing / speed)  │
└─────────────▲───────────────────────────── events / NetworkVariables ─────────────┘
              │
┌─────────────┴──────────────── GAMEPLAY (server-authoritative) ────────────────────┐
│ MatchManager      phase state machine, 25 s pings, Chewing Gum, role rotation     │
│ PlayerState       role, locked/frozen/tagged, locks, skin, buffs (replicated)     │
│ PlayerInteractor  Tag / L-Lock / K-Key — client sends intent, server validates    │
│ PowerUpSpawner*   PowerUp* (SpeedBuff, Decoy, Radar…)                              │
│ BotBrain*         NavMesh + utility AI, drives the same input API as humans       │
└─────────────▲─────────────────────────────────────────────────────────────────────┘
              │
┌─────────────┴──────────────── SIMULATION (owner) ─────────────────────────────────┐
│ PlayerMovement    CharacterController: walk/sprint/jump/crouch/climb/mantle        │
│ ClientNetworkTransform (owner-authoritative replication for MVP)                  │
└─────────────▲─────────────────────────────────────────────────────────────────────┘
              │
┌─────────────┴──────────────── DATA ───────────────────────────────────────────────┐
│ GameConfig (all tunables) · SkinDefinition (cosmetic + passive) · MapDefinition*  │
└───────────────────────────────────────────────────────────────────────────────────┘
                                                              * = not yet written
```

**Authority model.** Movement is owner-authoritative (instant feel); *everything that decides
who wins* — tags, locks, rescues, phases, role swaps — is decided only by the server.
Clients send "I pressed L", never "I locked player 7".

**Singleplayer = multiplayer.** Singleplayer starts a Host with no remote clients and spawns
bot players owned by the server. Bots implement `IBotBrain` and write `PlayerMovement.MoveInput`
/ call `PlayerInteractor.ServerTryTag()` etc. One code path, one set of bugs.

### Match state machine

```
Lobby ─StartMatch─▶ HunterFreeze (10 s, Hunters frozen, Runners scatter)
                        │
                        ▼
                     Hunt ── every 25 s: ping all un-locked Runners
                     │  │
       hunt timer ───┘  └── a Hunter reaches 3 locks (or all Runners locked)
       expires                │
          │                   ▼
          │         ChewingGum (all Runners freed; 3 s Hunter head start;
          │                     Runners tag the Hunter to become safe;
          │                     last un-safe Runner(s) lose)
          ▼                   │
      RoundEnd ◀──────────────┘   losers ⇒ next round's Hunters
          │
          └──▶ HunterFreeze …
```

### Lock & Key loop

1. **Tag** (LMB, Hunter): Runner within `tagRange`, inside a 90° cone, with line of sight, not immune →
   Runner becomes *tagged* for `tagWindowSeconds` (2.5 s) and is slowed.
2. **Lock** (L, Hunter): only the Hunter who tagged them, within `lockRange`, before the window closes.
   Escaping the window = clean getaway. This gives the spec's two-step "tag, then L" real skill tension.
3. **Key** (hold K, Runner): within `keyRange` of a locked teammate for `unlockSeconds` (1.5 s).
   Two rescuers stack. Letting go bleeds progress. Freed Runners get 3 s immunity (anti tag-farming).

### Files

| File | Responsibility |
|---|---|
| `Enums.cs` | `PlayerRole`, `MatchPhase`, `IBotBrain` marker |
| `GameConfig.cs` | Every tunable (timers, ranges, thresholds) |
| `SkinDefinition.cs` | Skin visuals + passive multipliers |
| `PlayerState.cs` | Replicated per-player state, server mutators, stat calculation |
| `PlayerMovement.cs` | Movement, crouch-for-cover, climb + mantle, server-requested teleport |
| `PlayerInteractor.cs` | Tag / Lock / Key input → server validation, bot API |
| `MatchManager.cs` | Phases, pings, Chewing Gum, losers → Hunters |
| `ClientNetworkTransform.cs` | Owner-authoritative transform sync |

---

## 2. Scene setup (≈20 min)

1. Install packages: **Netcode for GameObjects**, **Unity Transport**, **Cinemachine**, **AI Navigation**.
2. *Project Settings → Player → Active Input Handling* = **Both** (scripts use legacy `Input` for now).
3. Layers: `Player`, `Climbable`. Exclude `Player` from `environmentMask` and `lineOfSightBlockers`.
4. Create `GameConfig` (Create → Locked → Game Config) and 3–4 `SkinDefinition` assets.
5. **Player prefab**: CharacterController (radius 0.35, pivot at feet), `NetworkObject`,
   `ClientNetworkTransform`, `PlayerState`, `PlayerMovement`, `PlayerInteractor`. Assign GameConfig.
   Register it as the NetworkManager's Player Prefab.
6. **Scene**: `NetworkManager` (+ UnityTransport), an empty `MatchManager` with `NetworkObject` +
   `MatchManager` (assign config + spawn points). A greybox map with some cubes on the `Climbable` layer.
7. Temporary debug buttons: Host / Client / `MatchManager.Instance.ServerStartMatch()`.
   Use **Multiplayer Play Mode** to run 2–4 players in the editor.

**Controls:** WASD move · Shift sprint · Ctrl/C crouch · Space jump · walk into Climbable to climb ·
LMB tag · L lock · hold K free a teammate.

---

## 3. Design decisions I made (change in GameConfig / MatchManager)

| Gap in the spec | Prototype answer | Why |
|---|---|---|
| What if Hunters never get 3 locks? | `huntSeconds` cap (240 s); Runners win, new random Hunters | Rounds must end |
| Fewer than 3 Runners | Gum threshold = min(3, runner count) | Small lobbies still reach the climax |
| Multiple Hunters share locks, all Runners locked | Top locker starts Chewing Gum | No stalemate |
| Who sees the ping? | Event goes to everyone; UI shows it to Hunters (Runners get a "you were pinged" flash) | Runners should know they're exposed |
| Other Hunters during Chewing Gum | Frozen / spectate | Keeps the phase a clear 1-vs-many chase |
| "Last to tag loses" with >1 Hunter next round | Last *N* un-safe Runners lose, N = this round's Hunter count | Keeps Hunter count stable |
| Locked Runners when Gum starts | All freed | Everyone must participate in the chase |

---

## 4. MVP roadmap (≈12 weeks, 1–2 devs)

| # | Milestone | Weeks | Deliverables | Exit criteria |
|---|---|---|---|---|
| **M0** | Foundations | 1 | Project, packages, layers, greybox map, Player prefab, these scripts compiling | Host + 1 client walk, crouch, climb |
| **M1** | Core loop | 2–3 | Tag → L → K working; MatchManager phases; HUD timer + lock counter; rescue ring | 4 players play a full round in-editor |
| **M2** | Tracking & Chewing Gum | 4–5 | Minimap + ping blips; Gum phase UI, head-start countdown; round result screen; role rotation | 3 consecutive rounds rotate Hunters correctly |
| **M3** | Bots / singleplayer | 6–7 | `BotBrain` (NavMesh, NavMeshLinks for climbs): Runner flee/hide/rescue, Hunter patrol/chase/use pings | Solo player vs 5 bots is fun for 10 min |
| **M4** | Online lobbies | 8 | UGS Lobby + Relay, join codes, map select, ready-up, host migration fallback = end match | 6 players across the internet, <150 ms |
| **M5** | Content | 9–10 | 2 maps (no dead ends, loops + vertical routes), 4 power-ups, 4 skins with passives | Playtest: no "camping" spots found |
| **M6** | Polish & playtest | 11–12 | Animations, SFX (lock clank, key jingle, ping sonar), settings, rebinding (new Input System), balance pass | 3 external playtests, top-5 issues fixed |

**Power-up starter set:** Runner — *Smoke Bomb* (breaks line of sight), *Ghost Step* (skip next ping);
Hunter — *Radar* (instant extra ping), *Sprint Gum* (+25% speed 5 s). All via `SpeedBuff`-style
server-set stats so they replicate for free.

**Map rules (no dead ends):** every room ≥2 exits; every climb has an alternate floor route;
cover clusters every ~15 m; Hunter spawn ≥40 m path distance from Runner spawn.

### Post-MVP technical debt to plan for
- Server-authoritative movement with client prediction (anti-speedhack) + lag-compensated tag checks.
- Migrate input to the new Input System (gamepad, rebinding).
- Dedicated servers (Multiplay / Edgegap) if lobbies exceed ~8 players or go competitive.
