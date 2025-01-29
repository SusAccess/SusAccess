using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System;
using TMPro;
using BepInEx.Logging;
using BepInEx.Configuration;
using SusAccess.Speech;
using UnityEngine.SceneManagement;

namespace SusAccess.UI;

// Handles general UI accessibility
public class UIAccessibilityHandler {
    private const float VERTICAL_THRESHOLD = 0.1f;  // Distance threshold for grouping UI elements vertically

    private PassiveButton lastFocusedButton = null;
    private List<UiElement> currentElements = new List<UiElement>();
    private bool isProcessingInput = false;
    private readonly ManualLogSource logger;

    // Keybind configuration
    private readonly ConfigEntry<KeyCode> DebugKey;
    private readonly ConfigEntry<KeyCode> nextButtonKey;
    private readonly ConfigEntry<KeyCode> previousButtonKey;
    private readonly ConfigEntry<KeyCode> activateButtonKey;

    // Custom menu layout configuration
    private Dictionary<string, MenuLayoutConfig> sceneMenuConfigs = new Dictionary<string, MenuLayoutConfig>();
    private string currentScene = "";

    public string GetCurrentScene() {
        return currentScene;
    }

    public UIAccessibilityHandler(
        ManualLogSource log,
        ConfigEntry<KeyCode> debugKey,
        ConfigEntry<KeyCode> nextKey,
        ConfigEntry<KeyCode> prevKey,
        ConfigEntry<KeyCode> activateKey) {
        logger = log;
        this.DebugKey = debugKey;
        nextButtonKey = nextKey;
        previousButtonKey = prevKey;
        activateButtonKey = activateKey;
    }

    // Represents a custom menu layout configuration for a specific scene
    public class MenuLayoutConfig {
        public List<string> OrderedElements { get; set; } = new List<string>();
        public List<string> HiddenElements { get; set; } = new List<string>();
        public bool HideUnorganizedElements { get; set; } = false;
    }

    // Adds or updates a menu layout configuration for a specific scene
    public void SetMenuConfig(string sceneName, MenuLayoutConfig config) {
        sceneMenuConfigs[sceneName] = config;
        logger.LogInfo($"Updated menu configuration for scene: {sceneName}");
    }

    // Updates the current scene name and refreshes the UI if needed
    public void UpdateCurrentScene(string sceneName) {
        if (currentScene != sceneName) {
            logger.LogInfo($"Scene changing from '{currentScene}' to '{sceneName}'");
            currentScene = sceneName;
        }
    }

    // Gets UI elements sorted according to the current scene's layout configuration
    private List<UiElement> GetSortedElements(ControllerManager manager) {
        // Get all active UI elements
        var elements = new List<UiElement>();
        foreach (var e in manager.CurrentUiState.SelectableUiElements) {
            if (e != null && ((Behaviour)e).isActiveAndEnabled) {
                elements.Add(e);
            }
        }

        // Apply scene-specific configuration if available
        if (sceneMenuConfigs.TryGetValue(currentScene, out var config)) {
            return ApplyMenuConfig(elements, config);
        }

        // Fall back to default vertical position-based sorting
        return SortElementsByPosition(elements);
    }

    // Applies the menu configuration to sort and filter UI elements
    private List<UiElement> ApplyMenuConfig(List<UiElement> elements, MenuLayoutConfig config) {
        var orderedElements = new List<UiElement>();
        var remainingElements = new List<UiElement>(elements);

        // First, add elements in the specified order
        foreach (string elementIdentifier in config.OrderedElements) {
            var element = FindElementByIdentifier(remainingElements, elementIdentifier);
            if (element != null) {
                orderedElements.Add(element);
                remainingElements.Remove(element);
            }
        }

        // Remove hidden elements
        remainingElements.RemoveAll(e => IsElementHidden(e, config.HiddenElements));

        // Add remaining elements if not hidden
        if (!config.HideUnorganizedElements) {
            orderedElements.AddRange(SortElementsByPosition(remainingElements));
        }

        return orderedElements;
    }

    // Sorts elements by vertical position and left-to-right within rows
    private List<UiElement> SortElementsByPosition(List<UiElement> elements) {
        var groupedElements = elements.GroupBy(e => {
            float yPos = ((Component)e).transform.position.y;
            return Mathf.Round(yPos / VERTICAL_THRESHOLD) * VERTICAL_THRESHOLD;
        });

        return groupedElements
            .OrderByDescending(g => g.Key)
            .SelectMany(group => group.OrderBy(e => ((Component)e).transform.position.x))
            .ToList();
    }

    // Finds a UI element by its identifier (text content or GameObject name)
    private UiElement FindElementByIdentifier(List<UiElement> elements, string identifier) {
        return elements.FirstOrDefault(e => {
            string buttonText = GetButtonText(e as PassiveButton);
            string objectName = ((UnityEngine.Object)e).name;
            return buttonText.Equals(identifier, StringComparison.OrdinalIgnoreCase) ||
                   objectName.Equals(identifier, StringComparison.OrdinalIgnoreCase);
        });
    }

    // Checks if an element should be hidden based on the configuration
    private bool IsElementHidden(UiElement element, List<string> hiddenElements) {
        string buttonText = GetButtonText(element as PassiveButton);
        string objectName = ((UnityEngine.Object)element).name;
        return hiddenElements.Any(h =>
            buttonText.Equals(h, StringComparison.OrdinalIgnoreCase) ||
            objectName.Equals(h, StringComparison.OrdinalIgnoreCase));
    }

    // Gets the readable text for a button from various possible sources
    private string GetButtonText(PassiveButton button) {
        try {
            if (button == null)
                return "";

            // First try to get text from UI components
            var tmpText = button.GetComponentInChildren<TextMeshPro>();
            if (tmpText != null && !string.IsNullOrEmpty(tmpText.text))
                return tmpText.text;

            var tmpProText = button.GetComponentInChildren<TextMeshProUGUI>();
            if (tmpProText != null && !string.IsNullOrEmpty(tmpProText.text))
                return tmpProText.text;

            // Fall back to GameObject name - try multiple ways to get it
            try {
                if (!string.IsNullOrEmpty(button.name))
                    return button.name;

                if (button.gameObject != null && !string.IsNullOrEmpty(button.gameObject.name))
                    return button.gameObject.name;
            }
            catch {
                // Ignore errors in name getting attempts
            }

            return "";
        }
        catch (Exception e) {
            logger.LogError($"Error getting button text: {e}");
            return "";
        }
    }

    // Gets the element's position index in the current UI for announcements
    private string GetElementIndex(ControllerManager manager, UiElement element) {
        var elements = GetSortedElements(manager);
        int index = elements.IndexOf(element);
        return index >= 0 ? $" {index + 1} of {elements.Count}" : "";
    }

    // Main update handler for UI accessibility features
    public void HandleUpdate(ControllerManager manager) {
        HandleKeyboardNavigation(manager);
        HandleScreenReader(manager);
    }

    // Handles keyboard navigation input and button activation
    private void HandleKeyboardNavigation(ControllerManager manager) {
        if (!manager.IsUiControllerActive || isProcessingInput)
            return;

        try {
            isProcessingInput = true;
            var sortedElements = GetSortedElements(manager);
            var currentElement = manager.CurrentUiState.CurrentSelection;
            int currentIndex = sortedElements.IndexOf(currentElement);

            // Handle next element selection
            if (Input.GetKeyDown(nextButtonKey.Value)) {
                if (currentIndex < sortedElements.Count - 1) {
                    manager.HighlightSelection(sortedElements[currentIndex + 1]);
                }
            }
            // Handle previous element selection
            else if (Input.GetKeyDown(previousButtonKey.Value)) {
                if (currentIndex > 0) {
                    manager.HighlightSelection(sortedElements[currentIndex - 1]);
                }
            }
            // Handle button activation
            else if (Input.GetKeyDown(activateButtonKey.Value)) {
                var selectedButton = manager.CurrentUiState.CurrentSelection as PassiveButton;
                if (selectedButton != null) {
                    selectedButton.ReceiveClickDown();
                    selectedButton.ReceiveClickUp();
                }
            }

            // Handle debug keys
            if (Input.GetKeyDown(KeyCode.F3)) SpeechSynthesizer.SpeakText($"Current scene: {GetCurrentScene()} Current element: {currentElement}");
        }
        catch (Exception e) {
            logger.LogError($"Error in keyboard navigation: {e}");
        }
        finally {
            isProcessingInput = false;
        }
    }

    // Handles screen reader announcements for UI changes and focus updates
    private void HandleScreenReader(ControllerManager manager) {
        var newElements = GetSortedElements(manager);

        // Check if UI elements have changed
        bool elementsChanged = HasElementsChanged(newElements);

        if (elementsChanged) {
            HandleElementsChanged(manager, newElements);
        }

        // Handle focus changes
        HandleFocusChange(manager);
    }

    // Checks if the UI elements have meaningfully changed by comparing their IDs as sets
    private bool HasElementsChanged(List<UiElement> newElements) {
        try {
            if (currentElements == null || newElements == null)
                return true;

            // First check if counts are different
            if (currentElements.Count != newElements.Count)
                return true;

            // Create sets of instance IDs for comparison
            var currentIds = new HashSet<int>(currentElements
                .Where(e => e != null)
                .Select(e => ((MonoBehaviour)e).gameObject.GetInstanceID()));

            var newIds = new HashSet<int>(newElements
                .Where(e => e != null)
                .Select(e => ((MonoBehaviour)e).gameObject.GetInstanceID()));

            // Compare the sets - order doesn't matter
            return !currentIds.SetEquals(newIds);
        }
        catch (Exception e) {
            logger.LogError($"Error in HasElementsChanged: {e}");
            return true; // Default to true on error to ensure we don't miss updates
        }
    }

    // Handles UI element changes including logging and announcements
    private void HandleElementsChanged(ControllerManager manager, List<UiElement> newElements) {
        try {
            if (manager == null) {
                logger.LogWarning("Null manager in HandleElementsChanged");
                return;
            }

            if (newElements == null) {
                logger.LogWarning("Null elements list in HandleElementsChanged");
                return;
            }

            logger.LogInfo("Menu elements changed:");
            foreach (var e in newElements.Where(e => e != null)) {
                var obj = e as UnityEngine.Object;
                logger.LogInfo($"  - {(obj != null ? obj.name : "Unknown")}");
            }

            currentElements = newElements.Where(e => e != null).ToList();

            // Only announce if there are elements to announce
            if (currentElements.Count > 0) {
                SpeechSynthesizer.SpeakText($"{currentElements.Count} items available.");

                try {
                    // Auto-focus first element if available
                    var firstElement = currentElements[0];
                    if (firstElement != null) {
                        manager.HighlightSelection(firstElement);
                        var obj = firstElement as UnityEngine.Object;
                        logger.LogInfo($"Auto-focused on first element: {(obj != null ? obj.name : "Unknown")}");

                        AnnounceElement(firstElement, manager);
                        lastFocusedButton = firstElement as PassiveButton;
                    }
                }
                catch (Exception e) {
                    logger.LogError($"Error handling first element: {e}");
                }
            }
        }
        catch (Exception e) {
            logger.LogError($"Error in HandleElementsChanged: {e}");
        }
    }

    // Handles focus changes between UI elements
    private void HandleFocusChange(ControllerManager manager) {
        var currentButton = manager.CurrentUiState?.CurrentSelection?.GetComponent<PassiveButton>();

        if (currentButton != null && currentButton != lastFocusedButton) {
            AnnounceElement(manager.CurrentUiState.CurrentSelection, manager);
            lastFocusedButton = currentButton;
        }
    }

    // Announces the current UI element with its text and position
    private void AnnounceElement(UiElement element, ControllerManager manager) {
        try {
            if (element == null || manager == null) {
                logger.LogWarning("Attempted to announce null element or manager");
                return;
            }

            string buttonText = GetButtonText(element as PassiveButton);
            string indexInfo = GetElementIndex(manager, element);

            // Only announce if we have button text - avoid announcing just the index
            if (!string.IsNullOrEmpty(buttonText)) {
                SpeechSynthesizer.SpeakText($"{buttonText}{indexInfo}");
            }
        }
        catch (Exception e) {
            logger.LogError($"Error announcing element: {e}");
        }
    }
}
