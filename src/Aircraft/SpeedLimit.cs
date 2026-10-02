using System;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // Not past what the airframe can take. Parts are held on by joints with a
    // break force, and the air load grows with the square of the speed: an
    // FS-41 at 470-580 m/s lost its cockpit in an ordinary turn, three in one
    // sortie. Power comes off from 80% of the airframe's top speed and is at
    // idle by 92%, whatever set it -- our own flying, or the game's combat
    // pilot, which dives at full burner on a low target and never looks at
    // the airspeed.
    internal static class SpeedLimit
    {
        internal static float Over(Aircraft aircraft)
        {
            AircraftParameters p = aircraft != null ? aircraft.GetAircraftParameters() : null;
            if (p == null || p.maxSpeed <= 0f) return 0f;
            return Mathf.InverseLerp(p.maxSpeed * 0.8f, p.maxSpeed * 0.92f, aircraft.speed);
        }

        // True when power was taken off.
        internal static bool Apply(Aircraft aircraft, ControlInputs inputs)
        {
            if (aircraft == null || inputs == null || !(aircraft.autopilot is AutopilotPlane)) return false;
            float over = Over(aircraft);
            if (over <= 0f) return false;
            inputs.throttle = Mathf.Min(inputs.throttle, Mathf.Lerp(inputs.throttle, 0.1f, over));
            AuxAxis.Apply(aircraft, inputs);
            return true;
        }
    }

    // The same for our aircraft while the game's combat pilot has them.
    [HarmonyPatch(typeof(AIPilotCombatModes), nameof(AIPilotCombatModes.FixedUpdateState))]
    internal static class NativeSpeedLimitPatch
    {
        private const string Name = "Combat speed limit";
        private static float noteAt;

        private static void Postfix(Pilot pilot)
        {
            if (!Guard.Ok(Name)) return;
            try
            {
                Aircraft aircraft = pilot?.aircraft;
                if (aircraft == null || !(aircraft.autopilot is AutopilotPlane)) return;
                Flight flight = FlightOrders.Of(aircraft);
                if (flight == null || Host.IsFlownByPlayer(flight)) return;
                if (SpeedLimit.Apply(aircraft, aircraft.GetInputs()) && SpeedLimit.Over(aircraft) >= 1f && Time.timeSinceLevelLoad >= noteAt)
                {
                    noteAt = Time.timeSinceLevelLoad + 15f;
                    Tracing.Flight("[flight] " + flight.Name + " · overspeed in combat · " + aircraft.speed.ToString("0") + " m/s, power off");
                }
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }
    }
}
