using System.Collections.Generic;
using UnityEngine;

namespace NOrders
{
    // A pilot state that flies what it is told and nothing else.
    //
    // The native combat state hunts: it picks its own target and closes, which
    // is why an unmanaged aircraft flies straight at the enemy and dies. This
    // drives aircraft.autopilot directly instead, so a flight holds a route, an
    // orbit or a station until ordered otherwise -- and can sit there radiating
    // as an offboard sensor while the ship that launched it stays silent.
    internal sealed class NavalPilotState : PilotBaseState
    {
        private Flight flight;
        private float orbitPhase;
        private float nextReport;
        private float groundedSince = -1f;
        private Vector3 smoothForward;

        internal static void Install(Pilot pilot, Flight flight)
        {
            if (pilot == null || flight == null || pilot.aircraft == null) return;
            var state = new NavalPilotState { flight = flight, stateDisplayName = Host.ModName };
            state.Initialize(pilot);
            pilot.SwitchStateNew(state);
            Host.LogInfo("[flight] " + flight.Name + " under command · " + flight.Describe() + " · from " +
                (pilot.currentState?.GetType().Name ?? "none") + " · alt " + pilot.aircraft.radarAlt.ToString("0") +
                " m · speed " + pilot.aircraft.speed.ToString("0") + " m/s");
        }

        public override void EnterState(Pilot pilot)
        {
            this.pilot = pilot;
            controlInputs = aircraft.GetInputs();
            parameters = aircraft.GetAircraftParameters();
            FindNearestAirbase();

            // The same handover the native combat state performs. Without it a
            // helicopter arrives from its takeoff state with auto-hover still
            // engaged, and the controls filter then fights the autopilot's
            // collective all the way into the ground.
            aircraft.SetFlightAssistToDefault();
            aircraft.SetGear(deployed: false);
            heloHold = -1f;                         // start from wherever it is
            ControlsFilter filter = aircraft.GetControlsFilter();
            if (filter != null) filter.SetAutoHover(enabled: false);
        }

        private AircraftParameters parameters;
        private float recoveringSince = -1f;

        // The native combat and landing states never touch the throttle -- an
        // AI jet cruises flat out -- so whatever formation keeping left it at
        // would be inherited. Leave it where they expect it.
        public override void LeaveState()
        {
            if (controlInputs != null && aircraft != null && aircraft.autopilot is AutopilotPlane) controlInputs.throttle = 1f;
        }

        // Cruise below full power: fuel lasts, and a lead with wingmen holds a
        // little more back so they have speed in hand to close up with. A lead
        // whose wingmen have fallen well behind their slots throttles back
        // further until they catch up -- never so far that it drops towards
        // its own flying speed.
        private float CruiseThrottle()
        {
            // Speed before economy: an airframe that cannot hold well above its
            // corner speed at cruise power gets full power (and the burner).
            float corner = parameters != null ? parameters.cornerSpeed : 0f;
            if (corner > 0f && aircraft.speed < corner * 1.3f) return 1f;
            float cruise = Tuning.CruiseThrottle;
            if (!Wings.HasFollowers(flight)) return cruise;
            float lead = cruise - 0.05f;
            float behind = Wings.Straggle(flight);
            if (behind > 2000f) lead -= Mathf.Min(0.2f, (behind - 2000f) / 3000f * 0.2f);
            // Not faster than the slowest wingman still behind, plus a little.
            if (Wings.SlowestBehind(flight, out float slowest) && aircraft.speed > slowest + 4f)
                lead -= Mathf.Min(0.25f, (aircraft.speed - slowest - 4f) * 0.03f);
            float minimum = definitionTakeoffSpeed * 1.4f;
            if (minimum > 0f && aircraft.speed < minimum) lead = Mathf.Max(lead, cruise);
            return Mathf.Clamp(lead, 0.45f, 1f);
        }

        private float definitionTakeoffSpeed => aircraft.definition?.aircraftParameters != null
            ? aircraft.definition.aircraftParameters.takeoffSpeed : 0f;

        // The lead's cruise setting, for a wingman's throttle loop to centre on.
        private static float LeadCruise => Tuning.CruiseThrottle - 0.05f;

        public override void UpdateState(Pilot pilot) { }

        public override void FixedUpdateState(Pilot pilot)
        {
            if (flight == null || aircraft == null || aircraft.disabled) return;

            // Fuel outranks every order: an aircraft that runs dry on station is
            // worse than one that broke off early.
            if (flight.Mode != FlightMode.ReturnToBase && !fuelChecker.HasEnoughFuel())
            {
                FlightOrders.ReturnToBase(flight);
                Host.LogInfo("[flight] " + flight.Name + " returning · fuel at " +
                    (aircraft.GetFuelLevel() * 100f).ToString("0") + "%, below the pilot's 20% minimum");
                Host.Say(flight.Name + " · low fuel, returning");
            }

            Report();

            // On the ground and barely moving under our state: whatever put it
            // here, it cannot be flown from the deck by a navigation loop.
            // Hand it back to the game's taxi and takeoff and take it again
            // once it is up.
            if (aircraft.radarAlt < 3f && aircraft.speed < 10f)
            {
                if (groundedSince < 0f) groundedSince = Time.timeSinceLevelLoad;
                else if (Time.timeSinceLevelLoad - groundedSince > 8f)
                {
                    PilotBaseState takeoff = FlightOrders.IsRotary(pilot) ? (PilotBaseState)pilot.AIHeloTakeoffState : pilot.AITaxiState;
                    if (takeoff != null)
                    {
                        Host.LogWarning("[flight] " + flight.Name + " · on the ground under command, back to the native takeoff · " +
                            "gross " + aircraft.GetMass().ToString("0") + " kg of " +
                            (aircraft.definition?.aircraftInfo != null ? aircraft.definition.aircraftInfo.maxWeight.ToString("0") : "?") + " max");
                        flight.Adopted = false;
                        groundedSince = -1f;
                        pilot.SwitchStateNew(takeoff);
                        return;
                    }
                }
            }
            else groundedSince = -1f;

            // Full power where speed matters -- the run-in and the escape --
            // and cruise power everywhere else.
            if (aircraft.autopilot is AutopilotPlane && flight.Mode != FlightMode.Formation)
                controlInputs.throttle = Time.timeSinceLevelLoad < flight.ThrottleCutUntil ? IrDefence.EvasionThrottle(aircraft)
                    : flight.Mode == FlightMode.Strike || flight.Mode == FlightMode.Egress ? 1f
                    : CruiseThrottle();
            Reheat();

            // Energy first. The native pilot hands an aircraft back from a
            // radar evasion low and slow -- it dives to the deck at full power
            // and is reclaimed wherever the shot left it -- and a fighter put
            // straight into a three-kilometre orbit at cruise power from there
            // went into the ground twice. Below corner speed or below a safe
            // height, and not on an attack run: full power, wings near level,
            // a gentle climb toward the ordered height, nothing else until it
            // has the speed back.
            // Wingmen included: one went into the sea chasing its slot. Not on
            // a run-in. The threshold sits at corner speed itself: 1.25 times
            // it put a mod fighter with a 150 m/s corner speed into permanent
            // recovery, wandering off station at full power.
            // On a strike the height is the run's business, but a stall is
            // nobody's: a loaded fighter fell into the sea at 74 m/s straight
            // after its stand-off launch.
            if (aircraft.autopilot is AutopilotPlane)
            {
                float corner = parameters != null ? parameters.cornerSpeed : 0f;
                bool slow = corner > 0f && aircraft.speed < corner * 1.05f;
                bool low = aircraft.radarAlt < 150f && flight.Mode != FlightMode.Egress && flight.Mode != FlightMode.Strike;
                if ((slow || low) && !flight.EvadingInfrared)
                {
                    controlInputs.throttle = 1f;
                    Reheat();
                    Vector3 ahead = Flat(aircraft.transform.forward);
                    if (ahead.sqrMagnitude < 0.01f) ahead = Vector3.forward;
                    float ordered = flight.Altitude;
                    // Climb no more than 300 m above where it is: speed comes first.
                    flight.Altitude = Mathf.Clamp(aircraft.radarAlt + 300f, MinimumClearance, Mathf.Max(ordered, MinimumClearance));
                    if (recoveringSince < 0f) { recoveringSince = Time.timeSinceLevelLoad; Tracing.Flight("[flight] " + flight.Name + " · recovering energy · " + aircraft.speed.ToString("0") + " m/s at " + aircraft.radarAlt.ToString("0") + " m"); }
                    Steer(aircraft.GlobalPosition() + ahead.normalized * 5000f, default, 30f);
                    flight.Altitude = ordered;
                    return;
                }
            }
            recoveringSince = -1f;

            // A heat-seeker inbound: afterburner out (above), the shot on the
            // beam, flares going (IrDefence). Not on a run-in, which is held;
            // a wingman leaves the formation for it and rejoins after.
            if (flight.EvadingInfrared && flight.Mode != FlightMode.Strike)
            {
                if (aircraft.autopilot is AutopilotPlane) controlInputs.throttle = IrDefence.EvasionThrottle(aircraft);
                FlyBeam(flight.ThreatMissile, false);
                return;
            }

            // Covered and holding the task: the pods have the missile, but chaff
            // inside the range that matters still helps break its lock.
            if (flight.StandOn && flight.Threat == FlightThreat.Missile && !flight.ThreatIsInfrared &&
                flight.ThreatRange < 6000f && aircraft.autopilot is AutopilotPlane)
                Chaff(flight.ThreatMissile);

            // A radar shot: full power, the shot on the beam (a Doppler seeker
            // sees the least closing speed there), chaff in bursts, and a
            // gentle descent toward the floor -- not the native pilot's dive
            // to ten metres, which put loaded fighters into the sea.
            if (flight.EvadingRadar && aircraft.autopilot is AutopilotPlane)
            {
                controlInputs.throttle = 1f;
                Reheat();
                Chaff(flight.ThreatMissile);
                FlyBeam(flight.ThreatMissile, true);
                return;
            }
            if (chaffOffAt > 0f && Time.timeSinceLevelLoad >= chaffOffAt) { aircraft.Countermeasures(false, aircraft.countermeasureManager.activeIndex); chaffOffAt = -1f; }

            // A lead whose wing is still forming up circles where it is until
            // they have joined, rather than leaving them behind.
            bool joinable = flight.Mode == FlightMode.Orbit || flight.Mode == FlightMode.Route ||
                flight.Mode == FlightMode.Station || flight.Mode == FlightMode.Jam;
            if (joinable && Wings.JoinUp(flight, out GlobalPosition joinPoint))
            {
                if (aircraft.autopilot is AutopilotPlane) controlInputs.throttle = LeadCruise;
                FlyOrbit(joinPoint, 2500f);
                return;
            }

            switch (flight.Mode)
            {
                case FlightMode.Formation: FlyFormation(); break;
                case FlightMode.Route: FlyRoute(); break;
                case FlightMode.Orbit: FlyOrbit(flight.OrbitCentre); break;
                case FlightMode.Station: FlyStation(); break;
                case FlightMode.Egress: FlyEgress(); break;
                case FlightMode.Jam: FlyJamming(); break;
                // Both hand the aircraft to the native combat pilot; the
                // difference is that a strike has a designated target pinned
                // onto it. Missing this case left nothing driving the aircraft.
                case FlightMode.Strike:
                    if (flight.RunInDone || !(aircraft.autopilot is AutopilotPlane)) HandBackToCombat(pilot);
                    else FlyRunIn(pilot);
                    break;
                case FlightMode.Engage: HandBackToCombat(pilot); break;
                case FlightMode.ReturnToBase: FlyHome(pilot); break;
                // A mode with no arm here writes no control inputs at all, and
                // the aircraft simply falls out of the sky -- which is how both
                // Strike and Cargo were first found. Holding is always wrong
                // for the order, but it is never fatal, and it says so.
                default: FlyOrbit(aircraft.GlobalPosition()); WarnUnflown(); break;
            }
        }

        private FlightMode warnedMode = (FlightMode)(-1);

        private void WarnUnflown()
        {
            if (warnedMode == flight.Mode) return;
            warnedMode = flight.Mode;
            Host.LogWarning("[flight] " + flight.Name + " · nothing flies " + flight.Mode +
                " from this state; holding overhead instead");
        }

        // Periodic trace: if a flight wanders, this says whether it was given
        // the wrong destination or simply refused to fly to the right one.
        private void Report()
        {
            if (!Tuning.FlightTrace || Time.timeSinceLevelLoad < nextReport) return;
            nextReport = Time.timeSinceLevelLoad + 5f;
            Vector3 offset = destination - aircraft.GlobalPosition();
            float bearing = (Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg + 360f) % 360f;
            offset.y = 0f;
            float verticalError = (destination - aircraft.GlobalPosition()).y;
            Host.LogInfo("[flight] " + flight.Name + " · " + flight.Describe() +
                " · dest bearing " + bearing.ToString("000") + "° range " + (offset.magnitude / 1000f).ToString("0.0") +
                " km · alt " + aircraft.radarAlt.ToString("0") + " ordered " + flight.Altitude.ToString("0") +
                " (dest dy " + verticalError.ToString("0") + ")" +
                " · state " + (aircraft.autopilot != null ? aircraft.autopilot.GetType().Name : "none"));
        }

        private void FlyRoute()
        {
            if (flight.Route.Count == 0) { FlyOrbit(aircraft.GlobalPosition()); return; }
            GlobalPosition leg = flight.Route[0];
            // Arrival is horizontal only: altitude is held separately, so a leg
            // must not stay "unreached" because the aircraft is high over it.
            if (Horizontal(aircraft.GlobalPosition(), leg) < 600f)
            {
                flight.Route.RemoveAt(0);
                if (flight.Route.Count == 0 && FlightOrders.LeadInReached(flight, leg)) return;
                if (flight.Route.Count == 0)
                {
                    // Hold where the route ended rather than flying on forever.
                    flight.OrbitCentre = leg;
                    flight.Mode = FlightMode.Orbit;
                    return;
                }
                leg = flight.Route[0];
            }
            Steer(leg);
        }

        // Fly the tangent, not a point on the rim.
        //
        // Chasing a point 50 degrees around the circle put the destination two
        // and a half kilometres away at a wide angle, which is the regime where
        // AutopilotPlane starts adding pull-up and never settles. Steering along
        // the tangent with a long look-ahead keeps the destination far off and
        // nearly dead ahead, which is the cruise case the autopilot handles well.
        private const float LookAhead = 6000f;

        // About seventeen degrees. Steeper than this and a rotary aircraft
        // pitches up hard enough to lose control rather than climb.
        private const float MaxClimbGradient = 0.15f;
        private float heloHold = -1f;               // the height a helicopter is being walked up to

        // How far ahead a rotary aircraft is ever asked to steer. Beyond this
        // the tilt PID saturates and the aircraft wallows rather than flies.
        private const float RotaryLead = 1500f;

        private void FlyOrbit(GlobalPosition centre, float radiusOverride = 0f)
        {
            Vector3 offset = aircraft.GlobalPosition() - centre;
            offset.y = 0f;
            float radius = radiusOverride > 0f ? radiusOverride : Mathf.Max(flight.OrbitRadius, 400f);
            float distance = offset.magnitude;
            Vector3 outward = distance > 1f ? offset / distance : Flat(aircraft.transform.forward);

            // Transit first. Blending a tangent with an inward correction is
            // right on the circle and useless off it: the correction saturates
            // once well outside, leaving a 45-degree drift that takes an age to
            // close. Well outside the area, just go there.
            if (distance > radius * 1.5f)
            {
                Steer(centre + outward * radius, default, SafeBank());
                return;
            }

            // On station: fly the circle. Consistent left-hand circuit, the way
            // a holding pattern is flown.
            Vector3 tangent = new Vector3(-outward.z, 0f, outward.x);
            float error = Mathf.Clamp((distance - radius) / Mathf.Max(radius, 1f), -1f, 1f);
            Vector3 heading = (tangent - outward * error).normalized;
            // Never look further ahead than the circle itself, or a small area
            // gets a look-ahead that points clean outside it.
            float lookAhead = Mathf.Min(LookAhead, radius * 1.5f);
            Steer(aircraft.GlobalPosition() + heading * lookAhead, default, SafeBank());
        }

        // The afterburner. On airframes whose engines carry a parasitic thrust
        // loss (several mod jets), the game lights the burner only when the
        // player's own auxiliary axis is pushed past a third; no AI pilot ever
        // does, so those jets fly at a fraction of their thrust under any AI
        // and mush into the sea with a full load. Our state pushes the axis
        // whenever it wants full power and releases it otherwise, so the
        // heat-seeker throttle cut still cools the engine.
        private void Reheat()
        {
            if (!(aircraft.autopilot is AutopilotPlane)) return;
            controlInputs.customAxis1 = controlInputs.throttle >= 0.98f ? 1f : 0f;
        }

        // How hard to bank with the speed in hand. A fighter well above its
        // corner speed can hold the seventy degrees the transit code allows; one
        // near it is levelled off, or it holds the turn until it stalls -- seven
        // fighters went into the sea from a routine orbit in one run.
        private float SafeBank()
        {
            float corner = parameters != null ? parameters.cornerSpeed : 0f;
            if (corner <= 0f || !(aircraft.autopilot is AutopilotPlane)) return 60f;
            float margin = (aircraft.speed - corner) / corner;          // 0 at corner speed
            return Mathf.Lerp(25f, 70f, Mathf.Clamp01(margin / 0.6f));
        }

        private static Vector3 Flat(Vector3 value)
        {
            value.y = 0f;
            return value.sqrMagnitude > 0.001f ? value.normalized : Vector3.forward;
        }

        // Out low and away. Height is the thing a departing aircraft can trade
        // for survival, so the egress leg ignores the flight's ordered altitude
        // and runs at the egress height instead.
        // Put the missile on the beam: ninety degrees off its bearing, on
        // whichever side is the smaller turn, holding the height it has --
        // no climb to bleed the speed, no dive into the ground -- at a bank
        // that keeps the speed on (the first cut, idle and eighty degrees,
        // left fighters wallowing at eighty metres a second). From abeam
        // the seeker sees the flares well apart from the aircraft and the
        // engines at their coolest aspect.
        private float nextChaff, chaffOffAt = -1f;

        // Chaff against a radar shot: the station the game picks for the
        // seeker type, fired in short bursts.
        private static readonly System.Reflection.FieldInfo Stations = HarmonyLib.AccessTools.Field(typeof(CountermeasureManager), "countermeasureStations");

        private static int ChaffStation(CountermeasureManager cm, Missile missile)
        {
            string seeker = missile.GetSeekerType();
            if (seeker != "ARH" && seeker != "SARH") return -1;
            if (!(Stations?.GetValue(cm) is System.Collections.IList list)) return -1;
            for (int i = 0; i < list.Count && i < 255; i++)
            {
                object station = list[i];
                if (station == null) continue;
                var threats = HarmonyLib.AccessTools.Field(station.GetType(), "threatTypes")?.GetValue(station) as System.Collections.Generic.List<string>;
                if (threats == null || !threats.Contains(seeker)) continue;
                var carried = HarmonyLib.AccessTools.Field(station.GetType(), "countermeasures")?.GetValue(station) as System.Collections.IList;
                if (carried == null) continue;
                foreach (object item in carried)
                    if (item != null && !(item is RadarJammer)) return i;
            }
            return -1;
        }

        private void Chaff(Missile missile)
        {
            CountermeasureManager cm = aircraft.countermeasureManager;
            if (cm == null || missile == null) return;
            float now = Time.timeSinceLevelLoad;
            if (chaffOffAt > 0f && now >= chaffOffAt) { aircraft.Countermeasures(false, cm.activeIndex); chaffOffAt = -1f; }
            if (now < nextChaff) return;
            nextChaff = now + 1.2f;
            // Chaff, by what the station carries. The game's own choice takes the
            // first station that answers the seeker type, and the built-in radar
            // jammer answers ARH and SARH as chaff does: on airframes listing it
            // first, every "chaff" burst was a jammer pulse and no chaff went
            // out. The jammer is run separately (MissileJamming.SelfProtection).
            int station = ChaffStation(cm, missile);
            if (station < 0) return;
            cm.activeIndex = (byte)station;
            aircraft.Countermeasures(true, cm.activeIndex);
            chaffOffAt = now + 0.15f;
        }

        private void FlyBeam(Missile missile, bool descend)
        {
            Vector3 toMissile = Flat(missile.transform.position - aircraft.transform.position);
            if (toMissile.sqrMagnitude < 1f) toMissile = Flat(-aircraft.transform.forward);
            toMissile.Normalize();
            Vector3 left = new Vector3(-toMissile.z, 0f, toMissile.x);
            Vector3 forward = Flat(aircraft.transform.forward);
            Vector3 beam = Vector3.Dot(forward, left) >= 0f ? left : -left;
            // Whichever beam faces home, when one clearly does. Taking the side
            // nearer the nose every time let a string of shots walk a transport
            // deeper and deeper into enemy ground, one beam turn at a time.
            Vector3 home = Flat(flight.HomePosition - aircraft.GlobalPosition());
            if (home.sqrMagnitude > 1000f * 1000f)
            {
                float toward = Vector3.Dot(home.normalized, left);
                if (Mathf.Abs(toward) > 0.25f) beam = toward > 0f ? left : -left;
            }
            float ordered = flight.Altitude;
            flight.Altitude = descend
                ? Mathf.Max(Mathf.Min(aircraft.radarAlt * 0.7f, ordered), Tuning.RadarEvasionFloor)
                : Mathf.Max(Mathf.Min(aircraft.radarAlt, ordered), MinimumClearance);
            Steer(aircraft.GlobalPosition() + beam * 4000f, default, Mathf.Min(60f, SafeBank()));
            flight.Altitude = ordered;
        }

        private void FlyEgress()
        {
            float ordered = flight.Altitude;
            flight.Altitude = Mathf.Min(ordered, Tuning.EgressAltitude);
            Steer(flight.EgressPoint);
            flight.Altitude = ordered;
        }

        // Hold off the emitter and keep the pod on it. The pod switches itself
        // off in LateUpdate unless Fire is called again, so jamming has to be
        // asserted every frame rather than commanded once.
        private void FlyJamming()
        {
            Unit target = flight.Target;
            if (Host.Dead(target)) { FlyOrbit(aircraft.GlobalPosition()); return; }

            // The flight's own task area, flown at its ordered height, if the
            // pods reach every target from all of it; if not, the area moves
            // -- as far back on our side as still keeps every target in reach
            // from anywhere on its orbit. Too spread for one area to reach
            // them all, it covers as many as it can in priority order; the
            // rest get what reaches them.
            GlobalPosition was = flight.OrbitCentre;
            if (!JamPlanner.CurrentCovers(flight) && JamPlanner.MoveToIdeal(flight) &&
                FastMath.Distance(was, flight.OrbitCentre) > 500f && Time.timeSinceLevelLoad >= nextJamAreaNote)
            {
                nextJamAreaNote = Time.timeSinceLevelLoad + 30f;
                Host.Say(flight.Name + " · jamming area moved to keep its targets in reach");
            }
            FlyOrbit(flight.OrbitCentre);

            // The pods themselves are aimed by MissileJamming: each task
            // target a pod, and missiles fired at us taken first.
        }

        private float nextJamAreaNote;
        private void FlyStation()
        {
            Ship ship = flight.StationAnchor;
            if (ship == null && (flight.Home == null || flight.Home.disabled)) { FlyOrbit(aircraft.GlobalPosition()); return; }
            // A land field does not move: work an area over it.
            if (ship == null) { FlyOrbit(flight.HomePosition); return; }
            // The anchor moves with the ship, so the flight keeps company
            // instead of orbiting the spot the ship used to be.
            Vector3 forward = ship.transform.forward; forward.y = 0f; forward.Normalize();
            Vector3 right = new Vector3(forward.z, 0f, -forward.x);
            GlobalPosition anchor = ship.GlobalPosition()
                + right * flight.StationOffset.x + forward * flight.StationOffset.z;
            FlyOrbit(anchor);
        }

        // The two AutoAim overloads are implemented by different autopilots:
        // AutopilotPlane overrides the nine-argument one, AutopilotHelo and
        // AutopilotTiltwing the five-argument one. Everything else falls through
        // to an empty virtual on the base class, which issues no control inputs
        // at all -- a fixed-wing given the helicopter call simply coasts on
        // stale inputs until it stalls and goes in. Dispatch on the real type.
        // A wingman's slot, in the lead's frame, flown with a throttle loop on
        // the along-track gap. The jet autopilot does not match a target's
        // speed -- its velocity input only biases the climb -- so speed is held
        // with power; the helicopter autopilot does take a velocity to match.
        private void FlyFormation()
        {
            Flight lead = Wings.LeadOf(flight);
            if (!Wings.Slot(flight, out GlobalPosition slot, out Vector3 forward, out Vector3 velocity))
            {
                // No one to fly on: hold here until given something to do.
                flight.OrbitCentre = aircraft.GlobalPosition();
                flight.Mode = FlightMode.Orbit;
                return;
            }
            flight.Altitude = Wings.SlotAltitude(flight);

            // The lead's heading, smoothed over a second or two: its raw
            // velocity jitters, and a slot that jitters with it has the
            // wingman rolling from side to side chasing it.
            float blend = 1f - Mathf.Exp(-Time.fixedDeltaTime / 1.5f);
            smoothForward = smoothForward.sqrMagnitude < 0.5f ? forward : Vector3.Slerp(smoothForward, forward, blend).normalized;
            // Re-express the slot in the smoothed frame, about whatever it hangs
            // off: the lead for a wingman, the escorted lead for an escort.
            Flight anchorFlight = Wings.IsWingman(flight) ? Wings.LeadOf(flight) : Wings.EscortedLead(flight);
            Aircraft anchorAircraft = anchorFlight?.Aircraft;
            if (anchorAircraft != null)
            {
                Vector3 lateral = Vector3.Cross(Vector3.up, forward);
                Vector3 smoothLateral = Vector3.Cross(Vector3.up, smoothForward);
                GlobalPosition anchor = anchorAircraft.GlobalPosition();
                Vector3 fromAnchor = slot - anchor;
                float right = Vector3.Dot(fromAnchor, lateral), ahead = Vector3.Dot(fromAnchor, forward);
                slot = anchor + smoothLateral * right + smoothForward * ahead;
            }
            forward = smoothForward;

            // Too close to anyone in the group: step up and out of the way
            // before anything else. Close formation makes this worth having.
            // Never while still slow off the deck: the aircraft are close there
            // by necessity, and backing off the power then only sinks them.
            bool flying = definitionTakeoffSpeed <= 0f || aircraft.speed > definitionTakeoffSpeed * 1.4f;
            if (flying && Crowded(out Vector3 clear))
            {
                if (aircraft.autopilot is AutopilotPlane) controlInputs.throttle = 1f;
                Steer(aircraft.GlobalPosition() + (forward * 1500f + clear * 400f) + Vector3.up * 150f, velocity);
                return;
            }

            Vector3 gap = slot - aircraft.GlobalPosition();
            gap.y = 0f;
            float along = Vector3.Dot(gap, forward);          // positive: behind the slot
            float distance = gap.magnitude;

            if (!(aircraft.autopilot is AutopilotPlane))
            {
                FlyRotaryFormation(slot, forward, velocity, anchorFlight);
                return;
            }

            // Aim down the lead's track from the slot rather than at the slot
            // itself, so the aircraft settles alongside instead of overshooting
            // a point and circling back to it; further out, lead it more.
            GlobalPosition aim = slot + forward * Mathf.Clamp(distance * 0.5f + 1500f, 1500f, 6000f);
            float speedGap = Vector3.Dot(velocity, forward) - Vector3.Dot(aircraft.rb != null ? aircraft.rb.velocity : Vector3.zero, forward);
            float power = !flying || (distance > 3000f && along > 0f) ? 1f
                : Mathf.Clamp(LeadCruise + along * 0.0006f + speedGap * 0.02f, 0.35f, 1f);
            controlInputs.throttle = power;
            // Bank in proportion to the error: a few metres off the slot or a
            // few degrees off the lead's heading is a touch of bank, a long way
            // off is a hard turn. A fixed limit was either too much for the
            // small corrections -- rolling side to side -- or too little to rejoin.
            Vector3 across = Vector3.Cross(Vector3.up, forward);
            float sideways = Mathf.Abs(Vector3.Dot(gap, across));
            Vector3 track = aircraft.rb != null ? aircraft.rb.velocity : aircraft.transform.forward;
            track.y = 0f;
            float offHeading = track.sqrMagnitude > 1f ? Vector3.Angle(track, forward) : 0f;
            float bank = distance > 2000f ? 70f : Mathf.Clamp(6f + sideways * 0.08f + offHeading * 1.5f, 6f, 70f);
            Steer(aim, velocity, bank);
        }

        private bool Crowded(out Vector3 away)
        {
            away = Vector3.zero;
            var group = new List<Flight>(Wings.Group(flight));
            Flight escorted = Wings.EscortedLead(flight);
            if (escorted != null) group.AddRange(Wings.Group(escorted));
            foreach (Flight other in group)
            {
                if (other == flight || other.Aircraft == null || other.Aircraft.disabled) continue;
                Vector3 offset = aircraft.GlobalPosition() - other.Aircraft.GlobalPosition();
                if (offset.sqrMagnitude > 55f * 55f) continue;
                offset.y = 0f;
                away = offset.sqrMagnitude > 1f ? offset.normalized : aircraft.transform.right;
                return true;
            }
            return false;
        }

        internal enum Rotary { Plain, Compound, Tiltwing }
        private static readonly System.Reflection.FieldInfo CompoundHelo = HarmonyLib.AccessTools.Field(typeof(AutopilotHelo), "compoundHelo");

        internal static Rotary RotaryKind(Aircraft aircraft)
        {
            if (aircraft?.autopilot is AutopilotTiltwing) return Rotary.Tiltwing;
            if (aircraft?.autopilot is AutopilotHelo helo && CompoundHelo != null && (bool)CompoundHelo.GetValue(helo)) return Rotary.Compound;
            return Rotary.Plain;
        }

        // A rotary wingman: follow the leader.
        //
        // A slot hung off the lead's nose swings round with every turn it
        // makes, and one circling a station area swung it round a circle a
        // wingman could only chase. So along the lead's own track instead: the
        // point it passed a slot's distance back, offset to the slot's side,
        // with the way it was going there. Slow or hovering, the slot beside
        // it as before.
        //
        // And the speed. A tiltwing's autopilot asks for about 1.7·√d m/s to
        // a destination d metres off, and only blends into forward flight past
        // 500 m; a compound helicopter's pusher settles near d/20 m/s. Aimed
        // at a point a few hundred metres off, a Tarantula wingman stayed in
        // hover mode while its lead converted and left it, and an Ibis idled
        // its pusher. So each is aimed along the track at the distance that
        // asks for the speed it needs: the lead's, and more while behind.
        // A plain helicopter keeps the slot logic that works for the Chicane.
        private void FlyRotaryFormation(GlobalPosition slot, Vector3 forward, Vector3 velocity, Flight anchorFlight)
        {
            Aircraft anchor = anchorFlight?.Aircraft;
            GlobalPosition here = aircraft.GlobalPosition();
            GlobalPosition anchorAt = anchor != null ? anchor.GlobalPosition() : slot;
            Vector3 leadVelocity = anchor != null && anchor.rb != null ? anchor.rb.velocity : velocity;
            leadVelocity.y = 0f;
            float leadSpeed = leadVelocity.magnitude;

            Vector3 lateral = Vector3.Cross(Vector3.up, forward);
            Vector3 fromAnchor = slot - anchorAt;
            fromAnchor.y = 0f;
            float right = Vector3.Dot(fromAnchor, lateral), ahead = Vector3.Dot(fromAnchor, forward);

            GlobalPosition target = slot;
            Vector3 dir = forward;
            if (leadSpeed > 12f && Wings.TrailPoint(anchorFlight, Mathf.Max(-ahead, 60f), out GlobalPosition crumb, out Vector3 crumbForward))
            {
                dir = crumbForward;
                target = crumb + Vector3.Cross(Vector3.up, crumbForward) * right;
            }
            Vector3 gap = target - here;
            gap.y = 0f;
            float distance = gap.magnitude;
            float along = Vector3.Dot(gap, dir);            // positive: behind its point

            Rotary kind = RotaryKind(aircraft);
            if (kind == Rotary.Plain)
            {
                float leadIn = Mathf.Clamp(distance * 0.5f + 400f, 400f, RotaryLead);
                float aheadBy = Mathf.Clamp(leadIn + Mathf.Min(along, 0f), 0f, leadIn);
                Steer(target + dir * aheadBy, velocity, 70f, dir);
                return;
            }

            // The speed wanted: the lead's, more while behind, less while
            // ahead; well adrift, a good deal more.
            float want = leadSpeed + Mathf.Clamp(along * 0.04f, -20f, 30f);
            if (distance > 1500f && along > 0f) want = Mathf.Max(want, leadSpeed + 30f);
            want = Mathf.Max(want, 0f);
            float reach = kind == Rotary.Tiltwing ? (want / 1.7f) * (want / 1.7f) : want * 20f;
            // A tiltwing also scales its height hold down to nothing close in
            // and slow -- how a wingman aimed near sank towards the ground --
            // so never nearer than where that hold is whole.
            reach = Mathf.Clamp(reach, kind == Rotary.Tiltwing ? 400f : 150f, 8000f);

            // Towards the track a little ahead of its point, so it closes onto
            // the line rather than chasing the point itself.
            GlobalPosition toward = target + dir * Mathf.Clamp(distance * 0.5f, 100f, 800f);
            Vector3 heading = toward - here;
            heading.y = 0f;
            heading = heading.sqrMagnitude > 1f ? heading.normalized : dir;
            // Nose where it is going: the relative-velocity yaw this autopilot
            // uses is nothing for a wingman keeping pace.
            Steer(here + heading * reach, Vector3.zero, 70f, heading, reach);
        }

        private void Steer(GlobalPosition target, Vector3 velocity = default, float bank = 70f, Vector3 nose = default, float reachOverride = 0f)
        {
            Autopilot autopilot = aircraft.autopilot;
            if (autopilot == null) return;

            float aboveGround = Mathf.Max(flight.Altitude, MinimumClearance);

            if (autopilot is AutopilotPlane)
            {
                // Fixed wing: the destination's own height carries the altitude,
                // and altitudeHold is consulted only for terrain following.
                // Bank is held well short of the 180 degrees the combat state
                // allows, since this is transit rather than evasion.
                bool followTerrain = aboveGround < 400f;
                GlobalPosition point = AtAltitude(target, aboveGround);
                destination = point;
                autopilot.AutoAim(point, aimVelocity: true, ignoreCollisions: false, runwayAlign: false,
                    effort: 1f, bankAllowed: bank, followTerrain: followTerrain,
                    altitudeHold: aboveGround, targetVelocity: velocity);
                return;
            }

            // Rotary and tiltwing read altitudeHold as the height to hold above
            // the ground: it is fed into TerrainWaypoint and compared against
            // radarAlt. The native helo state passes minimumRadarAlt plus a
            // desiredHeight it slews by tens of metres a second -- it never
            // steps it.
            //
            // That gradualness is load-bearing. TerrainWaypoint places the
            // waypoint only max(speed, 100) * 6 metres ahead, so at low speed
            // that is 600 m; asking for 600 m of height there is a 45-degree
            // climb, and the attitude controller answers by standing the
            // aircraft on its tail until it departs. Ask for no more height
            // than the aircraft can reach at a sane gradient from where it is,
            // and let it walk up to the ordered altitude over successive frames.
            float floor = parameters != null ? parameters.minimumRadarAlt : 0f;
            float lookAhead = Mathf.Max(aircraft.speed, 100f) * 6f;
            float wanted = Mathf.Clamp(floor + aboveGround, floor, floor + 1000f);

            // Walk the held height toward the ordered one at the native
            // state's own pace: up at 10 m/s, down at 20. Asking instead for a
            // fixed margin above wherever it is now set a target that ran ahead
            // as fast as it climbed; low and slow after evading, it pitched up
            // after it, bled off its speed, sank, and went round again.
            if (heloHold < 0f) heloHold = Mathf.Max(aircraft.radarAlt, floor);
            float step = (heloHold < wanted ? 10f : 20f) * Time.deltaTime;
            heloHold = Mathf.MoveTowards(heloHold, wanted, step);
            // Never far ahead of where it actually is, and hardly any climb at
            // all until it has flying speed: height from collective alone at a
            // hover is what the attitude controller handles worst.
            float margin = aircraft.speed < 25f ? 25f : lookAhead * MaxClimbGradient;
            heloHold = Mathf.Min(heloHold, Mathf.Max(aircraft.radarAlt, floor) + margin);
            float commanded = Mathf.Clamp(heloHold, floor, floor + 1000f);

            // Bounded steering point.
            //
            // AutopilotHelo feeds the raw horizontal offset to the destination
            // straight into a tilt PID. The native state hands it a target a
            // few kilometres away at most; a task area tens of kilometres off
            // saturates that PID at maximum tilt and the aircraft oscillates
            // instead of flying. Aim at a point a bounded distance along the
            // bearing instead -- it moves with the aircraft, so the course is
            // unchanged, but the error the PID sees stays in its working range.
            //
            // The same distance is its speed: the tilt PID leans in proportion
            // to the offset, so a lead aimed 1.5 km out flies as fast as its
            // wingmen can, and one that fell behind never closed. A rotary lead
            // with a wing strung out aims shorter, and slows, until they are in.
            GlobalPosition here = aircraft.GlobalPosition();
            Vector3 bearing = target - here;
            bearing.y = 0f;
            float span = bearing.magnitude;
            //
            // Only a plain helicopter slows this way, and only by so much: a
            // tiltwing aimed short falls out of forward flight and off its wing
            // lift -- a Tarantula lead sank from 580 m to 18 m waiting -- and a
            // compound's pusher idles.
            float reach = reachOverride > 0f ? reachOverride : RotaryLead;
            if (reachOverride <= 0f && Wings.HasFollowers(flight) && RotaryKind(aircraft) == Rotary.Plain)
            {
                float behind = Mathf.Max(Wings.Straggle(flight), Wings.WorstOffSlot(flight) * 0.7f);
                if (behind > 300f) reach = Mathf.Lerp(RotaryLead, RotaryLead * 0.6f, Mathf.Clamp01((behind - 300f) / 1500f));
            }
            GlobalPosition aim = span > reach
                ? here + bearing / span * reach
                : target;

            destination = aim;
            autopilot.AutoAim(aim, commanded, nose, velocity, followTerrain: true);
        }

        // Only these autopilots actually implement an AutoAim; anything else
        // would be flown by a method with an empty body.
        internal static bool CanBeFlown(Aircraft aircraft) =>
            aircraft != null && (aircraft.autopilot is AutopilotPlane
                || aircraft.autopilot is AutopilotHelo
                || aircraft.autopilot is AutopilotTiltwing);

        // Ground clearance at the destination, the way Autopilot.TerrainWaypoint
        // does it: sample terrain, fall back to sea level, then add the ordered
        // height above it.
        //
        // GlobalPosition is a large-world coordinate and is NOT interchangeable
        // with transform.position -- building a Vector3 from its components and
        // calling ToGlobalPosition converts a second time, displacing every
        // destination by the floating-origin offset. Convert through a delta
        // from the aircraft, and return the point raised in its own space.
        private GlobalPosition AtAltitude(GlobalPosition point, float aboveGround)
        {
            Vector3 world = aircraft.transform.position + (point - aircraft.GlobalPosition());
            float ground = Datum.LocalSeaY;
            if (Physics.Linecast(new Vector3(world.x, ground + 5000f, world.z),
                                 new Vector3(world.x, ground - 5000f, world.z),
                                 out RaycastHit hit,
                                 (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ExclusionZonesMask))
                ground = Mathf.Max(hit.point.y, Datum.LocalSeaY);
            float wanted = ground + Mathf.Max(aboveGround, MinimumClearance);
            return point + Vector3.up * (wanted - world.y);
        }

        // Never command a flight lower than this above the ground, whatever is
        // selected: the autopilot needs room to arrest a descent.
        private static float MinimumClearance => Tuning.MinimumClearance;

        // Set up the attack: down to a height the weapon can be released from,
        // toward the target, and hand over once inside release range.
        //
        // Too high to line up in the range left, or simply too close, it first
        // opens out -- toward friendly lines, the side its home is on, rather
        // than on past the target -- to a set-up point far enough off to
        // descend and turn in, and runs in from there. The set-up is a stage
        // that completes, so it cannot flip between opening out and turning in.
        private void FlyRunIn(Pilot pilot)
        {
            Unit target = flight.Target;
            FactionHQ hq = aircraft.NetworkHQ;
            WeaponStation station = FlightOrders.NamedStation(aircraft, flight.PreferredWeapon) ??
                (target != null ? FlightOrders.BestStationFor(aircraft, target) : null);
            if (FlightOrders.IsAirTarget(target))
            {
                if (Bvr(flight, station)) FlyBvr(pilot, target, station);
                else CompleteRunIn(pilot, "air target");
                return;
            }
            // A missile we launch ourselves: no dive, no pressing in.
            WeaponInfo weapon = station?.WeaponInfo;
            if (weapon != null && weapon.missile && !weapon.laserGuided && !weapon.bomb && station.Ammo > 0 &&
                !Host.Dead(target) && hq != null && hq.TryGetKnownPosition(target, out GlobalPosition seen))
            {
                FlyStandoffLaunch(pilot, target, seen, station);
                return;
            }
            if (Host.Dead(target) || hq == null || !hq.TryGetKnownPosition(target, out GlobalPosition known) ||
                !FlightOrders.RunInFor(station?.WeaponInfo, out float height, out float release, out bool straight))
            {
                CompleteRunIn(pilot, "no run-in needed");
                return;
            }

            GlobalPosition here = aircraft.GlobalPosition();
            float range = Horizontal(known, here);
            bool low = aircraft.radarAlt <= height + Mathf.Max(250f, height * 0.25f);
            if (Time.timeSinceLevelLoad - flight.RunInStarted > 240f) { CompleteRunIn(pilot, "run-in timed out"); return; }

            // For a straight run: how far the track is off the target's bearing.
            Vector3 toTarget = known - here; toTarget.y = 0f;
            Vector3 track = aircraft.rb != null ? aircraft.rb.velocity : aircraft.transform.forward;
            track.y = 0f;
            float offTrack = toTarget.sqrMagnitude > 1f && track.sqrMagnitude > 1f ? Vector3.Angle(track, toTarget) : 0f;
            bool lined = !straight || offTrack <= 8f;

            if (!flight.SettingUp && (range < release * 0.5f || (!low && range < release) ||
                (straight && !lined && range < release * 0.8f)))
            {
                flight.SettingUp = true;
                Tracing.Flight("[flight] " + flight.Name + " · opening out to set up the run");
            }

            GlobalPosition aim = known;
            if (flight.SettingUp)
            {
                Vector3 friendly = flight.HomePosition - known;
                friendly.y = 0f;
                if (friendly.sqrMagnitude < 1f) friendly = here - known;
                friendly.y = 0f;
                if (friendly.sqrMagnitude < 1f) friendly = -aircraft.transform.forward;
                // A straight run needs room to settle on the line before handover.
                float outward = straight ? 1.8f : 1.3f;
                GlobalPosition setUp = known + friendly.normalized * release * outward;
                if (Horizontal(setUp, here) < 1500f || (low && range >= release * (outward - 0.15f)))
                {
                    flight.SettingUp = false;
                    Tracing.Flight("[flight] " + flight.Name + " · turning in for the run");
                }
                else aim = setUp;
            }
            else if (low && lined && range < release) { CompleteRunIn(pilot, straight ? "lined up" : "in position"); return; }

            float ordered = flight.Altitude;
            flight.Altitude = height;
            Steer(aim);
            flight.Altitude = ordered;
        }

        // A missile strike with nothing shooting at us: fly at the ordered
        // height to launch range and no closer, turn the target into the
        // missile's launch cone, fire -- as the native pilot fires, through
        // the weapon manager, a salvo 2.5 s apart until the game's own count
        // of missiles needed for this target is away -- and leave. The native
        // pilot, handed the attack, dove at the target to bring it into the
        // cone and pressed on into the defences' reach; an EW-25 went from
        // 4 km to 900 m and lost its track over the horizon. Anything it
        // cannot launch at within a minute of reaching range goes to the
        // native pilot as before.
        // An air target with a radar-guided missile: our own run-in, not the
        // combat pilot's.
        internal static bool Bvr(Flight flight, WeaponStation station)
        {
            if (flight?.Aircraft == null || !(flight.Aircraft.autopilot is AutopilotPlane)) return false;
            if (!(flight.Target is Aircraft) || station?.WeaponInfo == null || !station.WeaponInfo.missile || station.Ammo <= 0) return false;
            string seeker = ShotDisciplinePatch.Guidance(station.WeaponInfo);
            return seeker == "ARH" || seeker == "SARH";
        }

        // Beyond visual range: climb above the target while closing -- thinner
        // air and the height to fall through reach further, so it launches
        // sooner -- and launch at that range. Only with the way in clear of
        // known air defence and enemy forces, and nothing already shot at us,
        // press in to the no-escape range first. Both ranges are the game's
        // own (Missile.CalcRange, what the HUD shows a pilot), worked out
        // from our speed and height and the target's as they are now. After
        // the salvo the combat pilot has it, supporting the shot as it would.
        private const float BvrLoft = 1500f, BvrMargin = 0.95f;
        // Heights above sea level: a shot needing more than BvrTooHigh is not
        // climbed for; the flight holds at BvrHold and closes until the height
        // it needs is down to BvrHold, then climbs to that and fires.
        private const float BvrTooHigh = 10000f, BvrHold = 8000f;
        private bool bvrClosingIn;
        private string bvrNote;
        private float bvrRangeAt = -1f, bvrMax, bvrNoEscape, bvrLoft;

        private void FlyBvr(Pilot pilot, Unit target, WeaponStation station)
        {
            FactionHQ hq = aircraft.NetworkHQ;
            if (hq == null || Host.Dead(target) || !hq.TryGetKnownPosition(target, out GlobalPosition known)) { CompleteRunIn(pilot, "no track on the air target"); return; }
            WeaponInfo info = station.WeaponInfo;
            TargetRequirements needs = info.targetRequirements;
            GlobalPosition here = aircraft.GlobalPosition();
            float ground = here.y - aircraft.radarAlt;
            float range = FastMath.Distance(here, known);
            if (Time.timeSinceLevelLoad - flight.RunInStarted > 240f) { CompleteRunIn(pilot, "intercept timed out"); return; }
            if (range < needs.minRange * 1.1f) { CompleteRunIn(pilot, "inside minimum range"); return; }

            // How high to be: the lowest height from which the game says the
            // shot reaches the target from here, found by trying heights up to
            // the ceiling -- climb to that and fire, rather than closing. None
            // high enough yet: as high as we can go while closing, and the
            // needed height falls as the range does.
            float ceiling = Mathf.Max(BvrTooHigh, ground + MinimumClearance);
            Missile prefab = info.weaponPrefab != null ? info.weaponPrefab.GetComponent<Missile>() : null;
            if (Time.timeSinceLevelLoad - bvrRangeAt >= 1f || bvrRangeAt < 0f)
            {
                bvrRangeAt = Time.timeSinceLevelLoad;
                bvrMax = needs.maxRange;
                bvrNoEscape = needs.maxRange * 0.5f;
                bvrLoft = Mathf.Max(known.y + BvrLoft, here.y);
                if (prefab != null)
                {
                    try
                    {
                        bvrMax = prefab.CalcRange(aircraft.speed, here.y, known.y, range, target.speed, out bvrNoEscape);
                        float needed = -1f;
                        for (float h = Mathf.Max(here.y, ground + MinimumClearance); h <= ceiling + 1f; h += 500f)
                            if (prefab.CalcRange(aircraft.speed, h, known.y, range, target.speed, out _) * BvrMargin >= range) { needed = h; break; }
                        // Too high to climb for: hold at 8 km and close until it
                        // is not; 8 to 10 km, climb for it as found.
                        if (needed < 0f || needed > BvrTooHigh) bvrClosingIn = true;
                        else if (needed <= BvrHold) bvrClosingIn = false;
                        bvrLoft = bvrClosingIn ? Mathf.Min(BvrHold, Mathf.Max(here.y, known.y + BvrLoft)) : needed + 100f;
                    }
                    catch { bvrMax = needs.maxRange; bvrNoEscape = needs.maxRange * 0.5f; }
                }
            }
            // Never lower than now (height given up is range given up), and
            // not above the hold height while closing in.
            float loft = Mathf.Clamp(Mathf.Max(bvrLoft, bvrClosingIn ? Mathf.Min(here.y, BvrHold) : here.y), ground + MinimumClearance, ceiling);
            float extended = Mathf.Max(bvrMax * BvrMargin, needs.minRange * 1.5f);
            float noEscape = Mathf.Clamp(bvrNoEscape, needs.minRange * 1.5f, extended);

            // Pressing in to the no-escape range: only if the way there is
            // clear and nothing is already coming at us.
            Vector3 toTarget = known - here;
            GlobalPosition pressTo = here + toTarget * Mathf.Clamp01((range - noEscape) / Mathf.Max(range, 1f));
            string why = null;
            bool clear = flight.Threat != FlightThreat.Missile && !AirDefence.Threatens(aircraft, here, pressTo, out why);
            float launchAt = clear ? noEscape : extended;
            string note = (clear ? "pressing to " + (noEscape / 1000f).ToString("0") + " km, the way in is clear"
                : "launching at " + (extended / 1000f).ToString("0") + " km · " + (flight.Threat == FlightThreat.Missile ? "under fire" : why)) +
                (bvrClosingIn ? " · too high to climb for, closing in at " + (BvrHold / 1000f).ToString("0") + " km"
                    : " · climbing to " + (loft / 1000f).ToString("0") + " km");
            if (note != bvrNote) { bvrNote = note; Tracing.Flight("[flight] " + flight.Name + " · intercept · " + note); }

            float ordered = flight.Altitude;
            flight.Altitude = loft - ground;
            try
            {
                if (range > launchAt) { Steer(known); return; }

                float corner = parameters != null ? parameters.cornerSpeed : 0f;
                if (corner > 0f && aircraft.speed < corner * 1.15f)
                {
                    controlInputs.throttle = 1f; Reheat();
                    Steer(known);
                    return;
                }
                Steer(known);
                float off = Vector3.Angle(aircraft.transform.forward, toTarget);
                float cone = needs.minAlignment > 0f ? needs.minAlignment : 30f;
                if (off > cone * 0.9f || Time.timeSinceLevelLoad - flight.LastLaunchAt < 2.5f) return;
                if (aircraft.speed < needs.minOwnerSpeed) return;

                if (flight.SalvoLeft <= 0) flight.SalvoLeft = Mathf.Clamp(ShotDisciplinePatch.AllowedOn(flight, target, info), 1, Mathf.Max(station.Ammo, 1));
                aircraft.weaponManager.currentWeaponStation = station;
                List<Unit> targets = aircraft.weaponManager.GetTargetList();
                targets.Clear();
                int found = CombatAI.LookForMissileTargets(aircraft, target, station, targets);
                aircraft.weaponManager.TargetListChanged();
                if (found <= 0) return;
                int before = station.Ammo;
                pilot.Fire();
                flight.LastLaunchAt = Time.timeSinceLevelLoad;
                if (station.Ammo < before)
                {
                    flight.SalvoLeft--;
                    Tracing.Flight("[flight] " + flight.Name + " · launched " + info.weaponName + " at " + (range / 1000f).ToString("0.0") +
                        " km from " + aircraft.radarAlt.ToString("0") + " m · " + Mathf.Max(flight.SalvoLeft, 0) + " left in the salvo");
                }
                if (station.Ammo <= 0 || flight.SalvoLeft <= 0) CompleteRunIn(pilot, "launched");
            }
            finally { flight.Altitude = ordered; }
        }

        private void FlyStandoffLaunch(Pilot pilot, Unit target, GlobalPosition known, WeaponStation station)
        {
            WeaponInfo info = station.WeaponInfo;
            TargetRequirements needs = info.targetRequirements;
            GlobalPosition here = aircraft.GlobalPosition();
            float range = Horizontal(known, here);
            float launch = Mathf.Max(needs.maxRange * 0.85f, needs.minRange * 1.5f);

            if (range < needs.minRange * 1.1f) { CompleteRunIn(pilot, "inside minimum range"); return; }
            if (range > launch)
            {
                flight.InLaunchRangeSince = -1f;
                Steer(known);                                  // at the ordered height: Steer holds it
                return;
            }
            if (flight.InLaunchRangeSince < 0f) flight.InLaunchRangeSince = Time.timeSinceLevelLoad;
            if (Time.timeSinceLevelLoad - flight.InLaunchRangeSince > 60f) { CompleteRunIn(pilot, "no launch in a minute"); return; }

            // Speed before the shot: a loaded fighter that fired at 74 m/s went
            // straight into the sea. Below 1.15 times corner speed it flies on
            // at full power and lines up when it has the speed back.
            float corner = parameters != null ? parameters.cornerSpeed : 0f;
            if (corner > 0f && aircraft.speed < corner * 1.15f)
            {
                controlInputs.throttle = 1f; Reheat();
                Vector3 ahead = Flat(aircraft.transform.forward);
                if (ahead.sqrMagnitude < 0.01f) ahead = Vector3.forward;
                Steer(aircraft.GlobalPosition() + ahead.normalized * 5000f, default, 20f);
                return;
            }

            // Nose onto the target, level: the cone is a 3D angle, and from
            // height a distant target sits only a few degrees below.
            Steer(known);
            Vector3 toTarget = known - here;
            float off = Vector3.Angle(aircraft.transform.forward, toTarget);
            float cone = needs.minAlignment > 0f ? needs.minAlignment : 30f;
            if (off > cone * 0.9f || Time.timeSinceLevelLoad - flight.LastLaunchAt < 2.5f) return;
            if (aircraft.speed < needs.minOwnerSpeed) return;

            if (flight.SalvoLeft <= 0)
                flight.SalvoLeft = Mathf.Clamp(Mathf.CeilToInt(info.CalcAttacksNeeded(target)), 1, Mathf.Max(station.Ammo, 1));

            aircraft.weaponManager.currentWeaponStation = station;
            List<Unit> targets = aircraft.weaponManager.GetTargetList();
            targets.Clear();
            int found = CombatAI.LookForMissileTargets(aircraft, target, station, targets);
            aircraft.weaponManager.TargetListChanged();
            if (found <= 0) return;
            int before = station.Ammo;
            pilot.Fire();
            flight.LastLaunchAt = Time.timeSinceLevelLoad;
            if (station.Ammo < before || station.Ammo <= 0)
            {
                flight.SalvoLeft--;
                Tracing.Flight("[flight] " + flight.Name + " · launched " + info.weaponName + " at " +
                    (range / 1000f).ToString("0.0") + " km from " + aircraft.radarAlt.ToString("0") + " m · " +
                    Mathf.Max(flight.SalvoLeft, 0) + " left in the salvo");
            }
            if (station.Ammo <= 0) flight.SalvoLeft = 0;
        }

        private void CompleteRunIn(Pilot pilot, string why)
        {
            flight.RunInDone = true;
            flight.StrikeStarted = Time.timeSinceLevelLoad;     // patience runs from the attack, not the transit
            Tracing.Flight("[flight] " + flight.Name + " · run-in complete · " + why + " · alt " +
                aircraft.radarAlt.ToString("0") + " m");
            HandBackToCombat(pilot);
        }

        private void HandBackToCombat(Pilot pilot)
        {
            // No combat state to hand to: keep flying it ourselves rather than
            // leaving the aircraft with nobody at the controls.
            PilotBaseState combat = FlightOrders.CombatStateFor(pilot);
            if (combat == null) { FlyOrbit(flight.OrbitCentre); return; }
            Tracing.Flight("[flight] " + flight.Name + " · handing to the combat pilot · " + flight.Describe());
            pilot.SwitchStateNew(combat);
            NativePilot.Wake(combat, aircraft);
        }

        // Home to a ship that is turning: hold overhead until the deck has
        // settled, then hand over to the game's approach. Jets only -- a
        // helicopter comes straight down onto the spot whatever the heading --
        // and never on the last of the fuel.
        private bool marshalling;
        private void FlyHome(Pilot pilot)
        {
            Ship deck = flight.Parent;
            if (deck != null && !deck.disabled && Ownership.Acts(deck) && aircraft.autopilot is AutopilotPlane &&
                aircraft.GetFuelLevel() > 0.08f && !DeckWaveOff.Steady(deck))
            {
                if (!marshalling)
                {
                    marshalling = true;
                    Host.LogInfo("[flight] " + flight.Name + " · marshalling over " + ShipNames.Of(deck) + " until the deck steadies");
                    Host.Say(flight.Name + " · marshalling, " + ShipNames.Of(deck) + " turning");
                }
                FlyOrbit(deck.GlobalPosition(), 3000f);
                return;
            }
            marshalling = false;
            HandBackToLanding(pilot);
        }

        private void HandBackToLanding(Pilot pilot)
        {
            PilotBaseState landing = FlightOrders.IsRotary(pilot) && pilot.AIHeloLandingState != null
                ? (PilotBaseState)pilot.AIHeloLandingState : pilot.AILandingState;
            if (landing == null) { FlyOrbit(aircraft.GlobalPosition()); return; }
            Host.LogInfo("[flight] " + flight.Name + " recovering");
            pilot.SwitchStateNew(landing);
        }

        private static float Horizontal(GlobalPosition a, GlobalPosition b)
        {
            Vector3 offset = a - b;
            offset.y = 0f;
            return offset.magnitude;
        }
    }
}
