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
        private const float ShipMoving = 1.5f;      // m/s: below this the game's own check works
        private const float AtRest = 1.2f;          // m/s relative to the deck
        private const float OverDeck = 15f;         // metres above the deck's centre
        private const float Settle = 5f;            // seconds at rest before recovery

        private static readonly Dictionary<Aircraft, float> restingSince = new Dictionary<Aircraft, float>();
        private static float nextSweep;

        internal static void Tick()
        {
            if (Time.timeSinceLevelLoad < nextSweep) return;
            nextSweep = Time.timeSinceLevelLoad + 0.5f;
            if (!MissionManager.IsRunning) return;

            var seen = new HashSet<Aircraft>();
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Aircraft aircraft) || aircraft.disabled || aircraft.Player != null || aircraft.rb == null || !aircraft.IsServer) continue;
                Pilot pilot = FlightOrders.FirstPilot(aircraft);
                PilotBaseState state = pilot?.currentState;
                bool landedStates = state == null || state is AIPilotLandingState || state is AIPilotTaxiState ||
                                    state is PilotParkedState || state is AIHeloLandingState;
                if (!landedStates) continue;
                if (!OnMovingDeck(aircraft, out Ship ship)) continue;
                seen.Add(aircraft);
                if (!restingSince.TryGetValue(aircraft, out float since)) { restingSince[aircraft] = Time.timeSinceLevelLoad; continue; }
                if (Time.timeSinceLevelLoad - since < Settle) continue;

                restingSince.Remove(aircraft);
                seen.Remove(aircraft);
                Host.LogInfo("[deck] " + (FlightOrders.Of(aircraft)?.Name ?? aircraft.definition?.unitName ?? aircraft.name) +
                    " recovered aboard " + ShipNames.Of(ship) + " (ship under way)");
                aircraft.NetworkunitState = Unit.UnitState.Abandoned;
                aircraft.ReturnToInventory();
            }
            var gone = new List<Aircraft>();
            foreach (Aircraft aircraft in restingSince.Keys) if (!seen.Contains(aircraft)) gone.Add(aircraft);
            foreach (Aircraft aircraft in gone) restingSince.Remove(aircraft);
        }

        // At rest on the deck of a ship that is itself moving.
        private static bool OnMovingDeck(Aircraft aircraft, out Ship ship)
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
                if (deck.rb.velocity.magnitude < ShipMoving) continue;
                Vector3 centre = airbase.center != null ? airbase.center.position : airbase.transform.position;
                Vector3 flat = at - centre; flat.y = 0f;
                if (flat.magnitude > Mathf.Max(airbase.GetRadius(), deck.maxRadius)) continue;
                if (at.y - centre.y > OverDeck) continue;
                Vector3 relative = aircraft.rb.velocity - deck.rb.GetPointVelocity(at);
                if (relative.magnitude > AtRest) continue;
                if (!Ownership.Acts(deck)) continue;
                ship = deck;
                return true;
            }
            return false;
        }
    }
}
