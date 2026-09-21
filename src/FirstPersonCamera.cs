using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterCharacterController
{
    /// <summary>
    /// A first-person camera that engages once you zoom in past a threshold.
    ///
    /// Three things had to be got right, and each was a separate trap:
    ///
    /// 1. Vanilla's m_minDistance floors how far the camera may zoom in, well above any sensible
    ///    first-person threshold. Without lowering it the mode can never engage and you simply
    ///    stay in the closest third-person view - which orbits a pivot, so it rises when you look
    ///    up and sits forward of the body when level.
    ///
    /// 2. The position must not depend on the view direction. Offsetting along the camera's
    ///    forward vector ties camera height to pitch. Offsets here are in the character's
    ///    horizontal frame, and default to zero, so the camera sits exactly on its pivot.
    ///
    /// 3. Height comes from the capsule hitbox, not the head bone. The hitbox is not animated and
    ///    does not change when crouching, swimming or rolling, so there is no bob to damp in the
    ///    first place - as opposed to bob that has been smoothed.
    /// </summary>
    [HarmonyPatch]
    internal static class FirstPersonCamera
    {
        private static readonly AccessTools.FieldRef<GameCamera, float> DistanceRef =
            AccessTools.FieldRefAccess<GameCamera, float>("m_distance");

        private static readonly AccessTools.FieldRef<GameCamera, float> MinDistanceRef =
            AccessTools.FieldRefAccess<GameCamera, float>("m_minDistance");

        private static readonly AccessTools.FieldRef<GameCamera, float> NearClipMinRef =
            AccessTools.FieldRefAccess<GameCamera, float>("m_nearClipPlaneMin");

        private static readonly AccessTools.FieldRef<GameCamera, Camera> CameraRef =
            AccessTools.FieldRefAccess<GameCamera, Camera>("m_camera");

        // Character.SetVisible and its backing flag are both non-public.
        private static readonly System.Reflection.MethodInfo SetVisibleMethod =
            AccessTools.Method(typeof(Character), "SetVisible", new[] { typeof(bool) });

        private static readonly AccessTools.FieldRef<Character, bool> LodVisibleRef =
            AccessTools.FieldRefAccess<Character, bool>("m_lodVisible");

        internal static bool FirstPersonActive { get; private set; }

        private static float _savedNearClip = -1f;
        private static float _savedMinDistance = -1f;
        private static float _savedNearClipMin = -1f;

        // Smoothing tracks the character-derived base position only. Storing a position that
        // already includes the offset and feeding it back re-adds the offset every frame, which
        // settles at base + offset/smoothingFactor - an error that grows with frame rate.
        private static Vector3 _smoothedBase;
        private static bool _hasSmoothed;
        private static float _height;
        private static bool _warnedClamp;

        private static Player _cachedPlayer;
        private static Animator _playerAnimator;
        private static AnimatorCullingMode _savedCulling;
        private static bool _cullingOverridden;

        // Keyed by renderer instance id and written exactly once per renderer. A list that gets
        // cleared and re-populated can re-capture an ALREADY concealed value as the "original",
        // which makes the hide permanent - so the original is recorded once and never overwritten.
        private static readonly Dictionary<int, ShadowCastingMode> _originalShadowModes =
            new Dictionary<int, ShadowCastingMode>();
        private static readonly List<Renderer> _hidden = new List<Renderer>();

        // Set while we call SetVisible ourselves, so our own prefix lets it through.
        private static bool _forcingVisible;

        private static float _lastLog;
        private static float _lastStuckWarn = -999f;

        // The zoom floor as it stood before this mod overwrote it, so the debug line can show
        // whether another mod is competing for the field rather than only echoing our own write.
        private static float _minDistanceBeforeUs = -1f;

        /// <summary>Zoom distance at the moment first person engaged; exit is measured against it.</summary>
        private static float _engagedAtDistance;


        /// <summary>
        /// The zoom floor has to be written in a PREFIX on UpdateCamera, not in the LateUpdate
        /// postfix where it used to live.
        ///
        /// UpdateCamera reads m_minDistance into a local at the top and clamps m_distance against
        /// it at the bottom. Writing the field from a postfix therefore only affects the NEXT call -
        /// and ValheimPlus has its own prefix on UpdateCamera (BlockCameraScrollInAEM) that writes
        /// m_minDistance, so it always got the last word before the clamp and the zoom stayed
        /// floored at vanilla's minimum. First person could then only engage while V+'s own
        /// first person happened to be lowering the floor for its own purposes.
        ///
        /// Priority.Last makes this the last prefix to run, immediately before the original body,
        /// so the clamp in the same call uses our value regardless of what any other mod did.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
        private static void BeforeUpdateCamera(GameCamera __instance)
        {
            try
            {
                _minDistanceBeforeUs = MinDistanceRef(__instance);
                ApplyZoomLimit(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"zoom limit failed, disabling first person: {e}");
                Plugin.FirstPersonFaulted = true;
            }
        }

        /// <summary>
        /// LateUpdate is the outermost camera method - it calls UpdateCamera internally - so
        /// enforcing the position here lands after vanilla and after any mod patching UpdateCamera.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(GameCamera), "LateUpdate")]
        private static void AfterLateUpdate(GameCamera __instance)
        {
            try
            {
                Player player = Player.m_localPlayer;
                Camera cam = CameraRef(__instance);

                if (!ReferenceEquals(player, _cachedPlayer))
                {
                    ShowBody();
                    RestoreAnimatorCulling();
                    _cachedPlayer = player;
                    _playerAnimator = player != null ? player.GetComponentInChildren<Animator>() : null;
                    _hasSmoothed = false;
                    EquipmentWatcher.MarkDirty();
                }

                float distance = DistanceRef(__instance);


                // Hysteresis: enter below zoomThreshold, leave only above exitZoomThreshold. With a
                // single threshold, anything that nudges the distance as first person engages makes
                // the two conditions fight and the mode flickers in and out every frame.
                float enterAt = Plugin.FpZoomThreshold.Value;

                // Leaving is measured RELATIVE to where we engaged, not against an absolute value.
                // The game applies zoom in steps whose size we do not control, so any absolute exit
                // threshold has to be guessed against that step - and one set above it silently
                // costs an extra scroll tick to leave. A margin above the engaged distance reacts to
                // a single tick of any size, and cannot be tripped by anything else because the zoom
                // distance only moves on input. The absolute threshold stays as a ceiling.
                float leaveAt = FirstPersonActive
                    ? Mathf.Min(Mathf.Max(Plugin.FpExitZoomThreshold.Value, enterAt + 0.1f),
                                _engagedAtDistance + Plugin.FpExitMargin.Value)
                    : enterAt;
                bool zoomOk = FirstPersonActive ? distance <= leaveAt : distance <= enterAt;

                string blocker = null;
                if (!Plugin.FpEnabled.Value) blocker = "disabled in config";
                else if (Plugin.FirstPersonFaulted) blocker = "faulted earlier this session";
                else if (player == null) blocker = "no local player";
                else if (cam == null) blocker = "no camera";
                else if (player.IsDead()) blocker = "dead";
                else if (player.IsAttached()) blocker = "attached (sitting/riding)";
                else if (player.GetRagdoll() != null) blocker = "ragdolled";
                else if (player.GetControlledShip() != null) blocker = "steering a ship";
                else if (!zoomOk) blocker = $"zoom {distance:F2} > {(FirstPersonActive ? leaveAt : enterAt):F2}";

                bool want = blocker == null;

                // One-shot diagnostic for the case that is impossible to report otherwise: the
                // player has zoomed as far in as the game will allow, yet the threshold was never
                // reached, so first person simply never engages and nothing explains why. Fires
                // without debugLogging because whoever hits it has no reason to suspect a config.
                if (!want && !FirstPersonActive && Plugin.FpEnabled.Value
                    && Time.time - _lastStuckWarn > 10f)
                {
                    float floor = MinDistanceRef(__instance);
                    bool bottomedOut = distance <= floor + 0.05f;
                    if (bottomedOut && distance > enterAt)
                    {
                        // Repeated rather than one-shot: a single early line ends up near the top of
                        // the log, far from where anyone looks after reproducing the problem.
                        _lastStuckWarn = Time.time;
                        Plugin.Log.LogWarning(
                            $"first person never engages: the zoom bottoms out at {distance:F2}m but " +
                            $"engaging needs {enterAt:F2}m or less (minDistance={floor:F2}, " +
                            $"allowFullZoom={Plugin.FpAllowFullZoom.Value}). " +
                            (Plugin.FpAllowFullZoom.Value
                                ? "Something else is re-clamping the camera's minimum distance - another camera mod is the usual cause."
                                : "Set allowFullZoom = true, or raise zoomThreshold above the value above."));
                    }
                }

                // Heartbeat while refusing to engage. Without this, the refusing path returns in
                // silence and "no log output" is indistinguishable from "the patch never ran".
                if (Plugin.DebugLogging.Value && !want && Time.time - _lastLog > 1f)
                {
                    _lastLog = Time.time;
                    Plugin.Log.LogInfo(
                        $"fp idle: {blocker} | distance={distance:F2} minDistance={MinDistanceRef(__instance):F2} " +
                        $"enterAt={enterAt:F2} allowFullZoom={Plugin.FpAllowFullZoom.Value}");
                }

                if (!want)
                {
                    if (FirstPersonActive)
                    {
                        if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"first person off: {blocker}");
                        Exit(cam);
                    }
                    else if (_hidden.Count > 0)
                    {
                        // Failsafe: anything still concealed while inactive means a transition was
                        // missed. Recover rather than leaving the player invisible.
                        Plugin.Log.LogWarning("model was still hidden while first person was inactive; restoring.");
                        ShowBody();
                        ForceVisible(Player.m_localPlayer);
                    }
                    return;
                }

                if (!FirstPersonActive)
                {
                    FirstPersonActive = true;
                    _engagedAtDistance = distance;
                    _hasSmoothed = false;
                    _lastStuckWarn = -999f;
                    if (Plugin.DebugLogging.Value)
                        Plugin.Log.LogInfo($"first person on: zoom {distance:F2} (leaves above {leaveAt:F2})");
                    if (cam != null) _savedNearClip = cam.nearClipPlane;
                    if (_savedNearClipMin < 0f) _savedNearClipMin = NearClipMinRef(__instance);
                }

                // UpdateNearClipping recomputes the near plane from m_nearClipPlaneMin every
                // frame, so setting the camera value alone is undone immediately.
                float near = Plugin.FpNearClip.Value;
                if (NearClipMinRef(__instance) != near) NearClipMinRef(__instance) = near;
                if (cam != null && cam.nearClipPlane > near) cam.nearClipPlane = near;

                ApplyAnimatorCulling();

                Transform root = player.transform;
                Vector3 basePos = root.position + Vector3.up * EyeHeight(player);

                if (_hasSmoothed && Plugin.FpVerticalSmoothing.Value > 0f)
                {
                    // Vertical only: softens stair steps, never touches look or horizontal motion.
                    float vt = Mathf.Clamp01(1f - Mathf.Exp(-Plugin.FpVerticalSmoothing.Value * Time.unscaledDeltaTime));
                    basePos.y = Mathf.Lerp(_smoothedBase.y, basePos.y, vt);
                }

                _smoothedBase = basePos;
                _hasSmoothed = true;

                Vector3 pos = ApplyOffsets(__instance, basePos, root);

                float limit = Mathf.Max(0.5f, Plugin.FpMaxOffset.Value);
                if ((pos - basePos).sqrMagnitude > limit * limit)
                {
                    if (!_warnedClamp)
                    {
                        _warnedClamp = true;
                        Plugin.Log.LogWarning(
                            $"first-person position was {Vector3.Distance(pos, basePos):F2}m from the eye " +
                            $"point (limit {limit:F2}m); clamped. Check bodyForwardOffset/bodySideOffset.");
                    }
                    pos = basePos;
                }

                // Written to, never read: rotation is mouse look, position is ours.
                __instance.transform.position = pos;

                if (Plugin.FpHideBody.Value)
                {
                    // Rescan only when equipment visuals were actually rebuilt (EquipmentWatcher
                    // hooks the three VisEquipment methods that instantiate models), otherwise just
                    // re-assert what is already tracked. No hierarchy walk on an idle frame.
                    HideBody(player, rescan: EquipmentWatcher.ConsumeDirty());
                }
                else if (_hidden.Count > 0)
                {
                    ShowBody();
                    ForceVisible(player);
                }

                if (Plugin.DebugLogging.Value && Time.time - _lastLog > 1f)
                {
                    _lastLog = Time.time;
                    Plugin.Log.LogInfo(
                        $"fp: distance={distance:F2} minDistance={MinDistanceRef(__instance):F2} " +
                        $"(before us {_minDistanceBeforeUs:F2}) " +
                        $"eye={_height:F2} near={(cam != null ? cam.nearClipPlane.ToString("F3") : "-")} " +
                        $"hidden={_hidden.Count} culling={(_playerAnimator != null ? _playerAnimator.cullingMode.ToString() : "-")}");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"first person failed, disabling: {e}");
                Plugin.FirstPersonFaulted = true;   // session only; never written to the config file
                ShowBody();
                RestoreAnimatorCulling();
                FirstPersonActive = false;
            }
        }

        /// <summary>
        /// Character.SetVisible drives an LODGroup, and with the camera inside the character the
        /// engine can cull the whole body - arms and weapon included. Suppressing it for our own
        /// character while first person is active keeps the weapon on screen.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Character), "SetVisible")]
        private static bool BeforeSetVisible(Character __instance)
        {
            if (_forcingVisible) return true;   // our own restore call
            if (!FirstPersonActive) return true;
            if (!Plugin.FpEnabled.Value || Plugin.FirstPersonFaulted) return true;
            if (!Plugin.FpKeepBodyVisible.Value) return true;
            if (__instance == null || !__instance.IsPlayer()) return true;
            if (!ReferenceEquals(__instance, Player.m_localPlayer)) return true;

            return false;
        }

        private static void ApplyZoomLimit(GameCamera gameCamera)
        {
            if (Plugin.FpEnabled.Value && !Plugin.FirstPersonFaulted && Plugin.FpAllowFullZoom.Value)
            {
                if (_savedMinDistance < 0f) _savedMinDistance = MinDistanceRef(gameCamera);
                if (MinDistanceRef(gameCamera) != 0f) MinDistanceRef(gameCamera) = 0f;
                return;
            }

            if (_savedMinDistance >= 0f)
            {
                MinDistanceRef(gameCamera) = _savedMinDistance;
                _savedMinDistance = -1f;
            }
            if (_savedNearClipMin >= 0f)
            {
                NearClipMinRef(gameCamera) = _savedNearClipMin;
                _savedNearClipMin = -1f;
            }
        }

        /// <summary>
        /// Eye height above the character's feet, taken from the capsule hitbox so that no animated
        /// transform is involved and every pose is covered without a special case.
        /// </summary>
        private static float EyeHeight(Player player)
        {
            float h = player.GetHeight();
            if (h <= 0.1f) h = Plugin.FpFallbackEyeHeight.Value;
            _height = h - Plugin.FpEyeDropFromTop.Value;
            return _height;
        }

        /// <summary>
        /// Offsets are expressed in the CHARACTER's horizontal frame. In the view frame they would
        /// tie camera height to pitch and poke out of the hitbox when looking level. The raycast
        /// keeps any offset from crossing a surface, since overriding the position skips vanilla's
        /// own collision avoidance.
        /// </summary>
        private static Vector3 ApplyOffsets(GameCamera gameCamera, Vector3 basePos, Transform root)
        {
            Vector3 forward = root.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) return basePos;
            forward.Normalize();

            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 offset = forward * Plugin.FpBodyForwardOffset.Value
                             + right * Plugin.FpBodySideOffset.Value;

            float dist = offset.magnitude;
            if (dist < 0.001f) return basePos;

            Vector3 dir = offset / dist;

            if (Plugin.FpAvoidGeometry.Value)
            {
                const float skin = 0.08f;
                int mask = gameCamera.m_blockCameraMask.value;
                if (Physics.Raycast(basePos, dir, out RaycastHit hit, dist + skin, mask,
                                    QueryTriggerInteraction.Ignore))
                {
                    dist = Mathf.Max(0f, hit.distance - skin);
                }
            }

            return basePos + dir * dist;
        }

        /// <summary>
        /// Hiding the body can make Unity treat the character as off-screen and stop evaluating the
        /// Animator, freezing the skeleton so the held weapon no longer follows the swing.
        /// AlwaysAnimate keeps the bones updating regardless of what is considered visible.
        /// </summary>
        private static void ApplyAnimatorCulling()
        {
            if (!Plugin.FpAlwaysAnimate.Value || _playerAnimator == null || _cullingOverridden) return;

            _savedCulling = _playerAnimator.cullingMode;
            _playerAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _cullingOverridden = true;
        }

        private static void RestoreAnimatorCulling()
        {
            if (!_cullingOverridden) return;
            if (_playerAnimator != null) _playerAnimator.cullingMode = _savedCulling;
            _cullingOverridden = false;
        }

        /// <summary>
        /// Body, hair and armour are SkinnedMeshRenderers bound to the skeleton; weapons, shields
        /// and tools are plain MeshRenderers parented to hand bones. Hiding only skinned meshes is
        /// therefore what keeps the weapon visible.
        /// </summary>
        private static void HideBody(Player player, bool rescan)
        {
            if (player == null) return;

            if (rescan || _hidden.Count == 0)
            {
                // Additive: pick up anything new (equipment changes spawn fresh renderers) without
                // ever clearing, so an original is never re-captured from a concealed value.
                foreach (Renderer r in player.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    if (Plugin.FpHideSkinnedOnly.Value && !(r is SkinnedMeshRenderer)) continue;

                    int id = r.GetInstanceID();
                    if (!_originalShadowModes.ContainsKey(id))
                    {
                        _originalShadowModes[id] = r.shadowCastingMode;
                        _hidden.Add(r);
                    }
                }
            }

            foreach (Renderer r in _hidden)
            {
                if (r != null) Conceal(r);
            }

            // Drop destroyed renderers so the list cannot grow without bound.
            for (int i = _hidden.Count - 1; i >= 0; i--)
            {
                if (_hidden[i] == null) _hidden.RemoveAt(i);
            }
        }

        private static void Conceal(Renderer r)
        {
            if (Plugin.FpHideMethod.Value == Plugin.HideMethod.ShadowsOnly)
            {
                // Still counted as rendering, so the skeleton keeps animating and the weapon swings.
                if (r.forceRenderingOff) r.forceRenderingOff = false;
                if (r.shadowCastingMode != ShadowCastingMode.ShadowsOnly)
                    r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            }
            else if (!r.forceRenderingOff)
            {
                r.forceRenderingOff = true;
            }
        }

        private static void ShowBody()
        {
            if (_hidden.Count == 0 && _originalShadowModes.Count == 0) return;

            foreach (Renderer r in _hidden)
            {
                if (r == null) continue;
                r.forceRenderingOff = false;
                if (_originalShadowModes.TryGetValue(r.GetInstanceID(), out ShadowCastingMode mode))
                    r.shadowCastingMode = mode;
                else
                    r.shadowCastingMode = ShadowCastingMode.On;
            }

            _hidden.Clear();
            _originalShadowModes.Clear();
        }

        /// <summary>
        /// Undo the SetVisible suppression.
        ///
        /// Character.SetVisible early-returns when the value already matches its backing flag
        /// (if (m_lodVisible == value) return). So a suppressed call never updates that flag, and
        /// the game will not retry once suppression stops - it believes the LODGroup is already in
        /// the state it asked for. If the game tried to make the body visible while we were
        /// suppressing, the model stays culled indefinitely after leaving first person.
        ///
        /// Forcing the call once on exit, with the flag cleared first so the early-return cannot
        /// swallow it, puts the LODGroup back.
        /// </summary>
        private static void ForceVisible(Character character)
        {
            if (character == null || SetVisibleMethod == null) return;

            try
            {
                _forcingVisible = true;
                LodVisibleRef(character) = false;   // defeat the equality early-return
                SetVisibleMethod.Invoke(character, new object[] { true });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"could not force the model visible again: {e.Message}");
            }
            finally
            {
                _forcingVisible = false;
            }
        }

        private static void Exit(Camera cam)
        {
            FirstPersonActive = false;
            _hasSmoothed = false;
            ShowBody();
            ForceVisible(Player.m_localPlayer);
            RestoreAnimatorCulling();
            if (cam != null && _savedNearClip > 0f) cam.nearClipPlane = _savedNearClip;
            _savedNearClip = -1f;
        }
    }
}
