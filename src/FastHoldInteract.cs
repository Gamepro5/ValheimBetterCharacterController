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

        /// <summary>Our own rate limit, standing in for the gate we bypass.</summary>
        private static float _lastHeldInteract;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "Interact")]
        private static void BeforeInteract(Player __instance, bool hold)
        {
            if (!hold || !Plugin.FastHoldEnabled.Value || Plugin.FastHoldFaulted) return;

            try
            {
                float interval = Mathf.Max(0.01f, Plugin.FastHoldInterval.Value);
                if (interval >= VanillaInteractGate) return;   // nothing to gain

                if (Time.time - _lastHeldInteract < interval) return;
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

            float interval = Mathf.Max(0.01f, Plugin.FastHoldInterval.Value);
            if (current > interval) __instance.m_holdRepeatInterval = interval;
        }
    }
}
