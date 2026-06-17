# Client-Side Prediction with Server Reconciliation

## Goal

Allow owning clients to immediately change `WorldTransform` and selected `[Sync]` properties locally while the host/server also simulates them. On mismatch, the host sends authoritative state and the client corrects.

**Prediction is fully opt-in.** Nothing is predicted by default.

---

## Constraints

- Minimal engine changes — extend existing delta snapshot + `NetworkTable` pipeline
- **Performance-first** — hot paths must not allocate, no LINQ, no boxing, no closures on tick/network update paths
- Reuse `UserCommand.CommandNumber`, existing codegen, ownership model
- Console log on client when host **reconciliation** corrects a predicted slot (not host-initiated pushes)
- No global prediction toggle
- Follow [CONTRIBUTING.md](https://github.com/Facepunch/sbox-public/blob/master/CONTRIBUTING.md): minimal scope, `.editorconfig`, `dotnet format`
- **Minimize merge conflicts** — prefer `partial` classes and **new files** for prediction logic instead of large edits to hot existing files
- **Public API only when necessary** — everything else `internal`; public members require valid XML doc comments

---

## Public API for Game Developers

Only these are **public** (everything else `internal`):

| Member | Visibility |
|--------|------------|
| `SyncFlags.Predicted` | public enum flag |
| `NetworkFlags.PredictTransform` | public enum flag |
| `Network.ShouldSimulate` | public property + XML docs |
| `Network.SimulationInputScope()` | public method + XML docs |

Not public: `HasPrediction`, history buffer, reconcile, `PredictionHistoryBuffer`, internal input override.

### Opt-in flags

| Scope | Enable |
|-------|--------|
| Transform per **network root** `GameObject` | `NetworkFlags.PredictTransform` |
| Sync property per property on `Component` / root `GameObject` | `[Sync(SyncFlags.Predicted)]` |

`PredictTransform` only applies on the **network root** object. Ignored on descendants.

### Property writes — one code path, no manual reconcile

Developer writes assignments normally. Engine handles predict / authority / reconcile:

```csharp
Velocity = wishVelocity; // no manual "if host says otherwise revert"
```

Codegen + `Reconcile()` apply correction automatically when host snapshot mismatches.

Host authoritative overrides (knockback, teleport) also use normal assignment — host `[Sync(Predicted)]` setter writes authoritative value, syncs to owner.

### Simulation gating — `Network.ShouldSimulate` (new)

**Problem:** with prediction, host must run the **same sim code** on client-owned pawns, but `IsProxy == true` on host for those objects. Plain `if ( IsProxy ) return;` blocks host sim.

**Solution:** add to `GameObject.NetworkAccessor`:

```csharp
/// <summary>
/// True if this machine should run simulation logic for this object.
/// Non-predicted: equivalent to !IsProxy.
/// Predicted: also true on host for client-owned objects.
/// </summary>
public bool ShouldSimulate =>
    !IsProxy || ( Networking.IsHost && HasPrediction );

// internal on NetworkAccessor — reads root NetworkObject flag:
internal bool HasPrediction => go.FindNetworkRoot()?._net?.HasPrediction ?? false;
```

`HasPrediction` on `NetworkObject` is **`internal bool`**, cached at registration. `NetworkAccessor.HasPrediction` delegates to root `_net` — **not public**.

#### HasPrediction computation (internal)

Computed on **network root** `NetworkObject` only.

```csharp
// Called at end of RegisterPropertiesRecursive + when flags change + hotload
void RecalculateHasPrediction()
{
    HasPrediction =
        ( GameObject.NetworkFlags & NetworkFlags.PredictTransform ) != 0
        || dataTable.HasAnyPredictedSlot(); // any Entry.IsPredicted in this network object's table
}
```

| Source | Counts toward `HasPrediction`? |
|--------|-------------------------------|
| `NetworkFlags.PredictTransform` on root | Yes |
| `[Sync(Predicted)]` on any `Component` under this network object | Yes (registered in root `dataTable`) |
| `[Sync(Predicted)]` on root `GameObject` fields | Yes |
| `[Sync(Predicted)]` on child `GameObject` fields (snapshot mode) | No (not registered today — out of scope) |
| `[Sync(Predicted)]` on `GameObjectSystem` | **No — generator error** (out of scope) |

**Recalculate when:** `RegisterPropertiesRecursive`, `NetworkFlags` changed, hotload, `Network.Refresh`.

**Lazy opt-out:** if `!HasPrediction`, all prediction code paths early-exit; `ShouldSimulate` equals `!IsProxy`.

| Machine | Predicted pawn | Non-predicted pawn | Unowned (host) |
|---------|---------------|-------------------|----------------|
| Owner client | `ShouldSimulate=true` | `true` | — |
| Host (dedicated) | `true` | `false` | `true` |
| Other client | `false` | `false` | `false` |

**One guard for sim methods (NetworkObject / Component only):**

```csharp
protected override void OnFixedUpdate()
{
    if ( !Network.ShouldSimulate ) return;

    using ( Network.SimulationInputScope() )
    {
        InputMove();
    }
}
```

`ShouldSimulate` / `SimulationInputScope` apply to **networked GameObjects** only (pawns, props).

**Non-predicted objects:** `ShouldSimulate == !IsProxy` — same as today.

**Do not use** `Networking.IsHost || Network.IsOwner` as sim guard — on host it is true for every object on the machine.

| Property | Use for |
|----------|---------|
| `Network.ShouldSimulate` | sim / movement / FixedUpdate |
| `IsProxy` | legacy; use `ShouldSimulate` when object may be predicted |
| `Network.IsOwner` | ownership, UI, scoring |
| `Networking.IsHost` | spawn, rules, RPC host checks |
| `Network.Owner` | owning connection; use for explicit owner input (see below) |

### `UserCommand.CommandNumber` (existing, internal)

Each **fixed update**, every connected client sends a `UserCommand` to the host (`Scene.SendFixedUpdateUserCommand`, `InternalMessageType.UserCommand`). `ClientTick` at network rate only carries visibility origins.

```csharp
// UserCommand.cs — internal, monotonic counter per session
public uint CommandNumber { get; }  // 1, 2, 3, …
public ulong Actions;               // bitmask of held input actions
public Vector3 AnalogMove;          // snapshot of Input.AnalogMove for this fixed step
public Angles AnalogLook;           // snapshot of Input.AnalogLook for this fixed step
```

| Field | Purpose |
|-------|---------|
| `CommandNumber` | **Fixed-step id** for this client's input. Increments once per `FixedUpdate` on the owner client. Tags prediction history entries and aligns reconcile/truncation **one command per physics step**. |
| `Actions` | Which input actions are held (`Input.Actions` snapshot). Host applies via `Connection.ApplyUserCommand` once per fixed step. |
| `AnalogMove` | Owner's `Input.AnalogMove` for this fixed step (WASD + gamepad move stick, post-`Input.Process`). |
| `AnalogLook` | Owner's `Input.AnalogLook` for this fixed step (mouse + gamepad look, post-`Input.Process`, sensitivity applied). |

**Prediction use:** when **owner client (non-host)** writes a predicted value, tag history with that owner's latest `UserCommand.CommandNumber` (from `Connection.Local.Input.LastCommandNumber` after `SendFixedUpdateUserCommand`). Host `HostDirect` writes do **not** use history replay — clear slot instead.

Reset on disconnect / new session (`UserCommand.Reset()`).

### Input for simulation — `SimulationInputScope` (new)

**Problem today:** on host, `Input.Down("forward")`, `Input.AnalogMove`, and `Input.AnalogLook` read **host local input**, not owner client. `PlayerController` uses all three — host sim would use wrong input without help.

**What is synced today:** `UserCommand` → `Connection.Input` on host — action bitmask (`Down` / `Pressed` / `Released`), `AnalogMove`, `AnalogLook`.

**Per fixed update on owner client:** `SendFixedUpdateUserCommand` builds a command, applies it to `Connection.Local.Input` (command stream + `LastCommandNumber`), and sends `InternalMessageType.UserCommand` to the host.

**Per fixed update on host:** `Connection.ConsumeAllFixedUpdateUserCommands()` dequeues one pending command per remote connection and calls `ApplyUserCommand` **before** component `FixedUpdate`. `Pressed` / `Released` edges come from consecutive `Actions` snapshots — same semantics as the owner command stream.

#### Recommended API — scope (like existing `Input.PlayerScope`)

```csharp
/// <summary>
/// For the duration of the scope, Input.Down/Pressed/Released, AnalogMove,
/// and AnalogLook read from the connection that owns this simulation
/// (owner client on host, Connection.Local on owner client). No-op when not needed.
/// </summary>
public IDisposable SimulationInputScope()
```

**Usage — one sim method, correct input on owner and host:**

```csharp
protected override void OnFixedUpdate()
{
    if ( !Network.ShouldSimulate ) return;

    using ( Network.SimulationInputScope() )
    {
        InputMove(); // Input.Down / AnalogMove / AnalogLook work correctly
    }
}
```

| Who runs sim | Scope behaviour |
|--------------|-----------------|
| Owner client (predicting) | Redirect `Input.*` → `Connection.Local` **command stream** (`SimulationInputConnection`) |
| Host simulating client pawn | Redirect `Input.*` → `Network.Owner` command stream |
| Host on own pawn (listen) | No-op — local authority, no prediction overlay |
| Unowned on host | Redirect → `Connection.Local` (`Owner` invalid) |
| Proxy client | N/A — `ShouldSimulate` false |

**Implementation (internal, new file e.g. `SimulationInputScope.cs`):** reentrant; same save/restore pattern as `Input.PlayerScope`.

Scope resolves input connection:

```csharp
// internal only — not public API
Connection inputConnection = Network.OwnerId != Guid.Empty ? Network.Owner : Connection.Local;
```

When host sims a client-owned pawn, or when **owner client (non-host)** sims with prediction:

1. Set `Input.SimulationInputConnection` to the owner connection (`Network.Owner`, or `Connection.Local` on owner client)
2. `Input.Down` / `Pressed` / `Released` / `AnalogMove` / `AnalogLook` read from that connection's `InputState` (updated once per fixed step via `UserCommand`)
3. On dispose — restore previous `SimulationInputConnection`

Listen-server host on own pawn: **no-op** — `ShouldPredictLocally` is false; raw local `Input` is authoritative.

`BuildUserCommand` on owner client (extend `Connection.Input.cs`):

```csharp
cmd.Actions = Input.Actions;
cmd.AnalogMove = Input.AnalogMove;
cmd.AnalogLook = Input.AnalogLook;
```

`ApplyUserCommand` stores `AnalogMove` / `AnalogLook` alongside `Actions` in `Connection.InputState`.

**Why not `SimulationConnection` property?** `Network.Owner` already exists. On owner client `Owner == Local`. On host simulating client pawn `Owner == client connection`. Unowned: `OwnerId == Guid.Empty` → fall back to `Connection.Local`. Devs who want explicit input without scope: `Network.Owner.Down( "forward" )` (or `Connection.Local` for host-only).

| API | Reads from |
|-----|-----------|
| `Input.Down` / `Pressed` / `Released` inside scope | Owner connection when host sims; else local |
| `Input.AnalogMove` / `Input.AnalogLook` inside scope | Owner's latest `UserCommand` when host sims; else local |
| `Network.Owner.Down(...)` | Owner connection — explicit, no scope |
| `Input.*` outside scope | Always local machine |

**Do not silently redirect global `Input.*` outside scope.**

#### Input fixed-step alignment

`UserCommand` is sent **once per fixed update**, not once per network tick. Owner and host both advance the command stream once per physics step:

| Step | Owner client | Host |
|------|-------------|------|
| Start of `FixedUpdate` | `SendFixedUpdateUserCommand` → build, `ApplyUserCommand` locally, send to host | `ConsumeAllFixedUpdateUserCommands` → one `ApplyUserCommand` per remote connection |
| Sim (`SimulationInputScope`) | `Input.*` from local command stream | `Input.*` from owner command stream |
| `Pressed` / `Released` | Edge from consecutive local commands | Edge from consecutive received commands |

`ClientTick` at `NetworkRate` only updates visibility origins — input is not bundled there.

| Input | Owner client (in scope) | Host sim (in scope) |
|-------|------------------------|---------------------|
| `Input.Down` / `Pressed` / `Released` | Command stream (`Connection.Local`) | Owner command stream |
| `Input.AnalogMove` | Per fixed step | Owner per fixed step |
| `Input.AnalogLook` | Per fixed step | Owner per fixed step |
| Raw `Input.MouseDelta` | Local | **Not redirected** — use `AnalogLook` in sim code |

Use `Input.Pressed` / `Input.Released` freely inside `SimulationInputScope` — no game-code workarounds required.

### Prediction activation (engine-internal, client overlay + history)

```
IsOwner && !Networking.IsHost && (PredictTransform || entry.IsPredicted)
```

Listen-server host-player: `ShouldSimulate=true` on owned pawn, prediction overlay **disabled** on host machine (host is authority locally, no history/reconcile).

### Sync flag matrix

| Flags | Network write authority | Who can call setter | Client overlay |
|-------|------------------------|---------------------|----------------|
| `[Sync]` | Owner | Owner (`!IsProxy`) | No |
| `[Sync(Predicted)]` | Host | Owner client + Host | Owner client only |
| `[Sync(FromHost)]` | Host | Host only | No |
| `[Sync(FromHost \| Predicted)]` | — | **Generator error** | — |
| `[Sync(Predicted \| Interpolate)]` | Host | Owner + Host | Owner reads predicted local value; **proxies only** interpolate on read |

`Predicted` moves network authority to host. Owner client keeps local writes via codegen; host writes are always authoritative.

### Developer mental model

```
[Sync]              → "I own this, others copy me"
[Sync(FromHost)]    → "Server owns this, I only read"
[Sync(Predicted)]   → "I write immediately, server decides truth"
PredictTransform    → "I move immediately, server decides position"
ShouldSimulate         → "I should run sim logic for this object"
SimulationInputScope   → "Input.Down + AnalogMove + AnalogLook read from owner on host"
Network.Owner          → explicit owner input without scope
```

### Usage example

```csharp
protected override void OnFixedUpdate()
{
    if ( !Network.ShouldSimulate ) return;

    using ( Network.SimulationInputScope() )
    {
        InputMove();
    }
}

void InputMove()
{
    Velocity = Accelerate( WishVelocity ); // assignment only, engine reconciles
}

void ApplyKnockback( Vector3 force )
{
    if ( !Networking.IsHost ) return;
    Velocity += force; // host authoritative write
}

GameObject.Network.Flags |= NetworkFlags.PredictTransform;
[Sync(SyncFlags.Predicted)] public Vector3 Velocity { get; set; }
[Sync] public int Ammo { get; set; }
```

---

## How Prediction Works

### Roles

```
Owner client (non-host):  ShouldSimulate + predict locally, send state, accept corrections
Host:                     ShouldSimulate on predicted client pawns, authoritative sim, broadcast
Proxy clients:            !ShouldSimulate, receive authoritative state, interpolate
Listen-server host-player: ShouldSimulate, no prediction overlay (same as owner-authoritative)
```

### Host simulation requirement

Host must run **the same game sim code** (e.g. `InputMove`) for predicted client-owned objects, not a separate invisible sim. `ShouldSimulate` enables host to enter `FixedUpdate` on those pawns.

Host sim runs inside `SimulationInputScope()` so existing `Input.Down`, `Input.AnalogMove`, and `Input.AnalogLook` code reads owner `UserCommand` via owner connection — no rewrite to `Connection.*` required.

### Tick flow (predicted slot)

```
1. Owner client (non-host) changes value
   → apply immediately
   → push { commandNumber, slot, bytes } to history buffer
     (CommandNumber = UserCommand.CommandNumber at time of write)
   → mark dirty, send delta snapshot to host

2. Host: `ShouldSimulate` + `SimulationInputScope` runs same sim in FixedUpdate (authoritative)
   → host authoritative value + hash

   Host also receives owner snapshot (may arrive same tick or later)
   → compare host sim hash vs owner hash per predicted slot

3. Compare result (per predicted slot, owner-originated data only):
   → match:    no correction needed
   → mismatch: host sim value is truth → **reconciliation** on owner (+ Log.Warning)

3b. Host **directly writes** predicted slot (`HostDirect`: teleport, knockback)
   → **authoritative push** — immediate reliable send to owner
   → owner applies, **clears** history — no compare, no replay, no log

4. Host sends authoritative snapshot to ALL connections (including owner)
   Host must **write** predicted slots + transform into snapshot (today skipped: `!IsProxy` transform block, `HasControl` on sync entries)

5. Owner client receives snapshot **from host** for own object (new path):
   → `HostDirect`: apply + clear history
   → hash match (after sim compare): trim history
   → hash mismatch (reconciliation): Reconcile + Log.Warning + replay
```

### OnSnapshot / ReadSnapshot gating changes (explicit)

**Today:** transform in `OnSnapshot` only when `HasControl(source)` (owner is source). Owner never receives transform from host for own object.

**Predicted changes:**

| Source | Target | Slot | Action |
|--------|--------|------|--------|
| Owner | Host | predicted | Input for compare — host does **not** apply as truth |
| Host | Owner | predicted + `HostDirect` | Apply + clear history (push) |
| Host | Owner | predicted + mismatch | Reconcile + replay + log |
| Host | Owner | predicted + hash match | Trim history only |
| Host | Proxies | predicted | Apply + interpolate |
| Owner | Proxies | non-predicted | Unchanged (relay) |
| Anyone | Anyone | non-predicted | Unchanged |

Transform with `PredictTransform`: same rules; host→owner path **outside** `HasControl(source)` block.

`ReadSnapshot`: `source.IsHost && Network.IsOwner && entry.IsPredicted` → branch on `HostDirect` vs reconcile.

### When host overwrites

Only predicted slots when authoritative hash ≠ received owner hash.

| Case | Host overwrites? |
|------|-----------------|
| Not predicted | No — owner relay as today |
| Predicted, match | No |
| Predicted, mismatch | Yes → all clients; owner reconciles |
| Predicted, owner packet lost | Host sims from last state; corrects on next send |
| Host-initiated write (teleport) | **Authoritative push** — immediate reliable send, not reconciliation |

Transform: position/rotation/scale reconciled atomically on mismatch; host teleport uses push path.

### Authoritative push vs reconciliation

Two distinct paths when owner receives host snapshot for predicted slot:

| Path | Trigger | Owner behaviour | Log |
|------|---------|-----------------|-----|
| **Reconciliation** | Host sim hash ≠ owner hash | Apply authoritative, truncate history, replay pending | `Log.Warning` |
| **Authoritative push** | Host wrote slot directly (`__sync_SetValue` / transform on host, not compare) | Apply immediately, **clear** history for slot, no replay | None |

**Teleport example:** host sets `WorldPosition` or predicted `Velocity` on player object → engine marks slot as host-initiated → send **immediate reliable** delta (or `DeltaSnapshotSystem.Send` with `NetFlags.Reliable | SendImmediate`) → owner applies on receive, history cleared. Client does not wait for hash mismatch.

Implementation: internal flag on write path `PredictionWriteSource.HostDirect` vs `PredictionWriteSource.OwnerPredicted`. Compare path only runs for owner-originated snapshots on host.

### Reconciliation (owner client, sim mismatch only)

```
1. Log.Warning — GameObject name, entry.DebugName, commandNumber
2. Apply authoritative (IsReadingChanges = true)
3. Truncate history <= commandNumber for slot
4. Replay remaining entries (index loop, no LINQ)
5. Displayed value = replay result
```

No log on hash match.

### CommandNumber in snapshots

- Owner history entries: tag with owner client's `UserCommand.CommandNumber` at write time (one number per fixed step)
- Host reconciliation snapshot: include `commandNumber` per predicted slot (owner's last processed command when host compared) so owner truncates history correctly
- `HostDirect` snapshots: **no** commandNumber replay semantics — receiver clears history for affected slots

### Lifecycle edge cases

| Event | Action |
|-------|--------|
| Ownership change (`OnOwnerChanged`) | Clear prediction history; bump snapshot version (already clears delta state) |
| Host migration (P2P) | Full resync; clear all prediction buffers |
| `NetworkRefresh` / spawn | Full state; clear history |
| Hotload | Re-register `IsPredicted` on entries; keep or clear history (prefer clear) |

### Packet loss and unstable ping

**High ping:** client keeps predicting; late correction → reconcile + replay.

**Client→host lost:** host sims without update; owner reconciles on next authoritative packet.

**Host→client lost:** owner stays predicted until next snapshot.

**History cap (128):** oldest entries dropped; very long lag limits replay depth.

---

## Opt-In Granularity

| Scope | Toggle | Default |
|-------|--------|---------|
| Network root transform | `NetworkFlags.PredictTransform` | Off |
| Component field | `[Sync(SyncFlags.Predicted)]` | Off |
| GameObject field (network root) | `[Sync(SyncFlags.Predicted)]` | Off |

Mixed fields on one component allowed.

---

## Code Organization (merge conflict avoidance)

Prefer **new files** + `partial` over editing monolithic hot paths:

| New file | Contents |
|----------|----------|
| `PredictionHistoryBuffer.cs` | ring buffer, reconcile replay |
| `NetworkObject.Prediction.cs` | `partial NetworkObject` — history, push vs reconcile |
| `NetworkTable.Prediction.cs` | `partial NetworkTable` — predicted read/write |
| `SimulationInputScope.cs` | scope + internal input override |

Touch existing files only for: flag enums, thin hooks in `OnSnapshot` / `WriteSnapshotState`, registration in `DataTable`, minimal `NetworkAccessor` additions.

---

## Performance Requirements

### Must

- No LINQ in `Scene/Networking/`
- No per-tick GC in steady state — struct ring buffer, pre-allocated arrays
- Hash compare before deserialize / reconcile
- `HasPrediction` computed once at registration, cached internally on `NetworkObject`
- Early exit entire prediction path when `!HasPrediction`
- Log string format **only on mismatch**

### Correction log (owner client, mismatch only)

```csharp
Log.Warning(
    $"Prediction correction on {gameObject.Name}: {entry.DebugName} " +
    $"(cmd {commandNumber}) authoritative overwritten predicted state" );
```

Values in `#if DEBUG` only.

---

## Current Architecture (baseline)

- Owner-authoritative; host relays owner snapshots for owned objects
- Transform: slots 1–5, `TargetLocal`; **`WriteSnapshotState` skips transform when `IsProxy`** (line 586) — host does not emit transform for client-owned objects today
- **`NetworkTable.WriteSnapshotState` / `ReadSnapshot` gate on `entry.HasControl(source)`** — owner-controlled slots ignore host as source today
- `RemoteSnapshotState.AddPredicted` = ACK dedup, unrelated
- `UserCommand` to host each fixed update (`InternalMessageType.UserCommand`); `ClientTick` = visibility only
- Dedicated / listen / P2P: same code, `Networking.IsHost` divides prediction overlay
- `DeltaSnapshotSystem.Send`: host **already** sends for `IsProxy` objects (`IsProxy && !IsHost` skip only) — prediction must fix **what** host writes, not whether send runs

---

## Implementation Phases

### Phase 1 — Flags, registration, public API, input scope

- `SyncFlags.Predicted`, `NetworkFlags.PredictTransform`
- `NetworkTable.Entry.IsPredicted`, `dataTable.HasAnyPredictedSlot()`
- `internal HasPrediction` + `RecalculateHasPrediction()` on register / flag change / hotload
- Public: `ShouldSimulate`, `SimulationInputScope()` with XML docs
- Extend `UserCommand`: `AnalogMove`, `AnalogLook` — serialize in per-fixed `UserCommand` message, queue on host, consume once per `FixedUpdate`
- Extend `Connection.InputState` + `BuildUserCommand` to capture/restore analog values; command queue on host
- New file `SimulationInputScope.cs` — reentrant scope; sets `Input.SimulationInputConnection` (owner on host, local command stream on predicting owner client)
- `Scene.SendFixedUpdateUserCommand` at start of `InternalFixedUpdate`; `Connection.ConsumeAllFixedUpdateUserCommands` on host
- Reject `Predicted | FromHost` and `[Sync(Predicted)]` on `GameObjectSystem` in **source generator** (compile error)
- Predicted registration: host = network write authority; owner/host setter paths in codegen
- `PredictionWriteSource` enum (internal): `OwnerPredicted` vs `HostDirect`

### Phase 2 — `PredictionHistoryBuffer` (zero-GC)

- Shared internal helper; lazy init when `HasPrediction`
- Struct ring, capacity 128

### Phase 3 — Codegen + `NetworkTable` (`Component`, root `GameObject`)

**Setter paths:**

| Caller | Predicted slot |
|--------|---------------|
| Owner client (non-host) | local apply + history + dirty |
| Host | authoritative apply + dirty; mark `HostDirect` → immediate push |
| Proxy (non-host) | discard (unchanged) |

**ReadSnapshot:** host → owner: `HostDirect` = apply + clear history; mismatch = reconcile + log

### Phase 4 — Transform prediction

- Record slots 1–3 on **owner client (non-host)** write when `PredictTransform`
- Host transform write (`HostDirect`): immediate push, clear history — same as sync props
- Host→owner: push vs reconcile paths outside `HasControl(source)` block
- Host does not apply owner transform snapshot as truth when flag set

### Phase 5 — Host authoritative write + send path

- `WriteSnapshotState`: host includes transform when `PredictTransform` + `HasPrediction` (even if `IsProxy`)
- `NetworkTable`: predicted entries use host `ControlCondition`; host `WriteSnapshotState` emits authoritative values after sim
- `ReadSnapshot`: owner accepts host as source for predicted entries (bypass `HasControl(source)` for host→owner predicted)
- `HostDirect`: immediate reliable send path (existing `DeltaSnapshotSystem.Send` already runs on host for proxies)
- Owner accepts host snapshots for own predicted slots

### Phase 6 — Reconciliation, push, logging

- `Reconcile()` — sim mismatch only, owner client, + `Log.Warning`
- `ApplyAuthoritativePush()` — `HostDirect`, clear history, no log
- Snapshot metadata: `HostDirect` flag + optional `commandNumber` for reconcile path

### Phase 7 — Tests

- `ShouldSimulate` matrix (owner / host / proxy × predicted / not)
- Host knockback / teleport → authoritative push (immediate, no warning)
- Hash mismatch → reconciliation (log + replay)
- Hash match → no log
- Ownership change clears history
- Dedicated + listen host-player (no overlay)
- Host `SimulationInputScope`: `Input.Down`, `Pressed`, `Released`, `AnalogMove`, `AnalogLook` match owner per-fixed-step `UserCommand`
- Owner predicting client: `SimulationInputScope` uses local command stream; `Pressed`/`Released` match host

---

## Key Files

| Area | Files |
|------|-------|
| Core | `NetworkObject.cs`, `NetworkObject.DataTable.cs`, `NetworkTable.cs`, `DeltaSnapshotSystem.cs` |
| New | `PredictionHistoryBuffer.cs`, `NetworkObject.Prediction.cs`, `NetworkTable.Prediction.cs`, `SimulationInputScope.cs` |
| Codegen | `Component.Network.cs`, `GameObject.Network.cs` (minimal) |
| API | `GameObject.Network.cs` (`ShouldSimulate`, `SimulationInputScope` only) |
| Input | `SimulationInputScope.cs`, `Input.Actions.cs` (minimal hook) |
| Transform | `GameTransform.cs`, `GameObject.cs` |
| Tick | `Scene.Network.cs`, `UserCommand.cs` |

---

## Critical Behavior Changes

1. Owner accepts host corrections for **predicted slots only**.
2. Host simulates, **writes** authoritative predicted state into snapshots (not relay-only).
3. `WriteSnapshotState` / `ReadSnapshot` / `OnSnapshot` gating changes for predicted case.
4. `ShouldSimulate` enables host sim on client-owned predicted pawns.

---

## Out of Scope

- Raw `Input.MouseDelta` / `MouseWheel` redirect in scope (use `AnalogLook` in sim code)
- Silent global `Input.*` redirect without scope
- `NetworkMode.Snapshot` objects
- Global prediction toggle
- Public correction hooks (console log only)
- Predicted `[Sync]` on child `GameObject` fields under snapshot children (not registered today — fix separately if needed)
- `[Sync(Predicted)]` on `GameObjectSystem` — use `[Sync(FromHost)]` instead

---

## Resolved Ambiguities (reference)

| Question | Decision |
|----------|----------|
| `IsInControl` alias? | No — use `ShouldSimulate` instead (different semantics) |
| `IsOwner \|\| IsHost` for sim? | No — use `ShouldSimulate` |
| Manual reconcile in game code? | No — engine only |
| Who runs host sim? | Same `FixedUpdate` via `ShouldSimulate` |
| Host input for client pawn? | `SimulationInputScope()` → owner per-fixed-step `UserCommand` (`Actions`, `AnalogMove`, `AnalogLook`, `Pressed`/`Released`) |
| Owner input during prediction sim? | `SimulationInputScope()` → local command stream (same samples sent to host) |
| Host local input during sim? | `Input.*` outside scope, or `Connection.Local` |
| Explicit owner input? | `Network.Owner.Down(...)` — no separate property |
| `HasPrediction`? | `internal`, cached on root |
| Host teleport? | Authoritative push — immediate send, not reconciliation |
| Merge conflicts? | New `partial` files, minimal edits to existing |
| `Input.AnalogLook` on host sim? | Owner's per-fixed-step `UserCommand.AnalogLook` via scope |
| `Input.Pressed` in sim? | Supported — command stream edges on owner and host inside `SimulationInputScope` |
| `FromHost + Predicted`? | Generator error |
| Prediction on all clients for all objects? | No — owner overlay + host sim only |
| `RemoteSnapshotState.AddPredicted`? | Unchanged (ACK dedup) |
| `GameObjectSystem` prediction? | Out of scope — generator error on `[Sync(Predicted)]` |
| `ShouldSimulate` on child component? | Reads root `_net.HasPrediction` via internal `HasPrediction` |
| Snapshot commandNumber? | Included for reconcile; omitted for `HostDirect` |
| Unit tests? | Required where applicable per CONTRIBUTING.md |

---

## Plan review checklist (self-consistency)

- [x] Public API minimal: flags + `ShouldSimulate` + `SimulationInputScope` only
- [x] `HasPrediction` internal on `NetworkObject`; `NetworkAccessor.HasPrediction` reads root `_net`
- [x] Host sim uses `ShouldSimulate` + `SimulationInputScope` → owner per-fixed-step `Actions`, `AnalogMove`, `AnalogLook`, `Pressed`/`Released`
- [x] Owner predicting client uses `SimulationInputScope` → local command stream aligned with host
- [x] Two host→owner paths: **push** (`HostDirect`) vs **reconcile** (sim mismatch)
- [x] Property writes: dev assigns once; engine handles overlay / push / reconcile
- [x] Prediction scope: networked `GameObject` only — not `GameObjectSystem`
- [x] New logic in `partial` + new files; thin hooks in existing files
- [x] No LINQ / minimal GC on hot paths
- [x] `FromHost | Predicted` = generator error
- [x] Host authoritative path: fix `WriteSnapshotState` + `HasControl`, not `DeltaSnapshotSystem.Send` skip
