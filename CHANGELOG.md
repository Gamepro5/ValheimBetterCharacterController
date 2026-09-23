# Changelog

## 1.4.0

**Steam achievements work again in a modded session.**

Valheim blocks achievements when `Achievements.IsCheatedAtAll()` is true, and one of the four things
that makes it true is simply `Game.isModded`. So a rebalanced server loses achievements even when
nobody has cheated - the check asks whether the session is modified, not whether the player gained an
advantage.

The game ships a supported bypass. `PlayerProfile.s_bypassCheatChecks` is not a field; it reads a
per-character key, `bypasscheatchecks` set to `"1"`, which the console sets via
`setkey bypasscheatchecks 1`. Doing that manually means every player enabling devcommands first, so
this returns true from the getter instead: same effect, applied for everyone running the mod, and
**nothing is written to any character file** - removing the mod restores vanilla behaviour exactly.

Nothing is awarded retroactively. Progress made while achievements were blocked was discarded at the
time rather than withheld, so it cannot be recovered.

The scope note is widened accordingly, and deliberately: client-side per-player behaviour, which is
what achievements are (Steam awards them per account through the player's own client). Server balance
still belongs in ValheimPlus.

## 1.3.0

**Fixed: helmets stayed visible in first person and blocked the view, while some weapons vanished.**

Hiding was decided by renderer type - hide skinned meshes, keep the rest - on the assumption that
body and armour are skinned to the skeleton while held items are plain meshes. That assumption is
wrong in both directions: **helmets are not skinned**, so they stayed in view, and **some weapons
are**, so they disappeared.

Classification is now by equipment slot, read from `VisEquipment`'s own per-slot instances
(`m_rightItemInstance` / `m_leftItemInstance`). Everything except what is in your hands is hidden, so
a helmet is hidden because it *is* the helmet slot - nothing depends on what kind of renderer it
happens to use.

The new `hideScope` key defaults to `AllButHeldItems`, so existing configs pick up the fix without
being edited. `SkinnedOnly` keeps the old behaviour for comparison and `Everything` hides the weapon
too.

## 1.2.3

**Leaving first person now takes one scroll tick, whatever the step size, and without editing an
existing config.**

1.2.1 lowered the absolute exit threshold, which only helps if the game's zoom step happens to be
larger than the gap - and it does nothing for configs that already exist, since BepInEx keeps their
values. Exit is now measured **relative to the distance at which first person engaged**: once the
zoom rises more than `exitMargin` (0.15 m) above that, it disengages. That reacts to a single tick
of any size and cannot be tripped by anything else, because the zoom distance only moves on input.

`exitZoomThreshold` remains as an absolute ceiling, so a zoom somehow far outside first-person range
always disengages.

## 1.2.2

Removed the startup heartbeat now that first person works. It existed to prove the camera hook was
executing at all, which it did, but it has no reason to write to everyone's log every session.

Normal operation now logs two lines per launch — the version with the path it loaded from, and the
feature states — plus warnings only when something is actually wrong: a conflicting plugin, V+'s
first person also enabled, a missing camera patch, a zoom that cannot reach the threshold, malformed
synced angles, or a feature fault. Everything else requires `debugLogging = true`.

## 1.2.1

`exitZoomThreshold` default lowered from 1.2 to 0.9, so leaving first person takes one scroll tick
instead of two. The game moves the zoom in increments of roughly 1.0, so an exit threshold above
that means the first tick out of first person lands below it and changes nothing. A 0.3 gap above
`zoomThreshold` still keeps entry and exit from fighting over a single value.

Existing configs keep their own value — set `exitZoomThreshold = 0.9` by hand, or delete
`gameprog.bettercharactercontroller.cfg` to take the new defaults.

## 1.2.0

**Fixed: first person never engaged on its own.** It only ever worked while ValheimPlus's own
first person happened to be lowering the camera's zoom floor for its own purposes — with V+'s
first person off, as it is by default, this mod's first person could not engage at all.

The zoom floor was being written from the `GameCamera.LateUpdate` postfix. `UpdateCamera` reads
`m_minDistance` into a local at the top and clamps `m_distance` against it at the bottom, so a
postfix write only affects the *next* call — and ValheimPlus has a **prefix** on `UpdateCamera`
(`BlockCameraScrollInAEM`) that writes `m_minDistance` too. Its prefix therefore always got the
last word before the clamp, and the zoom stayed pinned at vanilla's minimum of 1.0 while this mod
needed 0.6.

The floor is now written from a prefix on `UpdateCamera` with `Priority.Last`, making it the last
writer before the original body runs, so the clamp in that same call uses it regardless of what
other mods do.

The heartbeat also reports the value that was in place *before* this mod overwrote it
(`before us 1.00`). Previously it logged only the value after our own write, which read as
`minDistance=0.00` and looked like proof the write was effective — it was not.

## 1.1.6

Adds an unconditional heartbeat from the camera hook for the first minute of a session:

```
camera hook alive: zoom=2.30 minDistance=1.50 enterAt=0.60 active=False (this reports for the first minute only)
```

Every diagnostic so far lived *inside* that hook, so if the hook is not executing they all stay
silent - which is indistinguishable from the feature being broken, and is the state several rounds
of debugging could not tell apart. This needs no config change and stops on its own.

## 1.1.5

Fixes two diagnostic mistakes that hid why first person was not engaging.

* **The "zoom cannot reach the threshold" warning was one-shot per session.** If it fired during
  load it ended up near the top of the log, nowhere near where anyone looks after reproducing the
  problem - so it read as "nothing is printed". It now repeats at most every 10 seconds while the
  condition holds.
* **Nothing reported a feature being switched off in config.** A disabled feature is
  indistinguishable from a broken one, and the config file survives updates - including a value an
  earlier version's error handler may have written itself. Startup now lists the state of all five
  features, and warns explicitly if first person is disabled.

## 1.1.4

Warns at startup when ValheimPlus's own `[FirstPerson]` is enabled **on this client**, since it
manages the camera's minimum zoom distance and can prevent this mod's first person from engaging.

The per-client part matters: `FirstPersonConfiguration` extends `ClientConfig`, so a V+ server does
**not** push that section to anyone. Setting it server-side fixes nothing - an assumption this
project made and acted on until the class hierarchy was actually checked. Two players can therefore
behave differently with no visible cause.

Read by reflection, so a V+ rename degrades to a missing warning rather than an exception.

## 1.1.3

The startup line now reports **where the assembly was loaded from**, not just its version:

```
BetterCharacterController 1.1.3 loaded from F:\...\Valheim\BepInEx\plugins\BetterCharacterController.dll
```

Two failures are indistinguishable from a broken mod without this, and both have already cost real
debugging time: a stale copy that ignores every fix, and a mod manager installing to a folder
BepInEx does not scan. Now one line rules out both.

Documented that the BepInEx console window is `[Logging.Console] Enabled` in `BepInEx.cfg` - stock
BepInEx defaults it off and the ValheimPlus package turns it on - so its absence says nothing about
whether mods loaded.

## 1.1.2

Diagnostic for a report that the local player's pitch appears mirrored onto remote players.

I could not reproduce the mechanism by reading the game: `ZDO.GetFloat(string, out float)` does
return false for an absent key (so a player not running the mod should be skipped), and
`CharacterAnimEvent.GetLookFromPos` turns out to be the averaged eye *origin* rather than a look
target, ruling out the idea that vanilla aims remote heads at the local camera.

So with `debugLogging = true` the lean now reports what it did to every character, once a second:

```
lean: self: leaned via local weight=1.00 scale=1.00 attack=Horizontal attacking=False | remote#4213: skipped: no synced angles
```

That line separates the two possibilities this report leaves open: either the mod is leaning remote
players with bad data, or it is not touching them at all and something else is moving them.

## 1.1.1

**Validate synced angles at the trust boundary.** Received values are only ever used as numbers, so
there is no route from them to executed code — but `NaN` and `Infinity` survive `Mathf.Clamp`
(comparisons against NaN are false), and would have reached `Quaternion.Euler` and
`SetLookAtPosition`, giving an invalid pose and a Unity error every frame for that character. Cheap
to send, annoying to receive.

Non-finite and implausibly large angles are now rejected outright, with a one-shot warning, and the
character is left vanilla. The bound is a fixed constant rather than the display limits, so
changing those cannot widen what is accepted off the wire.

## 1.1.0

**Look direction is now synced, so you can see where other players are actually aiming.**

Valheim networks no view pitch, which is why 1.0.1 had to restrict the lean to your own character.
This adds the missing data. Each client publishes its own pitch and yaw (yaw measured relative to
its body facing) and reads what others publish, so `localPlayerOnly` returns to `false` — safely,
because a player who does not publish is left vanilla rather than guessed at.

The transport is the player's own **ZDO**, not a routed RPC:

* ZDO fields already replicate to everyone who can see the character, on the game's own schedule,
  with ownership enforced — no RPC registration and no recipient bookkeeping.
* They pass through the server as opaque data, so **the server does not need this mod**.
* The last value persists in the ZDO, so someone who walks into view later gets current angles
  with no keepalive traffic.
* Clients without the mod never read the keys, so nothing breaks for them.

Yaw is sent relative to the body rather than as a world vector, so a body that has rotated since
the last update still yields a sane direction instead of a stale heading. Angles are published
unclamped and each viewer applies its own `maxYawDegrees` / `maxPitchDegrees`, so limits need not
be agreed between players. Updates are rate limited (`sendRateHz`, default 10) and skipped below
`minChangeDegrees` (1.5), since mouse look jitters fractionally every frame; the receiver's
existing `directionSmoothing` covers the gaps.

Publishing is deliberately independent of whether your own client displays a lean — turning the
lean off, or sitting down, should not make you invisible to everyone else.

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
