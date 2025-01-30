using UnityEngine;
using System.Collections.Generic;
using BepInEx.Logging;
using BepInEx.Configuration;
using SusAccess.Speech;

namespace SusAccess.Navigation;

// Handles in-game navigation announcements including room, task, and position announcements
public class NavigationHandler {
    // Detection ranges and thresholds
    private const float VISIBILITY_BUFFER = 0.3f; // Buffer for raycast checks

    private readonly ManualLogSource logger;
    private readonly ConfigEntry<KeyCode> scanSurroundingsKey;
    private readonly ConfigEntry<KeyCode> taskDetectionKey;

    // State tracking
    private Dictionary<int, float> lastAnnouncedDistances = new Dictionary<int, float>();
    private HashSet<int> announcedTaskIds = new HashSet<int>();
    private string currentRoom = "";
    private Vector3? lastRoomEntrancePos = null;
    private bool isInitialRoomEntry = true;
    private bool wasKeyHeld = false;

    public NavigationHandler(
        ManualLogSource log,
        ConfigEntry<KeyCode> scanKey,
        ConfigEntry<KeyCode> taskKey) {
        logger = log;
        scanSurroundingsKey = scanKey;
        taskDetectionKey = taskKey;
    }

    public void HandleUpdate(PlayerControl player) {
        if (!player.AmOwner) return;

        try {
            CheckRoomChange(player);
            HandleKeyInputs(player);
        }
        catch (System.Exception e) {
            logger.LogError($"Error in navigation update: {e}");
        }
    }

    // Handles all key inputs with GetKey
    private void HandleKeyInputs(PlayerControl player) {
        bool keyPressed = false;

        // Scan surroundings
        if (Input.GetKey(scanSurroundingsKey.Value)) {
            if (!wasKeyHeld) {
                ScanSurroundings(player);
                keyPressed = true;
            }
        }

        // Find nearest task
        if (Input.GetKey(taskDetectionKey.Value)) {
            if (!wasKeyHeld) {
                DetectClosestTask(player);
                keyPressed = true;
            }
        }

        wasKeyHeld = keyPressed;
    }

    // Helper class for task information
    private class TaskInfo {
        public Console console;
        public float distance;
        public float usableDistance;
        public bool isVisible;
    }

    // Gets all tasks in the current room (line of sight is stored but not currently used)
    private List<TaskInfo> GetTasksInCurrentRoom(PlayerControl player) {
        List<TaskInfo> tasks = new List<TaskInfo>();

        if (player == null || !player.AmOwner) return tasks;

        // If we're in a hallway, skip tasks entirely
        if (currentRoom.Equals("hallway", System.StringComparison.OrdinalIgnoreCase)) {
            return tasks;
        }

        Vector3 playerPos = player.GetTruePosition();

        // Grab all consoles and check if each console's Room property matches our currentRoom
        var consoles = Object.FindObjectsOfType<Console>();
        foreach (var console in consoles) {
            if (console == null) continue;

            // Convert the console's SystemTypes Room to a trimmed string
            string consoleRoomName = console.Room.ToString().Replace("System", "").Trim();

            // Also skip if the console's own room is hallway
            if (consoleRoomName.Equals("hallway", System.StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            // If console's room isn't the same as our current room, skip
            if (!consoleRoomName.Equals(currentRoom, System.StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            float distance = Vector2.Distance(playerPos, console.transform.position);

            // We keep track of visibility but do not filter based on it
            bool isVisible = IsTaskVisible(playerPos, console.transform.position);

            // Check if there's an associated player task that is incomplete
            var playerTask = console.FindTask(player);
            if (playerTask != null && !playerTask.IsComplete) {
                float usableDistance = console.UsableDistance;

                // If onlyFromBelow is true, skip if the player is above it
                if (console.onlyFromBelow && playerPos.y > console.transform.position.y) {
                    continue;
                }

                // Check if this console is valid for that task
                if (playerTask.ValidConsole(console)) {
                    tasks.Add(new TaskInfo {
                        console = console,
                        distance = distance,
                        usableDistance = usableDistance,
                        isVisible = isVisible
                    });
                }
            }
        }

        return tasks;
    }

    // Monitors player position and announces room changes
    private void CheckRoomChange(PlayerControl player) {
        if (ShipStatus.Instance == null) return;

        Vector3 playerPos = player.GetTruePosition();
        string newRoom = "hallway";
        string entranceDirection = "";

        // Attempt to find the new room via room areas
        foreach (var room in ShipStatus.Instance.FastRooms.Values) {
            if (room?.roomArea != null && room.roomArea.OverlapPoint(playerPos)) {
                newRoom = room.RoomId.ToString().Replace("System", "").Trim();

                if (newRoom != currentRoom && lastRoomEntrancePos.HasValue) {
                    Vector3 movement = lastRoomEntrancePos.Value - playerPos;
                    entranceDirection = GetCardinalEntryDirection(movement);
                }
                break;
            }
        }

        if (newRoom != currentRoom) {
            currentRoom = newRoom;
            lastRoomEntrancePos = playerPos;
            isInitialRoomEntry = true;
            AnnounceRoomAndTasks(player, entranceDirection, newRoom);
        }
    }

    // Announces current room and any visible tasks (skips task announcements if hallway)
    private void AnnounceRoomAndTasks(PlayerControl player, string entranceDirection, string roomName) {
        Vector3 playerPos = player.GetTruePosition();
        List<string> announcements = new List<string>();

        // Get current room and position
        PlainShipRoom room = FindPlayerRoom(playerPos);
        string roomPosition = GetRoomPosition(playerPos, room);

        // Initial room entry announcement
        if (isInitialRoomEntry) {
            if (!string.IsNullOrEmpty(entranceDirection)) {
                announcements.Add($"Entered {roomName} from the {entranceDirection}");
            }
            else {
                announcements.Add($"Entered {roomName}");
            }
            isInitialRoomEntry = false;
        }
        // Position update announcement
        else {
            if (!string.IsNullOrEmpty(roomPosition)) {
                announcements.Add($"In the {roomPosition} {roomName}");
            }
            else {
                announcements.Add($"In {roomName}");
            }
        }

        // Only announce tasks if it's not the hallway
        if (!roomName.Equals("hallway", System.StringComparison.OrdinalIgnoreCase)) {
            var tasks = GetTasksInCurrentRoom(player);
            if (tasks.Count == 0) {
                announcements.Add($"No tasks available in {roomName}");
            }
            else {
                foreach (var task in tasks) {
                    string taskName = GetTaskName(task.console);
                    string direction = GetRelativeDirection(
                        playerPos,
                        task.console.transform.position,
                        task.distance,
                        task.usableDistance
                    );
                    announcements.Add($"{taskName} {direction}");
                }
            }
        }

        SpeechSynthesizer.SpeakText(string.Join(". ", announcements));
    }

    // Finds the room containing the given position
    private PlainShipRoom FindPlayerRoom(Vector3 playerPos) {
        if (ShipStatus.Instance == null) return null;

        foreach (var room in ShipStatus.Instance.FastRooms.Values) {
            if (room?.roomArea != null && room.roomArea.OverlapPoint(playerPos)) {
                return room;
            }
        }
        return null;
    }

    // Gets the relative position within a room (e.g., "top left")
    private string GetRoomPosition(Vector3 playerPos, PlainShipRoom room) {
        if (room?.roomArea == null) return "";

        var bounds = room.roomArea.bounds;
        float xPercent = (playerPos.x - bounds.min.x) / bounds.size.x;
        float yPercent = (playerPos.y - bounds.min.y) / bounds.size.y;

        string horizontal = xPercent < 0.33f ? "left" : (xPercent > 0.66f ? "right" : "middle");
        string vertical = yPercent < 0.33f ? "bottom" : (yPercent > 0.66f ? "top" : "middle");

        if (vertical == "middle" && horizontal == "middle") {
            return "center of the";
        }

        return $"{vertical} {horizontal} of the";
    }

    // Checks if a task is visible from the player's position
    private bool IsTaskVisible(Vector3 playerPos, Vector3 taskPos) {
        Vector2 direction = (Vector2)(taskPos - playerPos);
        float distance = direction.magnitude;
        Vector2 playerPos2D = (Vector2)playerPos;

        Vector2[] checkPoints = new Vector2[] {
            (Vector2)taskPos,
            (Vector2)taskPos + new Vector2(VISIBILITY_BUFFER, 0),
            (Vector2)taskPos + new Vector2(-VISIBILITY_BUFFER, 0),
            (Vector2)taskPos + new Vector2(0, VISIBILITY_BUFFER),
            (Vector2)taskPos + new Vector2(0, -VISIBILITY_BUFFER)
        };

        // We'll store but not filter tasks by this
        foreach (var point in checkPoints) {
            RaycastHit2D hit = Physics2D.Raycast(
                playerPos2D,
                point - playerPos2D,
                distance,
                Constants.ShipAndObjectsMask
            );
            if (!hit) {
                return true;
            }

            if (hit && Vector2.Distance(hit.point, point) < VISIBILITY_BUFFER) {
                return true;
            }
        }

        return false;
    }

    // Gets a human-readable name for a task console
    private string GetTaskName(Console console) {
        try {
            // Quick check for Vent cleaning
            if (console.name.Contains("Vent")) {
                return "Vent Cleaning";
            }

            // If there's a defined TaskType
            if (console.TaskTypes.Length > 0) {
                var taskType = console.TaskTypes[0];
                string taskName = taskType.ToString();
                if (!string.IsNullOrEmpty(taskName) && !taskName.Equals("None")) {
                    return SanitizeTaskName(taskName);
                }
            }

            // Otherwise fall back on console.name
            if (!string.IsNullOrEmpty(console.name)) {
                return SanitizeTaskName(console.name);
            }

            return "task";
        }
        catch (System.Exception e) {
            logger.LogError($"Error getting task name: {e}");
            return "task";
        }
    }

    // Cleans up task names for better announcement
    private string SanitizeTaskName(string taskName) {
        taskName = taskName.Replace("Task", "")
                           .Replace("Mini", "")
                           .Replace("System", "")
                           .Replace("(Clone)", "");

        // Insert spaces before capital letters to improve TTS
        for (int i = taskName.Length - 2; i >= 0; i--) {
            if (char.IsUpper(taskName[i + 1]) && !char.IsWhiteSpace(taskName[i])) {
                taskName = taskName.Insert(i + 1, " ");
            }
        }

        return taskName.Trim();
    }

    // Gets cardinal direction between two points
    private string GetCardinalDirection(Vector3 difference) {
        float angle = Mathf.Atan2(difference.y, difference.x) * Mathf.Rad2Deg;
        angle = (angle + 360) % 360;

        if (angle >= 337.5f || angle < 22.5f) return "right";
        if (angle >= 22.5f && angle < 67.5f) return "up and right";
        if (angle >= 67.5f && angle < 112.5f) return "up";
        if (angle >= 112.5f && angle < 157.5f) return "up and left";
        if (angle >= 157.5f && angle < 202.5f) return "left";
        if (angle >= 202.5f && angle < 247.5f) return "down and left";
        if (angle >= 247.5f && angle < 292.5f) return "down";
        return "down and right";
    }

    // Gets cardinal direction for room entry announcements
    private string GetCardinalEntryDirection(Vector3 movement) {
        float angle = Mathf.Atan2(-movement.y, movement.x) * Mathf.Rad2Deg;
        angle = (angle + 360) % 360;

        if (angle >= 315 || angle < 45) return "east";
        if (angle >= 45 && angle < 135) return "south";
        if (angle >= 135 && angle < 225) return "west";
        return "north";
    }

    // Gets a relative direction and distance description between two points
    private string GetRelativeDirection(Vector3 from, Vector3 to, float distance, float usableDistance) {
        if (distance <= usableDistance) {
            return "within reach";
        }

        Vector3 difference = to - from;
        string direction = GetCardinalDirection(difference);
        return $"{direction}, {Mathf.Round(distance * 10) / 10} meters";
    }

    // Finds and announces the nearest task in the current room if available
    private void DetectClosestTask(PlayerControl player) {
        if (ShipStatus.Instance == null) return;

        // If hallway, don't announce tasks at all
        if (currentRoom.Equals("hallway", System.StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        Vector3 playerPos = player.GetTruePosition();
        TaskInfo closestTask = null;
        float closestDistance = float.MaxValue;

        var tasks = GetTasksInCurrentRoom(player);
        foreach (var task in tasks) {
            if (task.distance < closestDistance) {
                closestDistance = task.distance;
                closestTask = task;
            }
        }

        // If no tasks found
        if (closestTask == null) {
            SpeechSynthesizer.SpeakText($"No tasks available in {currentRoom}");
            return;
        }

        // If we found one
        string taskName = GetTaskName(closestTask.console);
        string direction = GetRelativeDirection(playerPos, closestTask.console.transform.position,
                                                closestTask.distance, closestTask.usableDistance);

        if (closestTask.distance <= closestTask.usableDistance) {
            SpeechSynthesizer.SpeakText($"At {taskName}");
        }
        else {
            SpeechSynthesizer.SpeakText($"{taskName} {direction}");
        }
    }

    // Announces current position and surroundings
    private void ScanSurroundings(PlayerControl player) {
        isInitialRoomEntry = false;
        AnnounceRoomAndTasks(player, "", currentRoom);
    }
}
