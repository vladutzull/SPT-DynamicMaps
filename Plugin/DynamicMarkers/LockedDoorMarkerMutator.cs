using System;
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

namespace DynamicMaps.DynamicMarkers
{
    using MapMarker=UI.Components.MapMarker;

    public class LockedDoorMarkerMutator : IDynamicMarkerProvider
    {
        private static readonly string DoorWithKeyPath = Path.Combine(Plugin.Path, "Markers/door_with_key.png");
        private static readonly string DoorWithLockPath = Path.Combine(Plugin.Path, "Markers/door_with_lock.png");

        // Static map markers were generated from door world positions, but allow some slack in
        // case the marker is slightly offset from the actual door.
        private const float MaxDoorMarkerDistance = 20f;
        
        private readonly Dictionary<string, List<MapMarker>> _doorMarkersByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _keyCounts = new(StringComparer.Ordinal);
        private readonly HashSet<Item> _trackedKeys = [];

        // Maps a map marker to the actual door it represents, so we can read the real lock state
        // instead of only checking whether the player has the key.
        private readonly Dictionary<MapMarker, WorldInteractiveObject> _markerDoors = [];

        private InventoryController _inventoryController;
        private InventoryEquipment _equipment;

        public void OnShowInRaid(MapView map)
        {
            StopTracking();

            var player = GameUtils.GetMainPlayer();
            if (player == null)
                return;

            _equipment = player.Inventory.Equipment;
            _inventoryController = player.InventoryController;

            foreach (var marker in map.GetMapMarkersByCategory("LockedDoor"))
            {
                if (string.IsNullOrWhiteSpace(marker.AssociatedItemId))
                    continue;

                if (!_doorMarkersByKey.TryGetValue(marker.AssociatedItemId, out var markers))
                {
                    markers = [];
                    _doorMarkersByKey[marker.AssociatedItemId] = markers;
                }
                
                markers.Add(marker);
            }

            if (_doorMarkersByKey.Count == 0)
                return;

            TrackDoors();

            foreach (var item in _equipment.GetAllItems())
            {
                TrackKey(item);
            }

            foreach (var keyId in _doorMarkersByKey.Keys)
            {
                UpdateDoors(keyId);
            }

            if (_inventoryController == null)
                return;

            _inventoryController.AddItemEvent += OnItemAdded;
            _inventoryController.RemoveItemEvent += OnItemRemoved;
        }

        public void OnShowOutOfRaid(MapView map)
        {
            var profile = GameUtils.GetPlayerProfile();
            var markers = new List<MapMarker>();
            
            foreach (var marker in map.GetMapMarkersByCategory("LockedDoor"))
            {
                if (!string.IsNullOrWhiteSpace(marker.AssociatedItemId))
                {
                    markers.Add(marker);
                }
            }
            
            if (markers.Count == 0)
                return;

            var stashItems = new HashSet<string>(StringComparer.Ordinal);
            var equipmentItems = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in profile.Inventory.Stash.GetAllItems())
            {
                stashItems.Add(item.StringTemplateId);
            }
            
            foreach (var item in profile.Inventory.Equipment.GetAllItems())
            {
                equipmentItems.Add(item.StringTemplateId);
            }

            foreach (var marker in markers)
            {
                var keyInStash = stashItems.Contains(marker.AssociatedItemId);
                var keyInEquipment = equipmentItems.Contains(marker.AssociatedItemId);

                // change icon
                marker.SetImagePath(keyInStash || keyInEquipment ? DoorWithKeyPath : DoorWithLockPath);

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
            StopTracking();
            var markers = map.GetMapMarkersByCategory("LockedDoor");

            // replace all markers with yellow and with lock
            foreach (var marker in markers)
            {
                if (string.IsNullOrWhiteSpace(marker.AssociatedItemId))
                {
                    continue;
                }

                marker.SetImagePath(DoorWithLockPath);
                marker.Color = Color.yellow;
            }
        }

        public void OnRaidEnd(MapView map)
        {
            StopTracking();
        }

        public void OnHideInRaid(MapView map)
        {
            StopTracking();
        }

        public void OnHideOutOfRaid(MapView map)
        {
            // do nothing
        }

        private void OnItemAdded(AddItemEventArgs args)
        {
            if (args.Status != CommandStatus.Succeed || !args.To.IsChildOf(_equipment))
                return;

            UpdateMovedItems(args.Item, true);
        }

        private void OnItemRemoved(RemoveItemEventArgs args)
        {
            if (args.Status != CommandStatus.Succeed || !args.From.IsChildOf(_equipment))
                return;

            UpdateMovedItems(args.Item, false);
        }

        private void UpdateMovedItems(Item item, bool added)
        {
            if (item is ContainerCollection)
            {
                foreach (var child in item.GetAllItems())
                {
                    UpdateKey(child, added);
                }
            }
            else
            {
                UpdateKey(item, added);
            }
        }

        private void UpdateKey(Item item, bool added)
        {
            if (item == null || !_doorMarkersByKey.ContainsKey(item.StringTemplateId))
                return;

            if (added)
            {
                if (TrackKey(item) && _keyCounts[item.StringTemplateId] == 1)
                    UpdateDoors(item.StringTemplateId);
            }
            else if (_trackedKeys.Remove(item))
            {
                var count = --_keyCounts[item.StringTemplateId];
                if (count == 0)
                    UpdateDoors(item.StringTemplateId);
            }
        }

        private bool TrackKey(Item item)
        {
            if (item == null || !_doorMarkersByKey.ContainsKey(item.StringTemplateId) || !_trackedKeys.Add(item))
                return false;

            _keyCounts.TryGetValue(item.StringTemplateId, out var count);
            _keyCounts[item.StringTemplateId] = count + 1;
            return true;
        }

        private void TrackDoors()
        {
            _markerDoors.Clear();

            if (_doorMarkersByKey.Count == 0)
                return;

            var doorsByKeyId = LocationScene.GetAllObjects<WorldInteractiveObject>(true)
                .Where(door => door != null
                    && door.Operatable
                    && !string.IsNullOrEmpty(door.KeyId))
                .GroupBy(door => door.KeyId)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

            foreach (var pair in _doorMarkersByKey)
            {
                if (!doorsByKeyId.TryGetValue(pair.Key, out var candidateDoors))
                    continue;

                foreach (var marker in pair.Value)
                {
                    // If there's only one door for this key, there's nothing to disambiguate
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
                            continue;

                        closestDistance = distance;
                        closestDoor = door;
                    }

                    if (closestDoor != null)
                    {
                        _markerDoors[marker] = closestDoor;
                    }
                }
            }

            foreach (var door in _markerDoors.Values.Distinct())
            {
                door.OnDoorStateChanged += OnDoorStateChanged;
            }
        }

        private void OnDoorStateChanged(WorldInteractiveObject door, EDoorState previousState, EDoorState nextState)
        {
            // Marker status only depends on whether the door is locked, so ignore plain
            // open/close/interact transitions.
            if ((previousState & EDoorState.Locked) == (nextState & EDoorState.Locked))
                return;

            if (!string.IsNullOrEmpty(door.KeyId) && _doorMarkersByKey.ContainsKey(door.KeyId))
            {
                UpdateDoors(door.KeyId);
            }
        }

        private void UpdateDoors(string keyId)
        {
            var hasKey = _keyCounts.TryGetValue(keyId, out var count) && count > 0;
            foreach (var marker in _doorMarkersByKey[keyId])
            {
                // A door stops being locked when anybody unlocks it (key, lockpick, bot, etc.),
                // not just when the local player opens it.
                var isUnlocked = _markerDoors.TryGetValue(marker, out var door)
                                 && door != null
                                 && (door.DoorState & EDoorState.Locked) == 0;

                var canOpen = hasKey || isUnlocked;

                marker.SetImagePath(canOpen ? DoorWithKeyPath : DoorWithLockPath);
                marker.Color = canOpen ? Color.green : Color.red;
            }
        }

        private void StopTracking()
        {
            if (_inventoryController != null)
            {
                _inventoryController.AddItemEvent -= OnItemAdded;
                _inventoryController.RemoveItemEvent -= OnItemRemoved;
                _inventoryController = null;
            }

            foreach (var door in _markerDoors.Values.Distinct())
            {
                if (door != null)
                {
                    door.OnDoorStateChanged -= OnDoorStateChanged;
                }
            }

            _markerDoors.Clear();

            _equipment = null;
            _doorMarkersByKey.Clear();
            _keyCounts.Clear();
            _trackedKeys.Clear();
        }
    }
}
