using UnityEngine;
using System.Collections.Generic;
using BepInEx.Logging;
using BepInEx.Configuration;
using SusAccess.Speech;

namespace SusAccess.Navigation
{
    // Handles in-game navigation assistance including room, task, and position announcements
    public class NavigationHandler
    {
        // Detection ranges and thresholds
        private const float TASK_DETECTION_RADIUS = 5f;  // Maximum distance to detect tasks
        private const float INTERACTION_DISTANCE = 1f;   // Distance at which player can interact with tasks
        private const float VISIBILITY_BUFFER = 0.3f;    // Buffer for raycast checks

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
            ConfigEntry<KeyCode> taskKey)
        {
            logger = log;
            scanSurroundingsKey = scanKey;
            taskDetectionKey = taskKey;
        }

        public void HandleUpdate(PlayerControl player)
        {
            if (!player.AmOwner) return;

            try
            {
                CheckRoomChange(player);
                HandleKeyInputs(player);
            }
            catch (System.Exception e)
            {
                logger.LogError($"Error in navigation update: {e}");
            }
        }

        // Handles all key inputs with GetKey
        private void HandleKeyInputs(PlayerControl player)
        {
            bool keyPressed = false;

            // Scan surroundings
            if (Input.GetKey(scanSurroundingsKey.Value))
            {
                if (!wasKeyHeld)
                {
                    ScanSurroundings(player);
                    keyPressed = true;
                }
            }

            // Find nearest task
            if (Input.GetKey(taskDetectionKey.Value))
            {
                if (!wasKeyHeld)
                {
                    DetectClosestTask(player);
                    keyPressed = true;
                }
            }

            wasKeyHeld = keyPressed;
        }

        // Determines cardinal direction based on movement vector
        private string GetCardinalEntryDirection(Vector3 movement)
        {
            float angle = Mathf.Atan2(-movement.y, movement.x) * Mathf.Rad2Deg;
            angle = (angle + 360) % 360;

            if (angle >= 315 || angle < 45) return "east";
            if (angle >= 45 && angle < 135) return "south";
            if (angle >= 135 && angle < 225) return "west";
            return "north";
        }

        // Monitors player position and announces room changes
        private void CheckRoomChange(PlayerControl player)
        {
            if (ShipStatus.Instance == null) return;

            Vector3 playerPos = player.GetTruePosition();
            string newRoom = "hallway";
            string entranceDirection = "";

            PlainShipRoom newRoomObj = null;
            foreach (var room in ShipStatus.Instance.FastRooms.Values)
            {
                if (room?.roomArea != null && room.roomArea.OverlapPoint(playerPos))
                {
                    newRoom = room.RoomId.ToString().Replace("System", "").Trim();
                    newRoomObj = room;

                    if (newRoom != currentRoom && lastRoomEntrancePos.HasValue)
                    {
                        Vector3 movement = lastRoomEntrancePos.Value - playerPos;
                        entranceDirection = GetCardinalEntryDirection(movement);
                    }
                    break;
                }
            }

            if (newRoom != currentRoom)
            {
                currentRoom = newRoom;
                lastRoomEntrancePos = playerPos;
                isInitialRoomEntry = true;
                AnnounceRoomAndTasks(player, entranceDirection, newRoom);
            }
        }

        // Announces current room and any visible tasks
        private void AnnounceRoomAndTasks(PlayerControl player, string entranceDirection, string roomName)
        {
            Vector3 playerPos = player.GetTruePosition();
            List<string> announcements = new List<string>();

            // Get current room and position
            PlainShipRoom room = FindPlayerRoom(playerPos);
            string roomPosition = GetRoomPosition(playerPos, room);

            // Initial room entry announcement
            if (isInitialRoomEntry)
            {
                if (!string.IsNullOrEmpty(entranceDirection))
                {
                    announcements.Add($"Entered {roomName} from the {entranceDirection}");
                }
                else
                {
                    announcements.Add($"Entered {roomName}");
                }
                isInitialRoomEntry = false;
            }
            // Position update announcement 
            else
            {
                if (!string.IsNullOrEmpty(roomPosition))
                {
                    announcements.Add($"In the {roomPosition} {roomName}");
                }
                else
                {
                    announcements.Add($"In {roomName}");
                }
            }

            // Add task announcements
            var tasks = GetTasksInCurrentRoom(player);
            foreach (var task in tasks)
            {
                string taskName = GetTaskName(task.console);
                string direction = GetRelativeDirection(playerPos, task.console.transform.position, task.distance);
                announcements.Add($"{taskName} {direction}");
            }

            SpeechSynthesizer.SpeakText(string.Join(". ", announcements));
        }

        // Finds the room containing the given position
        private PlainShipRoom FindPlayerRoom(Vector3 playerPos)
        {
            if (ShipStatus.Instance == null) return null;

            foreach (var room in ShipStatus.Instance.FastRooms.Values)
            {
                if (room?.roomArea != null && room.roomArea.OverlapPoint(playerPos))
                {
                    return room;
                }
            }
            return null;
        }

        // Gets the relative position within a room (e.g., "top left")
        private string GetRoomPosition(Vector3 playerPos, PlainShipRoom room)
        {
            if (room?.roomArea == null) return "";

            var bounds = room.roomArea.bounds;
            float xPercent = (playerPos.x - bounds.min.x) / bounds.size.x;
            float yPercent = (playerPos.y - bounds.min.y) / bounds.size.y;

            string horizontal = xPercent < 0.33f ? "left" : (xPercent > 0.66f ? "right" : "middle");
            string vertical = yPercent < 0.33f ? "bottom" : (yPercent > 0.66f ? "top" : "middle");

            if (vertical == "middle" && horizontal == "middle")
                return "center of the";

            return $"{vertical} {horizontal} of the";
        }

        // Helper class for task information
        private class TaskInfo
        {
            public Console console;
            public float distance;
        }

        // Gets all visible tasks in the current room
        private List<TaskInfo> GetTasksInCurrentRoom(PlayerControl player)
        {
            List<TaskInfo> tasks = new List<TaskInfo>();
            Vector3 playerPos = player.GetTruePosition();

            var consoles = Object.FindObjectsOfType<Console>();
            foreach (var console in consoles)
            {
                if (console == null) continue;

                bool inCurrentRoom = false;
                foreach (var room in ShipStatus.Instance.FastRooms.Values)
                {
                    if (room?.roomArea != null && room.roomArea.OverlapPoint(console.transform.position))
                    {
                        string roomName = room.RoomId.ToString().Replace("System", "").Trim();
                        if (roomName == currentRoom)
                        {
                            inCurrentRoom = true;
                            break;
                        }
                    }
                }

                if (inCurrentRoom && IsTaskVisible(playerPos, console.transform.position))
                {
                    float distance = Vector2.Distance(playerPos, console.transform.position);
                    tasks.Add(new TaskInfo { console = console, distance = distance });
                }
            }

            return tasks;
        }

        // Finds and announces the nearest task
        private void DetectClosestTask(PlayerControl player)
        {
            if (ShipStatus.Instance == null) return;

            Vector3 playerPos = player.GetTruePosition();
            Console closestConsole = null;
            float closestDistance = float.MaxValue;

            var consoles = Object.FindObjectsOfType<Console>();
            foreach (var console in consoles)
            {
                if (console == null) continue;

                float distance = Vector2.Distance(playerPos, console.transform.position);
                if (distance <= TASK_DETECTION_RADIUS &&
                    IsTaskVisible(playerPos, console.transform.position))
                {
                    if (distance < closestDistance)
                    {
                        closestDistance = distance;
                        closestConsole = console;
                    }
                }
            }

            if (closestConsole != null)
            {
                string taskName = GetTaskName(closestConsole);
                string direction = GetRelativeDirection(playerPos, closestConsole.transform.position, closestDistance);

                if (closestDistance <= INTERACTION_DISTANCE)
                {
                    SpeechSynthesizer.SpeakText($"At {taskName}");
                }
                else
                {
                    SpeechSynthesizer.SpeakText($"{taskName} {direction}");
                }
            }
        }

        // Announces current position and surroundings
        private void ScanSurroundings(PlayerControl player)
        {
            isInitialRoomEntry = false;
            AnnounceRoomAndTasks(player, "", currentRoom);
        }

        // Checks if a task is visible from the player's position
        private bool IsTaskVisible(Vector3 playerPos, Vector3 taskPos)
        {
            Vector2 direction = (Vector2)(taskPos - playerPos);
            float distance = direction.magnitude;
            Vector2 playerPos2D = (Vector2)playerPos;

            Vector2[] checkPoints = new Vector2[]
            {
                (Vector2)taskPos,
                (Vector2)taskPos + new Vector2(VISIBILITY_BUFFER, 0),
                (Vector2)taskPos + new Vector2(-VISIBILITY_BUFFER, 0),
                (Vector2)taskPos + new Vector2(0, VISIBILITY_BUFFER),
                (Vector2)taskPos + new Vector2(0, -VISIBILITY_BUFFER)
            };

            foreach (var point in checkPoints)
            {
                RaycastHit2D hit = Physics2D.Raycast(playerPos2D, point - playerPos2D, distance, Constants.ShipAndObjectsMask);
                if (!hit)
                {
                    return true;
                }

                if (hit && Vector2.Distance(hit.point, point) < VISIBILITY_BUFFER)
                {
                    return true;
                }
            }

            return false;
        }

        // Gets a human-readable name for a task console
        private string GetTaskName(Console console)
        {
            try
            {
                if (console.name.Contains("Vent"))
                {
                    return "Vent Cleaning";
                }

                if (console.TaskTypes.Length > 0)
                {
                    var taskType = console.TaskTypes[0];
                    {
                        string taskName = taskType.ToString();
                        if (!string.IsNullOrEmpty(taskName) && !taskName.Equals("None"))
                        {
                            return SanitizeTaskName(taskName);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(console.name))
                {
                    return SanitizeTaskName(console.name);
                }

                return "task";
            }
            catch (System.Exception e)
            {
                logger.LogError($"Error getting task name: {e}");
                return "task";
            }
        }

        // Cleans up task names for better announcement
        private string SanitizeTaskName(string taskName)
        {
            taskName = taskName.Replace("Task", "")
                             .Replace("Mini", "")
                             .Replace("System", "")
                             .Replace("(Clone)", "");

            for (int i = taskName.Length - 2; i >= 0; i--)
            {
                if (char.IsUpper(taskName[i + 1]) && !char.IsWhiteSpace(taskName[i]))
                {
                    taskName = taskName.Insert(i + 1, " ");
                }
            }

            return taskName.Trim();
        }

        // Gets cardinal direction between two points
        private string GetCardinalDirection(Vector3 difference)
        {
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

        // Gets a relative direction and distance description between two points
        private string GetRelativeDirection(Vector3 from, Vector3 to, float distance)
        {
            Vector3 difference = to - from;
            string direction = GetCardinalDirection(difference);
            return $"{direction}, {Mathf.Round(distance * 10) / 10} meters";
        }
    }
}