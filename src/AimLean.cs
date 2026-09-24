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

        // One summary line per second covering every character we were asked about, so "is the mod
        // even touching the other players?" is answerable without guessing. Keyed by instance.
        private static readonly Dictionary<int, string> _report = new Dictionary<int, string>();

        /// <summary>
        /// Runs after the game has set its own look-at values. Unity does not solve look-at until
        /// OnAnimatorIK returns, so re-issuing here replaces them for this evaluation.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacterAnimEvent), "UpdateLookat")]
        private static void AfterUpdateLookat(CharacterAnimEvent __instance)
        {
            try
            {
                Character character = CharacterRef(__instance);
                if (character == null) return;

                bool isLocal = Player.m_localPlayerExists && ReferenceEquals(character, Player.m_localPlayer);

                // One predicate drives both what we render and what we tell other clients, so the
                // two cannot disagree. They used to: publishing was deliberately independent of
                // display gating, which meant a player sitting in a boat or asleep still appeared
                // to everyone else to be aiming around.
                bool active = LeanActiveFor(character);

                if (isLocal) LookSync.PublishLocal(character, active);

                if (!active) return;

                if (Plugin.LeanLocalOnly.Value && !isLocal)
                {
                    Note(id0(__instance), isLocal, "skipped: localPlayerOnly");
                    Report(isLocal);
                    return;
                }

                Animator animator = AnimatorRef(__instance);
                if (animator == null || !animator.isHuman) return;

                Transform head = HeadRef(__instance);
                if (head == null) return;

                int id = __instance.GetInstanceID();
                bool firstThisFrame = NewFrame(id);

                Vector3 dir = ClampedLookDir(character, isLocal, id, firstThisFrame);
                if (dir.sqrMagnitude < 1e-6f)
                {
                    // For a remote character this is the normal outcome when its client is not
                    // publishing angles: we leave it vanilla rather than invent a direction.
                    Note(id, isLocal, isLocal ? "no local look dir" : "skipped: no synced angles");
                    Report(isLocal);
                    return;
                }

                animator.SetLookAtPosition(head.position + dir * Plugin.LeanTargetDistance.Value);

                float scale = WeaponLeanScale(character);
                float weight = Ramp(id, Plugin.LeanOverallWeight.Value * scale, firstThisFrame);

                animator.SetLookAtWeight(
                    weight,
                    Plugin.LeanBodyWeight.Value,
                    Plugin.LeanHeadWeight.Value,
                    Plugin.LeanEyeWeight.Value,
                    Plugin.LeanClampWeight.Value);

                if (Plugin.DebugLogging.Value)
                {
                    Attack dbg = CurrentAttack(character);
                    Note(id, isLocal,
                        $"leaned via {(isLocal ? "local" : "sync")} weight={weight:F2} scale={scale:F2} " +
                        $"attack={(dbg != null ? dbg.m_attackType.ToString() : "none")} " +
                        $"attacking={character.InAttack()}");
                }
                Report(isLocal);

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
        /// The look direction to aim the IK at: limited in yaw and pitch relative to the body's own
        /// facing, then eased over time.
        ///
        /// Both limits exist to stop the solver corkscrewing the spine when the view points
        /// somewhere the torso cannot reasonably follow - orbiting the camera around a stationary
        /// character otherwise puts the target behind it.
        ///
        /// Returns zero when there is nothing trustworthy to aim at, which is the case for a remote
        /// player whose client is not running this mod: Valheim networks no view pitch, so guessing
        /// would aim every such player identically.
        /// </summary>
        private static Vector3 ClampedLookDir(Character character, bool isLocal, int id, bool advance)
        {
            if (!TryGetAngles(character, isLocal, out float pitch, out float yaw)) return Vector3.zero;

            yaw = Mathf.Clamp(yaw, -Plugin.LeanMaxYaw.Value, Plugin.LeanMaxYaw.Value);
            pitch = Mathf.Clamp(pitch, -Plugin.LeanMaxPitch.Value, Plugin.LeanMaxPitch.Value);

            Vector3 bodyFlat = character.transform.forward;
            bodyFlat.y = 0f;
            if (bodyFlat.sqrMagnitude < 1e-6f) return Vector3.zero;
            bodyFlat.Normalize();

            // Rebuilt against the body's CURRENT forward. For a synced remote player that means a
            // body which has turned since the last update still yields a sensible direction, rather
            // than a stale world-space vector.
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

        /// <summary>
        /// View angles for this character: measured locally for our own player and published for
        /// others to read, or read from the ZDO for a remote player that publishes them.
        /// </summary>
        /// <summary>
        /// Whether the lean should be running for this character at all.
        ///
        /// Deliberately the single source of truth: it decides what we draw AND what we publish for
        /// other clients to draw. Anything that suppresses the lean locally must also stop us
        /// telling everyone else where we are looking, otherwise we sit motionless in a boat on our
        /// own screen while still craning our neck around on theirs.
        ///
        /// Note what is NOT here: localPlayerOnly. That is a viewer-side preference about whether
        /// we render OTHER people's leaning, and has nothing to do with whether ours is suppressed,
        /// so it must not gate publishing.
        /// </summary>
        private static bool LeanActiveFor(Character character)
        {
            if (!Plugin.LeanEnabled.Value || Plugin.LeanFaulted) return false;

            // Sitting, riding, steering or sitting in a boat, and sleeping all attach the character;
            // vanilla zeroes the look-at in these states too. InBed is checked separately because a
            // player can be in bed without being attached while the sleep is starting.
            if (character.IsAttached()) return false;
            if (character.InBed()) return false;
            if (character.IsDead()) return false;

            bool attacking = character.InAttack();
            if (attacking && !Plugin.LeanDuringAttack.Value) return false;
            if (!attacking && !Plugin.LeanAlways.Value) return false;

            return true;
        }

        private static bool TryGetAngles(Character character, bool isLocal, out float pitch, out float yaw)
        {
            return isLocal
                ? LookSync.ComputeAngles(character, out pitch, out yaw)
                : LookSync.TryRead(character, out pitch, out yaw);
        }

        private static int id0(CharacterAnimEvent e) => e.GetInstanceID();

        private static void Note(int id, bool isLocal, string state)
        {
            if (!Plugin.DebugLogging.Value) return;
            _report[id] = $"{(isLocal ? "self" : "remote#" + id)}: {state}";
        }

        /// <summary>
        /// Emits the collected per-character states once a second, driven off the local player's
        /// pass so the line is stable. This is the diagnostic that distinguishes "the mod is leaning
        /// other players wrongly" from "the mod is not touching them and something else is".
        /// </summary>
        private static void Report(bool isLocal)
        {
            if (!Plugin.DebugLogging.Value || !isLocal) return;
            if (Time.time - _lastLogTime < 1f) return;
            _lastLogTime = Time.time;

            if (_report.Count > 0)
                Plugin.Log.LogInfo("lean: " + string.Join(" | ", _report.Values));
            _report.Clear();
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
