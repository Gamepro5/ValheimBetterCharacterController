# BetterCharacterController

Client-side character and camera improvements for Valheim. One DLL, no dependencies beyond
BepInEx, nothing installed on the server.

| Feature | What it does |
|---|---|
| **Melee aim** | Melee attacks can be aimed up and down, so an enemy on a slope is reachable |
| **Aim lean** | The upper body pitches toward your aim, so the swing matches where the hit lands |
| **Diving** | Swim downward instead of being stuck at the surface |
| **First person** | Engages on zoom, no head bob, body hidden and weapon visible |

Built and tested against Valheim `l-1.0.14` (build 25364309) with BepInEx `5.4.23.5`.

Installable with any BepInEx mod manager, or by dropping the DLL in `BepInEx/plugins`.

## Install

1. Install BepInEx for Valheim if it isn't already (ValheimPlus ships it).
2. Drop `BetterCharacterController.dll` into `<Valheim>/BepInEx/plugins/`.
3. Launch once; the config appears at
   `BepInEx/config/gameprog.bettercharactercontroller.cfg`.

Works alongside **ValheimPlus**: everything here is cosmetic or local movement, and no
networked state is changed, so V+'s `enforceMod` version check is unaffected.

**If you use ValheimPlus, turn off its `[FirstPerson]` section** (`enabled = false`).
Both move the camera and they will fight. This mod needs no hotkey — scroll in and it
takes over.

## Configuration

Everything is configurable; the defaults are what the features were tuned to. The dials
that matter most:

| Setting | Default | Notes |
|---|---|---|
| `01 - Melee aim / maxAngleDegrees` | 45 | Vertical aim allowed for melee. 90 lets you hit straight up and down, but the camera sits above the character, so attacks can then pass under a close target |
| `02 - Aim lean / bodyWeight` | 0.55 | How far the torso leans. The main dial |
| `02 - Aim lean / maxYawDegrees` | 50 | Spine-twist guard; lower it if the torso ever corkscrews |
| `03 - Diving / diveKey` | LeftControl | Hold and steer with the mouse |
| `04 - First person / zoomThreshold` | 0.6 | Zoom distance at which first person engages |
| `04 - First person / exitZoomThreshold` | 1.2 | Zoom distance at which it disengages. The gap is hysteresis — with one shared threshold the mode flickers in and out every frame |
| `04 - First person / eyeDropFromTop` | 0.12 | Eye height below the top of the hitbox |

### Is it even running?

`BepInEx/LogOutput.log` should contain, at startup:

```
[Info   : BetterCharacterController] BetterCharacterController 1.1.0 loaded.
```

If that line is missing, the plugin is not loading and nothing in the config will change
anything — check the DLL really is in `BepInEx/plugins`. **Check the version in that line before
debugging anything else**: a stale DLL behaves exactly like a mod that ignores your fixes.

With `debugLogging = true` you additionally get the list of patched methods, and first person
reports each transition and why it declined to engage:

```
first person on: zoom 0.42 (leaves above 1.20)
first person off: zoom 1.35 > 1.20
fp idle: zoom 2.30 > 0.60 | distance=2.30 minDistance=0.00 enterAt=0.60 allowFullZoom=True
```

Startup also warns if a conflicting plugin is loaded — notably this mod's predecessor
`SpineAim`, or WatchWhereYouStab / VikingsDoSwim / VerticallyChallenged, whose features are
reimplemented here. **Two plugins patching the same methods cannot coexist**: each keeps its own
state, so they fight over the camera every frame and over hiding the body, which looks exactly
like a bug in this mod.

`99 - Advanced / debugLogging` prints lean weights, dive state, and the camera's
`distance` / `minDistance` / near clip once a second. That last one is how to tell whether
first person is actually engaging.

## How it works, and why it works that way

Each of these fought an existing engine behaviour, and the details are worth recording.

### Melee aim

Every `Attack` carries `m_maxYAngle`, the vertical range it may be aimed through, applied
in `Attack.GetMeleeAttackDir`. Vanilla leaves it low or zero on most weapons, which is why
an enemy slightly up a slope can be unhittable — your swing stays level while the target
sits above the arc.

The cap is raised for the duration of that one call and restored immediately afterward.
Attack instances come from shared item data, so leaving a modified value behind would
quietly alter that item type for the rest of the session.

### Aim lean

Valheim already bends the torso toward the look direction — Unity humanoid look-at IK, with
a `m_bodyLookWeight` field for exactly that. But `CharacterAnimEvent.UpdateLookat` contains:

```
if (m_character.InAttack())  weight = 0f;
```

so it is switched off precisely when you want it, which is why vanilla melee *and* bow
aiming never tilt the body. This re-issues `SetLookAtPosition`/`SetLookAtWeight` in a
postfix; Unity solves look-at only after `OnAnimatorIK` returns, so ours are the values it
consumes.

Two details that took several attempts:

* **Do not rotate bones directly.** Writing `Transform.rotation` from a late update
  compounds on any frame the Animator does not re-evaluate, which folds the character in
  half and then snaps back — a per-frame flicker. And the look-at solver overwrites those
  writes anyway, by a varying amount, because `m_lookAtWeight` ramps via `MoveTowards`.
  Driving the solver instead of fighting it removes the problem entirely.
* **Clamp the look direction to the body's own facing.** Feeding the solver a raw camera
  direction lets the target swing behind a stationary character while you orbit the camera,
  and the solver then corkscrews the torso to reach it. `maxYawDegrees` and
  `maxPitchDegrees` are that guard; vanilla does the equivalent with a pre-clamped
  `m_headLookDir`.

The lean is skipped for `Area` and `None` attack types — radial slams centred on the
character, such as a Stagbreaker — where aim direction is not consulted at all.

### Diving

`Character.UpdateSwimming` computes a target height of `GetLiquidLevel() - m_swimDepth` and
drives the rigidbody's vertical velocity toward it. That buoyancy is what pins a swimmer to
the surface. Running after it and setting the vertical velocity from your look pitch
overrides that for the frame; holding the key keeps overriding it, so you descend.

Local movement only. The resulting position is networked by the game as usual, so other
players see you underwater without needing the mod.

### Multiplayer

Valheim networks no view pitch: `Character.GetLookDir()` is `m_eye.forward`, and only the local
player's eye is driven by mouse input (`Player.SetMouseLook` → `SetLookDir`). For a remote player
that transform carries no pitch at all, so leaning one without extra information aims everybody
identically — worse than leaving them vanilla.

So the mod supplies the missing data itself. Each client publishes its own pitch and yaw and reads
what others publish, which means **you see where other players are really looking, provided they
are also running this mod**. Anyone without it is left vanilla rather than guessed at.

The transport is the player's own **ZDO** rather than a routed RPC. ZDO fields already replicate to
every client that can see the character, on the game's own schedule and with ownership enforced, so
there is no RPC to register and no recipient list to maintain; they pass through the server as
opaque data, so **the server needs no mod**; the last value persists, so someone who walks into
view later gets current angles without keepalive traffic; and clients without the mod simply never
read the keys.

Yaw is sent relative to the body rather than as a world direction, so a body that has turned since
the last update still produces a sane result. Angles go out unclamped and each viewer applies its
own limits, so `maxYawDegrees` and `maxPitchDegrees` need not match between players. Traffic is
bounded by `sendRateHz` (10) and `minChangeDegrees` (1.5) — mouse look jitters fractionally every
frame, and `directionSmoothing` covers the gaps on the receiving side.

Publishing is independent of whether your client displays a lean, so turning the lean off or
sitting down does not make you invisible to others. `[05 - Look sync] enabled = false` opts out
entirely.

First person and diving are inherently local, so multiplayer does not affect them: the resulting
camera and position are yours, and other players see your position move as normal.

### First person

Three separate traps, each of which looked like a positioning bug:

1. **`m_minDistance` floors the zoom.** It sits well above any sensible first-person
   threshold, so without forcing it to 0 the mode can never engage — you simply stay in
   vanilla's closest third-person view, which orbits a pivot and therefore rises when you
   look up and sits forward of the body when level. Symptoms that look exactly like broken
   first-person code, while none of that code is running.
2. **Offsets must be in the body's frame, not the view's.** An offset along the camera's
   forward vector ties camera height to pitch and pokes out of the hitbox when level. Here
   they are in the character's horizontal frame and default to zero, so the camera sits on
   its rotation pivot.
3. **Entry and exit need separate thresholds.** Sharing one makes the engage and disengage
   conditions fight the moment anything nudges `m_distance`, and first person flickers in and
   out every frame. `zoomThreshold` and `exitZoomThreshold` are that hysteresis.
4. **`UpdateNearClipping` rewrites the near plane every frame** from `m_nearClipPlaneMin`,
   so setting `camera.nearClipPlane` once on entry is undone immediately. Both the field and
   the live value are enforced each frame.

Height comes from the capsule hitbox (`Character.GetHeight()`), not the head bone. The
hitbox is not animated and does not change when crouching, so there is no bob to damp
rather than bob that has been smoothed — and crouching, swimming and rolling need no
special cases. `verticalSmoothing` only softens stair steps, and never touches horizontal
position or look, so it adds no aiming lag.

Rescanning for renderers to hide is event-driven: `VisEquipment` instantiates every equipment
model from one of three methods (`AttachItem`, `AttachArmor`, `AttachBackItem`), and a postfix on
those marks the renderer set dirty. There is no "equipment changed" event to use instead —
`CustomUpdate` calls `UpdateVisuals` every frame regardless — so hooking creation is what avoids
walking the player hierarchy on idle frames.

The body is hidden by setting skinned renderers to `ShadowsOnly`: body, hair and armour are
`SkinnedMeshRenderer`s bound to the skeleton, while weapons, shields and tools are plain
`MeshRenderer`s parented to hand bones — which is what keeps the weapon visible.
`ShadowsOnly` rather than `forceRenderingOff` matters, because fully hiding the renderers
makes Unity treat the character as off-screen and stop evaluating the Animator, freezing the
skeleton so the held weapon no longer follows the swing. `Animator.cullingMode` is also
forced to `AlwaysAnimate` for the same reason, and `Character.SetVisible` is suppressed for
your own character so the LODGroup cannot cull the body — and the weapon with it.

That suppression needs undoing carefully. `SetVisible` early-returns when the requested value
already matches `m_lodVisible`, so a suppressed call never updates the flag and the game will
not retry after suppression stops — it thinks the LODGroup is already as it asked. Leaving
first person therefore forces `SetVisible(true)` once, with the flag cleared first so the
equality check cannot swallow it. Original `shadowCastingMode` values are also recorded once
per renderer and never overwritten, since re-capturing an already-concealed value as the
"original" would make the hide permanent.

## Building

```sh
./build.sh
```

Needs the .NET SDK. It references Valheim's own assemblies, so nothing has to be copied out
of a Windows install:

```sh
# defaults assume ../ValheimServer/server; override for any other install
GAME_MANAGED=/path/to/valheim_Data/Managed \
BEPINEX_CORE=/path/to/BepInEx/core \
./build.sh
```

Targets `netstandard2.1`, matching the game's own assemblies (Unity 2022.3).

Patches are bound by method *name*, so a game update that renames one fails loudly in
`BepInEx/LogOutput.log` at startup rather than crashing. Grep it for
`Failed to apply patch` / `MissingMethodException` after any update.

## Relationship to other mods

The melee-aim and diving features cover the same ground as
[WatchWhereYouStab](https://thunderstore.io/c/valheim/p/Searica/WatchWhereYouStab/) and
[VikingsDoSwim](https://thunderstore.io/c/valheim/p/blacks7ar/VikingsDoSwim/). They are
independent implementations against the game's own API — **no code or assets from those mods
are included or redistributed here.** If you run either of them alongside this, disable the
overlapping feature on one side or they will both patch the same behaviour.

## License

MIT — see `LICENSE`.
