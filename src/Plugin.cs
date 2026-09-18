using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BetterCharacterController
{
    /// <summary>
    /// Client-side character and camera improvements for Valheim:
    ///
    ///   * Melee attacks can be aimed up and down, so an enemy on a slope is reachable.
    ///   * The upper body leans toward your aim, so the swing matches where the hit lands.
    ///   * Diving: swim downward instead of being pinned to the surface.
    ///   * A first-person camera that engages on zoom, with no head bob.
    ///
    /// Everything here is cosmetic or local movement. Nothing is installed on the server and
    /// no networked state is changed, so it can be used alongside ValheimPlus without
    /// affecting its version check.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "gameprog.bettercharactercontroller";
        public const string Name = "BetterCharacterController";
        public const string Version = "1.0.6";

        internal static ManualLogSource Log;

        // ---- melee aim ----
        internal static ConfigEntry<bool> MeleeAimEnabled;
        internal static ConfigEntry<float> MeleeMaxYAngle;

        // ---- aim lean ----
        internal static ConfigEntry<bool> LeanEnabled;
        internal static ConfigEntry<bool> LeanLocalOnly;
        internal static ConfigEntry<bool> LeanDuringAttack;
        internal static ConfigEntry<bool> LeanAlways;
        internal static ConfigEntry<float> LeanBodyWeight;
        internal static ConfigEntry<float> LeanHeadWeight;
        internal static ConfigEntry<float> LeanEyeWeight;
        internal static ConfigEntry<float> LeanClampWeight;
        internal static ConfigEntry<float> LeanOverallWeight;
        internal static ConfigEntry<float> LeanMaxYaw;
        internal static ConfigEntry<float> LeanMaxPitch;
        internal static ConfigEntry<float> LeanDirectionSmoothing;
        internal static ConfigEntry<float> LeanRampSpeed;
        internal static ConfigEntry<float> LeanTargetDistance;
        internal static ConfigEntry<bool> LeanSkipAreaAttacks;
        internal static ConfigEntry<bool> LeanSkipFacingYAim;

        // ---- diving ----
        internal static ConfigEntry<bool> DiveEnabled;
        internal static ConfigEntry<KeyCode> DiveKey;
        internal static ConfigEntry<float> DiveSpeed;
        internal static ConfigEntry<bool> DiveNeedsForward;

        // ---- first person ----
        internal static ConfigEntry<bool> FpEnabled;
        internal static ConfigEntry<bool> FpAllowFullZoom;
        internal static ConfigEntry<float> FpZoomThreshold;
        internal static ConfigEntry<float> FpExitZoomThreshold;
        internal static ConfigEntry<float> FpEyeDropFromTop;
        internal static ConfigEntry<float> FpFallbackEyeHeight;
        internal static ConfigEntry<float> FpVerticalSmoothing;
        internal static ConfigEntry<float> FpNearClip;
        internal static ConfigEntry<float> FpBodyForwardOffset;
        internal static ConfigEntry<float> FpBodySideOffset;
        internal static ConfigEntry<float> FpMaxOffset;
        internal static ConfigEntry<bool> FpAvoidGeometry;
        internal static ConfigEntry<bool> FpHideBody;
        internal static ConfigEntry<bool> FpHideSkinnedOnly;
        internal static ConfigEntry<HideMethod> FpHideMethod;
        internal static ConfigEntry<bool> FpAlwaysAnimate;
        internal static ConfigEntry<bool> FpKeepBodyVisible;

        internal static ConfigEntry<bool> DebugLogging;

        // Set when a feature throws, to stop it running for the rest of the session. Deliberately
        // NOT written to the config: BepInEx persists config writes to disk, so disabling a feature
        // that way survives restarts and looks like the feature is permanently broken.
        internal static bool LeanFaulted;
        internal static bool DiveFaulted;
        internal static bool FirstPersonFaulted;

        public enum HideMethod
        {
            /// <summary>
            /// Render into shadow maps only: invisible to the camera, but still counts as
            /// rendering, so Unity keeps evaluating the skeleton and the held weapon keeps
            /// animating. Your shadow stays correct too.
            /// </summary>
            ShadowsOnly,

            /// <summary>
            /// forceRenderingOff. Hides more thoroughly, but Unity may then treat the
            /// character as off-screen and stop updating bones, freezing the weapon.
            /// </summary>
            ForceOff
        }

        private void Awake()
        {
            Log = Logger;

            // ------------------------------------------------------------ melee aim ----
            MeleeAimEnabled = Config.Bind("01 - Melee aim", "enabled", true,
                "Let melee attacks be aimed up and down. Vanilla caps the vertical angle per " +
                "attack (Attack.m_maxYAngle), which is why an enemy slightly up a slope can be " +
                "impossible to hit. This raises that cap for attacks whose cap is lower.");

            MeleeMaxYAngle = Config.Bind("01 - Melee aim", "maxAngleDegrees", 45f,
                new ConfigDescription(
                    "Vertical aim allowed for melee attacks, degrees. 45 covers slopes without " +
                    "feeling unnatural. 90 lets you hit straight up and down, but the camera sits " +
                    "above the character so attacks can then pass under a close target. Attacks " +
                    "that already allow more than this are left alone.",
                    new AcceptableValueRange<float>(0f, 90f)));

            // ------------------------------------------------------------ aim lean ----
            LeanEnabled = Config.Bind("02 - Aim lean", "enabled", true,
                "Pitch the upper body toward where you aim, so the swing matches the hit " +
                "direction. Valheim has the machinery for this - humanoid look-at IK with a body " +
                "weight - but disables it during attacks, which is why vanilla melee and bow " +
                "aiming never tilt the body.");

            LeanBodyWeight = Config.Bind("02 - Aim lean", "bodyWeight", 0.55f,
                new ConfigDescription(
                    "How much the BODY follows your aim, 0-1. The main dial. 0.5-0.7 reads as " +
                    "leaning into the swing; 1.0 is a full bend.",
                    new AcceptableValueRange<float>(0f, 1f)));

            LeanHeadWeight = Config.Bind("02 - Aim lean", "headWeight", 0.8f,
                new ConfigDescription("How much the head follows your aim, 0-1.",
                    new AcceptableValueRange<float>(0f, 1f)));

            LeanEyeWeight = Config.Bind("02 - Aim lean", "eyeWeight", 0.5f,
                new ConfigDescription("How much the eyes follow your aim, 0-1.",
                    new AcceptableValueRange<float>(0f, 1f)));

            LeanClampWeight = Config.Bind("02 - Aim lean", "clampWeight", 0.35f,
                new ConfigDescription(
                    "Unity's look-at clamp, 0-1. Higher restricts the solver more. Raise if the " +
                    "pose looks strained at steep angles.",
                    new AcceptableValueRange<float>(0f, 1f)));

            LeanOverallWeight = Config.Bind("02 - Aim lean", "overallWeight", 1.0f,
                new ConfigDescription("Master multiplier over the whole lean, 0-1.",
                    new AcceptableValueRange<float>(0f, 1f)));

            LeanMaxYaw = Config.Bind("02 - Aim lean", "maxYawDegrees", 50f,
                new ConfigDescription(
                    "How far left/right of the body's own facing the look target may sit. This is " +
                    "the spine-twist guard: feeding the solver a raw camera direction lets the " +
                    "target swing behind the character while standing still, and the solver then " +
                    "corkscrews the torso to reach it.",
                    new AcceptableValueRange<float>(0f, 90f)));

            LeanMaxPitch = Config.Bind("02 - Aim lean", "maxPitchDegrees", 60f,
                new ConfigDescription("How far up/down the look target may sit, degrees.",
                    new AcceptableValueRange<float>(0f, 89f)));

            LeanDirectionSmoothing = Config.Bind("02 - Aim lean", "directionSmoothing", 10f,
                new ConfigDescription(
                    "How quickly the look target follows the camera. Lower damps fast mouse flicks.",
                    new AcceptableValueRange<float>(0f, 40f)));

            LeanRampSpeed = Config.Bind("02 - Aim lean", "rampSpeed", 6f,
                new ConfigDescription("How fast the lean fades in and out, weight per second.",
                    new AcceptableValueRange<float>(0.5f, 30f)));

            LeanTargetDistance = Config.Bind("02 - Aim lean", "targetDistance", 10f,
                new ConfigDescription("How far ahead the look target sits, metres.",
                    new AcceptableValueRange<float>(2f, 50f)));

            LeanDuringAttack = Config.Bind("02 - Aim lean", "leanDuringAttack", true,
                "Keep the lean alive while attacking. Vanilla sets look-at weight to 0 there, " +
                "which is the behaviour this works around. Leave true.");

            LeanAlways = Config.Bind("02 - Aim lean", "leanOutsideAttack", true,
                "Also lean while not attacking, which looks more consistent.");

            LeanLocalOnly = Config.Bind("02 - Aim lean", "localPlayerOnly", false,
                "Only lean your own character. Default false: this drives the engine's look-at " +
                "from each character's own networked look direction, so others lean correctly.");

            LeanSkipAreaAttacks = Config.Bind("02 - Aim lean", "skipAreaAttacks", true,
                "Do not lean for Area or None attack types - radial slams centred on the " +
                "character, such as a Stagbreaker, where aim direction is not used at all.");

            LeanSkipFacingYAim = Config.Bind("02 - Aim lean", "skipFacingYAim", true,
                "Do not lean for attacks flagged m_useCharacterFacingYAim, which deliberately " +
                "use body facing rather than where you look.");

            // -------------------------------------------------------------- diving ----
            DiveEnabled = Config.Bind("03 - Diving", "enabled", true,
                "Allow swimming downward. Vanilla pins you to a fixed depth below the surface by " +
                "driving vertical velocity toward GetLiquidLevel() - m_swimDepth; holding the dive " +
                "key overrides that while you steer with the mouse.");

            DiveKey = Config.Bind("03 - Diving", "diveKey", KeyCode.LeftControl,
                "Hold to dive. Aim with the mouse: look down to descend, up to rise. This is a " +
                "raw key check, so it does not follow Valheim's own key rebinding.");

            DiveSpeed = Config.Bind("03 - Diving", "diveSpeed", 3.5f,
                new ConfigDescription("Vertical speed while diving, metres per second.",
                    new AcceptableValueRange<float>(0.5f, 12f)));

            DiveNeedsForward = Config.Bind("03 - Diving", "requireMovement", false,
                "Require a movement input as well as the dive key. Off by default so you can " +
                "descend while stationary.");

            // --------------------------------------------------------- first person ----
            FpEnabled = Config.Bind("04 - First person", "enabled", true,
                "First-person camera that engages when you zoom in past zoomThreshold. Disable " +
                "ValheimPlus's own [FirstPerson] section if you use this, or the two fight over " +
                "the camera.");

            FpAllowFullZoom = Config.Bind("04 - First person", "allowFullZoom", true,
                "Force GameCamera.m_minDistance to 0 so the camera can zoom in far enough to " +
                "reach the threshold. Vanilla floors the zoom distance above any first-person " +
                "threshold, so WITHOUT this the mode can never engage and you simply stay in the " +
                "closest third-person view. The original value is restored when disabled.");

            FpZoomThreshold = Config.Bind("04 - First person", "zoomThreshold", 0.6f,
                new ConfigDescription("Engage below this zoom distance, metres.",
                    new AcceptableValueRange<float>(0.05f, 4f)));

            FpExitZoomThreshold = Config.Bind("04 - First person", "exitZoomThreshold", 1.2f,
                new ConfigDescription(
                    "Leave first person only once the zoom distance rises above this, in metres. " +
                    "Deliberately higher than zoomThreshold: with a single threshold, anything that " +
                    "nudges the distance the moment first person engages makes entry and exit fight " +
                    "each other and the mode flickers in and out every frame. The gap between the " +
                    "two values is that hysteresis. Must be greater than zoomThreshold.",
                    new AcceptableValueRange<float>(0.1f, 6f)));

            FpEyeDropFromTop = Config.Bind("04 - First person", "eyeDropFromTop", 0.12f,
                new ConfigDescription(
                    "How far below the top of the character's capsule hitbox the eyes sit, metres. " +
                    "Height comes from the hitbox rather than the head bone, so there is no " +
                    "animation bob at all and crouching, swimming and rolling need no special case.",
                    new AcceptableValueRange<float>(0f, 0.8f)));

            FpFallbackEyeHeight = Config.Bind("04 - First person", "fallbackEyeHeight", 1.65f,
                new ConfigDescription("Used only if the hitbox height cannot be read, metres.",
                    new AcceptableValueRange<float>(0.5f, 2.5f)));

            FpVerticalSmoothing = Config.Bind("04 - First person", "verticalSmoothing", 18f,
                new ConfigDescription(
                    "Softens vertical camera movement only, which takes the edge off stair steps. " +
                    "Overriding the camera position bypasses vanilla's own smoothing. Never " +
                    "touches horizontal movement or look, so it adds no aiming lag.",
                    new AcceptableValueRange<float>(0f, 40f)));

            FpNearClip = Config.Bind("04 - First person", "nearClipPlane", 0.02f,
                new ConfigDescription(
                    "Near clip while in first person. Enforced every frame and written into the " +
                    "camera's m_nearClipPlaneMin, because GameCamera.UpdateNearClipping recomputes " +
                    "the near plane from that field every frame.",
                    new AcceptableValueRange<float>(0.005f, 0.3f)));

            FpBodyForwardOffset = Config.Bind("04 - First person", "bodyForwardOffset", 0f,
                new ConfigDescription(
                    "Offset along the character's own horizontal forward, metres. In the BODY's " +
                    "frame on purpose: an offset along the view direction ties camera height to " +
                    "pitch and pokes out of the hitbox when level. 0 keeps the camera on its pivot.",
                    new AcceptableValueRange<float>(-0.5f, 0.5f)));

            FpBodySideOffset = Config.Bind("04 - First person", "bodySideOffset", 0f,
                new ConfigDescription("Lateral offset in the character's frame, metres.",
                    new AcceptableValueRange<float>(-0.5f, 0.5f)));

            FpMaxOffset = Config.Bind("04 - First person", "maxOffsetFromEye", 1.0f,
                new ConfigDescription(
                    "Safety limit, metres: anything further from the computed eye point is " +
                    "discarded in favour of the eye point itself, with one warning logged.",
                    new AcceptableValueRange<float>(0.5f, 5f)));

            FpAvoidGeometry = Config.Bind("04 - First person", "avoidGeometry", true,
                "Raycast any offset against the game's own camera-blocking layers and pull it " +
                "back before it crosses a surface, so it cannot poke through a thin wall or door.");

            FpHideBody = Config.Bind("04 - First person", "hideBody", true,
                "Hide the character model while in first person, keeping the weapon visible.");

            FpHideSkinnedOnly = Config.Bind("04 - First person", "hideSkinnedOnly", true,
                "Hide only skinned meshes. Body, hair and armour are skinned to the skeleton, " +
                "while weapons, shields and tools are plain meshes parented to the hands - which " +
                "is what keeps the weapon in view. Off hides everything, weapon included.");

            FpHideMethod = Config.Bind("04 - First person", "hideMethod", HideMethod.ShadowsOnly,
                "ShadowsOnly keeps the renderers counted as rendering, so Unity keeps animating " +
                "the skeleton and the held weapon keeps swinging. ForceOff hides harder but can " +
                "make Unity treat the character as off-screen and freeze the bones.");

            FpAlwaysAnimate = Config.Bind("04 - First person", "alwaysAnimate", true,
                "Force Animator.cullingMode to AlwaysAnimate while in first person, so the " +
                "skeleton is evaluated even when the game believes the body is not visible.");

            FpKeepBodyVisible = Config.Bind("04 - First person", "keepBodyVisible", true,
                "Stop the game LOD-culling your own character while in first person. " +
                "Character.SetVisible drives an LODGroup and at very close camera range can cull " +
                "the body away, taking your arms and weapon with it.");

            DebugLogging = Config.Bind("99 - Advanced", "debugLogging", false,
                "Log lean weights, first-person state and dive state once per second.");

            WarnAboutConflicts();

            Harmony harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(MeleeAim));
            harmony.PatchAll(typeof(AimLean));
            harmony.PatchAll(typeof(Diving));
            harmony.PatchAll(typeof(FirstPersonCamera));
            harmony.PatchAll(typeof(EquipmentWatcher));

            // One line, naming the version so a stale DLL is identifiable at a glance.
            Logger.LogInfo($"{Name} {Version} loaded.");

            // A patch that silently fails to apply is indistinguishable from a feature that does
            // not work, so the full list is available under debugLogging - but a missing camera
            // hook is always worth a warning, since first person cannot function without it.
            try
            {
                var patched = new List<string>();
                foreach (MethodBase m in harmony.GetPatchedMethods())
                    patched.Add($"{m.DeclaringType?.Name}.{m.Name}");
                patched.Sort();

                if (DebugLogging.Value)
                    Logger.LogInfo($"patched {patched.Count} methods: {string.Join(", ", patched)}");

                if (FpEnabled.Value && !patched.Contains("GameCamera.LateUpdate"))
                    Logger.LogWarning("GameCamera.LateUpdate is NOT patched - first person cannot work.");
            }
            catch (Exception e)
            {
                Logger.LogWarning($"could not enumerate patched methods: {e.Message}");
            }
        }

        /// <summary>
        /// Two plugins patching the same methods with independent state cannot work: each has its
        /// own idea of whether first person is active, so they fight over the camera every frame
        /// and over showing and hiding the body. The predecessor of this mod (SpineAim) patched the
        /// same methods, so a leftover copy in plugins/ produces flicker and a body that stays
        /// hidden - symptoms that look exactly like a bug in this mod.
        /// </summary>
        private void WarnAboutConflicts()
        {
            string[] known =
            {
                "gameprog.spineaim",                    // this mod's predecessor
                "searica.valheim.watchwhereyoustab",    // melee vertical aim
                "blacks7ar.VikingsDoSwim",              // diving
                "ComfyMods.VerticallyChallenged",       // melee vertical aim
            };

            foreach (string guid in known)
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(guid)) continue;

                Logger.LogWarning(
                    $"CONFLICT: '{guid}' is also loaded. It patches the same game methods as " +
                    $"{Name}, and two plugins with separate state will fight over the camera and " +
                    $"the player model - expect flicker, or a body that stays hidden. Remove it " +
                    $"from BepInEx/plugins, or disable the overlapping feature on one side.");
            }
        }
    }
}
