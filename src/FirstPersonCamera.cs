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

        private static readonly List<Renderer> _hidden = new List<Renderer>();
        private static readonly List<ShadowCastingMode> _savedShadowModes = new List<ShadowCastingMode>();

        private static float _lastLog;

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
                ApplyZoomLimit(__instance);

                Player player = Player.m_localPlayer;
                Camera cam = CameraRef(__instance);

                if (!ReferenceEquals(player, _cachedPlayer))
                {
                    ShowBody();
                    RestoreAnimatorCulling();
                    _cachedPlayer = player;
                    _playerAnimator = player != null ? player.GetComponentInChildren<Animator>() : null;
                    _hasSmoothed = false;
                }

                bool want = Plugin.FpEnabled.Value
                            && player != null
                            && cam != null
                            && !player.IsDead()
                            && !player.IsAttached()
                            && player.GetRagdoll() == null
                            && player.GetControlledShip() == null
                            && DistanceRef(__instance) <= Plugin.FpZoomThreshold.Value;

                if (!want)
                {
                    if (FirstPersonActive) Exit(cam);
                    return;
                }

                if (!FirstPersonActive)
                {
                    FirstPersonActive = true;
                    _hasSmoothed = false;
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
                    // Cheap periodic rescan so freshly equipped gear is caught too.
                    if (Time.frameCount % 30 == 0) ShowBody();
                    HideBody(player);
                }
                else
                {
                    ShowBody();
                }

                if (Plugin.DebugLogging.Value && Time.time - _lastLog > 1f)
                {
                    _lastLog = Time.time;
                    Plugin.Log.LogInfo(
                        $"fp: distance={DistanceRef(__instance):F2} minDistance={MinDistanceRef(__instance):F2} " +
                        $"eye={_height:F2} near={(cam != null ? cam.nearClipPlane.ToString("F3") : "-")} " +
                        $"hidden={_hidden.Count} culling={(_playerAnimator != null ? _playerAnimator.cullingMode.ToString() : "-")}");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"first person failed, disabling: {e}");
                Plugin.FpEnabled.Value = false;
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
            if (!FirstPersonActive) return true;
            if (!Plugin.FpEnabled.Value || !Plugin.FpKeepBodyVisible.Value) return true;
            if (__instance == null || !__instance.IsPlayer()) return true;
            if (!ReferenceEquals(__instance, Player.m_localPlayer)) return true;

            return false;
        }

        private static void ApplyZoomLimit(GameCamera gameCamera)
        {
            if (Plugin.FpEnabled.Value && Plugin.FpAllowFullZoom.Value)
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
        private static void HideBody(Player player)
        {
            if (player == null) return;

            if (_hidden.Count == 0)
            {
                foreach (Renderer r in player.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    if (Plugin.FpHideSkinnedOnly.Value && !(r is SkinnedMeshRenderer)) continue;

                    _hidden.Add(r);
                    _savedShadowModes.Add(r.shadowCastingMode);
                    Conceal(r);
                }
                return;
            }

            for (int i = _hidden.Count - 1; i >= 0; i--)
            {
                Renderer r = _hidden[i];
                if (r == null)
                {
                    _hidden.RemoveAt(i);
                    _savedShadowModes.RemoveAt(i);
                    continue;
                }
                Conceal(r);
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
            if (_hidden.Count == 0) return;

            for (int i = 0; i < _hidden.Count; i++)
            {
                Renderer r = _hidden[i];
                if (r == null) continue;
                r.forceRenderingOff = false;
                if (i < _savedShadowModes.Count) r.shadowCastingMode = _savedShadowModes[i];
            }
            _hidden.Clear();
            _savedShadowModes.Clear();
        }

        private static void Exit(Camera cam)
        {
            FirstPersonActive = false;
            _hasSmoothed = false;
            ShowBody();
            RestoreAnimatorCulling();
            if (cam != null && _savedNearClip > 0f) cam.nearClipPlane = _savedNearClip;
            _savedNearClip = -1f;
        }
    }
}
