using HarmonyLib;
using UnityEngine;

namespace BetterCharacterController
{
    /// <summary>
    /// Lets melee attacks be aimed vertically, and makes the resulting swing actually go where the
    /// crosshair points.
    ///
    /// Two separate problems, both in Attack.GetMeleeAttackDir:
    ///
    /// 1. Each Attack carries m_maxYAngle, the vertical range it may be aimed through, and vanilla
    ///    leaves it low or zero on most weapons. That is why an enemy a little way up a slope can
    ///    be unhittable. The cap is raised for the duration of that one call and restored
    ///    immediately - Attack instances come from shared item data, so leaving a modified value
    ///    behind would quietly alter that item type for the rest of the session.
    ///
    /// 2. The pitch it produces is not the pitch you aimed. The method keeps the body's horizontal
    ///    forward but takes only the vertical component of the aim direction, then normalises:
    ///
    ///        aim = GetAimDir(origin)      // a unit vector, so aim.y == sin(pitch)
    ///        aim.x = bodyForward.x
    ///        aim.z = bodyForward.z        // horizontal part now has length 1
    ///        aim.Normalize()
    ///
    ///    The result has horizontal length 1 and height sin(pitch), so its pitch is
    ///    atan(sin(pitch)) rather than pitch. That is always shallower than you aimed:
    ///
    ///        aim 10 deg -> 9.8    aim 30 deg -> 26.6    aim 45 deg -> 35.3
    ///
    ///    Aiming upward, the swing therefore lands BELOW the crosshair, and aiming down it lands
    ///    above it, by up to about ten degrees at the extremes. Rebuilding the direction from the
    ///    real pitch removes that.
    ///
    /// Only the vertical angle is corrected. Horizontally the swing stays on the body's facing,
    /// which is vanilla's deliberate behaviour and what the swing animation is built around.
    ///
    /// The correction is applied only to the local player. Melee damage is resolved on the
    /// attacker's own client, so that is the only place it changes an outcome, and confining it
    /// there keeps creature attacks exactly as the game shipped them.
    /// </summary>
    [HarmonyPatch]
    internal static class MeleeAim
    {
        private static readonly AccessTools.FieldRef<Attack, Humanoid> CharacterRef =
            AccessTools.FieldRefAccess<Attack, Humanoid>("m_character");

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Attack), "GetMeleeAttackDir")]
        private static void BeforeGetMeleeAttackDir(Attack __instance, out float __state)
        {
            __state = __instance.m_maxYAngle;

            if (!Plugin.MeleeAimEnabled.Value) return;

            float want = Plugin.MeleeMaxYAngle.Value;
            if (__instance.m_maxYAngle < want) __instance.m_maxYAngle = want;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Attack), "GetMeleeAttackDir")]
        private static void AfterGetMeleeAttackDir(Attack __instance, float __state,
                                                   ref Vector3 attackDir)
        {
            // Always restore, even when disabled mid-session.
            __instance.m_maxYAngle = __state;

            if (!Plugin.MeleeAimEnabled.Value || !Plugin.MeleeExactPitch.Value) return;
            if (Plugin.MeleeExactPitchFaulted) return;

            try
            {
                Humanoid character = CharacterRef(__instance);
                if (character == null) return;
                if (!Player.m_localPlayerExists || !ReferenceEquals(character, Player.m_localPlayer)) return;

                Vector3 bodyForward = character.transform.forward;
                bodyForward.y = 0f;
                if (bodyForward.sqrMagnitude < 1e-6f) return;
                bodyForward.Normalize();

                Vector3 look = character.GetLookDir();
                if (look.sqrMagnitude < 1e-6f) return;
                look.Normalize();

                // The angle the crosshair is actually at, rather than one recovered from a
                // renormalised vector.
                float pitch = Mathf.Asin(Mathf.Clamp(look.y, -1f, 1f)) * Mathf.Rad2Deg;

                // Respect the same cap the section is configured with, so raising accuracy cannot
                // quietly widen the range beyond what the player asked for.
                float cap = Mathf.Max(0f, Plugin.MeleeMaxYAngle.Value);
                pitch = Mathf.Clamp(pitch, -cap, cap);

                Vector3 right = Vector3.Cross(Vector3.up, bodyForward);
                if (right.sqrMagnitude < 1e-6f) return;

                // Positive rotation about `right` pitches downward, so negate to make a positive
                // pitch mean upward.
                attackDir = Quaternion.AngleAxis(-pitch, right.normalized) * bodyForward;
            }
            catch (System.Exception e)
            {
                // Session-only flag: writing the config entry would persist to disk and leave the
                // correction off after a restart with nothing explaining why.
                Plugin.Log.LogWarning($"melee pitch correction failed, disabling for this session: {e.Message}");
                Plugin.MeleeExactPitchFaulted = true;
            }
        }
    }
}
