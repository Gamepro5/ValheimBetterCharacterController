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
        /// The interval required right now, given how long the key has been held.
        ///
        /// A keyboard auto-repeat curve: nothing at all for initialDelay, then repeats beginning at
        /// startInterval and accelerating to the floor across rampSeconds. Without the delay a
        /// single tap fires many times, which on anything that TOGGLES - an item stand, a lever -
        /// reads as the thing being placed and taken back over and over.
        /// </summary>
        private static float RequiredInterval(float heldFor, out bool allowedYet)
        {
            float floor = Mathf.Max(0.01f, Plugin.FastHoldInterval.Value);
            float delay = Mathf.Max(0f, Plugin.FastHoldInitialDelay.Value);

            allowedYet = heldFor >= delay;
            if (!allowedYet) return floor;

            // Never slower than the floor, whatever the config says, so the two cannot cross over.
            float startAt = Mathf.Max(floor, Plugin.FastHoldStartInterval.Value);
            float ramp = Mathf.Max(0f, Plugin.FastHoldRampSeconds.Value);
            if (ramp <= 0f) return floor;

            float t = Mathf.Clamp01((heldFor - delay) / ramp);
            return Mathf.Lerp(startAt, floor, t);
        }

        /// <summary>
        /// Returns false to skip Player.Interact when a repeat is not due yet.
        ///
        /// Skipping is necessary rather than merely not helping: vanilla's own gate allows five a
        /// second, which is faster than the start of the ramp, so leaving the original to run would
        /// make the early part of the curve meaningless. Vanilla's behaviour when its gate blocks is
        /// also to return having done nothing, so this is the same outcome by the same reasoning.
        ///
        /// A fresh press arrives with hold == false and is never touched, so the first interaction
        /// is always immediate.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "Interact")]
        private static bool BeforeInteract(Player __instance, bool hold)
        {
            if (!Plugin.FastHoldEnabled.Value || Plugin.FastHoldFaulted) return true;

            try
            {
                if (!hold)
                {
                    // The press itself: let it through untouched and start the ramp from here.
                    _holdStarted = Time.time;
                    _lastHoldSeen = Time.time;
                    _lastHeldInteract = Time.time;
                    return true;
                }

                if (Time.time - _lastHoldSeen > NewHoldGap) _holdStarted = Time.time;
                _lastHoldSeen = Time.time;

                float required = RequiredInterval(Time.time - _holdStarted, out bool allowedYet);
                if (!allowedYet) return false;
                if (Time.time - _lastHeldInteract < required) return false;

                _lastHeldInteract = Time.time;

                // Present the field as exactly old enough to clear the 0.2s test. Vanilla
                // overwrites it with Time.time as soon as it accepts the interaction, so this does
                // not accumulate.
                LastHoverInteractRef(__instance) = Time.time - VanillaInteractGate;
                return true;
            }
            catch (System.Exception e)
            {
                // Session-only flag: writing the config entry would persist to disk and leave the
                // feature switched off after a restart for no visible reason. Returning true also
                // matters here - a fault must not leave interaction blocked.
                Plugin.Log.LogWarning($"fast hold interact failed, disabling for this session: {e.Message}");
                Plugin.FastHoldFaulted = true;
                return true;
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
