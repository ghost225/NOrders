using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // Why one of ours was abandoned before it ever flew.
    //
    // The game's taxi and take-off states give up on an aircraft by having the
    // pilot get out: on any damage while taxiing, a roll of a few degrees, no
    // route to a runway, or half a minute without progress (twelve seconds on
    // the runway). Stopped near an airbase, the airframe then goes back into
    // reserve -- which read as "recovered", with a sortie bonus, for an
    // aircraft that never left the ground. This records the state it was in
    // at the moment the pilot got out, so the cause can be read off the log.
    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.StartEjectionSequence))]
    internal static class AbandonTracePatch
    {
        private static readonly System.Reflection.FieldInfo Disembarking = AccessTools.Field(typeof(AIPilotTaxiState), "disembarking");
        private static readonly System.Reflection.FieldInfo TakeoffStuck = AccessTools.Field(typeof(AIPilotTakeoffState), "stuckTimer");

        private static void Prefix(Aircraft __instance) => Guard.Run("Abandon trace", () => Note(__instance));

        private static void Note(Aircraft aircraft)
        {
            Flight flight = aircraft != null ? FlightOrders.Of(aircraft) : null;
            if (flight == null) return;
            Pilot pilot = FlightOrders.FirstPilot(aircraft);
            PilotBaseState state = pilot?.currentState;
            if (pilot != null && pilot.flightInfo.HasTakenOff) return;      // an ejection in flight is its own story

            float roll = aircraft.cockpit != null ? Mathf.Asin(Mathf.Clamp(Vector3.Dot(aircraft.cockpit.xform.right, Vector3.up), -1f, 1f)) * Mathf.Rad2Deg : 0f;
            string detail = "";
            if (state is AIPilotTaxiState taxi)
                detail = " · stuck timers speed " + (taxi.stuckTimerSpeedPercent * 100f).ToString("0") + "% yaw " +
                    (taxi.stuckTimerYawPercent * 100f).ToString("0") + "%" +
                    (Disembarking != null && (bool)Disembarking.GetValue(taxi) ? " · disembark flagged" : "");
            else if (state is AIPilotTakeoffState takeoff && TakeoffStuck != null)
                detail = " · stuck on the runway " + ((float)TakeoffStuck.GetValue(takeoff)).ToString("0.0") + " s";

            Aircraft nearest = null;
            float gap = float.MaxValue;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Aircraft other) || other == aircraft || other.disabled) continue;
                float d = FastMath.Distance(other.GlobalPosition(), aircraft.GlobalPosition());
                if (d < gap) { gap = d; nearest = other; }
            }
            Host.LogWarning("[deck] " + flight.Name + " abandoned on the ground before take-off · " +
                (state != null ? (state.GetType().Name + " \"" + state.stateDisplayName + "\"") : "no state") +
                detail + " · speed " + aircraft.speed.ToString("0.0") + " m/s · roll " + roll.ToString("0.0") + "°" +
                " · nearest aircraft " + (nearest != null ? (FlightOrders.Of(nearest)?.Name ?? nearest.definition?.unitName ?? "?") +
                    " at " + gap.ToString("0") + " m" : "none"));
            Host.Say(flight.Name + " · abandoned on the ground before take-off, airframe back in reserve");
        }
    }
}
