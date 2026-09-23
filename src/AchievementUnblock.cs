using HarmonyLib;

namespace BetterCharacterController
{
    /// <summary>
    /// Keeps Steam achievements working in a modded session.
    ///
    /// Valheim refuses to award achievements when Achievements.IsCheatedAtAll() is true, and that is
    /// true if ANY of these hold:
    ///
    ///   PlayerProfile.m_usedCheats          console commands used on this character, and it sticks
    ///   Achievements.IsWorldCheated()       the world is flagged
    ///   Inventory.AnyCheatedItem()          a spawned item is in your inventory right now
    ///   Game.isModded                       the session is modded
    ///
    /// That last one is why a rebalanced server loses achievements even when nobody has cheated: the
    /// check is "is this session modified", not "has this player gained an advantage".
    ///
    /// The game ships a supported way out. PlayerProfile.s_bypassCheatChecks is not a field - it
    /// reads a per-character key:
    ///
    ///   Player.m_localPlayer.TryGetUniqueKeyValue("bypasscheatchecks", out v) &amp;&amp; v == "1"
    ///
    /// and CanGetAchievements returns true whenever it is set, whatever else is flagged. Setting it
    /// normally means running "setkey bypasscheatchecks 1" in the console, which every player would
    /// have to do individually after enabling devcommands. Returning true from the getter has the
    /// same effect for everyone running this mod, and writes nothing to anyone's character file - so
    /// removing the mod restores the vanilla behaviour exactly.
    ///
    /// This unlocks achievements for play that actually happened. It does not award anything
    /// retroactively, and progress made while achievements were blocked is not recovered: the stat
    /// increments were discarded at the time, not merely withheld.
    /// </summary>
    [HarmonyPatch]
    internal static class AchievementUnblock
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerProfile), "get_s_bypassCheatChecks")]
        private static void AfterBypassCheatChecks(ref bool __result)
        {
            if (Plugin.AchievementsEnabled.Value) __result = true;
        }
    }
}
