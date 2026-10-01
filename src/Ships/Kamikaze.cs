using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // Kamikaze surface drones -- the Aryx Naval Expansion's Keres USV.
    //
    // A Keres is a ship whose AI (AryxKamikazeShipAI) hunts on its own: it
    // picks an enemy ship from the faction's tracks (seen in the last minute),
    // follows the sea lanes to it, weaves on the run-in and fires its warhead
    // inside 50 m of the hull. A commanded destination outranks all of that,
    // and our navigation holds every commanded ship "under orders" so the
    // native controller cannot wander off -- so a Keres under our command
    // never chose a target, never ran in and never detonated, even rammed
    // into one. Waypoints still work as for any ship; these are the two
    // orders that use it as it was built to be used:
    //
    //   Attack -- the route is cleared and steering handed back to its own AI
    //   with our target forced on it, held through the AI's own reassessment
    //   for as long as the target stands and is tracked.
    //   Hunt   -- steering handed back with no target: it picks its own.
    //
    // A carrier with a well deck (the Annex) carries Keres in its hold like
    // its landing craft, and launches them from the well-deck door.
    public static class Kamikaze
    {
        private static Type aiType;
        private static bool looked;
        private static MethodInfo setTarget, clearTarget;

        internal static Type AiType
        {
            get
            {
                if (!looked)
                {
                    looked = true;
                    aiType = AccessTools.TypeByName("AryxKamikazeUSV.AryxKamikazeShipAI");
                    if (aiType != null)
                    {
                        setTarget = AccessTools.Method(aiType, "SetTarget");
                        clearTarget = AccessTools.Method(aiType, "ClearTarget");
                    }
                }
                return aiType;
            }
        }

        private static readonly Dictionary<Ship, Unit> assigned = new Dictionary<Ship, Unit>();

        // Usable only with everything we rely on present: the mod's drone AI,
        // the methods we drive it through, and a Keres in the encyclopedia.
        // An Aryx update that renamed any of them hides every Keres option
        // rather than offering ones that would do nothing.
        public static bool Available => AiType != null && setTarget != null && clearTarget != null &&
            AccessTools.Method(AiType, "ChooseTarget") != null && Definition() != null;

        public static bool Is(Ship ship) => ship != null && AiType != null && setTarget != null && ship.GetComponent(AiType) != null;

        public static bool IsType(UnitDefinition type) =>
            type?.unitPrefab != null && AiType != null && type.unitPrefab.GetComponent(AiType) != null;

        // The Keres definition, if the mod is loaded.
        private static UnitDefinition definition;
        private static float nextLookup;

        public static UnitDefinition Definition()
        {
            if (definition != null) return definition;
            if (AiType == null || Encyclopedia.Lookup == null || Time.unscaledTime < nextLookup) return null;
            nextLookup = Time.unscaledTime + 10f;
            foreach (UnitDefinition type in Encyclopedia.Lookup.Values) if (IsType(type)) return definition = type;
            return null;
        }

        public static Unit TargetOf(Ship ship) => ship != null && assigned.TryGetValue(ship, out Unit t) && !Host.Dead(t) ? t : null;

        public static bool Attack(Ship usv, Unit target, out string reason)
        {
            reason = null;
            if (!Is(usv)) { reason = "Not a kamikaze drone."; return false; }
            if (!(target is Ship) || Host.Dead(target)) { reason = "A Keres attacks ships."; return false; }
            if (target.NetworkHQ == usv.NetworkHQ) { reason = "That ship is ours."; return false; }
            if (!Release(usv, out reason)) return false;
            assigned[usv] = target;
            Force(usv, target);
            reason = ShipNames.Of(usv) + " · attacking " + ShipNames.Of(target);
            Host.LogInfo("[keres] " + reason);
            return true;
        }

        public static bool Hunt(Ship usv, out string reason)
        {
            reason = null;
            if (!Is(usv)) { reason = "Not a kamikaze drone."; return false; }
            if (!Release(usv, out reason)) return false;
            assigned.Remove(usv);
            reason = ShipNames.Of(usv) + " · hunting on its own";
            Host.LogInfo("[keres] " + reason);
            return true;
        }

        // Any order of ours other than an attack takes the target off it.
        internal static void Forget(Ship usv) { if (usv != null) assigned.Remove(usv); }

        private static bool Release(Ship usv, out string reason)
        {
            if (!NavigationOrders.ClearWaypoints(usv, out reason)) return false;
            NavigationOrders.ReleaseSpeed(usv, out _);
            return true;
        }

        // The target on the drone's own AI, with the faction's track of it.
        private static bool Force(Ship usv, Unit target)
        {
            Component ai = AiType != null ? usv.GetComponent(AiType) : null;
            if (ai == null || setTarget == null || usv.NetworkHQ == null) return false;
            if (!usv.NetworkHQ.trackingDatabase.TryGetValue(target.persistentID, out TrackingInfo track) || track == null) return false;
            setTarget.Invoke(ai, new object[] { target, track, 1000f });
            return true;
        }

        // Its own reassessment, every few seconds: ours stands while it can be
        // attacked; gone or lost, the drone goes back to hunting for itself.
        internal static bool KeepAssigned(Component ai)
        {
            if (!(ai is MonoBehaviour behaviour) || !(behaviour.GetComponent<Ship>() is Ship usv)) return true;
            if (!assigned.TryGetValue(usv, out Unit target)) return true;
            if (Host.Dead(target) || usv.NetworkHQ == null ||
                !usv.NetworkHQ.trackingDatabase.TryGetValue(target.persistentID, out TrackingInfo track) || track == null ||
                Time.timeSinceLevelLoad - track.lastSpottedTime > 55f)
            {
                assigned.Remove(usv);
                Host.LogInfo("[keres] " + ShipNames.Of(usv) + " · " + (Host.Dead(target) ? "target gone" : "lost the track") + " · hunting on its own");
                return true;
            }
            Force(usv, target);
            return false;
        }

        // ---- carried and launched from a well deck ---------------------------

        public static int Aboard(Ship carrier)
        {
            UnitDefinition type = Definition();
            Amphib.WellDeck deck = Amphib.Deck(carrier);
            return type == null || deck == null ? 0 : Amphib.Count(deck.Hold, type);
        }

        public static bool Buy(Ship carrier, int count, out string reason)
        {
            UnitDefinition type = Definition();
            if (type == null) { reason = "The Aryx Naval Expansion's Keres is not loaded."; return false; }
            return Amphib.Buy(carrier, type, count, out reason);
        }

        public static float Price() => Amphib.Price(Definition());

        // From the well-deck door, a few seconds apart, each sent to a point
        // astern to wait for orders -- not off hunting the moment it is wet.
        public static bool Launch(Ship carrier, int count, out string reason)
        {
            UnitDefinition type = Definition();
            Amphib.WellDeck deck = Amphib.Deck(carrier);
            if (type == null) { reason = "The Aryx Naval Expansion's Keres is not loaded."; return false; }
            if (deck == null) { reason = "This ship has no well deck."; return false; }
            if (!CommandableShip.CanCommand(carrier, out reason)) return false;
            int aboard = Amphib.Count(deck.Hold, type);
            if (aboard <= 0) { reason = "No Keres aboard · buy some first."; return false; }
            count = Mathf.Clamp(count, 1, aboard);
            var runner = carrier.gameObject.GetComponent<AmphibRunner>() ?? carrier.gameObject.AddComponent<AmphibRunner>();
            runner.StartCoroutine(LaunchAll(deck, type, count));
            reason = "Launching " + count + " Keres from " + ShipNames.Of(carrier);
            return true;
        }

        private static System.Collections.IEnumerator LaunchAll(Amphib.WellDeck deck, UnitDefinition type, int count)
        {
            Ship carrier = deck.Ship;
            UnitStorage hold = deck.Hold;
            for (float waited = 0f; !hold.DoorsOpen() && waited < 30f; waited += 0.5f)
            {
                hold.OpenDoors();
                yield return new WaitForSeconds(0.5f);
                if (carrier == null || carrier.disabled) yield break;
            }
            Transform at = Amphib.DoorOf(hold);
            for (int i = 0; i < count; i++)
            {
                if (carrier == null || carrier.disabled || at == null) yield break;
                hold.OpenDoors();
                if (Amphib.Count(hold, type) <= 0) break;
                hold.AddOrRemoveUnit(type, -1);
                Unit spawned = NetworkSceneSingleton<Spawner>.i.SpawnUnit(type, at.position, at.rotation,
                    carrier.rb != null ? carrier.rb.GetPointVelocity(at.position) : Vector3.zero, carrier, null);
                if (!(spawned is Ship usv)) { hold.AddOrRemoveUnit(type, 1); Host.Say("A Keres could not be launched"); break; }
                Ownership.Claim(usv);
                usv.Launch();
                Amphib.OnRail(hold, usv);
                Vector3 outward = at.forward; outward.y = 0f; outward.Normalize();
                Vector3 across = Vector3.Cross(Vector3.up, outward);
                Vector3 wait = at.position + outward * 400f + across * ((i - (count - 1) * 0.5f) * 60f);
                wait.y = Datum.LocalSeaY;
                // Out of the dock before its own steering has it: a boat
                // spawned in the well deck could not find its way out. Pushed
                // straight out of the door, carried with the ship's motion,
                // until clear of the stern (15 s at most).
                for (float pushed = 0f; pushed < 15f && usv != null && !usv.disabled && usv.rb != null &&
                     Vector3.Distance(usv.transform.position, at.position) < Amphib.ClearDistance + 40f; pushed += Time.fixedDeltaTime)
                {
                    hold.OpenDoors();
                    Vector3 carried = carrier != null && carrier.rb != null ? carrier.rb.GetPointVelocity(usv.transform.position) : Vector3.zero;
                    Vector3 v = carried + outward * 9f;
                    v.y = usv.rb.velocity.y;
                    usv.rb.velocity = v;
                    usv.rb.MoveRotation(Quaternion.Slerp(usv.rb.rotation, Quaternion.LookRotation(outward, Vector3.up), 0.1f));
                    yield return new WaitForFixedUpdate();
                }
                if (usv != null && !usv.disabled) NavigationOrders.ReplaceWaypoint(usv, wait.ToGlobalPosition(), out _);
                Host.LogInfo("[keres] " + ShipNames.Of(carrier) + " launched " + ShipNames.Of(usv) + " (" + (i + 1) + "/" + count + ")");
                yield return new WaitForSeconds(1f);
            }
            Host.Say(ShipNames.Of(carrier) + " · Keres launched");
        }

        // Ours, afloat: those near this ship (within 15 km) first, nearest first.
        public static List<Ship> Near(Ship from, float range = 15000f)
        {
            var list = new List<Ship>();
            if (from == null) return list;
            foreach (Unit unit in UnitRegistry.allUnits)
                if (unit is Ship s && !s.disabled && s.NetworkHQ == from.NetworkHQ && Is(s) && CommandableShip.CanCommand(s, out _) &&
                    FastMath.InRange(s.GlobalPosition(), from.GlobalPosition(), range)) list.Add(s);
            list.Sort((a, b) => FastMath.Distance(a.GlobalPosition(), from.GlobalPosition()).CompareTo(FastMath.Distance(b.GlobalPosition(), from.GlobalPosition())));
            return list;
        }
    }

    // The drone's own target choice: an assigned target is kept.
    [HarmonyPatch]
    internal static class KamikazeTargetPatch
    {
        private const string Name = "Keres target";

        private static bool Prepare() => Kamikaze.AiType != null && AccessTools.Method(Kamikaze.AiType, "ChooseTarget") != null;
        private static MethodBase TargetMethod() => AccessTools.Method(Kamikaze.AiType, "ChooseTarget");

        private static bool Prefix(object __instance)
        {
            if (!Guard.Ok(Name)) return true;
            try { return Kamikaze.KeepAssigned(__instance as Component); }
            catch (Exception ex) { Guard.Failed(Name, ex); return true; }
        }
    }
}
