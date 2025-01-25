using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Reactor;
using Reactor.Utilities;
using UnityEngine;
using SusAccess.UI;
using SusAccess.Speech;
using SusAccess.Navigation;
using InnerNet;

namespace SusAccess
{
    // Main plugin class for SusAccess - an accessibility mod for Among Us
    [BepInPlugin(Id, "SusAccess", VersionString)]
    [BepInProcess("Among Us.exe")]
    [BepInDependency(ReactorPlugin.Id)]
    public partial class SusAccessPlugin : BasePlugin
    {
        public const string Id = "greenbean.susaccess";
        public const string VersionString = "1.0.0";

        public Harmony Harmony { get; } = new(Id);

        // Configuration entries for keybinds
        public ConfigEntry<KeyCode> NextButtonKey { get; private set; }
        public ConfigEntry<KeyCode> PreviousButtonKey { get; private set; }
        public ConfigEntry<KeyCode> ActivateButtonKey { get; private set; }
        public ConfigEntry<KeyCode> ScanSurroundingsKey { get; private set; }
        public ConfigEntry<KeyCode> TaskDetectionKey { get; private set; }

        // Core handlers for UI and navigation features
        public UIAccessibilityHandler uiHandler;
        public NavigationHandler navigationHandler;

        // Initializes the plugin, sets up configuration, and starts core systems
        public override void Load()
        {
            Log.LogInfo("Loading SusAccess...");

            // Initialize keybind configuration
            NextButtonKey = Config.Bind("Controls", "NextButton", KeyCode.DownArrow, "Key to select next button");
            PreviousButtonKey = Config.Bind("Controls", "PreviousButton", KeyCode.UpArrow, "Key to select previous button");
            ActivateButtonKey = Config.Bind("Controls", "ActivateButton", KeyCode.Return, "Key to activate selected button");
            ScanSurroundingsKey = Config.Bind("Controls", "ScanSurroundings", KeyCode.Tab, "Key to scan surroundings");
            TaskDetectionKey = Config.Bind("Controls", "TaskDetection", KeyCode.T, "Key to detect nearest task");

            Log.LogInfo($"Navigation keys configured: Up={PreviousButtonKey.Value}, Down={NextButtonKey.Value}, " +
                       $"Activate={ActivateButtonKey.Value}, Scan={ScanSurroundingsKey.Value}, " +
                       $"Task={TaskDetectionKey.Value}");

            // Initialize core handlers
            uiHandler = new UIAccessibilityHandler(
                Log,
                NextButtonKey,
                PreviousButtonKey,
                ActivateButtonKey
            );

            navigationHandler = new NavigationHandler(
                Log,
                ScanSurroundingsKey,
                TaskDetectionKey
            );

            // Initialize speech system
            try
            {
                SpeechSynthesizer.Initialize(Log);
                Log.LogInfo("Speech synthesizer initialized");
                SpeechSynthesizer.SpeakText("SusAccess mod loaded");
            }
            catch (System.Exception e)
            {
                Log.LogError($"Failed to initialize speech: {e}");
            }

            // Apply Harmony patches
            try
            {
                Harmony.PatchAll();
                Log.LogInfo("Patches applied successfully");
            }
            catch (System.Exception e)
            {
                Log.LogError($"Failed to apply patches: {e}");
            }

            Log.LogInfo("SusAccess loaded successfully!");
        }

        // Cleans up resources when the plugin is unloaded
        public override bool Unload()
        {
            Log.LogInfo("Unloading SusAccess...");
            SpeechSynthesizer.Shutdown();
            Harmony.UnpatchAll();
            return true;
        }
    }

    // Patches the ControllerManager to handle UI accessibility features
    [HarmonyPatch(typeof(ControllerManager), nameof(ControllerManager.Update))]
    public static class ControllerManagerPatch
    {
        public static void Postfix(ControllerManager __instance)
        {
            var plugin = PluginSingleton<SusAccessPlugin>.Instance;
            plugin.uiHandler?.HandleUpdate(__instance);
        }
    }

    // Patches PlayerControl to handle navigation assistance features
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
    public static class PlayerUpdatePatch
    {
        public static void Postfix(PlayerControl __instance)
        {
            if (!__instance.AmOwner) return;
            var plugin = PluginSingleton<SusAccessPlugin>.Instance;
            plugin.navigationHandler?.HandleUpdate(__instance);
        }
    }

    // Patches AmongUsClient to announce when players leave the game
    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft))]
    public static class PlayerLeftAnnouncementsPatch
    {
        public static void Postfix(AmongUsClient __instance, [HarmonyArgument(0)] ClientData data)
        {
            var playerInfo = GameData.Instance?.GetPlayerById(data.Character.PlayerId);
            if (playerInfo != null)
            {
                string playerColor = playerInfo.DefaultOutfit.ColorId.ToString();
                SpeechSynthesizer.SpeakText($"{playerColor} player left the game");
            }
        }
    }

    // Patches MeetingHud to announce when meetings start
    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
    public static class MeetingAnnouncementsPatch
    {
        public static void Postfix(MeetingHud __instance)
        {
            if (__instance.state == MeetingHud.VoteStates.Discussion)
            {
                PlayerControl reporter = __instance.state == MeetingHud.VoteStates.Discussion ? PlayerControl.LocalPlayer : null;
                if (reporter != null)
                {
                    string reporterColor = reporter.Data.DefaultOutfit.ColorId.ToString();
                    SpeechSynthesizer.SpeakText($"Emergency meeting called by {reporterColor}");
                }
                else
                {
                    SpeechSynthesizer.SpeakText("Body reported, meeting started");
                }
            }
        }
    }
}