using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace UCHGamePerformance
{
    // 2.15.0: config split. The plugin's own Config (com.nexus.uch.gameperformance.cfg) is what mod managers such as
    // Barnyard list as the mod's settings page, so it holds only the everyday, live settings in section "Settings"
    // (2.15.0: two; 3.1.0: eight = Fix screen tearing, Uncap FPS, Show FPS counter and the counter's color/font/size/X/Y).
    // Every performance switch moved, with the same sections, keys, defaults and descriptions, to a second file
    // (com.nexus.uch.gameperformance.advanced.cfg). First start after the update: if the advanced file is new, the
    // values of the technical entries in the old main file (BepInEx keeps them as orphaned entries until something
    // binds them) are handed to the advanced file, and the main file is rewritten without them. Any error: log, defaults.
    internal static class SettingsConfig
    {
        internal const string AdvancedFileName = "com.nexus.uch.gameperformance.advanced.cfg";
        internal const string Section = "Settings";
        internal const string Header = "ADVANCED FILE of UCH Game Performance: every performance switch lives here and each one needs a game restart. The everyday settings (Fix screen tearing, Uncap FPS, Show FPS counter and its look) are in com.nexus.uch.gameperformance.cfg and on the mod's settings page in the mod manager. ";
        private const string CounterTag = "EnabledIf:Show FPS counter=True";
        private static ConfigFile advanced;
        internal static string Migration = "not run";

        // Plugin code binds every technical option through this; falls back to the main file if the advanced one failed.
        internal static ConfigFile Advanced(this BaseUnityPlugin plugin)
        {
            return advanced ?? plugin.Config;
        }

        internal static void Open(BaseUnityPlugin plugin, ManualLogSource log)
        {
            try
            {
                advanced = new ConfigFile(Utility.CombinePaths(Paths.ConfigPath, AdvancedFileName), true, plugin.Info.Metadata);
            }
            catch (Exception e)
            {
                advanced = null;
                Migration = "advanced file unavailable";
                log.LogWarning("Advanced config file could not be opened (" + e.GetType().Name + ": " + e.Message + "); the technical options stay in the main file.");
            }
            int dropped = 0;
            if (advanced != null)
            {
                try { dropped = Migrate(plugin.Config, advanced); }
                catch (Exception e)
                {
                    Migration = "failed: " + e.GetType().Name + ": " + e.Message;
                    log.LogWarning("Config migration to " + AdvancedFileName + " failed (" + e.GetType().Name + ": " + e.Message + "); technical options use their defaults.");
                }
            }
            ScreenTearingHooks.Entry = plugin.Config.Bind(Section, "Fix screen tearing", false, new ConfigDescription(
                "On = VSync on with a one-frame queue: no tearing, FPS capped at the monitor refresh rate, less input delay than the game's own VSync. Off = the game's own setting.", null, "Order:1"));
            // 3.1.0: Uncap FPS (Order:2) and the FPS counter's look (Order:4-8). Barnyard greys a row out while its
            // EnabledIf condition is false (it cannot hide rows); every row applies live through SettingChanged.
            UncapSettingHooks.Entry = plugin.Config.Bind(Section, "Uncap FPS", false, new ConfigDescription(
                "On = no FPS cap and VSync off (can tear on monitors without G-Sync/FreeSync). Off = the game's own cap.", null, "Order:2", "EnabledIf:Fix screen tearing=False"));
            FpsCounterHooks.Entry = plugin.Config.Bind(Section, "Show FPS counter", false, new ConfigDescription(
                "Shows the current FPS, in the top left corner by default. Color, font, size and position are the rows below.", null, "Order:3"));
            FpsCounterHooks.ColorEntry = plugin.Config.Bind(Section, "FPS counter color", FpsCounterHooks.CounterColor.White, new ConfigDescription(
                "Text color of the FPS counter.", null, "Order:4", CounterTag));
            FpsCounterHooks.FontEntry = plugin.Config.Bind(Section, "FPS counter font", FpsCounterHooks.CounterFont.Game, new ConfigDescription(
                "Game = the game's own font. Default = a plain built-in font.", null, "Order:5", CounterTag));
            FpsCounterHooks.SizeEntry = plugin.Config.Bind(Section, "FPS counter size", 28, new ConfigDescription(
                "Text size at 1080p (scaled with the screen height).", new AcceptableValueRange<int>(10, 80), "Order:6", CounterTag));
            FpsCounterHooks.XEntry = plugin.Config.Bind(Section, "FPS counter X", 0, new ConfigDescription(
                "Left edge of the counter in percent of the screen width (0 = left edge).", new AcceptableValueRange<int>(0, 100), "Order:7", CounterTag));
            FpsCounterHooks.YEntry = plugin.Config.Bind(Section, "FPS counter Y", 0, new ConfigDescription(
                "Top edge of the counter in percent of the screen height (0 = top edge).", new AcceptableValueRange<int>(0, 100), "Order:8", CounterTag));
            if (dropped > 0)
            {
                try { plugin.Config.Save(); }
                catch (Exception e) { log.LogWarning("Main config save after migration failed: " + e.Message); }
            }
            log.LogInfo("Config: settings page " + plugin.Config.ConfigFilePath + " (" + plugin.Config.Count + " entries); advanced " + (advanced == null ? "(main file)" : advanced.ConfigFilePath) + "; migration " + Migration);
        }

        // Returns the number of technical entries removed from the main file.
        private static int Migrate(ConfigFile main, ConfigFile target)
        {
            Dictionary<ConfigDefinition, string> from = Orphans(main), to = Orphans(target);
            bool fresh = to.Count == 0 && target.Count == 0;
            List<ConfigDefinition> technical = new List<ConfigDefinition>();
            foreach (KeyValuePair<ConfigDefinition, string> item in from)
                if (!string.Equals(item.Key.Section, Section, StringComparison.Ordinal)) technical.Add(item.Key);
            int carried = 0;
            if (fresh)
            {
                foreach (ConfigDefinition key in technical) { to[key] = from[key]; carried++; }
                if (carried > 0) target.Save();
            }
            foreach (ConfigDefinition key in technical) from.Remove(key);
            Migration = (fresh ? "new advanced file, " : "advanced file exists, ") + carried + " values carried over, " + technical.Count + " technical entries removed from the main file";
            return technical.Count;
        }

        private static Dictionary<ConfigDefinition, string> Orphans(ConfigFile file)
        {
            PropertyInfo property = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Dictionary<ConfigDefinition, string> orphans = property == null ? null : property.GetValue(file, null) as Dictionary<ConfigDefinition, string>;
            if (orphans == null) throw new MissingMemberException("ConfigFile", "OrphanedEntries");
            return orphans;
        }

        // Called after the General/Enabled master switch: starts the two live settings.
        internal static void StartSettings()
        {
            ScreenTearingHooks.Start();
            FpsCounterHooks.Start();
        }
    }
}
