using System;
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
            flight.AbandonedOnGround = true;                               // no sortie bonus for this one

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

    // Our aircraft taxi at a taxiing speed.
    //
    // The game's taxi state asks for up to 30 m/s and gives half throttle
    // whenever it is under that -- on a high-thrust jet, a mod's especially,
    // that shot aircraft out of the hangar and off the taxiway onto the
    // grass. Capped at 12 m/s, 8 for the first quarter-minute out of the
    // hangar; faster than that, off the power and on the brakes.
    [HarmonyPatch(typeof(AIPilotTaxiState), nameof(AIPilotTaxiState.FixedUpdateState))]
    internal static class TaxiSpeedPatch
    {
        private const string Name = "Taxi speed";
        private static readonly System.Reflection.FieldInfo Inputs = AccessTools.Field(typeof(PilotBaseState), "controlInputs");

        private static void Postfix(AIPilotTaxiState __instance, Pilot pilot)
        {
            if (!Guard.Ok(Name)) return;
            try
            {
                Aircraft aircraft = pilot?.aircraft;
                if (aircraft == null || pilot.playerControlled || pilot.flightInfo.HasTakenOff || FlightOrders.Of(aircraft) == null) return;
                if (!(Inputs?.GetValue(__instance) is ControlInputs inputs)) return;
                float limit = Time.timeSinceLevelLoad - pilot.flightInfo.spawnTime < 15f ? 8f : 12f;
                if (aircraft.speed > limit)
                {
                    inputs.throttle = Mathf.Min(inputs.throttle, 0.01f);
                    inputs.brake = Mathf.Max(inputs.brake, Mathf.Clamp01((aircraft.speed - limit) * 0.3f + 0.3f));
                }
                else if (aircraft.speed > limit * 0.8f)
                    inputs.throttle = Mathf.Min(inputs.throttle, 0.15f);
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }
    }

    // And only give up on one of ours for a real roll. The taxi state's check
    // bails the pilot out past about three degrees -- a bump on the grass --
    // where twelve is an aircraft actually going over.
    [HarmonyPatch(typeof(AIPilotTaxiState), "EjectCheck")]
    internal static class TaxiRollPatch
    {
        private const string Name = "Taxi roll check";
        private static readonly System.Reflection.FieldInfo AircraftOf = AccessTools.Field(typeof(PilotBaseState), "aircraft");

        private static bool Prefix(AIPilotTaxiState __instance)
        {
            if (!Guard.Ok(Name)) return true;
            try
            {
                if (!(AircraftOf?.GetValue(__instance) is Aircraft aircraft) || FlightOrders.Of(aircraft) == null || aircraft.cockpit == null) return true;
                float roll = Mathf.Abs(Vector3.Dot(aircraft.cockpit.xform.right, Vector3.up));
                return roll > Mathf.Sin(12f * Mathf.Deg2Rad);
            }
            catch (Exception ex) { Guard.Failed(Name, ex); return true; }
        }
    }
}
