using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // Heat-seekers: flared off, with the engines cold and the missile on the
    // beam, not run from.
    //
    // The seeker is decoyed when the flares' glare outweighs the aircraft's
    // heat; that heat is the engine's power setting, with the afterburner on
    // top, and it counts three times over from dead astern against once from
    // abeam. And a flare only counts for as much as it separates from the
    // aircraft in the seeker's eye: trailing straight back at a missile on
    // the tail it barely registers. So: throttle to idle the moment a
    // heat-seeker is inbound, a string of flares, repeated while it keeps
    // coming, and -- unless on an attack run, which is held -- a turn to put
    // the shot on the beam, flown by our own state (NavalPilotState.FlyBeam).
    // Radar-guided shots are still handed to the native pilot -- flares do
    // nothing for those.
    //
    // Pre-flaring: inside the reach of an IR launcher the faction knows about,
    // a striking flight drops a flare every couple of seconds on the run in, so
    // a shot fired without warning meets flares already in the air. A reserve
    // is kept back for the shots that do come.
    internal static class IrDefence
    {
        private static readonly Dictionary<WeaponInfo, bool> heatSeeking = new Dictionary<WeaponInfo, bool>();
        private static readonly List<(Unit unit, float reach)> launchers = new List<(Unit, float)>();
        private static float nextLauncherScan;

        internal static bool IsHeatSeeking(WeaponInfo info)
        {
            if (info == null) return false;
            if (heatSeeking.TryGetValue(info, out bool known)) return known;
            bool ir = info.weaponPrefab != null && info.weaponPrefab.GetComponentInChildren<IRSeeker>(true) != null;
            heatSeeking[info] = ir;
            return ir;
        }

        // Called from the threat pass, four times a second.
        internal static void Defend(Flight flight, Aircraft aircraft, bool missile, bool infrared, float shotRange)
        {
            // Our flights only, flown by their AI: never another faction's or an
            // AI wingman the game owns, and never an aircraft you have the
            // controls of yourself.
            if (aircraft.countermeasureManager == null) return;
            if (Host.IsFlownByPlayer(flight)) return;
            float now = Time.timeSinceLevelLoad;
            float flares = FlareFraction(aircraft);

            if (missile && infrared)
            {
                // Engines cold for as long as it is coming.
                if (!flight.StandOn) flight.ThrottleCutUntil = now + 0.5f;
                // A fresh shot, or the last one gone past: a new string. A
                // string that did not shake it is followed by another after a
                // pause, while the flares last.
                if (shotRange > Tuning.IrBurstRange * 1.5f) flight.FlaresThisShot = 0;
                if (flight.FlaresThisShot >= Tuning.IrBurstFlares && now - flight.LastBurstAt >= Tuning.IrBurstPause &&
                    flares > Tuning.FlareReserve * 0.5f)
                    flight.FlaresThisShot = 0;
                if (shotRange <= Tuning.IrBurstRange && flight.FlaresThisShot < Tuning.IrBurstFlares &&
                    now >= flight.NextFlare && flares > 0f)
                {
                    aircraft.countermeasureManager.PopFlares();
                    flight.FlaresThisShot++;
                    flight.NextFlare = now + 0.3f;
                    flight.LastBurstAt = now;
                    if (flight.FlaresThisShot == 1)
                        Tracing.Flight("[flight] " + flight.Name + " · heat-seeker at " +
                            UnitConverter.DistanceReading(shotRange) + " · idle, flaring" +
                            (flight.Mode == FlightMode.Strike ? ", holding the run" : flight.StandOn ? ", holding its orders" : ", turning to the beam"));
                }
                return;
            }
            if (!missile) flight.FlaresThisShot = 0;
            if (flight.Mode != FlightMode.Strike) return;

            // On the attack itself -- not while opening out to set it up.
            bool attacking = flight.RunInDone || !flight.SettingUp;
            if (!Tuning.PreFlare || !attacking || now < flight.NextPreFlare) return;
            if (flares <= Tuning.FlareReserve) return;
            if (!IrLauncherInReach(aircraft)) return;
            aircraft.countermeasureManager.PopFlares();
            flight.NextPreFlare = now + Tuning.PreFlareInterval;
        }

        // The power to hold while a heat-seeker is on us: just under where the
        // afterburner lights, read off the aircraft's own nozzles, so the
        // engine is as cool as it gets without the aircraft falling out of
        // the sky -- idling through a hard turn left fighters at eighty
        // metres a second, and two went into the sea. Below corner speed,
        // full power regardless: a stall is a surer death than a warm engine.
        private static readonly Dictionary<AircraftDefinition, float> evasionThrottle = new Dictionary<AircraftDefinition, float>();

        internal static float EvasionThrottle(Aircraft aircraft)
        {
            if (aircraft == null) return 0.6f;
            AircraftParameters parameters = aircraft.GetAircraftParameters();
            if (parameters != null && parameters.cornerSpeed > 0f && aircraft.speed < parameters.cornerSpeed * 1.05f) return 1f;
            AircraftDefinition def = aircraft.definition;
            if (def != null && evasionThrottle.TryGetValue(def, out float known)) return known;
            float start = 1f;
            foreach (JetNozzle nozzle in aircraft.GetComponentsInChildren<JetNozzle>(true))
            {
                if (!(Traverse.Create(nozzle).Field("afterburners").GetValue() is IList burners)) continue;
                foreach (object burner in burners)
                {
                    if (burner == null) continue;
                    float at = Traverse.Create(burner).Field("throttleStart").GetValue<float>();
                    if (at > 1f) at /= 100f;                       // the field's default reads as a percentage
                    if (at > 0f && at < start) start = at;
                }
            }
            float throttle = start < 1f ? Mathf.Clamp(start - 0.05f, 0.4f, 0.85f) : 0.6f;
            if (def != null) evasionThrottle[def] = throttle;
            return throttle;
        }

        // Only launchers the faction actually has a position for, and only
        // their known position: nothing here reveals a hidden MANPADS.
        private static bool IrLauncherInReach(Aircraft aircraft)
        {
            FactionHQ hq = aircraft.NetworkHQ;
            if (hq == null) return false;
            if (Time.timeSinceLevelLoad >= nextLauncherScan)
            {
                nextLauncherScan = Time.timeSinceLevelLoad + 2f;
                launchers.Clear();
                foreach (Unit unit in UnitRegistry.allUnits)
                {
                    if (unit == null || unit.disabled || unit is Aircraft || unit is Missile) continue;
                    if (unit.NetworkHQ == null || unit.weaponStations == null) continue;
                    float reach = 0f;
                    foreach (WeaponStation station in unit.weaponStations)
                        if (station?.WeaponInfo != null && IsHeatSeeking(station.WeaponInfo))
                            reach = Mathf.Max(reach, station.WeaponInfo.targetRequirements.maxRange);
                    if (reach > 0f) launchers.Add((unit, reach));
                }
            }
            GlobalPosition here = aircraft.GlobalPosition();
            foreach ((Unit unit, float reach) in launchers)
            {
                if (unit == null || unit.disabled || unit.NetworkHQ == hq) continue;
                if (!hq.TryGetKnownPosition(unit, out GlobalPosition known)) continue;
                if (FastMath.Distance(here, known) <= reach) return true;
            }
            return false;
        }

        // "Flares 24/30 · Chaff 12/20": every countermeasure aboard, by what
        // the game calls it.
        private static readonly FieldInfo Stations = AccessTools.Field(typeof(CountermeasureManager), "countermeasureStations");

        internal static string Readout(Aircraft aircraft)
        {
            if (aircraft?.countermeasureManager == null || Stations == null) return "";
            if (!(Stations.GetValue(aircraft.countermeasureManager) is IList list) || list.Count == 0) return "no countermeasures";
            var parts = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == null) continue;
                Traverse t = Traverse.Create(list[i]);
                string name = t.Field("displayName").GetValue<string>();
                int ammo = t.Field("ammo").GetValue<int>();
                int max = Full(aircraft, i, ammo, t.Field("maxAmmo").GetValue<int>());
                parts.Add((string.IsNullOrEmpty(name) ? "CM" : name) + " " + ammo + (max > 0 ? "/" + max : ""));
            }
            return string.Join("  ·  ", parts.ToArray());
        }

        // The game fills in a countermeasure's maximum only on a rearm, so an
        // aircraft fresh off the deck reports a maximum of nothing -- and its
        // own flare proportion reads zero with every flare still aboard. The
        // load it was first seen with is taken as full instead.
        private static readonly Dictionary<Aircraft, Dictionary<int, int>> fullLoad =
            new Dictionary<Aircraft, Dictionary<int, int>>();

        private static int Full(Aircraft aircraft, int station, int ammo, int reported)
        {
            if (!fullLoad.TryGetValue(aircraft, out Dictionary<int, int> stations))
            {
                if (fullLoad.Count > 64)
                {
                    var gone = new List<Aircraft>();
                    foreach (Aircraft key in fullLoad.Keys) if (key == null || key.disabled) gone.Add(key);
                    foreach (Aircraft key in gone) fullLoad.Remove(key);
                }
                fullLoad[aircraft] = stations = new Dictionary<int, int>();
            }
            stations.TryGetValue(station, out int seen);
            int full = Mathf.Max(seen, ammo, reported);
            stations[station] = full;
            return full;
        }

        // Fraction of flares left: the station that answers heat-seekers, or
        // the first one if none says so.
        internal static float FlareFraction(Aircraft aircraft)
        {
            if (aircraft?.countermeasureManager == null || Stations == null) return 0f;
            if (!(Stations.GetValue(aircraft.countermeasureManager) is IList list) || list.Count == 0) return 0f;
            int index = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var types = list[i] != null ? Traverse.Create(list[i]).Field("threatTypes").GetValue<List<string>>() : null;
                if (types != null && types.Contains("IR")) { index = i; break; }
            }
            Traverse t = Traverse.Create(list[index]);
            int ammo = t.Field("ammo").GetValue<int>();
            int max = Full(aircraft, index, ammo, t.Field("maxAmmo").GetValue<int>());
            return max > 0 ? Mathf.Clamp01((float)ammo / max) : 0f;
        }
    }

    // The combat pilot's own heat-seeker evasion waits out a reaction time
    // with the throttle wherever it was -- usually wide open, afterburner
    // lit -- then idles and holds the flare button down until the missile is
    // gone, emptying the dispensers. For any flight of ours the native pilot
    // is flying, the strings above do the flaring and the throttle comes off
    // at once; this only follows that.
    [HarmonyPatch(typeof(AIPilotCombatModes), "EvadeModeIR")]
    internal static class NativeIrEvasionPatch
    {
        private const string Name = "IR evasion";
        private static readonly AccessTools.FieldRef<PilotBaseState, Aircraft> AircraftOf =
            AccessTools.FieldRefAccess<PilotBaseState, Aircraft>("aircraft");
        private static readonly AccessTools.FieldRef<PilotBaseState, ControlInputs> InputsOf =
            AccessTools.FieldRefAccess<PilotBaseState, ControlInputs>("controlInputs");

        private static bool Prefix(AIPilotCombatModes __instance)
        {
            if (!Guard.Ok(Name)) return true;
            try
            {
                Aircraft aircraft = AircraftOf(__instance);
                Flight flight = FlightOrders.Of(aircraft);
                if (flight == null || Host.IsFlownByPlayer(flight)) return true;
                ControlInputs inputs = InputsOf(__instance);
                if (inputs != null && aircraft.autopilot is AutopilotPlane)
                    inputs.throttle = Time.timeSinceLevelLoad < flight.ThrottleCutUntil ? IrDefence.EvasionThrottle(aircraft) : 1f;
                return false;
            }
            catch (Exception ex) { Guard.Failed(Name, ex); return true; }
        }
    }
}
