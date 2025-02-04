using UnityEngine;
using TMPro;
using System;
using BepInEx.Logging;

namespace SusAccess.UI;

public partial class MenuLayoutConfigs {
    private readonly UIAccessibilityHandler uiHandler;
    private readonly ManualLogSource logger;

    public MenuLayoutConfigs(UIAccessibilityHandler handler, ManualLogSource log) {
        uiHandler = handler;
        logger = log;
    }

    // Sets up all menu configurations
    public void ConfigureAllMenus() {
        try {
            // Configure FindAGame scene
            ConfigureFindAGameMenu();

            // Add additional menu configurations here as needed
        }
        catch (Exception ex) {
            logger.LogError($"Error configuring menus: {ex.Message}");
        }
    }

    private void ConfigureFindAGameMenu() {
        MenuLayoutBuilder.ForScene("FindAGame")
            .WithElements("JoinMMGame(Clone)")  // Just target the main lobby object
            .WithCustomSpeechProvider("JoinMMGame(Clone)", (element) => {
                try {
                    // Get username from the main button text
                    var username = element.GetComponent<PassiveButton>()?.GetComponentInChildren<TextMeshPro>()?.text ?? "";

                    // Find specific child GameObjects and get their TextMeshPro components
                    var playerCountText = "";
                    var playerCountTMP = element.transform.Find("PlayerCountText_TMP")?.GetComponent<TextMeshPro>();
                    if (playerCountTMP != null) {
                        playerCountText = playerCountTMP.text; // Format: "X/Y"
                    }

                    // Extract just the current players number from the format "X/Y"
                    string playerCount = playerCountText.Split('/')[0].Trim();

                    var impostorCount = "";
                    var impostorCountTMP = element.transform.Find("ImpostorCountText_TMP")?.GetComponent<TextMeshPro>();
                    if (impostorCountTMP != null) {
                        impostorCount = impostorCountTMP.text;
                    }

                    var language = "";
                    var languageTMP = element.transform.Find("LanguageText")?.GetComponent<TextMeshPro>();
                    if (languageTMP != null) {
                        language = languageTMP.text;
                    }

                    // Remove the index info since it's added automatically later
                    if (username.Contains(" of ")) {
                        username = username.Substring(0, username.LastIndexOf(" "));
                    }

                    // Construct message in requested format
                    return $"{language} lobby by {username} with {impostorCount} impostors and {playerCountText} players";
                }
                catch (Exception ex) {
                    logger.LogError($"Error reading lobby information: {ex.Message}");
                    return "Error reading lobby information";
                }
            })
            .Apply(uiHandler);
    }
}