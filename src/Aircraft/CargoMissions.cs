using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // Cargo delivery to a chosen place.
    //
    // AIHeloTransportState already knows how to run an approach, pick a
    // touchdown point on ground it can actually use, land, unload, or make a
    // parachute pass -- none of which is worth rewriting. What it will not do
    // is go where it is told: it picks its own destination from the nearest
    // ground enemy or the nearest mission objective.
    //
    // So the state keeps flying the aircraft and its choice is overridden each
    // tick, which is the approach NOCommander takes for the same problem.
    internal static class CargoMissions
    {
        private static readonly Type DestinationType =
            AccessTools.Inner(typeof(AIHeloTransportState), "TransportDestination");

        private static readonly FieldInfo StateAircraft = AccessTools.Field(typeof(PilotBaseState), "aircraft");
        private static readonly FieldInfo TransportMode = AccessTools.Field(typeof(AIHeloTransportState), "transportMode");
        private static readonly FieldInfo Airdrop = AccessTools.Field(typeof(AIHeloTransportState), "airdrop");
        private static readonly FieldInfo Destination = AccessTools.Field(typeof(AIHeloTransportState), "transportDestination");
        private static readonly FieldInfo LastSpotCheck = AccessTools.Field(typeof(AIHeloTransportState), "lastLandingSpotCheck");
        private static readonly FieldInfo ValidMission = DestinationType != null
            ? AccessTools.Field(DestinationType, "validMission") : null;
        private static readonly MethodInfo UpdateTouchdown = DestinationType != null
            ? AccessTools.Method(DestinationType, "UpdateTouchdownPoint", new[] { typeof(float), typeof(Aircraft) })
            : null;
        private static readonly FieldInfo Lz = DestinationType != null
            ? AccessTools.Field(DestinationType, "LZ") : null;
        private static readonly FieldInfo Touchdown = DestinationType != null
            ? AccessTools.Field(DestinationType, "touchdownPoint") : null;
        private static readonly MethodInfo UpdateLzOnUnit = DestinationType != null
            ? AccessTools.Method(DestinationType, "UpdateLZ", new[] { typeof(Aircraft), typeof(Unit) })
            : null;
        private static readonly ConstructorInfo NewDestination = DestinationType != null
            ? AccessTools.Constructor(DestinationType,
                new[] { typeof(GlobalPosition), typeof(GlobalPosition), typeof(float) })
            : null;

        internal static string Report() =>
            "cargo missions:" +
            Line("AIHeloTransportState.transportMode", TransportMode) +
            Line("AIHeloTransportState.airdrop", Airdrop) +
            Line("AIHeloTransportState.transportDestination", Destination) +
            Line("TransportDestination.LZ", Lz) +
            Line("TransportDestination.touchdownPoint", Touchdown) +
            Line("TransportDestination.UpdateTouchdownPoint", UpdateTouchdown) +
            Line("TransportDestination..ctor", NewDestination) +
            Line("TransportDestination.UpdateLZ(Aircraft, Unit)", UpdateLzOnUnit);

        private static string Line(string name, MemberInfo member) =>
            "\n  " + (member != null ? "ok      " : "MISSING ") + name;

        internal static bool Available =>
            TransportMode != null && Airdrop != null && Destination != null &&
            UpdateTouchdown != null && ValidMission != null && NewDestination != null &&
            Lz != null && Touchdown != null;

        // Can this aircraft actually carry anything?
        internal static bool CanCarry(Aircraft aircraft)
        {
            if (aircraft == null || aircraft.weaponStations == null) return false;
            foreach (WeaponStation station in aircraft.weaponStations)
            {
                if (station == null) continue;
                // The station itself declares it carries cargo -- a container
                // or troops sit on one of these, not on a hook or a ramp, which
                // is why looking only for those missed an aircraft that was
                // plainly loaded.
                if (station.Cargo) return true;
                if (station.Weapons == null) continue;
                foreach (Weapon weapon in station.Weapons)
                    if (weapon is SlingloadHook) return true;
            }
            return aircraft.GetComponentInChildren<CargoRamp>(true) != null;
        }

        // How often the landing zone is re-solved. Doing it every physics tick
        // never lets the state settle on an approach: UpdateLZ commits once the
        // aircraft is within three kilometres of its touchdown point, and
        // re-solving continually keeps moving that point out from under it. The
        // aircraft then overflies the zone without dropping, turns back, and
        // thrashes. Three seconds is what NOCommander uses for the same reason.
        private const float ReplanSeconds = 3f;

        // Point the state at our landing zone instead of its own idea of one.
        internal static void Apply(AIHeloTransportState state, Flight flight)
        {
            if (!Available || state == null || flight == null) return;
            if (flight.SupplyShip != null) { ApplySupply(state, flight); return; }

            // Cheap every tick: says what job this is, and that there is one.
            TransportMode.SetValue(state, AIHeloTransportState.TransportMode.LandSuppy);
            Airdrop.SetValue(state, flight.Airdrop);
            state.stateDisplayName = flight.Airdrop ? "Airdropping cargo" : "Delivering cargo";

            // A destination the state built for itself carries its own idea of
            // where to go, and one it never built carries nothing: a default
            // struct reads as slope 0, which UpdateTouchdownPoint takes to mean
            // "this ground is already perfectly flat, keep the point you have"
            // -- and the point it has is the world origin. Either way the first
            // order for a zone starts the destination again from that zone.
            object destination;
            if (!flight.CargoSeeded)
            {
                destination = NewDestination.Invoke(new object[] { flight.CargoPoint, flight.CargoPoint, 90f });
                flight.CargoSeeded = true;
                flight.LastCargoPlan = 0f;
            }
            else
            {
                destination = Destination.GetValue(state);
                if (destination == null) return;
            }
            ValidMission.SetValue(destination, true);
            Destination.SetValue(state, destination);

            if (Time.timeSinceLevelLoad - flight.LastCargoPlan < ReplanSeconds) return;
            flight.LastCargoPlan = Time.timeSinceLevelLoad;

            Aircraft aircraft = StateAircraft?.GetValue(state) as Aircraft;
            if (aircraft == null) return;

            // UpdateLZ is not "go to this point" -- it is the state's standoff
            // solver. Given an enemy position it backs a landing zone away from
            // it, along the inbound track, by as much as ten kilometres, so
            // troops are not set down on top of what they came to fight. Fed a
            // zone the commander picked, it walked that zone back up the track
            // until it sat on the aircraft itself, and the load went out the
            // door where the aircraft happened to be. The zone is not a guess
            // to be refined; it is the order. It stays where it was put.
            //
            // A struct field has to be unboxed, mutated and written back; the
            // methods act on the box, not on the field in place.
            Lz.SetValue(destination, flight.CargoPoint);
            if (flight.Airdrop)
            {
                // A parachute pass wants the point itself. What the ground is
                // like underneath is the cargo's problem, not the approach's.
                Touchdown.SetValue(destination, flight.CargoPoint);
            }
            else
            {
                // A landing has to be put down on something usable, so the
                // state's own search runs -- but close in, around the ordered
                // point rather than around a zone of its choosing.
                // A wing landing together searches only round its own slot,
                // or two aircraft settle on the same patch of ground.
                UpdateTouchdown.Invoke(destination, new object[] { flight.CargoSearch > 0f ? flight.CargoSearch : 120f, aircraft });
            }
            Destination.SetValue(state, destination);
            // Only stamped when we actually re-solved, or the state's own
            // throttling is defeated and it re-plans as fast as we do.
            LastSpotCheck.SetValue(state, Time.timeSinceLevelLoad);
        }

        // A naval supply run. The state already knows how to deliver to a ship
        // -- lead a moving one, come over its wake, and drop when it is lined
        // up -- but picks for itself which ship, from every request the faction
        // has open. Keep it on ours: the same naval-supply job, solved against
        // our ship every tick, and its own search held off so it never
        // chooses another.
        private static void ApplySupply(AIHeloTransportState state, Flight flight)
        {
            if (UpdateLzOnUnit == null) return;
            Aircraft aircraft = StateAircraft?.GetValue(state) as Aircraft;
            Ship ship = flight.SupplyShip;
            if (aircraft == null || ship == null || ship.disabled) return;

            TransportMode.SetValue(state, AIHeloTransportState.TransportMode.NavalSupply);
            Airdrop.SetValue(state, true);
            state.stateDisplayName = "Delivering naval supplies";

            // The container it drops is whatever its current station holds.
            WeaponStation supply = Replenishment.SupplyStation(aircraft);
            if (supply != null && aircraft.weaponManager != null) aircraft.weaponManager.currentWeaponStation = supply;

            object destination = flight.CargoSeeded ? Destination.GetValue(state) : null;
            if (destination == null)
            {
                destination = NewDestination.Invoke(new object[] { ship.GlobalPosition(), ship.GlobalPosition(), 0f });
                flight.CargoSeeded = true;
            }
            ValidMission.SetValue(destination, true);
            UpdateLzOnUnit.Invoke(destination, new object[] { aircraft, ship });
            Destination.SetValue(state, destination);
            LastSpotCheck.SetValue(state, Time.timeSinceLevelLoad);
        }
    }

    [HarmonyPatch(typeof(AIHeloTransportState), "FixedUpdateState")]
    internal static class CargoTargetPatch
    {
        private static readonly FieldInfo StateAircraft = AccessTools.Field(typeof(PilotBaseState), "aircraft");

        // Before the state acts on its own choice, replace it with ours.
        private static void Prefix(AIHeloTransportState __instance)
        {
            if (!Guard.Ok(Name)) return;
            try
            {
                if (!(StateAircraft?.GetValue(__instance) is Aircraft aircraft)) return;
                Flight flight = FlightOrders.Of(aircraft);
                if (flight == null || flight.Mode != FlightMode.Cargo) return;
                CargoMissions.Apply(__instance, flight);
                Trace(aircraft, flight, __instance);
                liftFor = aircraft;
                liftFloor = OverWater(aircraft, flight) && FastMath.Distance(aircraft.GlobalPosition(), flight.CargoPoint) > 3000f ? SeaFloor : 0f;
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }

        // Ibises cruised at 120-138 m/s, 30 m over the sea, pusher at full
        // (the state asks for full forward thrust whenever the landing zone is
        // more than a few hundred metres off). The pusher starved the rotor
        // (see RotorFirstPatch), and the game's autopilot answers a sagging
        // rotor by dropping the collective to nothing -- from 30 m that is the
        // sea. One shed its blades flying straight and level. So the pusher
        // eases to neutral from 60% of top speed and is neutral by 75%, and
        // over open water the transit is flown at 80 m instead of 30.
        private const float SeaFloor = 80f;
        internal static Aircraft liftFor;
        internal static float liftFloor;

        private static void Postfix(AIHeloTransportState __instance)
        {
            Aircraft aircraft = liftFor;
            liftFor = null;
            if (!Guard.Ok(Name) || aircraft == null) return;
            try
            {
                if (!(aircraft.autopilot is AutopilotHelo) || aircraft.radarAlt < 10f || aircraft.speed < 40f || aircraft.IsAutoHoverEnabled()) return;
                AircraftParameters p = aircraft.GetAircraftParameters();
                ControlInputs inputs = aircraft.GetInputs();
                if (p == null || p.maxSpeed <= 0f || inputs == null) return;
                float over = Mathf.InverseLerp(p.maxSpeed * 0.6f, p.maxSpeed * 0.75f, aircraft.speed);
                if (over > 0f) inputs.customAxis1 = Mathf.Min(inputs.customAxis1, Mathf.Lerp(inputs.customAxis1, 0.5f, over));
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }

        // Over water, checked once a second per flight (a linecast).
        private static readonly System.Collections.Generic.Dictionary<Flight, (float at, bool wet)> wetAt = new System.Collections.Generic.Dictionary<Flight, (float, bool)>();
        private static bool OverWater(Aircraft aircraft, Flight flight)
        {
            float now = Time.timeSinceLevelLoad;
            if (wetAt.TryGetValue(flight, out var seen) && now - seen.at < 1f) return seen.wet;
            bool wet = TaskForces.Navigable(aircraft.GlobalPosition(), 1f);
            if (wetAt.Count > 200) wetAt.Clear();
            wetAt[flight] = (now, wet);
            return wet;
        }

        // The rotor's speed against its governed speed, for the trace.
        private static string Rotor(Aircraft aircraft)
        {
            float ratio = RotorFirstPatch.RotorRatio(aircraft);
            return ratio >= 0f ? (ratio * 100f).ToString("0") + "%" : "?";
        }

        // How the game's transport state flies our cargo flights: our own
        // trace stops when it takes over, and Ibises went into the sea under it
        // far from their landing zones. Every four seconds per flight.
        private static readonly System.Collections.Generic.Dictionary<Flight, float> tracedAt = new System.Collections.Generic.Dictionary<Flight, float>();
        private static void Trace(Aircraft aircraft, Flight flight, AIHeloTransportState state)
        {
            if (!Tuning.FlightTrace) return;
            float now = Time.timeSinceLevelLoad;
            if (tracedAt.TryGetValue(flight, out float at) && now - at < 4f) return;
            tracedAt[flight] = now;
            if (tracedAt.Count > 200) tracedAt.Clear();
            ControlInputs inputs = aircraft.GetInputs();
            float sink = aircraft.rb != null ? -aircraft.rb.velocity.y : 0f;
            bool overWater = TaskForces.Navigable(aircraft.GlobalPosition(), 1f);
            Host.LogInfo("[cargo] " + flight.Name + " · " + (state.stateDisplayName ?? "transport") +
                " · alt " + aircraft.radarAlt.ToString("0") + " m · spd " + aircraft.speed.ToString("0") + " · sink " + sink.ToString("0") +
                " · collective " + (inputs != null ? (inputs.throttle * 100f).ToString("0") + "%" : "?") +
                " · pusher " + (inputs != null ? inputs.customAxis1.ToString("0.00") : "?") +
                " · rotor " + Rotor(aircraft) +
                " · top " + (aircraft.GetAircraftParameters()?.maxSpeed.ToString("0") ?? "?") +
                " · LZ " + (FastMath.Distance(aircraft.GlobalPosition(), flight.CargoPoint) / 1000f).ToString("0.0") + " km" +
                (overWater ? " · over water" : "") + " · flight assist " + (aircraft.flightAssist ? "on" : "off"));
        }

        private const string Name = "Cargo missions";
    }

    // The transit height the transport state asks for, raised over open
    // water for our cargo flights (see CargoTargetPatch). The state calls
    // AutoAim from inside its FixedUpdateState, between our prefix and postfix.
    [HarmonyPatch(typeof(AutopilotHelo), nameof(AutopilotHelo.AutoAim),
        new[] { typeof(GlobalPosition), typeof(float), typeof(Vector3), typeof(Vector3), typeof(bool) })]
    internal static class CargoSeaHeightPatch
    {
        private static void Prefix(AutopilotHelo __instance, ref float altitudeHold)
        {
            if (CargoTargetPatch.liftFor == null || __instance.aircraft != CargoTargetPatch.liftFor) return;
            // 200 m is the parachute pass; leave that, and anything already higher.
            if (altitudeHold < CargoTargetPatch.liftFloor) altitudeHold = CargoTargetPatch.liftFloor;
        }
    }
}

namespace NOrders
{
    // The game's transport state answers a missile with chaff and flares and
    // nothing else: a cargo helicopter flew straight on at the shot. Shot at
    // with a radar missile, it notches -- turns across the missile's line,
    // whichever way is nearer its heading, and comes down low into the ground
    // clutter a semi-active radar has to look through -- and picks its
    // delivery up again once the shot is gone. The transport state keeps
    // running throughout; only where it steers changes.
    [HarmonyPatch(typeof(AutopilotHelo), nameof(AutopilotHelo.AutoAim),
        new[] { typeof(GlobalPosition), typeof(float), typeof(Vector3), typeof(Vector3), typeof(bool) })]
    internal static class CargoNotchPatch
    {
        private const string Name = "Cargo notch";
        private const float NotchReach = 1500f;      // bounded, for the helicopter's tilt controller
        private const float NotchHeight = 40f;
        private static readonly System.Collections.Generic.HashSet<Missile> noted = new System.Collections.Generic.HashSet<Missile>();

        private static void Prefix(AutopilotHelo __instance, ref GlobalPosition destination, ref float altitudeHold)
        {
            if (!Guard.Ok(Name)) return;
            try
            {
                Aircraft aircraft = CargoTargetPatch.liftFor;
                if (aircraft == null || __instance.aircraft != aircraft) return;
                Flight flight = FlightOrders.Of(aircraft);
                Missile missile = flight?.ThreatMissile;
                if (flight == null || flight.Threat != FlightThreat.Missile || flight.ThreatIsInfrared || missile == null || missile.disabled) return;
                GlobalPosition here = aircraft.GlobalPosition();
                Vector3 line = here - missile.GetEvasionPoint();
                line.y = 0f;
                if (line.sqrMagnitude < 1f) return;
                Vector3 beam = Vector3.Cross(line.normalized, Vector3.up);
                Vector3 heading = aircraft.transform.forward; heading.y = 0f;
                if (Vector3.Dot(beam, heading) < 0f) beam = -beam;
                destination = here + beam * NotchReach;
                altitudeHold = Mathf.Min(altitudeHold, NotchHeight);
                if (noted.Add(missile))
                {
                    if (noted.Count > 200) noted.Clear();
                    Tracing.Flight("[cargo] " + flight.Name + " · radar shot at " + UnitConverter.DistanceReading(flight.ThreatRange) + ", notching");
                }
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }
    }
}
