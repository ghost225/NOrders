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
                " m · speed " + pilot.aircraft.speed.ToString("0") + " m/s" + Speeds(pilot.aircraft));
        }

        private static string Speeds(Aircraft aircraft)
        {
            AircraftParameters p = aircraft?.GetAircraftParameters();
            return p == null ? "" : " · corner " + p.cornerSpeed.ToString("0") + " / top " + p.maxSpeed.ToString("0") + " m/s";
        }

        public override void EnterState(Pilot pilot)
        {
            this.pilot = pilot;
            controlInputs = aircraft.GetInputs();
            parameters = aircraft.GetAircraftParameters();
            FindNearestAirbase();
            // Nothing left firing from the combat pilot's attack. A ripple
            // (WeaponManager.SalvoFire) walks the live target list after the
            // state has moved on: Mallet, a Vagrant, pulled off after its
            // four rockets and went back on station still putting the rest of
            // the pod into the list. Cleared, the ripple ends.
            try
            {
                if (aircraft.weaponManager != null && aircraft.weaponManager.GetTargetList().Count > 0)
                {
                    aircraft.weaponManager.GetTargetList().Clear();
                    aircraft.weaponManager.TargetListChanged();
                }
            }
            catch { }

            // The same handover the native combat state performs. Without it a
            // helicopter arrives from its takeoff state with auto-hover still
            // engaged, and the controls filter then fights the autopilot's
            // collective all the way into the ground.
            //
            // A fixed-wing gets flight assist on, as the combat state does.
            // ToDefault only tells the controls filter and leaves the
            // aircraft's own flag where the taxi state put it -- off -- and
            // with it off the fly-by-wire's G limit and the AoA limiters do
            // nothing: our jets flew off the deck with no limits at all.
            if (aircraft.autopilot is AutopilotPlane) aircraft.SetFlightAssist(enabled: true);
            else aircraft.SetFlightAssistToDefault();
            aircraft.SetGear(deployed: false);
            heloHold = -1f;                         // start from wherever it is
            ControlsFilter filter = aircraft.GetControlsFilter();
            if (filter != null) filter.SetAutoHover(enabled: false);
        }

        private AircraftParameters parameters;
        private float recoveringSince = -1f, recoveredAt = -100f;

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
            // Speed before economy: full power (and the burner) below corner
            // speed, easing to cruise by 1.3 times it:
            // a flat full power anywhere under 1.3 times kept a fast-cornering
            // jet (the FS-41) on its burner, zooming past its ordered height
            // and diving back at 470 m/s.
            float corner = parameters != null ? parameters.cornerSpeed : 0f;
            if (corner > 0f && aircraft.speed < corner) return 1f;
            float result = CruiseFor();
            if (corner > 0f && aircraft.speed < corner * 1.3f)
                result = Mathf.Lerp(1f, result, (aircraft.speed - corner) / (corner * 0.3f));
            // And no faster than cruise wants: from 65% of top speed the power
            // eases off, to 30% by 75%. Cruise power alone carried a Medusa
            // (top 300 m/s) to 250 in a station orbit, where the air load of an
            // ordinary 2.5 g turn tore its tail and a wing off -- two pickets,
            // one each side, in the same minute. Speed before economy below
            // corner; economy, and the airframe, above.
            float top = parameters != null ? parameters.maxSpeed : 0f;
            if (top > 0f && aircraft.speed > top * 0.65f)
                result = Mathf.Min(result, Mathf.Lerp(result, 0.3f, Mathf.InverseLerp(top * 0.65f, top * 0.75f, aircraft.speed)));
            return result;
        }

        private float CruiseFor()
        {
            float cruise = Tuning.CruiseThrottle;
            if (!Wings.HasFollowers(flight)) return cruise;
            float lead = cruise - 0.05f;
            float behind = Wings.Straggle(flight);
            if (behind > 2000f) lead -= Mathf.Min(0.2f, (behind - 2000f) / 3000f * 0.2f);
            // Not faster than the slowest wingman still behind, plus a little.
            if (Wings.SlowestBehind(flight, out float slowest) && aircraft.speed > slowest + 4f)
                lead -= Mathf.Min(0.25f, (aircraft.speed - slowest - 4f) * 0.03f);
            float minimum = WingBorneTakeoff() * 1.4f;
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

            // A flat spin is not flown out by steering: the autopilot's pitch
            // and roll only wind it tighter. Idle, rudder against the rotation,
            // stick forward, until the rotation stops; then the energy
            // recovery below dives it out. (Warden-1, an FS-20, spun from
            // 1,100 m at 50 m/s into the sea.)
            if (aircraft.autopilot is AutopilotPlane && SpinRecovery()) return;

            // On the ground and barely moving under our state: whatever put it
            // here, it cannot be flown from the deck by a navigation loop.
            // Hand it back to the game's taxi and takeoff and take it again
            // once it is up.
            if (aircraft.radarAlt < 3f && aircraft.speed < 10f)
            {
                if (groundedSince < 0f) groundedSince = Time.timeSinceLevelLoad;
                else if (Time.timeSinceLevelLoad - groundedSince > 8f && flight.Mode == FlightMode.ReturnToBase)
                {
                    // Down on the way home: finish the landing the game's way
                    // -- park, or turn round at an airfield -- never a takeoff.
                    groundedSince = -1f;
                    HandBackToLanding(pilot);
                    return;
                }
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
            // Slow is judged against what the airframe can do, not a fighter's
            // numbers: corner speed alone kept a prop Cricket, flat out at
            // 88 m/s, in recovery for ever. The bar is the lowest of corner
            // speed, half again its takeoff speed (the stall margin that
            // matters), and 60% of its top speed. Not on the way home: the
            // landing state flies a slow aircraft down itself, and recovery
            // had blocked the RTB order outright.
            if (aircraft.autopilot is AutopilotPlane && flight.Mode != FlightMode.ReturnToBase)
            {
                float bar = SlowBar();
                bool justOut = recoveringSince < 0f && Time.timeSinceLevelLoad - recoveredAt < 20f;
                // Once in recovery it stays until well over the bar: a Vortex
                // flipped in and out at 181 and 191 against 189 and never
                // gained anything.
                bool slow = bar > 0f && aircraft.speed < bar * (recoveringSince >= 0f ? 1.12f : justOut ? 0.93f : 1f);
                // A wingman flies the lead's speed and its formation code sets
                // the power: only real stall danger takes it out of formation.
                // Wingmen of a heavy lead cruising under the bar were leaving
                // the formation every few seconds to "recover".
                if (slow && flight.Mode == FlightMode.Formation && WingBorneTakeoff() > 0f)
                    slow = aircraft.speed < WingBorneTakeoff() * 1.1f;
                // A wingman follows its lead's height, which may be low on
                // purpose; it is only "low" close to the ground.
                bool low = aircraft.radarAlt < (flight.Mode == FlightMode.Formation ? 60f : LowBar) &&
                           flight.Mode != FlightMode.Egress && flight.Mode != FlightMode.Strike;
                if ((slow || low) && !flight.EvadingInfrared)
                {
                    controlInputs.throttle = 1f;
                    Reheat();
                    // Straight on along the way it is actually going: a point
                    // ahead of the nose, with a slow prop crabbing, pulled the
                    // velocity after the nose and flew it round in circles.
                    Vector3 ahead = aircraft.speed > 5f ? Flat(aircraft.rb.velocity) : Flat(aircraft.transform.forward);
                    if (ahead.sqrMagnitude < 0.01f) ahead = Vector3.forward;
                    float ordered = flight.Altitude;
                    // Low: climb, but no more than 300 m above where it is.
                    // Slow but high enough: ease down, trading height for speed
                    // -- climbing bled it further.
                    // The climb always clears the low bar: capped at the ordered
                    // height alone, a wingman whose ordered height read 0 held
                    // at 53 m -- still "low" -- and flew straight on for 30 km.
                    // Slow: unload decisively -- 600 m down over the 5 km run,
                    // about seven degrees; the 150 m ease-down never produced a
                    // descent and a Vortex mushed from 1,550 m at full power.
                    // Low and not slow: climb. Low AND slow: no climb -- a
                    // King Viper at 99 m/s was pulled up 300 m and went into
                    // the water at 38 m/s -- level at full power until the
                    // speed is back, unless nearly on the ground, where a
                    // gentle climb is the only way out.
                    // ... and never below 300 m over the ground: a Vortex fed
                    // "600 m lower" every tick from 2,300 m followed it in.
                    if (slow && !low)
                    {
                        flight.Altitude = Mathf.Max(aircraft.radarAlt - 600f, LowBar + 150f);
                        // Falling already, it follows its own flight path down
                        // until the speed is back: the autopilot steers the
                        // flight path, and a Vortex at 60 m/s sinking steeply,
                        // told to fly a seven-degree path, was pulled up into
                        // the stall it was in, at full power. No terrain
                        // following meanwhile (that sets its own climb), and
                        // the 300 m floor still holds.
                        Vector3 v = aircraft.rb != null ? aircraft.rb.velocity : Vector3.zero;
                        float path = v.sqrMagnitude > 1f ? Mathf.Asin(Mathf.Clamp(v.y / v.magnitude, -1f, 1f)) * Mathf.Rad2Deg : 0f;
                        if (path < -7f)
                        {
                            unloadTo = -path + 2f;
                            flight.Altitude = Mathf.Max(aircraft.radarAlt + 5000f * Mathf.Tan((path - 2f) * Mathf.Deg2Rad), LowBar + 150f);
                        }
                    }
                    else if (slow) flight.Altitude = aircraft.radarAlt < 40f ? aircraft.radarAlt + 80f : aircraft.radarAlt;
                    else flight.Altitude = Mathf.Clamp(aircraft.radarAlt + 300f, LowBar + 150f, Mathf.Max(ordered, LowBar + 150f));
                    flight.Doing("RECOVERING ENERGY");
                    if (recoveringSince < 0f) { recoveringSince = Time.timeSinceLevelLoad; Tracing.Flight("[flight] " + flight.Name + " · recovering energy · " + aircraft.speed.ToString("0") + " m/s (bar " + bar.ToString("0") + ") at " + aircraft.radarAlt.ToString("0") + " m"); }
                    try { Steer(aircraft.GlobalPosition() + ahead.normalized * 5000f, default, 30f); }
                    finally { unloadTo = 0f; }
                    flight.Altitude = ordered;
                    return;
                }
            }
            if (recoveringSince >= 0f) recoveredAt = Time.timeSinceLevelLoad;
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
                " km · alt " + aircraft.radarAlt.ToString("0") + " ordered " + flight.Altitude.ToString("0") + " · spd " + aircraft.speed.ToString("0") +
                " · peak " + GLimitPatch.Peak(aircraft).ToString("0.0") + " g" +
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
        private const float MinSteerDistance = 2500f;
        // Set by the energy recovery while a stalled aircraft follows its own
        // flight path down: the descent it may be steered at, in degrees.
        private float unloadTo;
        private const float MaxSteerDescent = 10f;
        private const float MaxSteerClimb = 20f;

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
            // Never a circle tighter than the speed allows at a sane bank: a
            // 2.5 km join-up circle at 437 m/s wants 82 degrees and over 7 g,
            // and a Vortex lead forming up pulled 9-14 g on it until it came
            // apart. A sustained turn at up to 70% of the airframe's G limit
            // (an FS-41's 7 g: about 4.9 g, 78 degrees), and less where the
            // stall margin says so; the circle widens instead. At 60 degrees
            // (2 g) as first written it flew far wider circles than a pilot
            // would, and wider than the FS-41 turns comfortably at 7 g.
            if (aircraft.autopilot is AutopilotPlane)
            {
                float sustained = Mathf.Max(GLimitPatch.LimitOf(aircraft) * 0.7f, 1.5f);
                float bank = Mathf.Min(SafeBank(), Mathf.Acos(1f / sustained) * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                float tightest = aircraft.speed * aircraft.speed / (9.81f * Mathf.Tan(bank)) * 1.15f;
                radius = Mathf.Max(radius, tightest);
            }
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
        // heat-seeker throttle cut still cools the engine. Only on those: on a
        // swivel-duct VTOL the same axis points the ducts (see AuxAxis).
        private void Reheat()
        {
            if (!(aircraft.autopilot is AutopilotPlane)) return;
            AuxAxis.Apply(aircraft, controlInputs);
        }

        // Not past what the airframe can take (see SpeedLimit).
        private void LimitSpeed()
        {
            if (!SpeedLimit.Apply(aircraft, controlInputs) || SpeedLimit.Over(aircraft) < 1f) return;
            if (Time.timeSinceLevelLoad >= overspeedNoteAt)
            {
                overspeedNoteAt = Time.timeSinceLevelLoad + 15f;
                Tracing.Flight("[flight] " + flight.Name + " · overspeed · " + aircraft.speed.ToString("0") + " m/s, power off");
            }
        }
        private float overspeedNoteAt;

        // How hard to bank with the speed in hand: from the load the wing can
        // give above its stall -- (speed / stall)^2 g -- with a wide margin,
        // the bank whose level turn needs no more than that. Twenty degrees
        // at least, eighty at most. Reckoned from corner speed instead, a
        // jet cruising right at it (the FS-41's 180 m/s) was held to 25
        // degrees, could not fly its circle, and the autopilot made up the
        // turn by pulling -- at that bank mostly upward, so it spiralled up
        // on full power to 4 km over a 600 m station. (Seven fighters went
        // into the sea from a routine orbit long ago with flight assist off;
        // the game's own G and AoA limits are on now, see EnterState.)
        private float SafeBank()
        {
            if (!(aircraft.autopilot is AutopilotPlane)) return 60f;
            float takeoff = WingBorneTakeoff();
            float corner = parameters != null ? parameters.cornerSpeed : 0f;
            float stall = takeoff > 0f ? takeoff / 1.15f : corner * 0.45f;
            if (stall <= 0f) return 60f;
            float ratio = aircraft.speed / stall;
            float load = ratio * ratio * 0.45f;
            float bank = load <= 1f ? 0f : Mathf.Acos(1f / load) * Mathf.Rad2Deg;
            return Mathf.Clamp(bank, 20f, 80f);
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

        private Missile beamShot;
        private float beamAlt;

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
            // The descent is set once per shot: 70% of the height it started
            // at. Taken from the present height every step it was a target
            // that ran away downward, and an F-16 beaming a radar shot went
            // from 3,700 m to the ground at 400 m/s.
            if (descend && beamShot != missile) { beamShot = missile; beamAlt = Mathf.Max(Mathf.Min(aircraft.radarAlt * 0.7f, ordered), Tuning.RadarEvasionFloor); }
            flight.Altitude = descend
                ? beamAlt
                : Mathf.Max(Mathf.Min(aircraft.radarAlt, ordered), MinimumClearance);
            Steer(aircraft.GlobalPosition() + beam * 4000f, default, Mathf.Min(60f, SafeBank()));
            flight.Altitude = ordered;
        }

        private void FlyEgress()
        {
            if (flight.Cranking) { FlyCrank(); return; }
            float ordered = flight.Altitude;
            flight.Altitude = Mathf.Min(ordered, Tuning.EgressAltitude);
            Steer(flight.EgressPoint);
            flight.Altitude = ordered;
        }

        // The crank itself. The targets' bearings are averaged and the nose
        // set off that by as much as the radar's cone allows with the widest
        // of them still inside it, and their elevation allowed for; the side
        // is whichever the nose is already on, then kept. Down as height and
        // speed allow, to the crank's floor.
        private readonly List<Unit> crankTargets = new List<Unit>();
        private float crankScanAt = -1f;
        private string crankNote;
        private float crankNoteAt;

        private void FlyCrank()
        {
            float now = Time.timeSinceLevelLoad;
            if (now - crankScanAt >= 0.5f || crankScanAt > now)
            {
                crankScanAt = now;
                Crank.Supported(aircraft, crankTargets);
            }
            FactionHQ hq = aircraft.NetworkHQ;
            GlobalPosition here = aircraft.GlobalPosition();
            float ground = here.y - aircraft.radarAlt;
            Vector3 centre = Vector3.zero;
            var flats = new List<Vector3>();
            float elevation = 0f;
            foreach (Unit target in crankTargets)
            {
                if (Host.Dead(target) || hq == null || !hq.TryGetKnownPosition(target, out GlobalPosition known)) continue;
                Vector3 to = known - here;
                Vector3 flat = new Vector3(to.x, 0f, to.z);
                if (flat.sqrMagnitude < 1f) continue;
                elevation = Mathf.Max(elevation, Mathf.Abs(Mathf.Atan2(to.y, flat.magnitude) * Mathf.Rad2Deg));
                flats.Add(flat.normalized);
                centre += flat.normalized;
            }
            if (flats.Count == 0 || centre.sqrMagnitude < 0.01f)
            {
                // Nothing to look at: fly the egress line meanwhile.
                float held = flight.Altitude;
                flight.Altitude = Mathf.Min(held, Tuning.EgressAltitude);
                Steer(flight.EgressPoint);
                flight.Altitude = held;
                return;
            }
            centre.Normalize();
            float spread = 0f;
            foreach (Vector3 flat in flats) spread = Mathf.Max(spread, Vector3.Angle(centre, flat));

            float cone = Crank.Cone(aircraft);
            float offset = Mathf.Sqrt(Mathf.Max(cone * cone - elevation * elevation, 0f)) - spread;
            offset = Mathf.Clamp(offset, 0f, 60f);
            if (flight.CrankSide == 0)
                flight.CrankSide = Vector3.SignedAngle(centre, Flat(aircraft.transform.forward), Vector3.up) >= 0f ? 1 : -1;
            Vector3 heading = Quaternion.AngleAxis(offset * flight.CrankSide, Vector3.up) * centre;

            // The descent is earned, not set: the full seven degrees only with
            // height and speed in hand, easing to level flight approaching the
            // floor (1,500 m over whatever ground is under the aim point), when
            // slow (under corner speed it holds height and keeps its energy)
            // and when fast (diving towards top speed only adds more). Held
            // as height over the ground ahead, so rising terrain lifts it.
            const float reach = 5000f;
            float agl = aircraft.radarAlt;
            float floorAgl = Mathf.Min(Crank.FloorAboveGround, agl);            // started low: hold, never climb for it
            float byHeight = Mathf.Clamp01((agl - Crank.FloorAboveGround) / Crank.FloorAboveGround);
            float corner = parameters != null ? parameters.cornerSpeed : 0f;
            float top = parameters != null ? parameters.maxSpeed : 0f;
            float bySpeed = 1f;
            if (corner > 0f) bySpeed = Mathf.Min(bySpeed, Mathf.Clamp01((aircraft.speed - corner * 1.1f) / (corner * 0.3f)));
            if (top > 0f) bySpeed = Mathf.Min(bySpeed, 1f - Mathf.Clamp01((aircraft.speed - top * 0.8f) / (top * 0.15f)));
            float angle = Crank.MaxDiveDegrees * Mathf.Min(byHeight, bySpeed);
            float aimAgl = agl - reach * Mathf.Tan(angle * Mathf.Deg2Rad);
            aimAgl = Mathf.Max(aimAgl, floorAgl, flight.CrankFloor - ground);  // and no more than the crank's own drop
            float ordered = flight.Altitude;
            flight.Altitude = Mathf.Max(aimAgl, MinimumClearance);
            try { Steer(here + heading * reach); }
            finally { flight.Altitude = ordered; }

            int guided = crankTargets.Count;
            string note = "CRANKING · " + offset.ToString("0") + "° off · guiding on " + guided + " target" + (guided == 1 ? "" : "s");
            flight.Doing(note);
            string trace = offset.ToString("0") + "° " + (flight.CrankSide > 0 ? "right" : "left") + " of " + guided + " · cone " + cone.ToString("0") + "° · descent " + angle.ToString("0") + "°";
            string key = flight.CrankSide + "/" + guided;
            if (key != crankNote || now >= crankNoteAt)
            {
                crankNote = key;
                crankNoteAt = now + 10f;
                Tracing.Flight("[flight] " + flight.Name + " · crank · " + trace + " · alt " + aircraft.radarAlt.ToString("0") + " m · " + aircraft.speed.ToString("0") + " m/s");
            }
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
                // Terrain following up to 1,500 m of ordered height: below
                // that the order means height over the ground along the way,
                // not over the ground at the point. Two Ifrits on a 600 m
                // run-in held the height of the target's ground while the land
                // rose under them -- radar altitude 354, 231, 46 -- and hit it.
                // ... but not while recovering energy with height in hand: the
                // autopilot's terrain-following pull-up at low speed stalled
                // Vortexes over the hills -- Talon-1, 162 -> 75 m/s at 5.6 g from
                // 1,100 m, into a spin with no height left to fly it out. Our own
                // ground floor (the steer point never under ground plus
                // clearance) still holds; under 250 m the autopilot has it again.
                bool followTerrain = aboveGround < 1500f && unloadTo <= 0f && !(recoveringSince >= 0f && aircraft.radarAlt > 250f);
                GlobalPosition from = aircraft.GlobalPosition();
                // Never a steering point close in: the autopilot pulls up hard
                // -- two kilometres of up on a one-kilometre vector, fifty
                // degrees at fighter speeds -- for any point inside 2 km and
                // more than 60 degrees off its path, and an orbit flown wide
                // at low level kept handing it one rejoining the circle. An
                // FS-41 ordered to 600 m zoomed to 2,100. Same bearing, pushed
                // out to 2.5 km: the course is unchanged.
                Vector3 flat = target - from; flat.y = 0f;
                float flatRange = flat.magnitude;
                if (flatRange > 1f && flatRange < MinSteerDistance) target = from + flat / flatRange * MinSteerDistance;
                // No sharper a turn asked for than the bank allows. What the
                // bank cannot give, the autopilot takes by pulling the nose
                // toward the point, and at a shallow bank that pull is mostly
                // upward: a held-down bank and a hard turn make a climbing
                // spiral. The point is swung back toward the current track,
                // so the turn comes from the bank and takes a little longer.
                {
                    Vector3 track = aircraft.rb != null ? aircraft.rb.velocity : aircraft.transform.forward;
                    track.y = 0f;
                    Vector3 toward = target - from; toward.y = 0f;
                    // The bank it will really get: ours, and the speed's, cut
                    // as the autopilot cuts it near the ground.
                    float banked = Mathf.Min(bank, SafeBank()) * Mathf.Clamp(aircraft.radarAlt * 0.003f - 1f, 0.6f, 1.2f);
                    float lateral = Mathf.Lerp(12f, 90f, Mathf.InverseLerp(20f, 65f, banked));
                    // And less the faster it goes: the same angle off is a
                    // harder pull at speed. About 85% of the airframe's G
                    // limit at the autopilot's pace -- for an FS-41 (7 g),
                    // 15 degrees at 440 m/s, 44 at 150. A wingman gets half
                    // as much again, to hold its slot.
                    float pull = GLimitPatch.LimitOf(aircraft) * 0.85f * 9.81f * 2f;
                    float bySpeed = Mathf.Clamp(Mathf.Rad2Deg * pull / Mathf.Max(aircraft.speed, 1f), 8f, 90f);
                    if (flight.Mode == FlightMode.Formation) bySpeed *= 1.5f;
                    else lateral = Mathf.Min(lateral, bySpeed);
                    if (flight.Mode == FlightMode.Formation) lateral = bySpeed;
                    // But never under the autopilot's rudder threshold with
                    // the speed to turn: inside 20 degrees of its point it
                    // yaws rather than banks, and capped at 8-15 degrees at
                    // strike speeds every line-up was flown flat on the
                    // rudder. The G limit (GLimitPatch) keeps the bank honest.
                    else if (parameters != null && aircraft.speed >= parameters.cornerSpeed)
                        lateral = Mathf.Max(lateral, flight.Mode == FlightMode.Strike ? StrikeTurn : BankingTurn);
                    if (track.sqrMagnitude > 1f && toward.sqrMagnitude > 1f && Vector3.Angle(track, toward) > lateral)
                    {
                        float side = Mathf.Sign(Vector3.SignedAngle(track, toward, Vector3.up));
                        Vector3 swung = Quaternion.AngleAxis(lateral * side, Vector3.up) * track.normalized * toward.magnitude;
                        target = from + swung + Vector3.up * (target - from).y;
                    }
                }
                GlobalPosition point = AtAltitude(target, aboveGround);
                // Height changed smoothly: no steeper than 10 degrees down or
                // 20 up toward the point, so a flight above its height eases
                // down rather than nosing over, and one far below climbs
                // without standing on its tail. The point never goes below
                // the ground there; the autopilot's own terrain warning still
                // pulls up for anything in the way. Not on an attack run,
                // which may need its nose well down on a low target.
                {
                    Vector3 to = point - from; to.y = 0f;
                    float run = Mathf.Max(to.magnitude, MinSteerDistance);
                    float groundThere = point.y - Mathf.Max(aboveGround, MinimumClearance);
                    float groundFloor = groundThere + MinimumClearance;
                    if (flight.Mode != FlightMode.Strike)
                    {
                        // The descent allowed shrinks with speed: at 80% of top
                        // speed three degrees, not ten. Two King Vipers egressing
                        // at 530 m/s dived from 5,500 m to a 200 m egress height
                        // and could not pull out; nothing was shot at them.
                        float top = parameters != null ? parameters.maxSpeed : 0f;
                        float fast = top > 0f ? Mathf.InverseLerp(0.55f * top, 0.85f * top, aircraft.speed) : 0f;
                        float maxDown = Mathf.Max(Mathf.Lerp(MaxSteerDescent, 3f, fast), unloadTo);
                        float low = Mathf.Max(from.y - run * Mathf.Tan(maxDown * Mathf.Deg2Rad), groundFloor);
                        float high = Mathf.Max(from.y + run * Mathf.Tan(MaxSteerClimb * Mathf.Deg2Rad), groundFloor);
                        float y = Mathf.Clamp(point.y, low, high);
                        // And fast, never under 300 m over the ground by steering:
                        // the evasion code owns the deck, not a transit.
                        if (fast > 0.5f) y = Mathf.Max(y, groundThere + 300f);
                        point += Vector3.up * (y - point.y);
                    }
                    else if (point.y < groundFloor) point += Vector3.up * (groundFloor - point.y);   // an attack run may nose down, never under the ground
                }
                // Overspeed: besides power off, no descent -- a Medusa at
                // idle still ran to 267 m/s down a ten-degree slope and came
                // apart. Half way to the limit the point is no lower than the
                // aircraft; at the limit it is a little above.
                float over = SpeedLimit.Over(aircraft);
                if (over >= 0.5f && point.y < from.y + (over >= 1f ? 150f : 0f)) point += Vector3.up * (from.y + (over >= 1f ? 150f : 0f) - point.y);
                destination = point;
                LimitSpeed();
                IntendedThrottle = controlInputs.throttle;
                // The game's own G and AoA limits, kept on (see EnterState).
                if (!aircraft.flightAssist) aircraft.SetFlightAssist(enabled: true);
                // And the auto-hover off. A VTOL type (FS-20 Vortex, EW-25
                // Medusa) is handed over from a vertical or short takeoff with
                // the game's auto-hover still active, and while it is active it
                // flies the aircraft itself: levels it toward a hover, slows it to
                // hover speed, points the ducts down and sets the throttle for
                // height. A Vortex in energy recovery looked to have its throttle
                // cut and its airbrakes out, mushed into a flat spin and went in;
                // the same is likely behind most of the Vortex losses this week.
                if (aircraft.radarAlt > 5f && aircraft.IsAutoHoverEnabled())
                {
                    aircraft.GetControlsFilter().SetAutoHover(false);
                    Tracing.Flight("[flight] " + flight.Name + " · auto-hover was on under our control; switched off");
                }
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

            // Sink guard. A plain or compound helicopter sinking hard near the
            // ground gets collective, whatever the autopilot asked for: six
            // loaded Ibises fell from 700 m into the sea together with nothing
            // hit, collective at 0-12% at impact. Tiltwings fly on their wings
            // at speed and are left to their own autopilot.
            // Disabled: fired once in a live game (Ferry, an Ibis settling at
            // 252 m) and the helicopter still fell 120 m and crashed; pulling
            // collective into a settling rotor can make it worse. Kept for the
            // trace until the Ibis falls are understood.
            if (false && RotaryKind(aircraft) != Rotary.Tiltwing && aircraft.rb != null)
            {
                float sink = -aircraft.rb.velocity.y;
                if (sink > 12f && aircraft.radarAlt < 250f + sink * 4f)
                {
                    float wantCollective = Mathf.Lerp(0.7f, 1f, Mathf.InverseLerp(12f, 35f, sink));
                    if (controlInputs.throttle < wantCollective) controlInputs.throttle = wantCollective;
                    heloHold = Mathf.Max(heloHold, aircraft.radarAlt + 50f);
                    if (Time.timeSinceLevelLoad - sinkSaidAt > 15f) { sinkSaidAt = Time.timeSinceLevelLoad; Tracing.Flight("[flight] " + flight.Name + " · sinking " + sink.ToString("0") + " m/s at " + aircraft.radarAlt.ToString("0") + " m, collective up"); }
                }
            }
        }

        private float sinkSaidAt = -100f;

        // ---- flat spin --------------------------------------------------------------------
        // Signs from the game's own controllers: positive pitch input pitches
        // the nose down (rotation about +x), positive yaw yaws it right (+y).
        private float spinSince = -1f, spinClearSince = -1f, spinSaidAt = -100f;
        private bool spinning;
        internal float IntendedThrottle = -1f;     // what we last set, for the throttle guard

        private bool SpinRecovery()
        {
            if (aircraft.rb == null || aircraft.radarAlt < 20f) { spinning = false; spinSince = -1f; return false; }
            Vector3 w = aircraft.transform.InverseTransformDirection(aircraft.rb.angularVelocity);
            float yawRate = w.y * Mathf.Rad2Deg;
            float corner = parameters != null && parameters.cornerSpeed > 0f ? parameters.cornerSpeed : 150f;
            float sink = -aircraft.rb.velocity.y;
            bool spinNow = Mathf.Abs(yawRate) > 35f && aircraft.speed < corner * 0.7f && sink > 8f;
            float now = Time.timeSinceLevelLoad;
            if (!spinning)
            {
                if (!spinNow) { spinSince = -1f; return false; }
                if (spinSince < 0f) spinSince = now;
                if (now - spinSince < 1.5f) return false;              // a hard turn is not a spin
                spinning = true; spinClearSince = -1f;
            }
            else if (Mathf.Abs(yawRate) < 15f)
            {
                if (spinClearSince < 0f) spinClearSince = now;
                if (now - spinClearSince > 1f)
                {
                    spinning = false; spinSince = -1f;
                    Tracing.Flight("[flight] " + flight.Name + " · spin broken at " + aircraft.radarAlt.ToString("0") + " m, " + aircraft.speed.ToString("0") + " m/s; diving out");
                    return false;
                }
            }
            else spinClearSince = -1f;

            controlInputs.throttle = 0.05f;                         // idle, not zero: zero opens the airbrakes
            IntendedThrottle = controlInputs.throttle;
            controlInputs.yaw = -Mathf.Sign(yawRate);               // full rudder against the rotation
            controlInputs.pitch = 0.8f;                             // stick forward
            controlInputs.roll = 0f;
            controlInputs.brake = 0f;
            if (now - spinSaidAt > 5f)
            {
                spinSaidAt = now;
                Tracing.Flight("[flight] " + flight.Name + " · flat spin · yaw " + yawRate.ToString("0") + "°/s at " + aircraft.speed.ToString("0") + " m/s, sinking " + sink.ToString("0") + " m/s at " + aircraft.radarAlt.ToString("0") + " m · idle, opposite rudder, stick forward");
            }
            return true;
        }

        // Only these autopilots actually implement an AutoAim; anything else
        // would be flown by a method with an empty body.
        // The speed below which a fixed-wing aircraft of ours is too slow to
        // fly its orders: see the energy recovery. 0 when nothing is known.
        private const float LowBar = 150f;

        private float SlowBar()
        {
            if (parameters == null) return 0f;
            float bar = float.PositiveInfinity;
            if (parameters.cornerSpeed > 0f) bar = Mathf.Min(bar, parameters.cornerSpeed * 1.05f);
            float takeoff = WingBorneTakeoff();
            if (takeoff > 0f) bar = Mathf.Min(bar, takeoff * 1.25f);
            if (parameters.maxSpeed > 0f) bar = Mathf.Min(bar, parameters.maxSpeed * 0.6f);
            return float.IsInfinity(bar) ? 0f : bar;
        }

        // The takeoff speed, when it says anything about the wing. A
        // vertical-landing type (the FS-20 Vortex) leaves the deck on thrust at
        // 35 m/s, and a stall bar of 44 m/s read off that let ten of them mush
        // out of a 6,000 m orbit at full power and go in without the energy
        // recovery ever firing: their wing-borne stall is three times that.
        // Zero for such a type, and for any whose takeoff speed is under 40%
        // of its corner speed and whose thrust can carry most of its weight;
        // the corner and top speeds set the bar then. A short-field type
        // that cannot hold itself up on thrust (the T/A-30 Compass, T/W
        // about 0.7) takes off on its wing: read off its corner speed
        // instead, its stall came out near 72 m/s and it was held to 30
        // degrees of bank at its normal 115 m/s -- turning very slowly.
        private float WingBorneTakeoff()
        {
            if (parameters == null || parameters.takeoffSpeed <= 0f) return 0f;
            if (parameters.verticalLanding) return 0f;
            if (parameters.cornerSpeed > 0f && parameters.takeoffSpeed < parameters.cornerSpeed * 0.4f && ThrustBorne()) return 0f;
            return parameters.takeoffSpeed;
        }

        private float thrustToWeight = -1f;
        private bool ThrustBorne()
        {
            if (thrustToWeight < 0f)
            {
                float thrust = 0f;
                try { thrust = TakeoffCheck.MaxThrust(aircraft); } catch { }
                float weight = aircraft.rb != null ? aircraft.rb.mass * 9.81f : 0f;
                // Unknown thrust: assume it might, the cautious answer.
                thrustToWeight = thrust > 0f && weight > 0f ? thrust / weight : 1f;
            }
            return thrustToWeight >= 0.9f;
        }

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
            // A saturation: the chosen weapons one station after another, and
            // nothing else. All of them away, the egress takes it (FlightOrders).
            StrikeItem saturation = StrikePlans.SaturationOn(flight, target);
            WeaponStation station = saturation != null ? StrikePlans.SaturationStation(aircraft, saturation)
                : FlightOrders.NamedStation(aircraft, flight.PreferredWeapon) ??
                  (target != null ? FlightOrders.BestStationFor(aircraft, target) : null);
            if (saturation != null && station == null)
            {
                flight.SalvoLeft = 0;
                flight.Doing("SATURATION AWAY");
                Steer(aircraft.GlobalPosition() + Flat(aircraft.transform.forward).normalized * 5000f, default, 30f);
                return;
            }
            if (FlightOrders.IsAirTarget(target))
            {
                if (Bvr(flight, station)) FlyBvr(pilot, target, station);
                else CompleteRunIn(pilot, "air target");
                return;
            }
            // A missile we launch ourselves: no dive, no pressing in.
            WeaponInfo weapon = station?.WeaponInfo;
            // A glide bomb: dropped on the cue, it guides itself in.
            if (weapon != null && weapon.glideBomb && station.Ammo > 0 && !Host.Dead(target) && hq != null &&
                hq.TryGetKnownPosition(target, out GlobalPosition glideTo))
            {
                FlyGlideDrop(pilot, target, glideTo, station);
                return;
            }
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
            if (straight)
            {
                FlyBombRunIn(pilot, target, known, station);
                return;
            }

            GlobalPosition here = aircraft.GlobalPosition();
            float range = Horizontal(known, here);
            // Low enough, or simply looking down at the target at no more
            // than a shallow dive: the combat pilot shoots from there. Held
            // to the weapon's set height (300 m for rockets), a King Viper
            // reaching range at 870 m opened out and circled, again and again.
            float above = here.y - known.y;
            bool low = aircraft.radarAlt <= height + Mathf.Max(250f, height * 0.25f) ||
                (range > 1f && Mathf.Atan2(above, range) * Mathf.Rad2Deg <= ShallowDive);
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
            else if (low && lined && range < release) { CompleteRunIn(pilot, "in position"); return; }

            flight.Doing(flight.SettingUp ? "SETTING UP THE RUN" : "RUNNING IN · " + UnitConverter.DistanceReading(range));
            float ordered = flight.Altitude;
            flight.Altitude = height;
            Steer(aim);
            flight.Altitude = ordered;
            if (weapon != null && weapon.gun) { NativeSpeedLimitPatch.HoldGunSpeed(aircraft, controlInputs); IntendedThrottle = controlInputs.throttle; }
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
            if (seeker == "ARH" || seeker == "SARH") return true;
            // A helicopter, or anything as slow: ours to fly with a
            // heat-seeker too, from height. Handed to the game's combat pilot
            // it dived on them at full burner -- one FS-41 into a hill chasing
            // a helicopter it could no longer see, another torn apart at
            // 580 m/s.
            return seeker == "IR" && SlowAirTarget(flight.Target);
        }

        internal static bool SlowAirTarget(Unit target) =>
            target is Aircraft aircraft && !aircraft.disabled &&
            (aircraft.autopilot is AutopilotHelo || aircraft.autopilot is AutopilotTiltwing || aircraft.speed < 110f);

        // Beyond visual range: climb above the target while closing -- thinner
        // air and the height to fall through reach further, so it launches
        // sooner -- and launch at that range. Only with the way in clear of
        // known air defence and enemy forces, and nothing already shot at us,
        // press in to the no-escape range first. Both ranges are the game's
        // own (Missile.CalcRange, what the HUD shows a pilot), worked out
        // from our speed and height and the target's as they are now. After
        // the salvo the combat pilot has it, supporting the shot as it would.
        private const float BvrLoft = 1500f, BvrMargin = 0.95f;
        private const float SafeLowLevel = 800f;      // lowest an intercept noses down to, over the ground
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

            // Enough of ours on it already. The shot is one aircraft's to take
            // per missile allowed, in callsign order; the rest of the wing
            // covers -- and one that has fired, with the target's full count
            // of missiles closing, is done: before, it waited for the rest of
            // a salvo that discipline would never let it fire, pressing in
            // for ever, while its wingmen all read LAUNCHING.
            int allowed = ShotDisciplinePatch.AllowedOn(flight, target, info);
            int closing = ShotDisciplinePatch.Closing(hq, target);
            bool fired = flight.AmmoAtAttack >= 0 && FlightOrders.TotalAmmo(aircraft) < flight.AmmoAtAttack;
            if (closing >= allowed && fired)
            {
                flight.SalvoLeft = 0;
                if (FlightOrders.StartCrank(flight, info)) return;
                if (SlowAirTarget(target)) { FlightOrders.EgressNow(flight); return; }
                CompleteRunIn(pilot, closing + " missile(s) closing, its shot taken");
                return;
            }
            string shooter = closing >= allowed ? null : ShotTakenBy(target, allowed - closing);
            if (closing >= allowed || shooter != null)
            {
                flight.Doing("COVERING · " + (shooter != null ? shooter + " has the shot" : closing + " missile(s) closing on " + ShipNames.Of(target)));
                // Hold off at standoff: a circle round where it began covering,
                // at height, for as long as it waits (the intercept's own
                // time limit is not run down meanwhile).
                if (!covering) { covering = true; coverCentre = here - toTarget.normalized * 1000f; }
                flight.RunInStarted = Time.timeSinceLevelLoad;
                float held = flight.Altitude;
                flight.Altitude = Mathf.Max(here.y - ground, MinimumClearance);
                FlyOrbit(coverCentre, 3000f);
                flight.Altitude = held;
                return;
            }
            covering = false;

            flight.Doing(range > launchAt
                ? (bvrClosingIn ? "CLOSING IN AT " + (BvrHold / 1000f).ToString("0") + " km" : clear ? "PRESSING IN · " + UnitConverter.DistanceReading(range) : "CLIMBING TO LAUNCH · " + (loft / 1000f).ToString("0") + " km")
                : "LAUNCHING · " + UnitConverter.DistanceReading(range));
            float ordered = flight.Altitude;
            flight.Altitude = loft - ground;
            try
            {
                if (range > launchAt) { Steer(known); return; }

                // A target well below the nose -- high over a low helicopter --
                // never comes into the launch cone flying level at the loft
                // height: give the height up and point at it.
                float coneLimit = needs.minAlignment > 0f ? needs.minAlignment : 30f;
                Vector3 flatTo = toTarget; flatTo.y = 0f;
                float below = Mathf.Atan2(here.y - known.y, Mathf.Max(flatTo.magnitude, 1f)) * Mathf.Rad2Deg;
                // Only as far down as brings it to half the cone at this range,
                // and never below 800 m over the ground: diving to 300 m over
                // a helicopter among hills is how one went in.
                if (below > coneLimit * 0.6f)
                {
                    float over = flatTo.magnitude * Mathf.Tan(coneLimit * 0.5f * Mathf.Deg2Rad);
                    flight.Altitude = Mathf.Max(known.y - ground + over, SafeLowLevel, MinimumClearance);
                    flight.Doing("LAUNCHING · nose down · " + UnitConverter.DistanceReading(range));
                }

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

                if (flight.SalvoLeft <= 0) flight.SalvoLeft = Mathf.Clamp(allowed - closing, 1, Mathf.Max(station.Ammo, 1));
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
                if (station.Ammo <= 0 || flight.SalvoLeft <= 0)
                {
                    if (FlightOrders.StartCrank(flight, info)) return;
                    if (SlowAirTarget(target)) { FlightOrders.EgressNow(flight); return; }
                    CompleteRunIn(pilot, "launched");
                }
            }
            finally { flight.Altitude = ordered; }
        }

        // A glide bomb, dropped on the cockpit's cue rather than the combat
        // pilot's, which waits for a track accurate to a few tens of metres
        // and flew a flight straight over its targets at height without a
        // release. Level at the flight's height, full power for the glide,
        // nose on the target; dropped once the bomb can reach it -- the
        // game's own glide test, height above the target plus the speed's
        // worth of energy over the distance -- and the target is inside its
        // release cone. The bomb guides itself in.
        internal static bool GlideReach(Aircraft aircraft, WeaponInfo info, GlobalPosition known)
        {
            GlobalPosition here = aircraft.GlobalPosition();
            Vector3 to = known - here;
            float along = new Vector3(to.x, 0f, to.z).magnitude;
            if (along < Mathf.Max(info.targetRequirements.minRange, 1f)) return false;
            float drop = here.y - known.y + aircraft.speed * aircraft.speed * 0.03f;
            float cone = info.targetRequirements.minAlignment > 0f ? info.targetRequirements.minAlignment : 30f;
            return drop / along > GlideSlope && Vector3.Angle(aircraft.transform.forward, to) < cone;
        }
        private const float GlideSlope = 0.22f;     // the game's own test is 0.2; a little in hand

        private void FlyGlideDrop(Pilot pilot, Unit target, GlobalPosition known, WeaponStation station)
        {
            WeaponInfo info = station.WeaponInfo;
            if (Time.timeSinceLevelLoad - flight.RunInStarted > 240f) { CompleteRunIn(pilot, "glide run timed out"); return; }
            if (Horizontal(known, aircraft.GlobalPosition()) < Mathf.Max(info.targetRequirements.minRange, 300f)) { CompleteRunIn(pilot, "too close for a glide"); return; }
            controlInputs.throttle = 1f;
            flight.Doing("GLIDE RUN · " + UnitConverter.DistanceReading(Horizontal(known, aircraft.GlobalPosition())));
            Steer(known);
            if (!GlideReach(aircraft, info, known) || Time.timeSinceLevelLoad - flight.LastLaunchAt < 2f) return;
            StrikeItem saturation = StrikePlans.SaturationOn(flight, target);
            if (saturation != null) flight.SalvoLeft = Mathf.Max(StrikePlans.SaturationRounds(aircraft, saturation), 1);
            else if (flight.SalvoLeft <= 0) flight.SalvoLeft = Mathf.Clamp(ShotDisciplinePatch.AllowedOn(flight, target, info), 1, Mathf.Max(station.Ammo, 1));
            aircraft.weaponManager.currentWeaponStation = station;
            List<Unit> targets = aircraft.weaponManager.GetTargetList();
            targets.Clear();
            int found = CombatAI.LookForMissileTargets(aircraft, target, station, targets);
            aircraft.weaponManager.TargetListChanged();
            if (found <= 0) { targets.Add(target); aircraft.weaponManager.TargetListChanged(); }
            int before = station.Ammo;
            pilot.Fire();
            flight.LastLaunchAt = Time.timeSinceLevelLoad;
            if (station.Ammo < before)
            {
                flight.SalvoLeft--;
                Tracing.Flight("[flight] " + flight.Name + " · glide bomb away · " + (info.weaponName ?? "bomb") + " at " +
                    (Horizontal(known, aircraft.GlobalPosition()) / 1000f).ToString("0.0") + " km from " + aircraft.radarAlt.ToString("0") + " m");
            }
            // A saturation carries on with its next weapon; the egress follows the last.
            if (saturation != null) { flight.SalvoLeft = StrikePlans.SaturationRounds(aircraft, saturation); return; }
            if (station.Ammo <= 0 || flight.SalvoLeft <= 0) CompleteRunIn(pilot, "glide bombs away");
        }

        // Unguided (level) bombs, released by us: at the flight's height,
        // flown at the target, each bomb's fall worked out the way the
        // cockpit's CCIP pipper works it -- the aircraft's velocity, a small
        // push down off the rack, gravity and the bomb's own drag -- and the
        // bombs released, a short stick, as that impact point reaches the
        // target. Bombs steer a little in the last of their fall, so close is
        // close enough. Overflown with nothing away, it comes round again.
        private const float BombLead = 60f;         // release this far short, so a stick straddles it
        private const float BombWindow = 200f;      // and still this far past
        private const float BombCross = 150f;       // off to the side, at most
        private const float BombInterval = 0.25f;

        // The run in for unguided bombs: no descent -- a bomb's fall is
        // worked out from whatever height the flight is at, and it guides
        // itself the last of the way -- only a heading. Far enough out, the
        // flight turns onto the target and the bomb run (FlyLevelDrop) takes
        // it from there. Too close to line up before the bombs' forward throw
        // -- how far ahead they would land, released now -- it opens out
        // toward friendly lines by that throw, a turn and a kilometre, and
        // comes round.
        private void FlyBombRunIn(Pilot pilot, Unit target, GlobalPosition known, WeaponStation station)
        {
            if (flight.BombRun && station.Ammo > 0) { FlyLevelDrop(pilot, target, known, station); return; }
            if (Time.timeSinceLevelLoad - flight.RunInStarted > 420f) { CompleteRunIn(pilot, "bomb run-in timed out"); return; }
            GlobalPosition here = aircraft.GlobalPosition();
            float range = Horizontal(known, here);
            float forward = Horizontal(BombImpact(aircraft, station.WeaponInfo, known.y), here);
            float turn = aircraft.speed * aircraft.speed / (9.81f * 3f);        // a 3 g turn's radius
            Vector3 toTarget = known - here; toTarget.y = 0f;
            Vector3 track = Flat(aircraft.rb != null ? aircraft.rb.velocity : aircraft.transform.forward);
            // Flat() is already a unit vector: tested against 1 it read as
            // nothing, offTrack fell to 0 and a flight flying across the
            // target's bearing was "on the line" -- and overshot, every frame.
            float offTrack = toTarget.sqrMagnitude > 1f ? Vector3.Angle(track, toTarget) : 0f;
            float room = forward + turn + 1000f;

            if (!flight.SettingUp)
            {
                if (offTrack <= BombLine && range > forward - BombLead)
                {
                    flight.BombRun = true;
                    flight.BombsThisPass = 0;
                    flight.NotedNoLock = false;
                    Tracing.Flight("[flight] " + flight.Name + " · on the line · bomb run from " + UnitConverter.DistanceReading(range) +
                        " at " + aircraft.radarAlt.ToString("0") + " m · bombs carry " + UnitConverter.DistanceReading(forward) + " forward");
                    FlyLevelDrop(pilot, target, known, station);
                    return;
                }
                if (range < room * (offTrack > BombLine ? offTrack / 90f + 0.3f : 1f))
                {
                    flight.SettingUp = true;
                    Tracing.Flight("[flight] " + flight.Name + " · too close to line up the bombs (" + offTrack.ToString("0") + "° off, " +
                        UnitConverter.DistanceReading(range) + ", they carry " + UnitConverter.DistanceReading(forward) + ") · opening out");
                }
            }

            GlobalPosition aim = known;
            if (flight.SettingUp)
            {
                Vector3 friendly = flight.HomePosition - known;
                friendly.y = 0f;
                if (friendly.sqrMagnitude < 1f) friendly = here - known;
                friendly.y = 0f;
                if (friendly.sqrMagnitude < 1f) friendly = -aircraft.transform.forward;
                GlobalPosition setUp = known + friendly.normalized * (room + 1000f);
                if (Horizontal(setUp, here) < 1500f || range >= room + 500f)
                {
                    flight.SettingUp = false;
                    Tracing.Flight("[flight] " + flight.Name + " · turning in for the bomb run");
                }
                else aim = setUp;
            }
            flight.Doing(flight.SettingUp ? "SETTING UP THE RUN" : "RUNNING IN · " + UnitConverter.DistanceReading(range));
            Steer(aim);
        }
        private const float ShallowDive = 15f;      // degrees down to the target the combat pilot is handed a run at: inside its 20-degree attack cone
        // On a strike, firmer still: at 25 degrees a Vagrant run-in circled
        // the target at 7.5 km for a minute before coming round.
        private const float StrikeTurn = 50f;
        private const float BankingTurn = 25f;      // degrees off asked for, at the least: past the autopilot's 20-degree yaw zone
        private const float BombLine = 8f;          // degrees off the target's bearing that counts as on the line

        private void FlyLevelDrop(Pilot pilot, Unit target, GlobalPosition known, WeaponStation station)
        {
            WeaponInfo info = station.WeaponInfo;
            if (Time.timeSinceLevelLoad - flight.RunInStarted > 420f) { flight.BombRun = false; CompleteRunIn(pilot, "bomb run timed out"); return; }
            GlobalPosition here = aircraft.GlobalPosition();
            Vector3 track = Flat(aircraft.rb != null ? aircraft.rb.velocity : aircraft.transform.forward);

            GlobalPosition impact = BombImpact(aircraft, info, known.y);
            Vector3 miss = impact - known; miss.y = 0f;
            float along = Vector3.Dot(miss, track);                 // + : falling past the target
            Vector3 side = miss - track * along;
            float across = side.magnitude;

            // A bomb steers onto its target only if it is released with one
            // -- a lock -- and the seeker starts from the faction's known
            // position: released on a stale track it falls on the old spot.
            // The game's own AI will not bomb without a track good to 50 m.
            bool locked = FlightOrders.CanReleaseNow(aircraft, info, target);
            flight.Doing("BOMB RUN · " + UnitConverter.DistanceReading(Horizontal(known, here)) + (locked ? "" : " · NO LOCK"));
            // Steer the fall onto the target rather than the nose: aim off by
            // however far to the side the bombs would land.
            Steer(known - side);

            if (along > BombWindow)
            {
                flight.BombRun = false;
                if (flight.BombsThisPass > 0) { CompleteRunIn(pilot, flight.BombsThisPass + " bomb(s) away"); return; }
                flight.SettingUp = true;
                flight.NotedNoLock = false;
                Tracing.Flight("[flight] " + flight.Name + " · bomb run overshot (" + across.ToString("0") + " m off the line) · coming round");
                return;
            }
            if (along < -BombLead || across > BombCross || Time.timeSinceLevelLoad - flight.LastLaunchAt < BombInterval) return;
            if (!locked)
            {
                if (!flight.NotedNoLock)
                {
                    flight.NotedNoLock = true;
                    Tracing.Flight("[flight] " + flight.Name + " · at the release point without a track good to 50 m · holding the bombs");
                }
                return;
            }

            if (flight.BombsThisPass == 0)
                flight.SalvoLeft = Mathf.Clamp(ShotDisciplinePatch.AllowedOn(flight, target, info), 1, Mathf.Max(station.Ammo, 1));
            aircraft.weaponManager.currentWeaponStation = station;
            List<Unit> targets = aircraft.weaponManager.GetTargetList();
            targets.Clear();
            targets.Add(target);
            aircraft.weaponManager.TargetListChanged();
            int before = station.Ammo;
            pilot.Fire();
            flight.LastLaunchAt = Time.timeSinceLevelLoad;
            if (station.Ammo < before)
            {
                flight.SalvoLeft--;
                flight.BombsThisPass++;
                if (flight.BombsThisPass == 1)
                    Tracing.Flight("[flight] " + flight.Name + " · bombs away · " + (info.weaponName ?? "bomb") + " from " +
                        aircraft.radarAlt.ToString("0") + " m at " + aircraft.speed.ToString("0") + " m/s · predicted " +
                        along.ToString("0") + " m along, " + across.ToString("0") + " m across · locked on " + ShipNames.Of(target));
            }
            if (station.Ammo <= 0 || flight.SalvoLeft <= 0)
            {
                flight.BombRun = false;
                CompleteRunIn(pilot, flight.BombsThisPass + " bomb(s) away");
            }
        }

        // Where a bomb released now comes down at this height: the cockpit
        // CCIP's own model (HUDBombingState.CCIPTrajectory), in even steps.
        private static readonly Dictionary<WeaponInfo, float> bombDrag = new Dictionary<WeaponInfo, float>();
        internal static GlobalPosition BombImpact(Aircraft aircraft, WeaponInfo info, float groundY)
        {
            if (!bombDrag.TryGetValue(info, out float drag))
            {
                drag = 0f;
                try
                {
                    Missile prefab = info.weaponPrefab != null ? info.weaponPrefab.GetComponent<Missile>() : null;
                    if (prefab != null && info.massPerRound > 0f)
                        drag = 0.5f * prefab.GetDragCoef(Mathf.PI / 360f) * prefab.GetFinArea() / info.massPerRound;
                }
                catch { drag = 0f; }
                bombDrag[info] = drag;
            }
            GlobalPosition p = aircraft.GlobalPosition() - Vector3.up * aircraft.definition.spawnOffset.y;
            float k = drag * LevelInfo.GetAirDensity(p.y);
            Vector3 v = aircraft.rb.velocity - Vector3.up * 9.81f * 0.25f + info.muzzleVelocity * aircraft.transform.forward;
            const float dt = 0.1f;
            for (int i = 0; i < 1200; i++)
            {
                GlobalPosition next = p + v * dt;
                if (next.y <= groundY)
                {
                    float t = (p.y - groundY) / Mathf.Max(p.y - next.y, 0.001f);
                    return p + v * dt * t;
                }
                p = next;
                v -= (Vector3.up * 9.81f + v.normalized * k * v.sqrMagnitude) * dt;
            }
            return p;
        }

        private void FlyStandoffLaunch(Pilot pilot, Unit target, GlobalPosition known, WeaponStation station)
        {
            WeaponInfo info = station.WeaponInfo;
            TargetRequirements needs = info.targetRequirements;
            GlobalPosition here = aircraft.GlobalPosition();
            float range = Horizontal(known, here);
            float launch = Mathf.Max(needs.maxRange * 0.85f, needs.minRange * 1.5f);

            if (range < needs.minRange * 1.1f) { CompleteRunIn(pilot, "inside minimum range"); return; }
            flight.Doing((range > launch ? "STANDOFF RUN · " : "LAUNCHING · ") + UnitConverter.DistanceReading(range));
            if (range > launch)
            {
                flight.InLaunchRangeSince = -1f;
                Steer(known);                                  // at the ordered height: Steer holds it
                return;
            }
            // A saturation fired together: in range, hold near the launch
            // point until the rest of the wing is in range too.
            StrikeItem saturation = StrikePlans.SaturationOn(flight, target);
            if (saturation != null)
            {
                flight.SalvoLeft = Mathf.Max(StrikePlans.SaturationRounds(aircraft, saturation), 1);
                if (StrikePlans.HoldForWing(flight, saturation, out string waiting))
                {
                    flight.InLaunchRangeSince = -1f;
                    flight.Doing("HOLDING FOR THE WING · " + waiting);
                    float radius = Mathf.Clamp(launch * 0.2f, 800f, 2500f);
                    Vector3 back = here - known; back.y = 0f;
                    if (back.sqrMagnitude < 1f) back = -aircraft.transform.forward;
                    FlyOrbit(known + back.normalized * Mathf.Max(launch - radius - 300f, needs.minRange * 1.5f + radius), radius);
                    return;
                }
            }
            if (flight.InLaunchRangeSince < 0f) flight.InLaunchRangeSince = Time.timeSinceLevelLoad;
            if (Time.timeSinceLevelLoad - flight.InLaunchRangeSince > 60f) { CompleteRunIn(pilot, "no launch in a minute"); return; }

            // Its shot taken and the target's full count of missiles closing
            // (some of them a wingman's): the salvo is done, egress.
            if (saturation == null && flight.AmmoAtAttack >= 0 && FlightOrders.TotalAmmo(aircraft) < flight.AmmoAtAttack &&
                ShotDisciplinePatch.Closing(aircraft.NetworkHQ, target) >= ShotDisciplinePatch.AllowedOn(flight, target, info))
            {
                flight.SalvoLeft = 0;
                return;
            }

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
            // A saturation goes as fast as the racks allow.
            if (off > cone * 0.9f || Time.timeSinceLevelLoad - flight.LastLaunchAt < (saturation != null ? 0.6f : 2.5f)) return;
            if (aircraft.speed < needs.minOwnerSpeed) return;
            if (saturation != null && (!station.Ready() || station.SalvoInProgress)) return;

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
            if (saturation != null) flight.SalvoLeft = StrikePlans.SaturationRounds(aircraft, saturation);
        }

        // Who in the wing takes the shot at this target: the first `shots`
        // members, in callsign order, striking it with something that can
        // reach it and not yet done. Null when this aircraft is one of them;
        // otherwise the name of the first that is.
        private bool covering;
        private GlobalPosition coverCentre;

        private string ShotTakenBy(Unit target, int shots)
        {
            if (flight.Wing == null || shots <= 0) return null;
            int ahead = 0;
            string first = null;
            foreach (Flight member in Wings.Members(flight.Wing))
            {
                if (member == flight) return ahead < shots ? null : first;
                if (member.Aircraft == null || member.Aircraft.disabled || member.Mode != FlightMode.Strike || member.Target != target) continue;
                if (FlightOrders.BestStationFor(member.Aircraft, target) == null) continue;
                bool done = member.AmmoAtAttack >= 0 && FlightOrders.TotalAmmo(member.Aircraft) < member.AmmoAtAttack;
                if (done) continue;
                if (first == null) first = member.Name;
                ahead++;
            }
            return null;
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

        // Home, in order (RecoveryQueue): a fixed-wing flight flies back to
        // its field itself, joins the marshal stack overhead within range, and
        // is handed to the game's approach only when cleared -- one at a time,
        // and never to a ship that is turning (DeckWaveOff). Helicopters land
        // on their own pads and go straight to the game's landing.
        private bool marshalling;
        private void FlyHome(Pilot pilot)
        {
            Airbase field = flight.Home;
            if (field == null || field.disabled || !(aircraft.autopilot is AutopilotPlane))
            {
                marshalling = false;
                HandBackToLanding(pilot);
                return;
            }
            GlobalPosition centre = Airfields.PositionOf(field);
            Ship deck = Airfields.ShipOf(field);
            Vector3 astern = deck != null ? -Flat(deck.transform.forward).normalized : Vector3.zero;
            // The stack: overhead a land field, astern of a ship.
            GlobalPosition stack = deck != null ? centre + astern * RecoveryQueue.MarshalAstern : centre;
            float distance = Horizontal(stack, aircraft.GlobalPosition());
            string name = Airfields.NameOf(field);
            if (distance > RecoveryQueue.MarshalRange)
            {
                flight.Doing("RETURNING · " + UnitConverter.DistanceReading(Horizontal(centre, aircraft.GlobalPosition())) + " to " + name);
                Steer(stack);
                return;
            }
            if (RecoveryQueue.Cleared(flight, field, out int place, out string why))
            {
                // Its turn: straight to the game's own approach. From the stack
                // astern the native landing lines itself up well enough; the
                // legs flown first only made the turn-in slow.
                if (marshalling) Host.LogInfo("[flight] " + flight.Name + " · on the approach to " + name);
                marshalling = false;
                HandBackToLanding(pilot);
                return;
            }
            if (!marshalling)
            {
                marshalling = true;
                Host.LogInfo("[flight] " + flight.Name + " · marshalling " + (deck != null ? "astern of " : "over ") + name + (why != null ? " · " + why : ""));
            }
            // The next to land lowest, each after it higher.
            float held = flight.Altitude;
            flight.Altitude = RecoveryQueue.MarshalBase + Mathf.Max(place - 1, 0) * RecoveryQueue.MarshalStep;
            flight.Doing("MARSHAL · " + (place > 0 ? "#" + place + " to land" : "holding") + (why != null ? " · " + why : "") + " · " + name);
            FlyOrbit(stack, RecoveryQueue.MarshalRadius);
            flight.Altitude = held;
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
