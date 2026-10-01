using System.Collections.Generic;
using UnityEngine;

namespace NOrders
{
    // Aircraft landing on a ship that is under way were never recovered.
    //
    // The game ends a landing only once the aircraft is all but stopped (under
    // 1 m/s) and returns it to inventory only under 2 m/s -- speed over the
    // ground, not relative to the deck. Aboard a moving ship a parked aircraft
    // moves at the ship's speed, so it sat braking on the deck for ever,
    // blocking it, or, if its crew did get out, was left there abandoned. A
    // commanded carrier is usually moving, which is why it showed up so much.
    //
    // So an aircraft resting on a moving ship's deck -- at rest relative to
    // the deck, low over it, in the landing, taxi or parked state, flown by
    // no player -- for a few seconds is recovered as the game recovers one:
    // abandoned and returned to inventory, which frees the deck. A ship that
    // is not moving is left to the game's own recovery. A fix to the game's
    // behaviour for every ship, so it follows Ownership.Acts: the ship's
    // owner does it, or the steward for ships no mod owns.
    internal static class MovingDeckRecovery
    {
        private const float ShipMoving = 0.8f;      // m/s: the game's own check wants under 1 m/s over the ground
        private const float AtRest = 1.2f;          // m/s relative to the deck
        private const float OverDeck = 15f;         // metres above the deck's centre
        private const float Settle = 5f;            // seconds at rest before recovery
        private const float TaxiSettle = 1.5f;      // seconds, for one taxiing on a deck
        private const float TaxiSlow = 6f;          // m/s relative to the deck

        private static readonly Dictionary<Aircraft, float> restingSince = new Dictionary<Aircraft, float>();
        private static readonly Dictionary<Aircraft, (float at, Ship ship)> exiting = new Dictionary<Aircraft, (float, Ship)>();
        private const float CrewOut = 7f;           // seconds from the crew starting out to the airframe's return
        private static readonly System.Reflection.FieldInfo ToRunway = HarmonyLib.AccessTools.Field(typeof(AIPilotTaxiState), "toRunway");
        private static float nextSweep;

        internal static void Tick()
        {
            if (Time.timeSinceLevelLoad < nextSweep) return;
            nextSweep = Time.timeSinceLevelLoad + 0.5f;
            if (!MissionManager.IsRunning) return;

            var seen = new HashSet<Aircraft>();
            // Recovery returns the aircraft to the inventory, which edits the
            // registry: collect during the sweep, recover after it.
            var recover = new List<(Aircraft, Ship)>();
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Aircraft aircraft) || aircraft.disabled || aircraft.Player != null || aircraft.rb == null || !aircraft.IsServer) continue;
                Pilot pilot = FlightOrders.FirstPilot(aircraft);
                PilotBaseState state = pilot?.currentState;
                bool landedStates = state == null || state is AIPilotLandingState || state is AIPilotTaxiState ||
                                    state is PilotParkedState || state is AIHeloLandingState;
                if (!landedStates) continue;
                // Back from a flight, not waiting to start one: an aircraft
                // just brought up, sitting still on the deck for its takeoff
                // clearance, was taken for one that had landed and recovered
                // before it ever flew -- one of a wing of four off an Annex.
                if (pilot == null || !pilot.flightInfo.HasTakenOff || exiting.ContainsKey(aircraft)) continue;
                if (state is AIPilotTaxiState taxi && ToRunway != null && (bool)ToRunway.GetValue(taxi)) continue;
                // Down on any ship's deck in the taxi state (held braking by
                // DeckNoTaxiPatch): recovered as soon as it has slowed, moving
                // ship or not.
                bool taxiing = state is AIPilotTaxiState;
                if (!OnMovingDeck(aircraft, out Ship ship, taxiing)) continue;
                seen.Add(aircraft);
                if (!restingSince.TryGetValue(aircraft, out float since)) { restingSince[aircraft] = Time.timeSinceLevelLoad; continue; }
                if (Time.timeSinceLevelLoad - since < (taxiing ? TaxiSettle : Settle)) continue;

                restingSince.Remove(aircraft);
                seen.Remove(aircraft);
                recover.Add((aircraft, ship));
            }
            // The crew get out first, the game's own way -- canopy, then each
            // of them, a few seconds in all -- and the airframe is returned a
            // moment after. The game returns it itself only under 2 m/s over
            // the ground, which a deck under way never is.
            foreach ((Aircraft aircraft, Ship ship) in recover)
            {
                if (exiting.ContainsKey(aircraft)) continue;
                exiting[aircraft] = (Time.timeSinceLevelLoad, ship);
                if (!aircraft.HasEjected()) aircraft.StartEjectionSequence();
                Host.LogInfo("[deck] " + (FlightOrders.Of(aircraft)?.Name ?? aircraft.definition?.unitName ?? aircraft.name) +
                    " · crew out aboard " + ShipNames.Of(ship));
            }
            var returned = new List<Aircraft>();
            foreach (KeyValuePair<Aircraft, (float at, Ship ship)> entry in exiting)
            {
                Aircraft aircraft = entry.Key;
                if (aircraft == null || aircraft.disabled) { returned.Add(aircraft); continue; }
                if (Time.timeSinceLevelLoad - entry.Value.at < CrewOut) continue;
                returned.Add(aircraft);
                Host.LogInfo("[deck] " + (FlightOrders.Of(aircraft)?.Name ?? aircraft.definition?.unitName ?? aircraft.name) +
                    " recovered aboard " + ShipNames.Of(entry.Value.ship));
                aircraft.NetworkunitState = Unit.UnitState.Abandoned;
                aircraft.ReturnToInventory();
            }
            foreach (Aircraft aircraft in returned) exiting.Remove(aircraft);
            var gone = new List<Aircraft>();
            foreach (Aircraft aircraft in restingSince.Keys) if (!seen.Contains(aircraft)) gone.Add(aircraft);
            foreach (Aircraft aircraft in gone) restingSince.Remove(aircraft);
        }

        // The ship whose deck this aircraft is on, if any, whatever either is
        // doing: low over a ship's airbase, inside its footprint.
        internal static Ship DeckUnder(Aircraft aircraft)
        {
            FactionHQ hq = aircraft?.NetworkHQ;
            if (hq == null) return null;
            Vector3 at = aircraft.transform.position;
            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (airbase == null || airbase.disabled || !airbase.AttachedAirbase) continue;
                Ship deck = Airfields.ShipOf(airbase);
                if (deck == null || deck.disabled) continue;
                Vector3 centre = airbase.center != null ? airbase.center.position : airbase.transform.position;
                Vector3 flat = at - centre; flat.y = 0f;
                if (flat.magnitude > Mathf.Max(airbase.GetRadius(), deck.maxRadius)) continue;
                if (at.y - centre.y > OverDeck) continue;
                return deck;
            }
            return null;
        }

        // At rest on the deck of a ship that is itself moving.
        private static bool OnMovingDeck(Aircraft aircraft, out Ship ship, bool taxiing = false)
        {
            ship = null;
            FactionHQ hq = aircraft.NetworkHQ;
            if (hq == null) return false;
            Vector3 at = aircraft.transform.position;
            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (airbase == null || airbase.disabled || !airbase.AttachedAirbase) continue;
                Ship deck = Airfields.ShipOf(airbase);
                if (deck == null || deck.disabled || deck.rb == null) continue;
                if (!taxiing && deck.rb.velocity.magnitude < ShipMoving) continue;
                Vector3 centre = airbase.center != null ? airbase.center.position : airbase.transform.position;
                Vector3 flat = at - centre; flat.y = 0f;
                if (flat.magnitude > Mathf.Max(airbase.GetRadius(), deck.maxRadius)) continue;
                if (at.y - centre.y > OverDeck) continue;
                Vector3 relative = aircraft.rb.velocity - deck.rb.GetPointVelocity(at);
                if (relative.magnitude > (taxiing ? TaxiSlow : AtRest)) continue;
                if (!Ownership.Acts(deck)) continue;
                ship = deck;
                return true;
            }
            return false;
        }
    }

    // No taxiing on a ship's deck after a landing: the aircraft brakes to a
    // stop where it is and waits to be recovered (MovingDeckRecovery). The
    // game taxis it to a service point, and a deck is no place for that -- an
    // Eclipse landed, taxied, and rolled off the side into the sea. Launches
    // (taxiing out to the runway, before take-off) are left alone. A fix to
    // the game's behaviour, so Ownership.Acts on the ship.
    [HarmonyLib.HarmonyPatch(typeof(AIPilotTaxiState), nameof(AIPilotTaxiState.FixedUpdateState))]
    internal static class DeckNoTaxiPatch
    {
        private const string Name = "No taxiing on deck";
        private static readonly System.Reflection.FieldInfo ToRunway = HarmonyLib.AccessTools.Field(typeof(AIPilotTaxiState), "toRunway");

        private static bool Prefix(AIPilotTaxiState __instance, Pilot pilot)
        {
            if (!Guard.Ok(Name)) return true;
            try
            {
                Aircraft aircraft = pilot?.aircraft;
                if (aircraft == null || aircraft.Player != null || !aircraft.IsServer || !pilot.flightInfo.HasTakenOff) return true;
                if (ToRunway != null && (bool)ToRunway.GetValue(__instance)) return true;
                Ship deck = MovingDeckRecovery.DeckUnder(aircraft);
                if (deck == null || !Ownership.Acts(deck)) return true;
                ControlInputs inputs = aircraft.GetInputs();
                if (inputs != null) { inputs.throttle = 0f; inputs.brake = 1f; inputs.yaw = 0f; }
                return false;
            }
            catch (System.Exception ex) { Guard.Failed(Name, ex); return true; }
        }
    }
}
