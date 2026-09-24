using HarmonyLib;
using UnityEngine;

namespace BetterCharacterController
{
    /// <summary>
    /// Publishes the local player's view angles so other clients running this mod can lean a
    /// character toward where its owner is actually looking.
    ///
    /// Valheim does not network view pitch. Character.GetLookDir() is m_eye.forward, and only the
    /// local player's eye is driven by mouse input (Player.SetMouseLook -> SetLookDir); for a
    /// remote player that transform carries no pitch at all. Without something like this, leaning
    /// a remote player aims them all identically, which is worse than leaving them vanilla.
    ///
    /// The transport is the player's own ZDO rather than a routed RPC:
    ///
    ///   * ZDO fields already replicate to every client that can see the character, on the game's
    ///     own schedule, with ownership enforced - no RPC registration, no manual recipient list.
    ///   * They pass through the server as opaque data, so the SERVER does not need this mod.
    ///   * The last value persists in the ZDO, so someone who walks into view later gets the
    ///     current angles without a keepalive.
    ///   * Clients without the mod simply never read the keys.
    ///
    /// Two floats are sent: pitch, and yaw measured relative to the character's own facing. Yaw is
    /// relative on purpose - the receiver rebuilds the direction against the body's *current*
    /// forward, so a body that has rotated since the last update still produces a sane result
    /// instead of a stale world-space vector.
    /// </summary>
    internal static class LookSync
    {
        private const string PitchKey = "bcc_look_pitch";
        private const string YawKey = "bcc_look_yaw";

        /// <summary>
        /// Whether the publisher's lean is active at all. Needed because ZDO fields cannot be
        /// removed: without an explicit flag, the last angles we published would linger and a
        /// player who sat down in a boat would keep aiming those angles on everyone else's screen
        /// forever.
        ///
        /// 1 = leaning, 0 = suppressed on that client. ABSENT means a peer on a build that never
        /// published the flag, and is treated as active so those peers keep working as before.
        /// </summary>
        private const string ActiveKey = "bcc_look_on";

        private static readonly AccessTools.FieldRef<Character, ZNetView> NViewRef =
            AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");

        /// <summary>Hard bound on accepted angles, independent of any config value.</summary>
        private const float SaneAngleLimit = 720f;

        private static bool _warnedBadData;
        private static float _lastSend;
        private static float _lastActive = float.NaN;
        private static float _lastPitch = float.NaN;
        private static float _lastYaw = float.NaN;

        /// <summary>
        /// Rejects anything that is not a finite, plausibly sized angle. Deliberately independent of
        /// the display limits in config, so tightening or loosening those cannot widen what is
        /// accepted off the wire.
        /// </summary>
        private static bool IsSane(float angle)
        {
            return !float.IsNaN(angle) && !float.IsInfinity(angle) && Mathf.Abs(angle) <= SaneAngleLimit;
        }

        /// <summary>
        /// View angles of a character measured from its own eye transform, relative to its body.
        /// Only meaningful for the local player, whose eye follows the mouse.
        /// </summary>
        internal static bool ComputeAngles(Character character, out float pitch, out float yaw)
        {
            pitch = 0f;
            yaw = 0f;

            Vector3 look = character.GetLookDir();
            if (look.sqrMagnitude < 1e-6f) return false;
            look.Normalize();

            Vector3 bodyFlat = character.transform.forward;
            bodyFlat.y = 0f;
            if (bodyFlat.sqrMagnitude < 1e-6f) return false;
            bodyFlat.Normalize();

            pitch = Mathf.Asin(Mathf.Clamp(look.y, -1f, 1f)) * Mathf.Rad2Deg;

            Vector3 lookFlat = new Vector3(look.x, 0f, look.z);
            yaw = lookFlat.sqrMagnitude < 1e-6f
                ? 0f
                : Vector3.SignedAngle(bodyFlat, lookFlat.normalized, Vector3.up);

            return true;
        }

        /// <summary>
        /// Publishes the local player's angles. Deliberately independent of whether this client
        /// displays a lean: someone who turns the lean off, or sits down, should still be visible
        /// to everyone else as looking where they are looking.
        ///
        /// Angles are published unclamped, so each viewer can apply its own limits without everyone
        /// having to agree on them.
        /// </summary>
        internal static void PublishLocal(Character character, bool active)
        {
            try
            {
                if (!Plugin.LookSyncEnabled.Value || Plugin.LookSyncFaulted) return;

                if (!active)
                {
                    // Still has to reach the wire: this is what tells everyone to stop leaning us.
                    PublishActive(character, false);
                    return;
                }

                if (!ComputeAngles(character, out float pitch, out float yaw)) return;
                Publish(character, pitch, yaw);
            }
            catch (System.Exception e)
            {
                // A sync failure must not take the lean down with it. Session-only flag: writing
                // the config entry would persist to disk and disable the feature across restarts.
                Plugin.Log.LogWarning($"look sync failed, disabling for this session: {e.Message}");
                Plugin.LookSyncFaulted = true;
            }
        }

        /// <summary>
        /// Writes the active flag, and only when it changes. Not rate limited: going inactive has
        /// to land promptly or the character keeps aiming stale angles on other screens for as long
        /// as the throttle lasts.
        /// </summary>
        private static void PublishActive(Character character, bool active)
        {
            ZNetView nview = NViewRef(character);
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

            ZDO zdo = nview.GetZDO();
            if (zdo == null) return;

            float flag = active ? 1f : 0f;
            // ReSharper disable once CompareOfFloatsByEqualityOperator both sides are 0f or 1f.
            if (!float.IsNaN(_lastActive) && _lastActive == flag) return;

            zdo.Set(ActiveKey, flag);
            _lastActive = flag;
        }

        private static void Publish(Character character, float pitch, float yaw)
        {
            PublishActive(character, true);

            ZNetView nview = NViewRef(character);
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

            float minInterval = 1f / Mathf.Max(1f, Plugin.LookSyncRateHz.Value);
            bool due = Time.time - _lastSend >= minInterval;
            bool moved = float.IsNaN(_lastPitch)
                         || Mathf.Abs(Mathf.DeltaAngle(_lastPitch, pitch)) >= Plugin.LookSyncMinChange.Value
                         || Mathf.Abs(Mathf.DeltaAngle(_lastYaw, yaw)) >= Plugin.LookSyncMinChange.Value;

            if (!due || !moved) return;

            ZDO zdo = nview.GetZDO();
            if (zdo == null) return;

            zdo.Set(PitchKey, pitch);
            zdo.Set(YawKey, yaw);

            _lastSend = Time.time;
            _lastPitch = pitch;
            _lastYaw = yaw;
        }

        /// <summary>
        /// Reads a remote character's published angles. Returns false when that client is not
        /// running the mod, in which case the caller should leave the character vanilla rather
        /// than inventing a direction for it.
        /// </summary>
        internal static bool TryRead(Character character, out float pitch, out float yaw)
        {
            pitch = 0f;
            yaw = 0f;

            if (!Plugin.LookSyncEnabled.Value || Plugin.LookSyncFaulted) return false;

            ZNetView nview = NViewRef(character);
            if (nview == null || !nview.IsValid()) return false;

            ZDO zdo = nview.GetZDO();
            if (zdo == null) return false;

            // A peer publishing 0 has the lean suppressed on its own client - in a boat, asleep,
            // dead, or simply switched off - and must not appear to be aiming. Absent means a peer
            // on an older build that never published the flag, so treat that as active.
            if (zdo.GetFloat(ActiveKey, out float activeFlag) && activeFlag < 0.5f) return false;

            // The bool overloads distinguish "absent" from "present and zero", which matters:
            // zero pitch is a perfectly normal value.
            if (!zdo.GetFloat(PitchKey, out pitch)) return false;
            if (!zdo.GetFloat(YawKey, out yaw)) return false;

            // Trust boundary. These two floats arrived over the network from a client we do not
            // control, so treat them as hostile input even though they are only ever used as
            // numbers - nothing here turns them into code, a path, a type name or a lookup.
            //
            // The specific hazard is that NaN and Infinity survive Mathf.Clamp: comparisons against
            // NaN are false, so a clamp passes it straight through. It would then reach
            // Quaternion.Euler and Animator.SetLookAtPosition, giving an invalid pose and a Unity
            // error every frame for that character - cheap for an attacker, annoying for us.
            if (!IsSane(pitch) || !IsSane(yaw))
            {
                if (!_warnedBadData)
                {
                    _warnedBadData = true;
                    Plugin.Log.LogWarning(
                        $"ignoring malformed look angles from a remote player (pitch={pitch}, yaw={yaw}). " +
                        "Their client is either buggy or deliberately sending nonsense; the character " +
                        "is left vanilla.");
                }
                pitch = 0f;
                yaw = 0f;
                return false;
            }

            return true;
        }
    }
}
