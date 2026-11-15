using System.Collections.Generic;
using System.IO;
using System.Linq;
using DynamicMaps.Data;
using DynamicMaps.UI.Components;
using DynamicMaps.Utils;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;
using MapMarker = DynamicMaps.UI.Components.MapMarker;

namespace DynamicMaps.DynamicMarkers
{
    public class LockedDoorMarkerMutator : IDynamicMarkerProvider
    {
        // FIXME: move to configuration somehow
        private static readonly string DoorWithKeyPath = Path.Combine(Plugin.Path, "Markers/door_with_key.png");
        private static readonly string DoorWithLockPath = Path.Combine(Plugin.Path, "Markers/door_with_lock.png");

        // Static map markers were generated from door world positions, but allow some slack in
        // case the marker is slightly offset from the actual door.
        private const float MaxDoorMarkerDistance = 20f;

        // Maps a map marker to the actual door it represents, so we can read the real lock state
        // instead of only checking whether the player has the key.
        private readonly Dictionary<MapMarker, WorldInteractiveObject> _markerDoors = new();

        public void OnShowInRaid(MapView map)
        {
            RebuildDoorMapping(map);

            foreach (var marker in map.GetMapMarkersByCategory("LockedDoor"))
            {
                if (string.IsNullOrWhiteSpace(marker.AssociatedItemId))
                {
                    continue;
                }

                _markerDoors.TryGetValue(marker, out var door);
                UpdateMarkerInRaid(marker, door);
            }
        }

        public void OnShowOutOfRaid(MapView map)
        {
            UnsubscribeFromDoors();

            var profile = GameUtils.GetPlayerProfile();
            var markers = map.GetMapMarkersByCategory("LockedDoor");

            foreach (var marker in markers)
            {
                if (string.IsNullOrWhiteSpace(marker.AssociatedItemId))
                {
                    continue;
                }

                // TODO: GetAllItems is a BSG extension method under GClass
                var keyInStash = profile.Inventory.Stash.GetAllItems().Any(i => i.TemplateId == marker.AssociatedItemId);
                var keyInEquipment = profile.Inventory.Equipment.GetAllItems().Any(i => i.TemplateId == marker.AssociatedItemId);

                // change icon
                marker.Image.sprite = (keyInStash || keyInEquipment)
                    ? TextureUtils.GetOrLoadCachedSprite(DoorWithKeyPath)
                    : TextureUtils.GetOrLoadCachedSprite(DoorWithLockPath);

                // change color
                marker.Color = keyInEquipment
                    ? Color.green
                    : keyInStash
                        ? Color.yellow
                        : Color.red;
            }
        }

        public void OnMapChanged(MapView map, MapDef mapDef)
        {
            if (GameUtils.IsInRaid())
            {
                OnShowInRaid(map);
            }
            else
            {
                OnShowOutOfRaid(map);
            }
        }

        public void OnDisable(MapView map)
        {
            UnsubscribeFromDoors();

            var markers = map.GetMapMarkersByCategory("LockedDoor");

            // replace all markers with yellow and with lock
            foreach (var marker in markers)
            {
                if (string.IsNullOrWhiteSpace(marker.AssociatedItemId))
                {
                    continue;
                }

                marker.Image.sprite = TextureUtils.GetOrLoadCachedSprite(DoorWithLockPath);
                marker.Color = Color.yellow;
            }
        }

        public void OnRaidEnd(MapView map)
        {
            UnsubscribeFromDoors();
        }

        public void OnHideInRaid(MapView map)
        {
            // Stop listening while the map is hidden; OnShowInRaid will refresh and re-subscribe.
            UnsubscribeFromDoors();
        }

        public void OnHideOutOfRaid(MapView map)
        {
            UnsubscribeFromDoors();
        }

        private void RebuildDoorMapping(MapView map)
        {
            UnsubscribeFromDoors();

            var markers = map.GetMapMarkersByCategory("LockedDoor").ToList();
            if (markers.Count == 0)
            {
                return;
            }

            var doors = LocationScene.GetAllObjects<WorldInteractiveObject>(true)
                .Where(door => door != null
                    && door.Operatable
                    && !string.IsNullOrEmpty(door.KeyId))
                .ToList();

            if (doors.Count == 0)
            {
                return;
            }

            var doorsByKeyId = doors
                .GroupBy(door => door.KeyId)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var marker in markers)
            {
                if (string.IsNullOrWhiteSpace(marker.AssociatedItemId)
                    || !doorsByKeyId.TryGetValue(marker.AssociatedItemId, out var candidateDoors))
                {
                    continue;
                }

                // If there's only one door for this key, there's nothing to disambiguate.
                if (candidateDoors.Count == 1)
                {
                    _markerDoors[marker] = candidateDoors[0];
                    continue;
                }

                WorldInteractiveObject closestDoor = null;
                var closestDistance = MaxDoorMarkerDistance;

                foreach (var door in candidateDoors)
                {
                    var distance = Vector3.Distance(marker.Position, MathUtils.ConvertToMapPosition(door.transform));
                    if (distance > closestDistance)
                    {
                        continue;
                    }

                    closestDistance = distance;
                    closestDoor = door;
                }

                if (closestDoor is not null)
                {
                    _markerDoors[marker] = closestDoor;
                }
            }

            foreach (var door in _markerDoors.Values.Distinct())
            {
                door.OnDoorStateChanged += HandleDoorStateChanged;
            }
        }

        private void UnsubscribeFromDoors()
        {
            foreach (var door in _markerDoors.Values.Distinct().ToList())
            {
                if (door != null)
                {
                    door.OnDoorStateChanged -= HandleDoorStateChanged;
                }
            }

            _markerDoors.Clear();
        }

        private void HandleDoorStateChanged(WorldInteractiveObject door, EDoorState previousState, EDoorState nextState)
        {
            // Marker status only depends on whether the door is locked, so ignore plain
            // open/close/interact transitions.
            if ((previousState & EDoorState.Locked) == (nextState & EDoorState.Locked))
            {
                return;
            }

            foreach (var pair in _markerDoors)
            {
                if (pair.Value == door && pair.Key != null)
                {
                    UpdateMarkerInRaid(pair.Key, door);
                }
            }
        }

        private void UpdateMarkerInRaid(MapMarker marker, WorldInteractiveObject door)
        {
            var player = GameUtils.GetMainPlayer();
            if (player is null)
            {
                return;
            }

            // TODO: GetAllItems is a BSG extension method under GClass
            var hasKey = player.Inventory.Equipment.GetAllItems().Any(i => i.TemplateId == marker.AssociatedItemId);

            // A door stops being locked when anybody unlocks it (key, lockpick, bot, etc.),
            // not just when the local player opens it.
            var isUnlocked = door != null && (door.DoorState & EDoorState.Locked) == 0;
            var canOpen = isUnlocked || hasKey;

            marker.Image.sprite = TextureUtils.GetOrLoadCachedSprite(canOpen ? DoorWithKeyPath : DoorWithLockPath);

            marker.Color = canOpen
                ? Color.green
                : Color.red;
        }
    }
}
