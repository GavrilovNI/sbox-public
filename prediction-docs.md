# Client-Side Prediction

Prediction lets the **owning client** apply movement and selected sync properties immediately, while the **host** runs the same simulation authoritatively. If the host disagrees with the client, the engine corrects the owner automatically. There is no global toggle — you opt in per object and per property.

Nothing is predicted unless you enable it.

---

## Quick start

```csharp
public sealed class PredictedPlayer : Component
{
	protected override void OnFixedUpdate()
	{
		if ( !Network.ShouldSimulate ) return;

		using ( Network.SimulationInputScope() )
		{
			SimulateMovement();
		}
	}

	void SimulateMovement()
	{
		if ( Input.Pressed( "Jump" ) && controller.IsOnGround )
			controller.Punch( Vector3.Up * JumpSpeed );

		Velocity = Accelerate( WishVelocity );
		WorldPosition += Velocity * Time.Delta;
	}

	void ApplyKnockback( Vector3 force )
	{
		if ( !Networking.IsHost ) return;
		Velocity += force;
	}
}

// On the network root GameObject (e.g. in OnAwake or spawn setup):
Network.Flags |= NetworkFlags.PredictTransform;

// On the component:
[Sync( SyncFlags.Predicted )] public Vector3 Velocity { get; set; }
[Sync] public int Ammo { get; set; }
```

Assign values normally. Do **not** write manual reconcile or rollback logic.

---

## Opt-in flags

| What to predict | How to enable | Where it applies |
|-----------------|---------------|------------------|
| Transform (position / rotation / scale) | `Network.Flags \|= NetworkFlags.PredictTransform` | **Network root** `GameObject` only — ignored on child objects |
| A sync property | `[Sync( SyncFlags.Predicted )]` | `Component` or **root** `GameObject` fields |

An object is treated as predicted when it has `PredictTransform` and/or at least one `[Sync(Predicted)]` property registered on that network object.

### Valid combinations

| Attribute | Meaning |
|-----------|---------|
| `[Sync]` | Owner-authoritative (default). No prediction. |
| `[Sync( SyncFlags.Predicted )]` | Host-authoritative; owner writes locally; engine reconciles. |
| `[Sync( SyncFlags.FromHost )]` | Host-only writes; clients read. No prediction. |
| `[Sync( SyncFlags.Predicted \| SyncFlags.Interpolate )]` | Predicted + proxies interpolate on read. |
| `[Sync( SyncFlags.FromHost \| SyncFlags.Predicted )]` | **Compile error** |

`[Sync(Predicted)]` on `GameObjectSystem` is **not supported** (compile error). Use `[Sync(FromHost)]` there instead.

---

## Simulation: `Network.ShouldSimulate`

Use this instead of `if ( !IsProxy ) return;` when an object may be predicted.

| Machine | Non-predicted pawn | Predicted pawn |
|---------|-------------------|----------------|
| Owner client | `true` | `true` |
| Host (dedicated) | `false` | `true` |
| Other clients (proxy) | `false` | `false` |
| Listen-server host on **own** pawn | `true` | `true` |

`ShouldSimulate` is equivalent to `!IsProxy` for objects without prediction.

**Do not** use `Networking.IsHost || Network.IsOwner` as a simulation guard. On the host machine that is true for every object you own locally.

### Recommended pattern

```csharp
protected override void OnFixedUpdate()
{
	if ( !Network.ShouldSimulate ) return;

	using ( Network.SimulationInputScope() )
	{
		RunSimulation();
	}
}
```

| API | Use for |
|-----|---------|
| `Network.ShouldSimulate` | Movement, physics, gameplay sim in `FixedUpdate` |
| `Network.IsOwner` | UI, scoring, owner-only effects |
| `Networking.IsHost` | Spawning, game rules, host-only RPC checks |
| `IsProxy` | Legacy; prefer `ShouldSimulate` when prediction may be enabled |

---

## Input: `Network.SimulationInputScope()`

Simulation input is sampled **once per fixed update** on the owner client and replayed on the host at the same cadence. This keeps `Input.Pressed`, `Input.Released`, `Input.AnalogMove`, and `Input.AnalogLook` aligned between owner prediction and host authority.

Wrap all sim code that reads `Input.*` in `SimulationInputScope()`:

```csharp
using ( Network.SimulationInputScope() )
{
	InputMove();
}
```

| Who runs sim | Scope behaviour |
|--------------|-----------------|
| Owner client (predicting) | `Input.*` reads from the **local command stream** (same samples sent to the host) |
| Host simulating a client pawn | `Input.*` reads from the **owner connection** command stream |
| Listen-server host on own pawn | No-op — host is local authority |
| Proxy client | N/A — `ShouldSimulate` is false |

### Per fixed update (engine, automatic)

| Step | Owner client | Host |
|------|-------------|------|
| Start of `FixedUpdate` | Build `UserCommand`, apply locally, send to host | Consume one queued `UserCommand` per remote connection |
| Sim in scope | `Input.*` from command stream | `Input.*` from owner command stream |

`ClientTick` at network rate only updates visibility — input is **not** sent there.

### What is redirected inside the scope

| Input | Owner client (predicting) | Host (via scope) |
|-------|--------------------------|------------------|
| `Input.Down` / `Pressed` / `Released` | Local command stream | Owner command stream |
| `Input.AnalogMove` | Per fixed step | Owner per fixed step |
| `Input.AnalogLook` | Per fixed step | Owner per fixed step |
| `Input.MouseDelta` | Local | **Not redirected** — use `AnalogLook` in sim code |

Use `Input.Pressed` and `Input.Released` in sim code — no custom edge tracking needed.

Input outside the scope is always **local machine** input (UI, menus, debug). The engine does not silently redirect global `Input.*`.

### Explicit owner input (no scope)

```csharp
Network.Owner.Down( "forward" );
```

Useful for one-off checks without wrapping the whole sim method.

---

## Property writes

Write predicted state the same way as non-networked code:

```csharp
Velocity = wishVelocity;
WorldPosition += delta;
```

The engine handles:

- **Owner client (non-host):** apply immediately, keep a short history tagged by `UserCommand.CommandNumber` for replay after correction.
- **Host:** authoritative simulation; writes are truth for all clients.
- **Host knockback / teleport:** normal assignment on the host → immediate authoritative push to the owner (no warning log).
- **Mismatch after sim:** host sends authoritative state → owner **reconciles** (apply, trim history, replay pending inputs). A `Log.Warning` is printed on the owner client for reconciliation only — not for host-initiated pushes.

You do not call reconcile manually.

### What not to predict

Avoid `[Sync(Predicted)]` on values that integrate every frame but are not tied to command replay (e.g. `TimeSinceLastJump += Time.Delta`). Use a private `TimeSince` field instead, or derive from command-numbered events.

### Listen server

If you are the host **and** the owner of your pawn, you simulate locally with full authority. The client-side prediction overlay (history / reconcile) is **not** active on the host machine for your own objects.

---

## Transform prediction

Enable on the **network root**:

```csharp
go.Network.Flags |= NetworkFlags.PredictTransform;
```

- Owner client moves the transform immediately.
- Host simulates the same transform authoritatively (when `ShouldSimulate` is true).
- Position, rotation, and scale are reconciled **together** on mismatch.
- Host teleport: set `WorldPosition` / transform on the host — pushed to the owner immediately.

`PredictTransform` on a child object is ignored. Set the flag on the root networked object.

---

## Mixing predicted and non-predicted state

A single pawn can mix flags:

```csharp
[Sync( SyncFlags.Predicted )] public Vector3 Velocity { get; set; }
[Sync] public int Ammo { get; set; }
[Sync( SyncFlags.FromHost )] public float RoundTime { get; set; }
```

- `Ammo` stays owner-authoritative (`[Sync]`).
- `RoundTime` is host-only (`[Sync(FromHost)]`).
- `Velocity` is predicted.

---

## Mental model

```
[Sync]                 → "I own this; others copy me"
[Sync(FromHost)]       → "Server owns this; I only read"
[Sync(Predicted)]      → "I write immediately; server decides truth"
PredictTransform       → "I move immediately; server decides position"
ShouldSimulate         → "This machine should run sim logic for this object"
SimulationInputScope   → "Input reads from the per-fixed-step command stream"
```

---

## Checklist

1. Enable `PredictTransform` on the network root if you predict movement.
2. Mark sim-driven properties with `[Sync(Predicted)]`.
3. Replace `if ( !IsProxy )` with `if ( !Network.ShouldSimulate )` in sim code.
4. Wrap sim that uses `Input.*` in `using ( Network.SimulationInputScope() )`.
5. Use `Input.Pressed` / `Input.Released` / `Input.AnalogLook` inside the scope — they match host sim.
6. Keep host-only gameplay (knockback, teleport, round rules) behind `Networking.IsHost` — assign predicted fields normally on the host.
7. Do not implement manual rollback or correction.

---

## Limitations

- No global prediction toggle.
- No prediction on `GameObjectSystem` sync properties.
- No prediction on child `GameObject` snapshot fields (only components under the network object and root `GameObject` fields).
- `Input.MouseDelta` is not redirected on the host — use `AnalogLook` in simulation code.
- Raw `Input.*` outside `SimulationInputScope` always reads local input.
- Integrating floats (`+= Time.Delta`) should not be `[Sync(Predicted)]` unless tied to deterministic command replay.
