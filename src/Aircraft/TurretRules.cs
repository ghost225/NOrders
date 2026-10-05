using System;
using System.Collections.Generic;
using UnityEngine;

namespace NOrders
{
    // Turrets on aircraft: the Chicane's chin gun, the Tarantula's side guns
    // and artillery, the Cricket's ventral turret.
    //
    // With no player aboard a turret is entirely its own master: it picks a
    // target from what the faction tracks inside its firing cones and fires on
    // it, whatever the pilot is doing and whatever the flight's rules of
    // engagement say. Only the aircraft the player flies has a gate
    // ("engage at will" / "hold fire"). So a flight on Weapons Hold shot at
    // everything in reach, and a strike's target was only one of the things its
    // turrets might choose.
    //
    // The ship rules (EngagementPolicy), applied to the flight:
    //   Free   fire at will (what the game does).
    //   Tight  the ordered target, units that have fired on the flight, and
    //          missiles coming at it.
    //   Hold   missiles coming at it only.
    // A mount can have rules of its own over the flight's. An explicit order
    // (the strike's target) overrides the flight's rules, but not a mount that
    // has been set to Hold: that one was muted on purpose.
    //
    // Enforced twice, as for ships: a sweep holds a turret whose pick is not
    // allowed (cleared, and manual -- a manual turret never fires on its own),
    // and the check on every Weapon.Fire (EngagementPolicy.Allows) denies the
    // shot whatever path it took.
    //
    // Applied where the player directs the flights (Host.PlayerDirected, Naval
    // Power); an AI commander's flights (High Command) keep the game's own
    // turret fire, which its roles assume.
    //
    // Who flies a pass for a turret is a separate matter. The helicopter combat
    // pilot (helicopters and tiltwings) has a gunship mode for turret weapons.
    // The fixed-wing combat pilot has no turret code at all, so a fixed-wing
    // turret fires at what it can reach and is not offered as a strike weapon
    // (OpportunisticOnly).
    internal static class TurretRules
    {
        private const string Name = "Turret rules";
        private const float AttackerMemory = 90f;       // seconds a unit that fired on the flight stays an attacker (the flight rule's own, StrikeDesignation)
        private const float SweepInterval = 0.25f;

        // Turrets this module holds on manual, and only those are released.
        private static readonly HashSet<Turret> held = new HashSet<Turret>();
        private static readonly HashSet<AircraftDefinition> logged = new HashSet<AircraftDefinition>();

        // A turret on a fixed-wing: nothing flies a pass for it.
        internal static bool OpportunisticOnly(Aircraft aircraft, WeaponStation station) =>
            station != null && station.HasTurret() && !FlightOrders.IsRotary(FlightOrders.FirstPilot(aircraft));

        // The mode a mount is under: its own, or the flight's.
        internal static EngagementMode ModeFor(Flight flight, string key)
        {
            if (flight.TurretModes.TryGetValue(key, out EngagementMode own)) return own;
            return flight.Roe == FlightRoe.Free ? EngagementMode.WeaponsFree
                : flight.Roe == FlightRoe.Tight ? EngagementMode.WeaponsTight : EngagementMode.WeaponsHold;
        }

        // The mount's own rules, or null when it follows the flight.
        internal static EngagementMode? OwnMode(Flight flight, string key) =>
            flight != null && key != null && flight.TurretModes.TryGetValue(key, out EngagementMode own) ? own : (EngagementMode?)null;

        internal static bool Ordered(Flight flight, Unit target)
        {
            if (target == null) return false;
            if (target == flight.Target && (flight.Mode == FlightMode.Strike || flight.Mode == FlightMode.Egress)) return true;
            foreach (StrikeItem item in flight.StrikeList) if (item.Target == target) return true;
            return false;
        }

        // May this mount fire at this target?
        internal static bool Permits(Flight flight, string key, Unit target)
        {
            EngagementMode? own = OwnMode(flight, key);
            if (own == EngagementMode.WeaponsHold) return false;           // muted by hand
            if (target == null) return true;                               // an idle mount is fine
            if (target is Missile missile && flight.Aircraft != null && missile.targetID == flight.Aircraft.persistentID) return true;
            if (Ordered(flight, target)) return true;
            EngagementMode mode = own ?? ModeFor(flight, key);
            if (mode == EngagementMode.WeaponsFree) return true;
            if (mode == EngagementMode.WeaponsHold) return false;
            return flight.Attackers.TryGetValue(target, out float at) && Time.timeSinceLevelLoad - at < AttackerMemory;
        }

        // The check on every shot (EngagementPolicy.Allows).
        internal static bool Allows(Aircraft aircraft, Unit target, WeaponStation station)
        {
            if (!Host.PlayerDirected) return true;          // an AI commander's flights keep the game's own turret fire
            if (aircraft == null || station == null || !station.HasTurret()) return true;
            Flight flight = FlightOrders.Of(aircraft);
            if (flight == null || Host.IsFlownByPlayer(flight) || aircraft.Player != null) return true;
            return Permits(flight, FlightOrders.WeaponKey(station.WeaponInfo), target);
        }

        // The sweep, for one flight: hold any turret whose pick is not
        // allowed, release those whose pick is.
        internal static void Tick(Flight flight)
        {
            if (!Guard.Ok(Name) || !Host.PlayerDirected) return;
            try
            {
                Aircraft aircraft = flight.Aircraft;
                if (aircraft == null || aircraft.disabled || aircraft.weaponStations == null) return;
                float now = Time.timeSinceLevelLoad;
                if (now < flight.NextTurretSweep) return;
                flight.NextTurretSweep = now + SweepInterval;

                bool player = Host.IsFlownByPlayer(flight) || aircraft.Player != null;
                bool restricted = flight.Roe != FlightRoe.Free || flight.TurretModes.Count > 0;
                if (!aircraft.LocalSim || !aircraft.IsServer) return;
                foreach (WeaponStation station in aircraft.weaponStations)
                {
                    if (station == null || !station.HasTurret()) continue;
                    string key = FlightOrders.WeaponKey(station.WeaponInfo);
                    foreach (Turret turret in station.Turrets)
                    {
                        if (turret == null) continue;
                        if (player || !restricted) { Release(turret); continue; }
                        Unit pick = turret.GetTarget();
                        if (Permits(flight, key, pick)) { Release(turret); continue; }
                        if (held.Contains(turret)) continue;
                        if (NativeBindings.TurretChooseTarget != null)
                            NativeBindings.TurretChooseTarget.Invoke(turret, new object[] { true });
                        turret.SetManual(true);
                        held.Add(turret);
                        Tracing.Flight("[turret] " + flight.Name + " · " + (station.WeaponInfo.weaponName ?? "turret") + " held · " +
                            (pick != null ? ShipNames.Of(pick) + " is not allowed under " + EngagementPolicy.Describe(ModeFor(flight, key)) : "rules"));
                    }
                }
                if (held.Count > 64) held.RemoveWhere(t => t == null);
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }

        private static void Release(Turret turret)
        {
            if (held.Remove(turret)) turret.SetManual(false);
        }

        // Once per airframe type: what its turrets are, so the next change is
        // made from what they really are (cone, traverse, range, flags) and not
        // from a guess. Always logged (a few lines, once per type per session).
        internal static void LogOnce(Aircraft aircraft)
        {
            try
            {
                AircraftDefinition definition = aircraft != null ? aircraft.definition : null;
                if (definition == null || aircraft.weaponStations == null || !logged.Add(definition)) return;
                bool rotary = FlightOrders.IsRotary(FlightOrders.FirstPilot(aircraft));
                foreach (WeaponStation station in aircraft.weaponStations)
                {
                    if (station == null || !station.HasTurret() || station.WeaponInfo == null) continue;
                    WeaponInfo info = station.WeaponInfo;
                    string cone = "no firing cone";
                    if (station.GetFiringConeDirection(out Vector3 dir, out float angle))
                    {
                        Vector3 local = aircraft.transform.InverseTransformDirection(dir);
                        cone = "cone " + angle.ToString("0") + "° about (right " + local.x.ToString("0.0") + ", up " + local.y.ToString("0.0") +
                            ", forward " + local.z.ToString("0.0") + ")";
                    }
                    Turret first = station.GetTurret();
                    Host.LogInfo("[turret] " + (definition.unitName ?? aircraft.name) + " · station " + station.Number + " · " +
                        (info.weaponName ?? info.name) + " · " + station.TurretCount() + " turret(s) · traverse " + station.TurretTraverseRange().ToString("0") +
                        "° · " + cone + " · range " + (info.targetRequirements.maxRange / 1000f).ToString("0.0") + " km · " +
                        (info.gun ? "gun" : "not a gun") + (info.boresight ? ", boresight" : "") +
                        (first != null && NativeBindings.FiresWithoutAiming(first) ? ", fires without aiming" : "") +
                        " · " + station.Ammo + " rounds · " + (rotary ? "helicopter/tiltwing: native gunship pass" : "fixed-wing: fires at what it can reach, not a strike weapon"));
                }
            }
            catch (Exception ex) { Host.LogWarning("[turret] probe failed: " + ex.Message); }
        }
    }
}
