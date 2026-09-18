using HarmonyLib;
using UnityEngine;

namespace BetterCharacterController
{
    /// <summary>
    /// Lets melee attacks be aimed vertically.
    ///
    /// Each Attack carries m_maxYAngle, the vertical range that attack may be aimed through, and
    /// Attack.GetMeleeAttackDir is the method that applies it. Vanilla leaves the cap low or zero
    /// on most weapons, which is why an enemy a little way up a slope can be unhittable: your
    /// swing stays level while the target sits above the arc.
    ///
    /// Rather than reimplementing the aiming maths, the cap is raised for the duration of that one
    /// call and restored immediately. Attack instances come from shared item data, so leaving a
    /// modified value behind would quietly alter that item type for the rest of the session.
    /// </summary>
    [HarmonyPatch]
    internal static class MeleeAim
    {
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
        private static void AfterGetMeleeAttackDir(Attack __instance, float __state)
        {
            // Always restore, even when disabled mid-session.
            __instance.m_maxYAngle = __state;
        }
    }
}
