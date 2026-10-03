using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // Which way to dodge a radar-guided missile.
    //
    // The combat pilot beams the missile -- turns across its line of sight --
    // and of the two ways across it takes whichever is nearer the way the
    // aircraft is already pointing. That keeps its speed, but says nothing
    // about where the enemy is: half the time it breaks towards them. For our
    // flights it breaks to the side of friendly lines -- the side its home
    // deck or field is on -- and keeps the rest of the manoeuvre as the game
    // flies it: the dive, the countermeasure timing, the last-second pull.
    [HarmonyPatch(typeof(AIPilotCombatModes), "EvadeModeRadar")]
    internal static class EvadeTowardFriendsPatch
    {
        private const string Name = "Evasion toward friendly lines";
        private static readonly AccessTools.FieldRef<PilotBaseState, Aircraft> AircraftOf =
            AccessTools.FieldRefAccess<PilotBaseState, Aircraft>("aircraft");
        private static readonly AccessTools.FieldRef<PilotBaseState, GlobalPosition> DestinationOf =
            AccessTools.FieldRefAccess<PilotBaseState, GlobalPosition>("destination");
        private static readonly AccessTools.FieldRef<AIPilotCombatModes, Vector3> EvadeVectorOf =
            AccessTools.FieldRefAccess<AIPilotCombatModes, Vector3>("missileEvadeVector");
        private static readonly AccessTools.FieldRef<AIPilotCombatModes, Vector3> ErrorOf =
            AccessTools.FieldRefAccess<AIPilotCombatModes, Vector3>("randomErrorVector");
        private static readonly AccessTools.FieldRef<AIPilotCombatModes, float> ImpactTimeOf =
            AccessTools.FieldRefAccess<AIPilotCombatModes, float>("missileImpactTime");

        private static readonly AccessTools.FieldRef<AIPilotCombatModes, float> TargetHeightOf =
            AccessTools.FieldRefAccess<AIPilotCombatModes, float>("targetHeight");
        private static readonly AccessTools.FieldRef<AIPilotCombatModes, List<Missile>> AlertsOf =
            AccessTools.FieldRefAccess<AIPilotCombatModes, List<Missile>>("missileAlerts");
        private const float StepDown = 300f;
        private static readonly HashSet<Missile> noted = new HashSet<Missile>();

        private static void Postfix(AIPilotCombatModes __instance, ref GlobalPosition evadeDestination)
        {
            if (!Guard.Ok(Name)) return;
            try
            {
                Aircraft aircraft = AircraftOf(__instance);
                Flight flight = FlightOrders.Of(aircraft);
                if (flight == null || Host.IsFlownByPlayer(flight)) return;

                // Down to the deck, but walked there. The game's radar
                // evasion terrain-follows to 10 m at full power from wherever
                // the shot found it; aimed at 10 m from kilometres up, loaded
                // aircraft plunged and could not pull out over the sea. The
                // height it aims for is never more than StepDown under where it
                // is now, so it descends steadily to 10 m over the ground (the
                // game's own floor) instead of diving at it. The notch, the
                // chaff, the ECM and the last-second pull are the game's; the
                // ground guard still catches a hard sink near the ground.
                List<Missile> alerts = AlertsOf(__instance);
                if (alerts != null && alerts.Count > 0)
                {
                    ref float height = ref TargetHeightOf(__instance);
                    float walked = aircraft.radarAlt - StepDown;
                    if (height < walked) height = walked;
                    Missile shot = alerts[0];
                    if (noted.Add(shot))
                    {
                        if (noted.Count > 200) noted.Clear();
                        Tracing.Flight("[flight] " + flight.Name + " · evading a radar shot under the combat pilot from " +
                            aircraft.radarAlt.ToString("0") + " m · down to 10 m over the ground, " + StepDown.ToString("0") + " m at a time");
                    }
                }

                if (flight.Home == null) return;

                Vector3 friendly = flight.HomePosition - aircraft.GlobalPosition();
                friendly.y = 0f;
                if (friendly.sqrMagnitude < 1f) return;
                ref Vector3 evade = ref EvadeVectorOf(__instance);
                Vector3 flat = new Vector3(evade.x, 0f, evade.z);
                if (flat.sqrMagnitude < 0.0001f || Vector3.Dot(flat, friendly) >= 0f) return;

                // The other way across the missile's line: the same beam, on
                // the friendly side. Rebuilt the way the game builds it.
                evade = -evade;
                float impact = ImpactTimeOf(__instance);
                float blend = impact > 7f ? 0.3f : 1f;
                Vector3 target = aircraft.transform.position + evade * 1000f +
                    ErrorOf(__instance) * 100f / Mathf.Max(aircraft.skill, 0.01f);
                evadeDestination = Vector3.Lerp(DestinationOf(__instance).ToLocalPosition(), target, blend).ToGlobalPosition();
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }
    }
}

namespace NOrders
{
    // A heat-seeker at one of our jets, with the game's combat pilot flying
    // it: its own heat-seeker evasion does not manoeuvre at all -- throttle
    // to zero (the airbrake) and the flare button held -- and NativeIrEvasionPatch
    // replaces that with idle and our flare strings. This gives it the turn:
    // the shot put on the beam, whichever side faces home when one clearly
    // does, otherwise the side nearer the nose, the way our own beam flew it.
    // And the throttle comes off whether or not the game has noticed the shot
    // (a heat-seeker often gives no warning), which its own evasion never
    // would. Everything else -- height, the fight it was in, the end of it --
    // is the game's, under the G, speed, airbrake and ground guards.
    [HarmonyPatch(typeof(AIPilotCombatModes), "RunEvadeMode")]
    internal static class NativeIrBeamPatch
    {
        private const string Name = "Heat-seeker beam";
        private static readonly AccessTools.FieldRef<PilotBaseState, Aircraft> AircraftOf =
            AccessTools.FieldRefAccess<PilotBaseState, Aircraft>("aircraft");
        private static readonly AccessTools.FieldRef<PilotBaseState, ControlInputs> InputsOf =
            AccessTools.FieldRefAccess<PilotBaseState, ControlInputs>("controlInputs");
        private static readonly HashSet<Missile> noted = new HashSet<Missile>();

        private static void Postfix(AIPilotCombatModes __instance, ref GlobalPosition evadeDestination)
        {
            if (!Guard.Ok(Name) || Tuning.OwnIrEvasion) return;
            try
            {
                Aircraft aircraft = AircraftOf(__instance);
                if (aircraft == null || !(aircraft.autopilot is AutopilotPlane)) return;
                Flight flight = FlightOrders.Of(aircraft);
                if (flight == null || Host.IsFlownByPlayer(flight) || !flight.EvadingInfrared) return;
                Missile missile = flight.ThreatMissile;

                ControlInputs inputs = InputsOf(__instance);
                if (inputs != null && Time.timeSinceLevelLoad < flight.ThrottleCutUntil)
                {
                    inputs.throttle = IrDefence.EvasionThrottle(aircraft);
                    AuxAxis.Apply(aircraft, inputs);
                }

                Vector3 toMissile = missile.transform.position - aircraft.transform.position;
                toMissile.y = 0f;
                if (toMissile.sqrMagnitude < 1f) return;
                toMissile.Normalize();
                Vector3 left = new Vector3(-toMissile.z, 0f, toMissile.x);
                Vector3 forward = aircraft.transform.forward; forward.y = 0f;
                Vector3 beam = Vector3.Dot(forward, left) >= 0f ? left : -left;
                Vector3 home = flight.HomePosition - aircraft.GlobalPosition(); home.y = 0f;
                if (flight.Home != null && home.sqrMagnitude > 1000f * 1000f)
                {
                    float toward = Vector3.Dot(home.normalized, left);
                    if (Mathf.Abs(toward) > 0.25f) beam = toward > 0f ? left : -left;
                }
                evadeDestination = (aircraft.transform.position + beam * 1000f).ToGlobalPosition();
                if (noted.Add(missile))
                {
                    if (noted.Count > 200) noted.Clear();
                    Tracing.Flight("[flight] " + flight.Name + " · heat-seeker at " + UnitConverter.DistanceReading(flight.ThreatRange) +
                        " · the combat pilot beams it, idle, our flares");
                }
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }
    }
}
