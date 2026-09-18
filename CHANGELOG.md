# Changelog

## 1.0.6

Quiet by default. With first person confirmed working, the diagnostics added while chasing it
are no longer worth writing to everyone's log every session:

* Startup logs a single `BetterCharacterController 1.0.6 loaded.` line. The version is kept in
  it deliberately - a stale DLL is otherwise invisible, and that cost a long debugging detour.
* The patched-method list and the first-person enter/leave lines now require
  `debugLogging = true`, as the lean, dive and camera heartbeats already did.
* Still unconditional, because each only appears when something is actually wrong: conflicting
  plugin warnings, a missing `GameCamera.LateUpdate` patch, the body-still-hidden failsafe, and
  feature faults.

## 1.0.5

Instrumentation, because "first person does nothing and logs nothing" was ambiguous between
two very different causes and I had been guessing between them.

* **Startup lists every patched method**, e.g.
  `patched 6 methods: Attack.GetMeleeAttackDir, Character.SetVisible, ...`, and warns explicitly
  if `GameCamera.LateUpdate` is missing from that list. A patch that silently fails to apply is
  indistinguishable from a feature that does not work, and only the first can be ruled out this
  way.
* **The refusing path now has a heartbeat** (with `debugLogging = true`). Previously, when first
  person declined to engage it returned in silence, so no log output meant either "the zoom never
  got low enough" or "the patch never ran" - with no way to tell which. It now reports the
  blocking condition once a second along with the live numbers:
  `fp idle: zoom 2.30 > 0.60 | distance=2.30 minDistance=0.00 enterAt=0.60 allowFullZoom=True`.

## 1.0.4

Diagnostics, after a report of the mod apparently doing nothing while logging nothing:

* The load line is now a **Message** rather than Info, and states which features are enabled:
  `BetterCharacterController 1.0.4 loaded. melee aim=True lean=True dive=True firstPerson=True`.
  If that line is absent from `BepInEx/LogOutput.log`, the plugin is not loading at all and no
  amount of configuration will change its behaviour - check the DLL is in `BepInEx/plugins`.
* **Conflicting plugins are detected and warned about at startup.** Two plugins patching the
  same methods each keep their own state, so they fight over the camera every frame and over
  hiding the body - which looks exactly like a bug in this mod. Checked GUIDs: this mod's
  predecessor `gameprog.spineaim`, plus WatchWhereYouStab, VikingsDoSwim and
  VerticallyChallenged, whose features this mod reimplements.

## 1.0.3

**Body-hiding rescans are now event-driven instead of polled.** The additive rescan introduced
in 1.0.1 needs to notice renderers that do not exist yet — equipping armour or a weapon
instantiates new models under the player — and with no event hooked, the only way to notice was
to re-walk the hierarchy and look. `GetComponentsInChildren<Renderer>` walks every descendant
and allocates a fresh array per call, so 1.0.1 did that every frame and 1.0.2 every 30 frames,
for a state that changes maybe once a minute.

There is no "equipment changed" event: `VisEquipment.CustomUpdate` calls `UpdateVisuals`
unconditionally every frame, with the skip-unchanged guards further in, so hooking either is no
better than polling. What is worth hooking is where objects are actually created — every
equipment model in `VisEquipment` comes from one of exactly three methods, `AttachItem`,
`AttachArmor` and `AttachBackItem`. A postfix on those (all overloads) sets a flag, and the
rescan consumes it. Idle frames now do no hierarchy walk at all.

## 1.0.2

**Fixed: first person engaged for a single frame and immediately dropped out.**

The engage condition and the exit condition shared one threshold, so anything that nudged
`m_distance` as first person engaged put the two in direct conflict and the mode flickered in
and out every frame. There is now hysteresis: entry below `zoomThreshold` (0.6), exit only
above `exitZoomThreshold` (1.2).

**Fixed: a transient error could disable a feature permanently.** Each feature's error handler
set its own `ConfigEntry.Value = false`, and BepInEx persists config writes to disk - so one
exception silently wrote `enabled = false` into the config file and the feature stayed dead
across restarts, with nothing obviously wrong. Faults are now tracked in session-only flags
and the config file is never written to at runtime.

Also: entering and leaving first person is now logged with the reason, e.g.
`first person off: zoom 1.35 > 1.20` or `attached (sitting/riding)`, so a refusal to engage can
be read straight out of `LogOutput.log` instead of inferred. Renderer rescanning is periodic
again rather than every frame, which avoids walking the whole player hierarchy each frame while
keeping the additive behaviour that 1.0.1 introduced.

## 1.0.1

**Fixed: the player model could stay hidden after leaving first person.**

`Character.SetVisible` early-returns when the requested value already matches its backing
flag (`if (m_lodVisible == value) return;`). First person suppresses that call so the engine
cannot LOD-cull the body out from under the camera - but a suppressed call never updates the
flag, so the game believes the LODGroup is already in the state it asked for and does not
retry once suppression stops. If it tried to make the body visible while suppressed, the
model stayed culled indefinitely.

Exiting first person now forces `SetVisible(true)` once, clearing `m_lodVisible` first so the
equality check cannot swallow it.

Two related hardening changes:

* Original `shadowCastingMode` values are recorded per renderer in a dictionary, written once
  and never overwritten. The previous list was cleared and repopulated on each rescan, which
  could re-capture an already-concealed value as the "original" and make the hide permanent.
* Renderer hiding is now additive rather than clear-and-rescan, and a failsafe restores the
  model (with a logged warning) if anything is still concealed while first person is inactive,
  so a missed transition recovers on the next frame instead of leaving you invisible.

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
