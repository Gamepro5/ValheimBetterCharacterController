using HarmonyLib;
using UnityEngine;

namespace BetterCharacterController
{
    /// <summary>
    /// Lets you swim downward.
    ///
    /// Character.UpdateSwimming computes a target height of GetLiquidLevel() - m_swimDepth and
    /// drives the rigidbody's vertical velocity toward it, which is what pins a swimmer to the
    /// surface. Running after that method and setting the vertical velocity ourselves overrides
    /// the buoyancy for that frame; holding the key keeps overriding it, so you descend.
    ///
    /// Purely local movement. The position that results is networked by the game as usual, so
    /// other players see you underwater without needing the mod.
    /// </summary>
    [HarmonyPatch]
    internal static class Diving
    {
        private static readonly AccessTools.FieldRef<Character, Rigidbody> BodyRef =
            AccessTools.FieldRefAccess<Character, Rigidbody>("m_body");

        private static readonly AccessTools.FieldRef<Character, Vector3> MoveDirRef =
            AccessTools.FieldRefAccess<Character, Vector3>("m_moveDir");

        internal static bool Diving_Active { get; private set; }

        private static float _lastLog;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Character), "UpdateSwimming")]
        private static void AfterUpdateSwimming(Character __instance)
        {
            if (!Plugin.DiveEnabled.Value || Plugin.DiveFaulted) return;

            try
            {
                // Local player only: this is an input-driven movement override.
                if (!Player.m_localPlayerExists) return;
                if (!ReferenceEquals(__instance, Player.m_localPlayer)) return;
                if (!__instance.IsSwimming()) return;

                if (!Input.GetKey(Plugin.DiveKey.Value))
                {
                    Diving_Active = false;
                    return;
                }

                if (Plugin.DiveNeedsForward.Value && MoveDirRef(__instance).sqrMagnitude < 0.01f)
                {
                    Diving_Active = false;
                    return;
                }

                Rigidbody body = BodyRef(__instance);
                if (body == null) return;

                // Steer with the mouse: looking down descends, looking up rises.
                Vector3 look = __instance.GetLookDir();
                if (look.sqrMagnitude < 1e-6f) return;
                look.Normalize();

                Vector3 velocity = body.linearVelocity;
                velocity.y = look.y * Plugin.DiveSpeed.Value;
                body.linearVelocity = velocity;

                Diving_Active = true;

                if (Plugin.DebugLogging.Value && Time.time - _lastLog > 1f)
                {
                    _lastLog = Time.time;
                    Plugin.Log.LogInfo(
                        $"diving: lookY={look.y:F2} vy={velocity.y:F2} " +
                        $"liquid={__instance.GetLiquidLevel():F2} y={__instance.transform.position.y:F2}");
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"diving failed, disabling: {e}");
                Plugin.DiveFaulted = true;   // session only; never written to the config file
                Diving_Active = false;
            }
        }
    }
}
