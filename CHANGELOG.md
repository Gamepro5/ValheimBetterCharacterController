# Changelog

## 1.0.0

First release as a single mod. Consolidates work previously split across a private
plugin (SpineAim) and two third-party mods, reimplemented so nothing needs bundling.

* **Melee aim** — raises `Attack.m_maxYAngle` for the duration of `GetMeleeAttackDir`,
  so melee attacks can be aimed up and down and an enemy on a slope is reachable. The
  value is restored immediately afterward because Attack instances come from shared
  item data.
* **Aim lean** — drives Valheim's own humanoid look-at IK so the torso pitches toward
  your aim. The look direction is clamped in yaw and pitch relative to the body's own
  facing, which is what stops the solver corkscrewing the spine when the camera orbits
  a stationary character. Skipped for `Area`/`None` attack types.
* **Diving** — overrides the vertical velocity that `Character.UpdateSwimming` uses to
  pin a swimmer to the surface, so holding the dive key descends while the mouse steers.
* **First person** — engages on zoom, positioned from the capsule hitbox rather than the
  head bone so there is no animation bob, with the body hidden via `ShadowsOnly` and the
  weapon left visible.
