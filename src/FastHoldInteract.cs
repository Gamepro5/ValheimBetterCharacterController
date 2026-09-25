using HarmonyLib;
using UnityEngine;

namespace BetterCharacterController
{
    /// <summary>
    /// Makes holding the use key fill a station at a usable rate, instead of one item every
    /// second or so.
    ///
    /// Two independent gates throttle a held interaction, and both have to be dealt with:
    ///
    ///   Player.Interact       returns early unless Time.time - m_lastHoverInteractTime >= 0.2f.
    ///                         A hard-coded ceiling of five interactions per second, whatever the
    ///                         thing being used says.
    ///
    ///   Switch.Interact       returns early unless Time.time - m_lastUseTime >=
    ///                         m_holdRepeatInterval, a per-prefab value. This is the one that
    ///                         actually hurts: an ore chute's interval is far longer than 0.2s, so
    ///                         a stack of 30 ore takes the better part of a minute to feed in.
    ///
    /// The 0.2s ceiling is bypassed by back-dating the field rather than by rewriting the method
    /// body. A transpiler would be tidier to read, but this is patching a hard-coded constant in a
    /// method the game touches every frame, and the game updates often - back-dating keeps working
    /// even if that constant moves or the surrounding branch is restructured. Our own timer does
    /// the real rate limiting, so removing the vanilla gate does not mean removing all gating.
    ///
    /// A switch whose interval is already zero is deliberately left alone. Zero is how the game
    /// says "this thing does not support being held" - a door, a lever, a bed - and forcing hold
    /// onto those would add behaviour nobody asked for. Only switches that already repeat are sped
    /// up, which also means a station added by a future update is covered the moment it exists.
    /// </summary>
    [HarmonyPatch]
    internal static class FastHoldInteract
    {
        private static readonly AccessTools.FieldRef<Player, float> LastHoverInteractRef =
            AccessTools.FieldRefAccess<Player, float>("m_lastHoverInteractTime");

        /// <summary>The vanilla ceiling in Player.Interact, mirrored so we can undo exactly it.</summary>
        private const float VanillaInteractGate = 0.2f;

        /// <summary>
        /// A gap this long between held frames means the key was released and pressed again, so the
        /// ramp starts over. Only a safety net: a real press arrives as hold == false first.
        /// </summary>
        private const float NewHoldGap = 0.25f;

        private static float _holdStarted;
        private static float _lastHeldInteract;
        private static float _lastHoldSeen;

        /// <summary>
        /// What the ramp is currently tracking. The timers are meaningless across a change of
        /// target: the press that starts a hold is SKIPPED by Player.Update when nothing is hovered
        /// (it is guarded on m_hovering), so pressing while looking at nothing and then aiming at a
        /// station would otherwise inherit whatever state the last object left behind - sometimes
        /// mid-ramp and instantly fast, sometimes freshly reset. That is what made this feel random.
        /// </summary>
        private static GameObject _holdTarget;

        /// <summary>
        /// The interval we want right now, given how long the key has been held.
        ///
        /// A keyboard auto-repeat curve: vanilla's own rate until initialDelay has passed, then
        /// accelerating from startInterval down to the floor across rampSeconds.
        ///
        /// Note what this does NOT do: ask for anything slower than vanilla. Returning a value above
        /// the 0.2s gate simply means we do not intervene and the game's own rate applies.
        /// </summary>
        private static float RequiredInterval(float heldFor)
        {
            float floor = Mathf.Max(0.01f, Plugin.FastHoldInterval.Value);
            float delay = Mathf.Max(0f, Plugin.FastHoldInitialDelay.Value);

            if (heldFor < delay) return float.MaxValue;   // do not intervene yet

            float startAt = Mathf.Max(floor, Plugin.FastHoldStartInterval.Value);
            float ramp = Mathf.Max(0f, Plugin.FastHoldRampSeconds.Value);
            if (ramp <= 0f) return floor;

            float t = Mathf.Clamp01((heldFor - delay) / ramp);
            return Mathf.Lerp(startAt, floor, t);
        }

        /// <summary>
        /// Speeds a held interaction up, and never slows one down or suppresses one.
        ///
        /// This started out returning false to skip Player.Interact when a repeat was not due, which
        /// broke holding to deposit into a container: that is vanilla's quick-stack, driven by
        /// repeated held calls, and starving it of calls for the length of the initial delay meant it
        /// never happened at all. The lesson is the invariant this now keeps - we may only ever let
        /// MORE through than vanilla would, by back-dating its timestamp. When we decline to
        /// intervene, the original method runs untouched and the game's own 0.2s gate decides, which
        /// is five a second: the rate the game itself ships, so nothing can be worse than vanilla.
        ///
        /// That also makes the early part of the ramp vanilla-paced rather than blocked, and the tap
        /// problem stays fixed for the same reason - the ramp only starts accelerating past the
        /// game's rate once the key has genuinely been held.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "Interact")]
        private static void BeforeInteract(Player __instance, GameObject go, bool hold)
        {
            if (!Plugin.FastHoldEnabled.Value || Plugin.FastHoldFaulted) return;

            try
            {
                // A different object, or a gap long enough to mean the key was released: start over.
                bool restart = go != _holdTarget || Time.time - _lastHoldSeen > NewHoldGap;

                if (!hold)
                {
                    // The press itself. Never touched, and it starts the ramp from here.
                    _holdTarget = go;
                    _holdStarted = Time.time;
                    _lastHoldSeen = Time.time;
                    _lastHeldInteract = Time.time;
                    return;
                }

                if (restart)
                {
                    _holdTarget = go;
                    _holdStarted = Time.time;
                    _lastHeldInteract = Time.time;
                }
                _lastHoldSeen = Time.time;

                float required = RequiredInterval(Time.time - _holdStarted);

                // Above vanilla's gate means we have nothing to add: leave it alone entirely.
                if (required >= VanillaInteractGate) return;
                if (Time.time - _lastHeldInteract < required) return;

                _lastHeldInteract = Time.time;

                // Present the field as exactly old enough to clear the 0.2s test. Vanilla
                // overwrites it with Time.time as soon as it accepts the interaction, so this does
                // not accumulate.
                LastHoverInteractRef(__instance) = Time.time - VanillaInteractGate;
            }
            catch (System.Exception e)
            {
                // Session-only flag: writing the config entry would persist to disk and leave the
                // feature switched off after a restart for no visible reason.
                Plugin.Log.LogWarning($"fast hold interact failed, disabling for this session: {e.Message}");
                Plugin.FastHoldFaulted = true;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Switch), "Interact")]
        private static void BeforeSwitchInteract(Switch __instance, bool hold)
        {
            if (!hold || !Plugin.FastHoldEnabled.Value || Plugin.FastHoldFaulted) return;

            // > 0 checked first: zero means this switch opts out of hold entirely, and must stay
            // that way. Only shorten an interval, never lengthen one.
            float current = __instance.m_holdRepeatInterval;
            if (current <= 0f) return;

            // Shortened to the floor, not to the ramp's current value: the ramp is enforced above,
            // and this only has to stop the switch's own long interval blocking a repeat we allow.
            float interval = Mathf.Max(0.01f, Plugin.FastHoldInterval.Value);
            if (current > interval) __instance.m_holdRepeatInterval = interval;
        }
    }
}
