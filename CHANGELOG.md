# Changelog

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
