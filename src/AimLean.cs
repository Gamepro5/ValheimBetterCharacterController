using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterCharacterController
{
    [HarmonyPatch]
    internal static class AimLean
    {
        private static readonly AccessTools.FieldRef<CharacterAnimEvent, Character> CharacterRef =
            AccessTools.FieldRefAccess<CharacterAnimEvent, Character>("m_character");

        private static readonly AccessTools.FieldRef<CharacterAnimEvent, Animator> AnimatorRef =
            AccessTools.FieldRefAccess<CharacterAnimEvent, Animator>("m_animator");

        private static readonly AccessTools.FieldRef<CharacterAnimEvent, Transform> HeadRef =
            AccessTools.FieldRefAccess<CharacterAnimEvent, Transform>("m_head");

        // Non-public in the game assembly, so it goes through Harmony like the rest.
        private static readonly AccessTools.FieldRef<Humanoid, Attack> CurrentAttackRef =
            AccessTools.FieldRefAccess<Humanoid, Attack>("m_currentAttack");

        private static readonly Dictionary<int, float> _weight = new Dictionary<int, float>();
        private static readonly Dictionary<int, Vector3> _dir = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, int> _frame = new Dictionary<int, int>();

        private static float _lastLogTime;

        /// <summary>
        /// Runs after the game has set its own look-at values. Unity does not solve look-at until
        /// OnAnimatorIK returns, so re-issuing here replaces them for this evaluation.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacterAnimEvent), "UpdateLookat")]
        private static void AfterUpdateLookat(CharacterAnimEvent __instance)
        {
            if (!Plugin.LeanEnabled.Value || Plugin.LeanFaulted) return;

            try
            {
                Character character = CharacterRef(__instance);
                if (character == null) return;

                if (Plugin.LeanLocalOnly.Value &&
                    !(Player.m_localPlayerExists && ReferenceEquals(character, Player.m_localPlayer)))
                    return;

                Animator animator = AnimatorRef(__instance);
                if (animator == null || !animator.isHuman) return;

                Transform head = HeadRef(__instance);
                if (head == null) return;

                if (character.IsAttached()) return;   // sitting or riding: vanilla zeroes this too

                bool attacking = character.InAttack();
                if (attacking && !Plugin.LeanDuringAttack.Value) return;
                if (!attacking && !Plugin.LeanAlways.Value) return;

                int id = __instance.GetInstanceID();
                bool firstThisFrame = NewFrame(id);

                Vector3 dir = ClampedLookDir(character, id, firstThisFrame);
                if (dir.sqrMagnitude < 1e-6f) return;

                animator.SetLookAtPosition(head.position + dir * Plugin.LeanTargetDistance.Value);

                float scale = WeaponLeanScale(character);
                float weight = Ramp(id, Plugin.LeanOverallWeight.Value * scale, firstThisFrame);

                animator.SetLookAtWeight(
                    weight,
                    Plugin.LeanBodyWeight.Value,
                    Plugin.LeanHeadWeight.Value,
                    Plugin.LeanEyeWeight.Value,
                    Plugin.LeanClampWeight.Value);

                if (Plugin.DebugLogging.Value && Time.time - _lastLogTime > 1f)
                {
                    _lastLogTime = Time.time;
                    Attack dbg = CurrentAttack(character);
                    Plugin.Log.LogInfo(
                        $"lean weight={weight:F2} weaponScale={scale:F2} " +
                        $"attackType={(dbg != null ? dbg.m_attackType.ToString() : "none")} " +
                        $"maxY={(dbg != null ? dbg.m_maxYAngle.ToString("F1") : "-")} " +
                        $"facingYAim={(dbg != null && dbg.m_useCharacterFacingYAim)} " +
                        $"attacking={attacking} fp={FirstPersonCamera.FirstPersonActive}");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"aim lean failed, disabling: {e}");
                Plugin.LeanFaulted = true;   // session only; never written to the config file
            }
        }

        /// <summary>
        /// How much this character's current attack should lean, 0-1.
        ///
        /// Every Attack carries m_maxYAngle, the vertical range that attack may be aimed through,
        /// and m_attackType. That is the same field melee-aiming mods raise, so reading it means
        /// this tracks their configuration automatically instead of hardcoding a weapon list:
        /// a Stagbreaker slam is an Area attack and never leans, while a sword whose cap was
        /// raised to 45 degrees leans fully.
        /// </summary>
        private static float WeaponLeanScale(Character character)
        {
            Attack attack = CurrentAttack(character);
            if (attack == null) return 1f;   // unarmed or nothing equipped

            // Attack TYPE is the reliable signal. Area and None are radial slams centred on the
            // character - a Stagbreaker, and the secondary of several heavy weapons - where aim
            // direction is not consulted at all, so leaning into them reads as wrong.
            if (Plugin.LeanSkipAreaAttacks.Value &&
                (attack.m_attackType == Attack.AttackType.Area ||
                 attack.m_attackType == Attack.AttackType.None))
                return 0f;

            if (Plugin.LeanSkipFacingYAim.Value && attack.m_useCharacterFacingYAim)
                return 0f;

            return 1f;
        }

        /// <summary>
        /// The live attack if one is running, otherwise the equipped weapon's primary attack so
        /// the lean is already correct as you raise a weapon rather than snapping mid-swing.
        /// </summary>
        private static Attack CurrentAttack(Character character)
        {
            Humanoid humanoid = character as Humanoid;
            if (humanoid == null) return null;

            Attack live = CurrentAttackRef(humanoid);
            if (live != null) return live;

            ItemDrop.ItemData weapon = humanoid.GetCurrentWeapon();
            return weapon?.m_shared?.m_attack;
        }

        /// <summary>
        /// The look direction, limited in yaw and pitch relative to the body's own facing and
        /// eased over time. Both limits exist to stop the solver corkscrewing the spine when the
        /// camera points somewhere the torso cannot reasonably follow.
        /// </summary>
        private static Vector3 ClampedLookDir(Character character, int id, bool advance)
        {
            Vector3 look = character.GetLookDir();
            if (look.sqrMagnitude < 1e-6f) return Vector3.zero;
            look.Normalize();

            Vector3 bodyFlat = character.transform.forward;
            bodyFlat.y = 0f;
            if (bodyFlat.sqrMagnitude < 1e-6f) return look;
            bodyFlat.Normalize();

            Vector3 lookFlat = new Vector3(look.x, 0f, look.z);
            float yaw = lookFlat.sqrMagnitude < 1e-6f
                ? 0f
                : Vector3.SignedAngle(bodyFlat, lookFlat.normalized, Vector3.up);
            yaw = Mathf.Clamp(yaw, -Plugin.LeanMaxYaw.Value, Plugin.LeanMaxYaw.Value);

            float pitch = Mathf.Asin(Mathf.Clamp(look.y, -1f, 1f)) * Mathf.Rad2Deg;
            pitch = Mathf.Clamp(pitch, -Plugin.LeanMaxPitch.Value, Plugin.LeanMaxPitch.Value);

            // Negative X euler pitches up in Unity.
            Quaternion rot = Quaternion.LookRotation(bodyFlat, Vector3.up) * Quaternion.Euler(-pitch, yaw, 0f);
            Vector3 target = rot * Vector3.forward;

            if (Plugin.LeanDirectionSmoothing.Value <= 0f)
            {
                _dir[id] = target;
                return target;
            }

            if (!_dir.TryGetValue(id, out Vector3 current) || current.sqrMagnitude < 1e-6f)
            {
                _dir[id] = target;
                return target;
            }

            if (advance)
            {
                float t = Mathf.Clamp01(1f - Mathf.Exp(-Plugin.LeanDirectionSmoothing.Value * Time.deltaTime));
                current = Vector3.Slerp(current, target, t).normalized;
                _dir[id] = current;
            }

            return current;
        }

        /// <summary>True the first time this instance is seen in the current frame.</summary>
        private static bool NewFrame(int id)
        {
            int frame = Time.frameCount;
            _frame.TryGetValue(id, out int last);
            if (last == frame) return false;

            _frame[id] = frame;
            if (_frame.Count > 256) { _frame.Clear(); _weight.Clear(); _dir.Clear(); }
            return true;
        }

        private static float Ramp(int id, float target, bool advance)
        {
            _weight.TryGetValue(id, out float current);
            if (advance)
            {
                current = Mathf.MoveTowards(current, target, Plugin.LeanRampSpeed.Value * Time.deltaTime);
                _weight[id] = current;
            }
            return current;
        }
    }
}
