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
        private static float noteAt, groundNoteAt;
        // Aircraft in a pull-out, held until climbing. Checked afresh each
        // frame, the combat pilot had the stick back the moment the sink
        // eased and nosed down at the target again: Bolt, an F-99 on a gun
        // run at 334 m/s, went in a few seconds after a pull-out at 510 m.
        private static readonly System.Collections.Generic.HashSet<Aircraft> pulling = new System.Collections.Generic.HashSet<Aircraft>();

        private static void Postfix(Pilot pilot)
        {
            if (!Guard.Ok(Name)) return;
            try
            {
                Aircraft aircraft = pilot?.aircraft;
                if (aircraft == null || !(aircraft.autopilot is AutopilotPlane)) return;
                Flight flight = FlightOrders.Of(aircraft);
                if (flight == null || Host.IsFlownByPlayer(flight)) return;
                // Auto-hover acts beneath any pilot state; the combat pilot never
                // switches it off either (see NavalPilotState.Steer).
                if (aircraft.radarAlt > 5f && aircraft.IsAutoHoverEnabled()) aircraft.GetControlsFilter().SetAutoHover(false);
                // Throttle at exactly zero is the airbrake. The combat pilot
                // asks for it in a gun run on a much slower target (a
                // helicopter) and when evading a heat-seeker; slow or low,
                // with airbrakes out, a jet spins or mushes in. Idle instead,
                // there; at speed and height it may still brake.
                ControlInputs inputs = aircraft.GetInputs();
                AircraftParameters p = aircraft.GetAircraftParameters();
                if (inputs != null && inputs.throttle <= 0f && aircraft.radarAlt > 5f &&
                    (aircraft.radarAlt < 300f || (p != null && p.cornerSpeed > 0f && aircraft.speed < p.cornerSpeed * 1.2f)))
                    inputs.throttle = 0.02f;
                if (SpeedLimit.Apply(aircraft, inputs) && SpeedLimit.Over(aircraft) >= 1f && Time.timeSinceLevelLoad >= noteAt)
                {
                    noteAt = Time.timeSinceLevelLoad + 15f;
                    Tracing.Flight("[flight] " + flight.Name + " · overspeed in combat · " + aircraft.speed.ToString("0") + " m/s, power off");
                }
                // Ground ahead: the combat pilot dives on a low target and does
                // not always come out -- a Vortex went into the ground at
                // 449 m/s while "engaging". Under four seconds from impact at the
                // present sink (or the terrain warning already sounding), our
                // autopilot call replaces its inputs for the frame: wings toward
                // level, nose up the way it is going, until it is climbing.
                // (Sink rate and height only: the game's terrain warning is an
                // exclusion-zone check, not a ground one.)
                if (aircraft.rb != null)
                {
                    float sink = -aircraft.rb.velocity.y;
                    // Earlier the faster it goes: a pull-out's radius grows
                    // with the square of the speed.
                    float lookAhead = Mathf.Max(4f, aircraft.speed / 60f);
                    bool holding = pulling.Contains(aircraft);
                    if (holding && (aircraft.rb.velocity.y > 5f || aircraft.radarAlt > 1500f)) { pulling.Remove(aircraft); holding = false; }
                    if (holding || (sink > 15f && aircraft.radarAlt < sink * lookAhead + 150f))
                    {
                        if (pulling.Count > 200) pulling.Clear();
                        pulling.Add(aircraft);
                        Vector3 ahead = aircraft.rb.velocity; ahead.y = 0f;
                        if (ahead.sqrMagnitude < 1f) ahead = aircraft.transform.forward; ahead.y = 0f;
                        GlobalPosition up = aircraft.GlobalPosition() + ahead.normalized * 3000f + Vector3.up * 1200f;
                        aircraft.autopilot.AutoAim(up, aimVelocity: true, ignoreCollisions: false, runwayAlign: false,
                            effort: 1f, bankAllowed: 30f, followTerrain: true, altitudeHold: 300f, targetVelocity: Vector3.zero);
                        if (Time.timeSinceLevelLoad >= groundNoteAt)
                        {
                            groundNoteAt = Time.timeSinceLevelLoad + 10f;
                            Tracing.Flight("[flight] " + flight.Name + " · pulled out under the combat pilot · sinking " + sink.ToString("0") + " m/s at " + aircraft.radarAlt.ToString("0") + " m");
                        }
                    }
                }
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }
    }
}
