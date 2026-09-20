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

        private static readonly AccessTools.FieldRef<Character, ZNetView> NViewRef =
            AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");

        private static float _lastSend;
        private static float _lastPitch = float.NaN;
        private static float _lastYaw = float.NaN;

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
        internal static void PublishLocal(Character character)
        {
            try
            {
                if (!Plugin.LookSyncEnabled.Value || Plugin.LookSyncFaulted) return;
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

        private static void Publish(Character character, float pitch, float yaw)
        {

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

            // The bool overloads distinguish "absent" from "present and zero", which matters:
            // zero pitch is a perfectly normal value.
            if (!zdo.GetFloat(PitchKey, out pitch)) return false;
            if (!zdo.GetFloat(YawKey, out yaw)) return false;

            return true;
        }
    }
}
