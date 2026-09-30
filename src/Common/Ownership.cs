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

        // Whether this mod should act on the unit: it owns it, or nobody does
        // and this mod is the steward. A patch that fixes the game's own
        // behaviour for everyone (a slower deck approach, say) must run once
        // per unit, not once per mod loaded -- the steward is the one that
        // does it for units no mod has claimed.
        public static bool Acts(Unit unit)
        {
            if (unit == null) return false;
            string owner = OwnerOf(unit);
            return owner == null ? Steward : owner == Host.ModId;
        }

        // The first NOrders mod to ask becomes the steward for the session:
        // a scene-independent marker object names it.
        private const string StewardName = "__NOrders.Steward";
        private static int stewardChecked = -1;
        private static bool steward;

        public static bool Steward
        {
            get
            {
                if (stewardChecked == Time.frameCount) return steward;
                stewardChecked = Time.frameCount;
                GameObject holder = GameObject.Find(StewardName);
                if (holder == null)
                {
                    holder = new GameObject(StewardName);
                    Object.DontDestroyOnLoad(holder);
                    new GameObject(Host.ModId).transform.SetParent(holder.transform, false);
                }
                steward = holder.transform.childCount > 0 && holder.transform.GetChild(0).name == Host.ModId;
                return steward;
            }
        }

        // Owned by some other mod: hands off.
        public static bool Theirs(Unit unit)
        {
            string owner = OwnerOf(unit);
            return owner != null && owner != Host.ModId;
        }

        // ---- Handover at the player's request ------------------------------
        //
        // A mod acting for the human player (Host.PlayerDirected) may ask for
        // a unit another mod owns: a request marker goes on the unit, and the
        // owner, on its next update, clears what it has on it and passes the
        // claim across in the same step -- never released in between, so no
        // one else can take it. Only a player's request is honoured: two AI
        // commanders cannot pull units off each other this way.
        private const string RequestPrefix = "__NOrders.Request:";
        private const string PlayerTag = "|player";

        // True once this mod owns the unit; false while the request waits.
        public static bool RequestHandover(Unit unit)
        {
            if (unit == null) return false;
            if (Mine(unit)) return true;
            if (OwnerOf(unit) == null) return Claim(unit);
            if (RequestMarker(unit) == null)
            {
                var marker = new GameObject(RequestPrefix + Host.ModId + (Host.PlayerDirected ? PlayerTag : ""));
                marker.transform.SetParent(unit.transform, false);
                marker.SetActive(false);
            }
            return false;
        }

        public static void WithdrawRequest(Unit unit)
        {
            Transform marker = RequestMarker(unit);
            if (marker == null || !marker.name.StartsWith(RequestPrefix + Host.ModId)) return;
            marker.name = "__NOrders.Withdrawn";
            Object.Destroy(marker.gameObject);
        }

        // Who is asking for the unit, and whether for the player.
        public static string RequestedBy(Unit unit, out bool player)
        {
            player = false;
            Transform marker = RequestMarker(unit);
            if (marker == null) return null;
            string who = marker.name.Substring(RequestPrefix.Length);
            player = who.EndsWith(PlayerTag);
            return player ? who.Substring(0, who.Length - PlayerTag.Length) : who;
        }

        // The claim moves to another mod in place: the marker is renamed, not
        // dropped and remade, so nothing can claim the unit in between.
        internal static void Transfer(Unit unit, string to)
        {
            if (unit == null || string.IsNullOrEmpty(to)) return;
            Transform marker = Marker(unit);
            if (marker == null)
            {
                var made = new GameObject(Prefix + to);
                made.transform.SetParent(unit.transform, false);
                made.SetActive(false);
            }
            else if (marker.name == Prefix + Host.ModId) marker.name = Prefix + to;
            Transform request = RequestMarker(unit);
            if (request != null) { request.name = "__NOrders.Served"; Object.Destroy(request.gameObject); }
        }

        private static float nextService;

        // The owner's side, from every host's update: any unit of ours a
        // player-directed mod has asked for is handed over.
        internal static void ServiceHandovers()
        {
            if (Time.unscaledTime < nextService) return;
            nextService = Time.unscaledTime + 0.5f;
            foreach (Unit unit in new System.Collections.Generic.List<Unit>(UnitRegistry.allUnits))
            {
                if (unit == null || !Mine(unit)) continue;
                string requester = RequestedBy(unit, out bool player);
                if (requester == null || !player || requester == Host.ModId) continue;
                Yield(unit, requester);
            }
        }

        private static void Yield(Unit unit, string requester)
        {
            if (unit is Ship ship) TaskForces.Forget(ship);
            try { Host.OnYield(unit); } catch (System.Exception ex) { Host.LogWarning("[own] OnYield: " + ex.Message); }
            // Everything this mod's copy of NOrders put on the unit -- its route,
            // its engagement rules -- goes, or it would go on steering it.
            System.Reflection.Assembly ours = typeof(Ownership).Assembly;
            foreach (MonoBehaviour behaviour in unit.GetComponents<MonoBehaviour>())
                if (behaviour != null && behaviour.GetType().Assembly == ours) Object.Destroy(behaviour);
            Transfer(unit, requester);
            string name = unit is Ship named ? ShipNames.Of(named) : unit.name;
            Host.LogInfo("[own] " + name + " handed to " + requester + " at the player's request");
        }

        private static Transform RequestMarker(Unit unit)
        {
            if (unit == null) return null;
            Transform root = unit.transform;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child != null && child.name.StartsWith(RequestPrefix)) return child;
            }
            return null;
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
