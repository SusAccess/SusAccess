using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System;
using TMPro;
using BepInEx.Logging;
using BepInEx.Configuration;
using SusAccess.Speech;

namespace SusAccess.UI
{
    // Handles UI accessibility features including keyboard navigation and screen reader announcements
    public class UIAccessibilityHandler
    {
        private const float VERTICAL_THRESHOLD = 0.1f;  // Distance threshold for grouping UI elements vertically

        private PassiveButton lastFocusedButton = null;
        private List<UiElement> currentElements = new List<UiElement>();
        private bool isProcessingInput = false;
        private readonly ManualLogSource logger;

        // Keybind configuration
        private readonly ConfigEntry<KeyCode> nextButtonKey;
        private readonly ConfigEntry<KeyCode> previousButtonKey;
        private readonly ConfigEntry<KeyCode> activateButtonKey;

        public UIAccessibilityHandler(
            ManualLogSource log,
            ConfigEntry<KeyCode> nextKey,
            ConfigEntry<KeyCode> prevKey,
            ConfigEntry<KeyCode> activateKey)
        {
            logger = log;
            nextButtonKey = nextKey;
            previousButtonKey = prevKey;
            activateButtonKey = activateKey;
        }

        // Gets UI elements sorted by their vertical position and left-to-right within each row
        private List<UiElement> GetSortedElements(ControllerManager manager)
        {
            // Get all active UI elements
            var elements = new List<UiElement>();
            foreach (var e in manager.CurrentUiState.SelectableUiElements)
            {
                if (e != null && ((Behaviour)e).isActiveAndEnabled)
                {
                    elements.Add(e);
                }
            }

            // Group elements by vertical position using threshold
            var groupedElements = elements.GroupBy(e => {
                float yPos = ((Component)e).transform.position.y;
                return Mathf.Round(yPos / VERTICAL_THRESHOLD) * VERTICAL_THRESHOLD;
            });

            // Sort groups by y-position (descending) and elements left-to-right within groups
            var sortedElements = groupedElements
                .OrderByDescending(g => g.Key)
                .SelectMany(group => group.OrderBy(e => ((Component)e).transform.position.x))
                .ToList();

            return sortedElements;
        }

        // Gets the element's position index in the current UI for announcements
        private string GetElementIndex(ControllerManager manager, UiElement element)
        {
            var elements = GetSortedElements(manager);
            int index = elements.IndexOf(element);
            return index >= 0 ? $" {index + 1} of {elements.Count}" : "";
        }

        // Gets the readable text for a button from various possible sources
        private string GetButtonText(PassiveButton button)
        {
            if (button == null)
                return "Unnamed Button";

            // Check TextMeshPro component
            var tmpText = button.GetComponentInChildren<TextMeshPro>();
            if (tmpText != null && !string.IsNullOrEmpty(tmpText.text))
                return tmpText.text;

            // Check TextMeshProUGUI component
            var tmpProText = button.GetComponentInChildren<TextMeshProUGUI>();
            if (tmpProText != null && !string.IsNullOrEmpty(tmpProText.text))
                return tmpProText.text;

            // Fall back to poolable behavior name
            var poolable = button.GetComponent<PoolableBehavior>();
            if (poolable != null)
                return poolable.name;

            return "Unnamed Button";
        }

        // Main update handler for UI accessibility features
        public void HandleUpdate(ControllerManager manager)
        {
            HandleKeyboardNavigation(manager);
            HandleScreenReader(manager);
        }

        // Handles keyboard navigation input and button activation
        private void HandleKeyboardNavigation(ControllerManager manager)
        {
            if (!manager.IsUiControllerActive || isProcessingInput)
                return;

            try
            {
                isProcessingInput = true;
                var sortedElements = GetSortedElements(manager);
                var currentElement = manager.CurrentUiState.CurrentSelection;
                int currentIndex = sortedElements.IndexOf(currentElement);

                // Handle next element selection
                if (Input.GetKeyDown(nextButtonKey.Value))
                {
                    if (currentIndex < sortedElements.Count - 1)
                    {
                        manager.HighlightSelection(sortedElements[currentIndex + 1]);
                    }
                }
                // Handle previous element selection
                else if (Input.GetKeyDown(previousButtonKey.Value))
                {
                    if (currentIndex > 0)
                    {
                        manager.HighlightSelection(sortedElements[currentIndex - 1]);
                    }
                }
                // Handle button activation
                else if (Input.GetKeyDown(activateButtonKey.Value))
                {
                    var selectedButton = manager.CurrentUiState.CurrentSelection as PassiveButton;
                    if (selectedButton != null)
                    {
                        selectedButton.ReceiveClickDown();
                        selectedButton.ReceiveClickUp();
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError($"Error in keyboard navigation: {e}");
            }
            finally
            {
                isProcessingInput = false;
            }
        }

        // Handles screen reader announcements for UI changes and focus updates
        private void HandleScreenReader(ControllerManager manager)
        {
            var newElements = GetSortedElements(manager);

            // Check if UI elements have changed
            bool elementsChanged = HasElementsChanged(newElements);

            if (elementsChanged)
            {
                HandleElementsChanged(manager, newElements);
            }

            // Handle focus changes
            HandleFocusChange(manager);
        }

        // Checks if the UI elements have changed by comparing instance IDs
        private bool HasElementsChanged(List<UiElement> newElements)
        {
            if (currentElements.Count != newElements.Count)
                return true;

            for (int i = 0; i < currentElements.Count; i++)
            {
                var currentElement = currentElements[i] as MonoBehaviour;
                var newElement = newElements[i] as MonoBehaviour;

                if (currentElement == null || newElement == null ||
                    currentElement.gameObject.GetInstanceID() != newElement.gameObject.GetInstanceID())
                {
                    return true;
                }
            }

            return false;
        }

        // Handles UI element changes including logging and announcements
        private void HandleElementsChanged(ControllerManager manager, List<UiElement> newElements)
        {
            logger.LogInfo("Menu elements changed:");
            foreach (var e in newElements)
            {
                logger.LogInfo($"  - {((UnityEngine.Object)e).name}");
            }

            currentElements = newElements;
            SpeechSynthesizer.SpeakText($"{currentElements.Count} items available.");

            // Auto-focus first element if available
            if (currentElements.Count > 0)
            {
                var firstElement = currentElements[0];
                manager.HighlightSelection(firstElement);
                logger.LogInfo($"Auto-focused on first element: {((UnityEngine.Object)firstElement).name}");

                AnnounceElement(firstElement, manager);
                lastFocusedButton = firstElement as PassiveButton;
            }
        }

        // Handles focus changes between UI elements
        private void HandleFocusChange(ControllerManager manager)
        {
            var currentButton = manager.CurrentUiState?.CurrentSelection?.GetComponent<PassiveButton>();

            if (currentButton != null && currentButton != lastFocusedButton)
            {
                AnnounceElement(manager.CurrentUiState.CurrentSelection, manager);
                lastFocusedButton = currentButton;
            }
        }

        // Announces the current UI element with its text and position
        private void AnnounceElement(UiElement element, ControllerManager manager)
        {
            string buttonText = GetButtonText(element as PassiveButton);
            string indexInfo = GetElementIndex(manager, element);
            SpeechSynthesizer.SpeakText($"{buttonText}{indexInfo}");
        }
    }
}