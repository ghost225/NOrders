using UnityEngine;

namespace NOrders
{
    // Which mod owns a unit, so that two mods compiled with NOrders can run at
    // once. Each DLL carries its own copy of every shared class and patch, and
    // two copies cannot share statics -- so the claim lives in the scene, as a
    // marker child named for the owning mod. Every shared patch that changes
    // behaviour acts only on units its own mod owns.
    public static class Ownership
    {
        private const string Prefix = "__NOrders.Owner:";

        // True if this mod now owns the unit (or already did); false if
        // another mod does.
        public static bool Claim(Unit unit)
        {
            if (unit == null) return false;
            string owner = OwnerOf(unit);
            if (owner != null) return owner == Host.ModId;
            var marker = new GameObject(Prefix + Host.ModId);
            marker.transform.SetParent(unit.transform, false);
            marker.SetActive(false);                        // no cost, nothing to render
            return true;
        }

        // Gives up this mod's claim; another mod's is left alone.
        public static void Release(Unit unit)
        {
            Transform marker = Marker(unit);
            if (marker == null || marker.name != Prefix + Host.ModId) return;
            marker.name = "__NOrders.Released";                // gone now, not at the end of the frame
            Object.Destroy(marker.gameObject);
        }

        // The ModId of the mod that owns the unit, or null.
        public static string OwnerOf(Unit unit)
        {
            Transform marker = Marker(unit);
            return marker != null ? marker.name.Substring(Prefix.Length) : null;
        }

        public static bool Mine(Unit unit) => unit != null && OwnerOf(unit) == Host.ModId;

        // Owned by some other mod: hands off.
        public static bool Theirs(Unit unit)
        {
            string owner = OwnerOf(unit);
            return owner != null && owner != Host.ModId;
        }

        private static Transform Marker(Unit unit)
        {
            if (unit == null) return null;
            Transform root = unit.transform;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                // A marker destroyed this frame still exists until the frame
                // ends; skip one already on its way out.
                if (child != null && child.name.StartsWith(Prefix)) return child;
            }
            return null;
        }
    }
}
