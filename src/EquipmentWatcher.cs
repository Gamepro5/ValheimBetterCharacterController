using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BetterCharacterController
{
    /// <summary>
    /// Flags when a character's equipment visuals have been rebuilt, so the first-person body
    /// hiding can rescan for new renderers at exactly the right moment instead of polling.
    ///
    /// There is no "equipment changed" event to hook. `VisEquipment.CustomUpdate` calls
    /// `UpdateVisuals` unconditionally every frame — the guards that skip unchanged slots live
    /// further in — so hooking either of those is no better than a per-frame scan.
    ///
    /// What is worth hooking is the point where new objects are actually created. Every equipment
    /// model in VisEquipment is instantiated by exactly one of three methods: AttachItem,
    /// AttachArmor and AttachBackItem. A postfix on those is precise and cheap: it sets a bool
    /// only when a renderer could have appeared.
    /// </summary>
    [HarmonyPatch]
    internal static class EquipmentWatcher
    {
        private static bool _dirty = true;

        /// <summary>True once since the last call; resets on read.</summary>
        internal static bool ConsumeDirty()
        {
            if (!_dirty) return false;
            _dirty = false;
            return true;
        }

        internal static void MarkDirty() => _dirty = true;

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets()
        {
            // All overloads, since these methods are overloaded and the set may grow.
            var names = new HashSet<string> { "AttachItem", "AttachArmor", "AttachBackItem" };

            foreach (MethodInfo m in typeof(VisEquipment)
                         .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (names.Contains(m.Name) && !m.IsAbstract) yield return m;
            }
        }

        [HarmonyPostfix]
        private static void AfterAttach() => _dirty = true;
    }
}
