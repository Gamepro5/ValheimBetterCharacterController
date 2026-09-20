# Changelog

## 1.0.2

**First person now explains itself when it refuses to engage.** If the zoom bottoms out at the
game's minimum but never reaches the threshold, a warning is logged once - without needing
`debugLogging`, since anyone hitting this has no reason to suspect a config value:

```
first person never engages: the zoom bottoms out at 1.50m but engaging needs 0.60m or less
(minDistance=1.50, allowFullZoom=True). Something else is re-clamping the camera's minimum
distance - another camera mod is the usual cause.
```

This is the one failure that could not previously be reported: the refusing path is silent by
design, so "first person does nothing" produced no output at all and looked identical to the mod
not being loaded.

## 1.0.1

**Fixed: in multiplayer the aim lean pointed every player the same way.** `localPlayerOnly` now
defaults to `true`.

The old default applied the lean to every character on the assumption that look direction is
networked. It is not. `Character.GetLookDir()` returns `m_eye.forward`, and only the local
player's eye is driven by mouse input (`Player.SetMouseLook` → `SetLookDir`); for a remote player
that transform carries no pitch at all. Feeding it to the IK solver aimed everyone alike, so you
could no longer tell where anyone was looking — worse than vanilla.

Remote players are now left alone. Showing their real aim would require syncing pitch over a
custom RPC that every client runs, which is a feature rather than a fix and is not implemented.

## 1.0

First release. Four client-side features, each of which had to work around an existing engine
behaviour — the reasoning is in the README, since it is the part worth keeping.

* **Melee aim** — melee attacks can be aimed up and down, so an enemy on a slope is reachable.
  Vanilla caps the vertical angle per attack in `Attack.m_maxYAngle`; the cap is raised for the
  duration of `GetMeleeAttackDir` and restored immediately, because Attack instances come from
  shared item data.
* **Aim lean** — the upper body pitches toward your aim. Valheim already has humanoid look-at IK
  with a body weight, but `CharacterAnimEvent.UpdateLookat` zeroes the weight during attacks,
  which is why neither melee nor bow aiming tilts the body. The look direction is clamped in yaw
  and pitch relative to the body's own facing, without which the IK solver corkscrews the spine
  when the camera orbits a stationary character.
* **Diving** — swim downward instead of being pinned to the surface, by overriding the vertical
  velocity `Character.UpdateSwimming` uses to hold a swimmer at `GetLiquidLevel() - m_swimDepth`.
* **First person** — engages on zoom, with no head bob. Height comes from the capsule hitbox
  rather than the head bone, so no animated transform feeds the view; the body is hidden via
  `ShadowsOnly` so Unity keeps animating the skeleton and the held weapon still swings.
