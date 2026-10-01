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
            string owner = Read(unit);                      // fresh, never the cache: two mods must not both claim
            if (owner != null) { owners[unit] = (owner, Time.unscaledTime); return owner == Host.ModId; }
            var marker = new GameObject(Prefix + Host.ModId);
            marker.transform.SetParent(unit.transform, false);
            marker.SetActive(false);                        // no cost, nothing to render
            owners[unit] = (Host.ModId, Time.unscaledTime);
            StripForeign(unit);
            return true;
        }

        // What another mod's copy of NOrders left on a unit it no longer owns
        // -- its route, still setting the throttle to its last speed order --
        // goes when this mod takes the unit on. Forty ships High Command had
        // let go of sat still in a Naval Power task force for minutes while
        // its leftover routes fought the new orders.
        private static void StripForeign(Unit unit)
        {
            System.Reflection.Assembly ours = typeof(Ownership).Assembly;
            foreach (MonoBehaviour behaviour in unit.GetComponents<MonoBehaviour>())
            {
                if (behaviour == null) continue;
                System.Type type = behaviour.GetType();
                if (type.Namespace != "NOrders" || type.Assembly == ours) continue;
                Host.LogInfo("[own] removing " + type.Name + " another mod left on " + unit.name);
                Object.Destroy(behaviour);
            }
        }

        // Gives up this mod's claim; another mod's is left alone.
        public static void Release(Unit unit)
        {
            Transform marker = Marker(unit);
            if (marker == null || marker.name != Prefix + Host.ModId) return;
            marker.name = "__NOrders.Released";                // gone now, not at the end of the frame
            Object.Destroy(marker.gameObject);
            owners.Remove(unit);
        }

        // The ModId of the mod that owns the unit, or null.
        //
        // Read off the unit's children, which is not cheap -- a name read and
        // a compare per child -- and asked for every unit, missiles and all,
        // by the sweeps and by every patch that acts on units no mod owns.
        // So the answer is kept for half a second; this mod's own claims and
        // releases update it at once, and another mod's show within that.
        private static readonly System.Collections.Generic.Dictionary<Unit, (string owner, float at)> owners =
            new System.Collections.Generic.Dictionary<Unit, (string, float)>();
        private const float OwnerFresh = 0.5f;

        public static string OwnerOf(Unit unit)
        {
            if (unit == null) return null;
            float now = Time.unscaledTime;
            if (owners.TryGetValue(unit, out var known) && now - known.at < OwnerFresh) return known.owner;
            string owner = Read(unit);
            if (owners.Count > 4000) Prune();
            owners[unit] = (owner, now);
            return owner;
        }

        private static string Read(Unit unit)
        {
            Transform marker = Marker(unit);
            return marker != null ? marker.name.Substring(Prefix.Length) : null;
        }

        private static void Prune()
        {
            var gone = new System.Collections.Generic.List<Unit>();
            float now = Time.unscaledTime;
            foreach (var entry in owners) if (entry.Key == null || entry.Key.disabled || now - entry.Value.at > 10f) gone.Add(entry.Key);
            foreach (Unit unit in gone) owners.Remove(unit);
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
        private static GameObject stewardHolder;
        private static float stewardAt = -100f;
        private static bool steward;

        // The marker object outlives scenes, so once found it is kept: a
        // GameObject.Find walks every object in the scene, and this was asked
        // once a frame whenever an AI aircraft landed or taxied on a ship.
        public static bool Steward
        {
            get
            {
                float now = Time.unscaledTime;
                if (stewardHolder != null && now - stewardAt < 5f) return steward;
                stewardAt = now;
                if (stewardHolder == null) stewardHolder = GameObject.Find(StewardName);
                if (stewardHolder == null)
                {
                    stewardHolder = new GameObject(StewardName);
                    Object.DontDestroyOnLoad(stewardHolder);
                    new GameObject(Host.ModId).transform.SetParent(stewardHolder.transform, false);
                }
                steward = stewardHolder.transform.childCount > 0 && stewardHolder.transform.GetChild(0).name == Host.ModId;
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
            owners.Remove(unit);
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
            // Ships are what gets handed over; the rest -- missiles above
            // all -- are not looked at.
            var asked = new System.Collections.Generic.List<(Unit unit, string by)>();
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Ship) || unit.disabled || !Mine(unit)) continue;
                string requester = RequestedBy(unit, out bool player);
                if (requester == null || !player || requester == Host.ModId) continue;
                asked.Add((unit, requester));
            }
            foreach (var request in asked) Yield(request.unit, request.by);
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
                if (child != null && child.name.StartsWith(RequestPrefix, System.StringComparison.Ordinal)) return child;
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
                if (child != null && child.name.StartsWith(Prefix, System.StringComparison.Ordinal)) return child;
            }
            return null;
        }
    }
}
