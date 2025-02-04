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

// Represents a custom menu layout configuration for a specific scene
public class MenuLayoutConfig {
    public List<string> OrderedElements { get; set; } = new();
    public List<string> HiddenElements { get; set; } = new();
    public bool HideUnorganizedElements { get; set; } = false;

    // Required elements for this layout to be active
    public List<string> RequiredElements { get; set; } = new();

    // Custom speech mappings (element name -> custom speech text)
    public Dictionary<string, string> CustomSpeechText { get; set; } = new();

    // Custom speech providers (element name -> speech generator)
    public Dictionary<string, Func<UiElement, string>> CustomSpeechProviders { get; set; } = new();

    // Custom action handlers (element name -> action to perform)
    public Dictionary<string, Action<UiElement>> ActionHandlers { get; set; } = [];
}

// Fluent builder for creating menu layouts
public class MenuLayoutBuilder {
    private readonly MenuLayoutConfig config = new();
    private readonly string sceneName;

    private MenuLayoutBuilder(string sceneName) {
        this.sceneName = sceneName;
    }

    // Creates a new menu layout builder for the specified scene
    public static MenuLayoutBuilder ForScene(string sceneName) {
        return new MenuLayoutBuilder(sceneName);
    }

    // Adds elements in the specified order
    public MenuLayoutBuilder WithElements(params string[] elements) {
        config.OrderedElements.AddRange(elements);
        return this;
    }

    // Hides the specified elements from the menu
    public MenuLayoutBuilder HideElements(params string[] elements) {
        config.HiddenElements.AddRange(elements);
        return this;
    }

    // Sets whether to hide elements not explicitly ordered
    public MenuLayoutBuilder HideUnorganizedElements(bool hide = true) {
        config.HideUnorganizedElements = hide;
        return this;
    }

    // Specifies elements that must be present for this layout to be active
    public MenuLayoutBuilder RequireElements(params string[] elements) {
        config.RequiredElements.AddRange(elements);
        return this;
    }

    // Adds custom speech text for an element
    public MenuLayoutBuilder WithCustomSpeech(string elementName, string speechText) {
        config.CustomSpeechText[elementName] = speechText;
        return this;
    }

    // Adds a custom speech provider for complex element reading
    public MenuLayoutBuilder WithCustomSpeechProvider(string elementName, Func<UiElement, string> provider) {
        config.CustomSpeechProviders[elementName] = provider;
        return this;
    }

    // Adds a custom action handler when navigating to an element
    public MenuLayoutBuilder WithActionHandler(string elementName, Action<UiElement> handler) {
        config.ActionHandlers[elementName] = handler;
        return this;
    }

    // Builds and applies the menu layout configuration
    public void Apply(UIAccessibilityHandler handler) {
        handler.SetMenuConfig(sceneName, config);
    }
}

// Extension methods for UIAccessibilityHandler to support simpler configuration
public static class UIAccessibilityHandlerExtensions {
    // Configure menu with just ordered elements
    public static void ConfigureMenu(
        this UIAccessibilityHandler handler,
        string sceneName,
        params string[] orderedElements) {
        MenuLayoutBuilder.ForScene(sceneName)
            .WithElements(orderedElements)
            .Apply(handler);
    }

    // Configure menu with ordered and hidden elements
    public static void ConfigureMenu(
        this UIAccessibilityHandler handler,
        string sceneName,
        string[] orderedElements,
        string[] hiddenElements,
        bool hideUnorganizedElements = false) {
        MenuLayoutBuilder.ForScene(sceneName)
            .WithElements(orderedElements)
            .HideElements(hiddenElements)
            .HideUnorganizedElements(hideUnorganizedElements)
            .Apply(handler);
    }
}

// Handles general UI accessibility features
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
    private readonly Dictionary<string, MenuLayoutConfig> sceneMenuConfigs = new();
    private string currentScene = "";

    public string GetCurrentScene() {
        UpdateSceneInfo();
        return currentScene;
    }

    private void UpdateSceneInfo() {
        try {
            Scene activeScene = SceneManager.GetActiveScene();
            if (activeScene.name != currentScene) {
                UpdateCurrentScene(activeScene.name);
            }
        }
        catch (Exception e) {
            logger.LogError($"Error updating scene info: {e}");
        }
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
            currentElements.Clear();
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
        // Check if required elements are present
        if (config.RequiredElements.Any() &&
            !config.RequiredElements.All(req =>
                elements.Any(e => ((UnityEngine.Object)e).name.Equals(req, StringComparison.OrdinalIgnoreCase)))) {
            return SortElementsByPosition(elements);
        }

        var orderedElements = new List<UiElement>();
        var remainingElements = new List<UiElement>(elements);

        // Add elements in specified order
        foreach (string elementName in config.OrderedElements) {
            var element = FindElementByName(remainingElements, elementName);
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

    // Finds a UI element by its name
    private UiElement FindElementByName(List<UiElement> elements, string name) {
        return elements.FirstOrDefault(e =>
            ((UnityEngine.Object)e).name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    // Checks if an element should be hidden based on the configuration
    private bool IsElementHidden(UiElement element, List<string> hiddenElements) {
        string objectName = ((UnityEngine.Object)element).name;
        return hiddenElements.Any(h =>
            objectName.Equals(h, StringComparison.OrdinalIgnoreCase));
    }

    // Gets the readable text from a button
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

            // Fall back to GameObject name
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

    // Gets the element's position index for announcements
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
            if (Input.GetKeyDown(KeyCode.F3)) {
                string currentSceneName = GetCurrentScene();
                string elementName = currentElement != null ? GetButtonText(currentElement as PassiveButton) : "None";
                SpeechSynthesizer.SpeakText($"Current scene: {currentSceneName}, Current element: {elementName}");
            }
        }
        catch (Exception e) {
            logger.LogError($"Error in keyboard navigation: {e}");
        }
        finally {
            isProcessingInput = false;
        }
    }

    // Handles screen reader announcements for UI changes
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

    // Checks if the UI elements have meaningfully changed
    private bool HasElementsChanged(List<UiElement> newElements) {
        try {
            if (currentElements == null || newElements == null)
                return true;

            if (currentElements.Count != newElements.Count)
                return true;

            var currentIds = new HashSet<int>(currentElements
                .Where(e => e != null)
                .Select(e => ((MonoBehaviour)e).gameObject.GetInstanceID()));

            var newIds = new HashSet<int>(newElements
                .Where(e => e != null)
                .Select(e => ((MonoBehaviour)e).gameObject.GetInstanceID()));

            return !currentIds.SetEquals(newIds);
        }
        catch (Exception e) {
            logger.LogError($"Error in HasElementsChanged: {e}");
            return true;
        }
    }

    // Handles UI element changes
    private void HandleElementsChanged(ControllerManager manager, List<UiElement> newElements) {
        try {
            UpdateSceneInfo();

            if (manager == null || newElements == null) {
                logger.LogWarning("Null manager or elements in HandleElementsChanged");
                return;
            }

            logger.LogInfo("Menu elements changed:");
            foreach (var e in newElements.Where(e => e != null)) {
                var obj = e as UnityEngine.Object;
                logger.LogInfo($"  - {(obj != null ? obj.name : "Unknown")}");
            }

            currentElements = newElements.Where(e => e != null).ToList();

            if (currentElements.Count > 0) {
                SpeechSynthesizer.SpeakText($"{currentElements.Count} items available.");

                try {
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
        try {
            var currentButton = manager.CurrentUiState?.CurrentSelection?.GetComponent<PassiveButton>();

            if (currentButton != null && currentButton != lastFocusedButton) {
                var element = manager.CurrentUiState.CurrentSelection;

                // Announce the element
                AnnounceElement(element, manager);

                // Check for and execute any custom action handlers
                if (sceneMenuConfigs.TryGetValue(currentScene, out var config)) {
                    string elementName = ((UnityEngine.Object)element).name;
                    if (config.ActionHandlers.TryGetValue(elementName, out var handler)) {
                        try {
                            handler(element);
                        }
                        catch (Exception e) {
                            logger.LogError($"Error executing action handler for {elementName}: {e}");
                        }
                    }
                }

                lastFocusedButton = currentButton;
            }
        }
        catch (Exception e) {
            logger.LogError($"Error in HandleFocusChange: {e}");
        }
    }

    // Announces the current UI element with its text and position
    private void AnnounceElement(UiElement element, ControllerManager manager) {
        try {
            if (element == null || manager == null) {
                logger.LogWarning("Attempted to announce null element or manager");
                return;
            }

            string elementName = ((UnityEngine.Object)element).name;
            string indexInfo = GetElementIndex(manager, element);
            string speechText = "";

            // Check current scene's config for custom speech
            if (sceneMenuConfigs.TryGetValue(currentScene, out var config)) {
                // Try custom speech provider first
                if (config.CustomSpeechProviders.TryGetValue(elementName, out var provider)) {
                    speechText = provider(element);
                }
                // Then try custom speech text
                else if (config.CustomSpeechText.TryGetValue(elementName, out var customText)) {
                    speechText = customText;
                }
            }

            // Fall back to default if no custom speech defined
            if (string.IsNullOrEmpty(speechText)) {
                speechText = GetButtonText(element as PassiveButton);
            }

            // Only announce if we have something to say
            if (!string.IsNullOrEmpty(speechText)) {
                SpeechSynthesizer.SpeakText($"{speechText}{indexInfo}");
            }
        }
        catch (Exception e) {
            logger.LogError($"Error announcing element: {e}");
        }
    }
}