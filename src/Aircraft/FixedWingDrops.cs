using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // Airdrops from fixed-wing transports.
    //
    // The game gives only rotary aircraft a transport state. Aryx's MC-260
    // Chimera adds one for aeroplanes -- AIFixedWingTransportState, a run-in
    // at a few hundred metres and a parachute release -- which it enters by
    // itself for any AI aeroplane with cargo aboard, and then picks its own
    // drop point every few seconds: the nearest enemy or objective. A
    // delivery order hands one of ours to that state and replaces its choice
    // of point with the one ordered; the run-in, release and egress stay the
    // Chimera's. Found by name, since the addon may be absent or load after
    // us, and patched on first use for the same reason.
    internal static class FixedWingDrops
    {
        private static Type state;
        private static float nextLookup;
        private static bool patched;
        private static FieldInfo missionValid, targetUnit, dropPoint, cargoStation;
        private static MethodInfo setMission, findGround, cargoStationOf;
        private static Type modeType;
        private static readonly FieldInfo AircraftOf = AccessTools.Field(typeof(PilotBaseState), "aircraft");
        private static FieldInfo runIn, rejoining, configOf;
        private static readonly Dictionary<PilotBaseState, float> nextTrace = new Dictionary<PilotBaseState, float>();
        private static readonly HashSet<Aircraft> tuned = new HashSet<Aircraft>();

        // What we last pointed each state at, so the run-in is not restarted
        // every time the state looks for a mission.
        private static readonly Dictionary<PilotBaseState, GlobalPosition> applied = new Dictionary<PilotBaseState, GlobalPosition>();

        internal static Type State()
        {
            if (state != null || Time.unscaledTime < nextLookup) return state;
            nextLookup = Time.unscaledTime + 10f;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                foreach (Type type in types)
                {
                    if (type == null || type.Name != "AIFixedWingTransportState" || !typeof(PilotBaseState).IsAssignableFrom(type)) continue;
                    state = type;
                    Bind(assembly);
                    return state;
                }
            }
            return null;
        }

        private static void Bind(Assembly assembly)
        {
            missionValid = AccessTools.Field(state, "missionValid");
            targetUnit = AccessTools.Field(state, "targetUnit");
            dropPoint = AccessTools.Field(state, "dropPoint");
            cargoStation = AccessTools.Field(state, "cargoStation");
            setMission = AccessTools.Method(state, "SetMission");
            findGround = AccessTools.Method(state, "TryFindGroundDropPoint");
            runIn = AccessTools.Field(state, "runInDirection");
            rejoining = AccessTools.Field(state, "runInRejoining");
            configOf = AccessTools.Field(state, "config");
            modeType = setMission?.GetParameters().Length == 4 ? setMission.GetParameters()[2].ParameterType : null;
            Type selector = null;
            try { foreach (Type type in assembly.GetTypes()) if (type.Name == "FixedWingTransportSelector") { selector = type; break; } }
            catch { }
            cargoStationOf = selector != null ? AccessTools.Method(selector, "TryGetCargoStation") : null;
            bool whole = missionValid != null && targetUnit != null && dropPoint != null && cargoStation != null &&
                setMission != null && modeType != null && cargoStationOf != null;
            Host.LogInfo("[flight] fixed-wing transport state found in " + assembly.GetName().Name +
                (whole ? "" : " · but not all of it is where expected; airdrop orders will not be taken"));
            if (!whole) state = null;
        }

        // An aeroplane that can airdrop: the state is loaded and it has cargo
        // aboard for the state to release.
        internal static bool CanAirdrop(Aircraft aircraft)
        {
            Pilot crew = FlightOrders.FirstPilot(aircraft);
            if (crew == null || crew.pilotType != Pilot.PilotType.Plane || State() == null) return false;
            return StationOf(aircraft) != null;
        }

        private static WeaponStation StationOf(Aircraft aircraft)
        {
            if (cargoStationOf == null || aircraft == null) return null;
            object[] args = { aircraft, null };
            try { return (bool)cargoStationOf.Invoke(null, args) ? args[1] as WeaponStation : null; }
            catch { return null; }
        }

        internal static bool Flying(Pilot pilot) => state != null && pilot?.currentState != null && state.IsInstanceOfType(pilot.currentState);

        internal static bool Start(Pilot pilot)
        {
            if (State() == null || pilot == null) return false;
            if (Flying(pilot)) return true;
            if (!Patch()) return false;
            PilotBaseState next;
            try { next = (PilotBaseState)Activator.CreateInstance(state); }
            catch (Exception ex) { Host.LogWarning("[flight] fixed-wing transport state: " + ex.Message); return false; }
            pilot.SwitchStateNew(next);
            return true;
        }

        private static bool Patch()
        {
            if (patched) return true;
            MethodInfo search = AccessTools.Method(state, "SearchForMission");
            if (search == null) { Host.LogWarning("[flight] fixed-wing transport state has no SearchForMission"); return false; }
            try
            {
                new Harmony("NOrders.FixedWingDrops." + Host.ModId).Patch(search,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(FixedWingDrops), nameof(SearchPrefix))));
                patched = true;
            }
            catch (Exception ex) { Host.LogWarning("[flight] fixed-wing transport patch: " + ex.Message); }
            return patched;
        }

        // Ours, on a delivery: the ordered point, not the state's own choice.
        // Anyone else's aircraft goes through untouched.
        private static bool SearchPrefix(PilotBaseState __instance)
        {
            if (!Guard.Ok("FixedWingDrops")) return true;
            bool run = true;
            Guard.Run("FixedWingDrops", () => run = !Steer(__instance));
            return run;
        }

        private static bool Steer(PilotBaseState instance)
        {
            Aircraft aircraft = AircraftOf?.GetValue(instance) as Aircraft;
            Flight flight = aircraft != null ? FlightOrders.Of(aircraft) : null;
            if (flight == null || flight.Mode != FlightMode.Cargo) { applied.Remove(instance); return false; }

            WeaponStation station = StationOf(aircraft);
            cargoStation.SetValue(instance, station);
            if (station == null) { missionValid.SetValue(instance, false); return true; }
            aircraft.weaponManager.currentWeaponStation = station;

            GlobalPosition want = flight.CargoPoint;
            if (findGround != null)
            {
                object[] args = { flight.CargoPoint, 400f, null };
                try { if ((bool)findGround.Invoke(instance, args)) want = (GlobalPosition)args[2]; }
                catch { }
            }
            bool current = applied.TryGetValue(instance, out GlobalPosition was) && (bool)missionValid.GetValue(instance) &&
                targetUnit.GetValue(instance) == null && FastMath.Distance(was, flight.CargoPoint) < 450f;
            if (current)
            {
                // Held where it was first put, not re-rolled every search.
                dropPoint.SetValue(instance, was);
            }
            else
            {
                missionValid.SetValue(instance, false);
                targetUnit.SetValue(instance, null);
                setMission.Invoke(instance, new object[] { want, null, Enum.ToObject(modeType, 0), true });
                applied[instance] = want;
                Tracing.Flight("[flight] " + flight.Name + " · airdrop run-in to the ordered point");
            }
            instance.stateDisplayName = "Airdrop at ordered point";
            Tune(instance, aircraft, flight);
            Trace(instance, aircraft, flight);
            return true;
        }

        // The game's autopilot scales the height it is asked to hold by
        // airspeed over landing speed, down to a tenth. The Chimera lands at
        // 120 m/s and runs its drop at 1.35 times that, where 250 m asked for
        // comes out near 125 -- under the 150 m it will release from, so it
        // flew every pass too low to drop. Asking ours for more lands the
        // held height inside the release band.
        private static void Tune(PilotBaseState instance, Aircraft aircraft, Flight flight)
        {
            if (tuned.Contains(aircraft) || configOf == null) return;
            tuned.Add(aircraft);
            object config = configOf.GetValue(instance);
            if (config == null) return;
            FieldInfo preferred = AccessTools.Field(config.GetType(), "preferredDropRadarAltitude");
            FieldInfo maximum = AccessTools.Field(config.GetType(), "maximumDropRadarAltitude");
            // The whole load on one pass, as a helicopter's is.
            FieldInfo perPass = AccessTools.Field(config.GetType(), "cargoReleaseCountPerPass");
            int aboard = FlightOrders.CargoAboard(aircraft);
            if (perPass != null && (int)perPass.GetValue(config) < aboard) perPass.SetValue(config, aboard);
            if (preferred == null || maximum == null) return;
            float was = (float)preferred.GetValue(config);
            float want = Mathf.Min((float)maximum.GetValue(config) * 0.93f, 420f);
            if (was >= want) return;
            preferred.SetValue(config, want);
            Host.LogInfo("[flight] " + flight.Name + " · drop height asked of the autopilot " + was.ToString("0") + " -> " + want.ToString("0") +
                " m, so its airspeed scaling still holds it inside the release band");
        }

        // Every few seconds on the run: where it is against the Chimera's own
        // release conditions, so a pass that drops nothing can be read off the log.
        private static void Trace(PilotBaseState instance, Aircraft aircraft, Flight flight)
        {
            if (nextTrace.TryGetValue(instance, out float next) && Time.timeSinceLevelLoad < next) return;
            nextTrace[instance] = Time.timeSinceLevelLoad + 3f;
            object config = configOf?.GetValue(instance);
            if (config == null || runIn == null) return;
            float F(string name) { FieldInfo f = AccessTools.Field(config.GetType(), name); return f != null ? (float)f.GetValue(config) : float.NaN; }
            GlobalPosition point = (GlobalPosition)dropPoint.GetValue(instance);
            Vector3 dir = (Vector3)runIn.GetValue(instance);
            Vector3 velocity = aircraft.rb != null ? aircraft.rb.velocity : Vector3.zero;
            velocity.y = 0f;
            float descent = F("parachuteDeploymentDelay") + Mathf.Max(aircraft.radarAlt, 0f) / Mathf.Max(F("parachuteDescentSpeed"), 0.1f);
            Vector3 lands = (aircraft.GlobalPosition() + velocity * (F("horizontalVelocityRetention") * descent)) - point;
            lands.y = 0f;
            Vector3 to = point - aircraft.GlobalPosition();
            to.y = 0f;
            float heading = Vector3.Angle(velocity, dir);
            float cross = Mathf.Abs(Vector3.Dot(to, Vector3.Cross(Vector3.up, dir)));
            float along = Vector3.Dot(to, dir);
            Tracing.Flight("[flight] " + flight.Name + " · drop run · " + (to.magnitude / 1000f).ToString("0.0") + " km to go (" +
                along.ToString("0") + " along, " + cross.ToString("0") + " across, limit " + F("maximumReleaseCrossTrackError").ToString("0") +
                ") · radar alt " + aircraft.radarAlt.ToString("0") + " m (release " + F("minimumDropRadarAltitude").ToString("0") + "-" +
                F("maximumDropRadarAltitude").ToString("0") + ") · " + aircraft.speed.ToString("0") + " m/s · heading off the run " +
                heading.ToString("0") + "° (limit " + F("maximumReleaseHeadingError").ToString("0") + ") · cargo would land " +
                lands.magnitude.ToString("0") + " m off (limit " + F("releaseRadius").ToString("0") + ")" +
                (rejoining != null && (bool)rejoining.GetValue(instance) ? " · coming round again" : "") +
                " · " + instance.stateDisplayName);
        }

        internal static void Forget()
        {
            var stale = new List<PilotBaseState>();
            foreach (PilotBaseState key in applied.Keys)
                if (!(AircraftOf?.GetValue(key) is Aircraft aircraft) || aircraft == null || aircraft.disabled) stale.Add(key);
            foreach (PilotBaseState key in stale) { applied.Remove(key); nextTrace.Remove(key); }
            tuned.RemoveWhere(a => a == null || a.disabled);
        }
    }
}
