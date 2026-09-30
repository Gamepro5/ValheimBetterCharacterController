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
    ///    above it, by up to about ten degrees at the extremes.
    ///
    /// 3. Even the true camera pitch is the wrong angle, because the swing does not start at the
    ///    camera. It starts at the attack joint, lower down and in front, so a swing parallel to
    ///    the camera ray lands short of and below the crosshair - aim slightly down at a tree and
    ///    the axe hits the ground. The pitch is therefore taken from the swing's origin to the
    ///    point the camera ray actually hits.
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
                                                   Transform originJoint, ref Vector3 attackDir)
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

                if (originJoint == null) return;
                Vector3 origin = originJoint.position
                                 + Vector3.up * __instance.m_attackHeight
                                 + character.transform.right * __instance.m_attackOffset;

                Vector3 aim = AimFromOrigin(character, origin);
                if (aim.sqrMagnitude < 1e-6f) return;

                float pitch = Mathf.Asin(Mathf.Clamp(aim.y, -1f, 1f)) * Mathf.Rad2Deg;

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

        private const float AimRayLength = 50f;
        private static readonly RaycastHit[] AimHits = new RaycastHit[32];

        /// <summary>
        /// Unit direction from the swing's origin to whatever the crosshair is on.
        ///
        /// The camera's own pitch is the wrong angle to swing at: the swing starts at the attack
        /// joint, well below the camera and (in third person) in front of it, so a line leaving
        /// the chest parallel to the camera ray lands short of and below the target. Aiming
        /// slightly down at a tree put the axe into the ground in front of it. Converging on the
        /// point the camera ray actually hits fixes that at any distance.
        ///
        /// Uses the game's own attack masks, so the crosshair "sees" exactly what a swing can hit.
        /// Falls back to a point far along the camera ray when it hits nothing, which converges on
        /// the camera's pitch.
        /// </summary>
        private static Vector3 AimFromOrigin(Humanoid character, Vector3 origin)
        {
            GameCamera cam = GameCamera.instance;
            Vector3 camPos, camFwd;
            if (cam != null)
            {
                camPos = cam.transform.position;
                camFwd = cam.transform.forward;
            }
            else
            {
                camPos = character.GetEyePoint();
                camFwd = character.GetLookDir();
            }

            Vector3 target = camPos + camFwd * AimRayLength;

            int mask = AttackMaskRef() | AttackMaskTerrainRef();
            if (mask != 0)
            {
                int n = Physics.RaycastNonAlloc(camPos, camFwd, AimHits, AimRayLength, mask,
                                                QueryTriggerInteraction.Ignore);
                float best = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    RaycastHit hit = AimHits[i];
                    if (hit.distance >= best) continue;
                    // Our own body sits on the ray in first person and near it in third.
                    if (hit.collider.GetComponentInParent<Character>() == character) continue;
                    // A hit between the camera and the swing (third person, something behind
                    // us) is not what we are aiming at.
                    if (Vector3.Dot(hit.point - origin, camFwd) <= 0f) continue;
                    best = hit.distance;
                    target = hit.point;
                }
            }

            return (target - origin).normalized;
        }

        private static readonly AccessTools.FieldRef<int> AttackMaskRef =
            AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(typeof(Attack), "m_attackMask"));

        private static readonly AccessTools.FieldRef<int> AttackMaskTerrainRef =
            AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(typeof(Attack), "m_attackMaskTerrain"));
    }
}
