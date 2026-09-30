using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // One target in a strike plan, and the weapon chosen for it (a
    // WeaponInfo name; null lets the flight choose).
    public sealed class StrikeItem
    {
        public Unit Target;
        public string Weapon;
    }

    // Strike planning: targets gathered first, the strike sent only when
    // authorised.
    //
    // A plan is the wing lead's list; nothing flies until AuthorizeStrike,
    // which shares it out over the wing by what each aircraft carries and hands
    // each its own list. A flight works down its list: one target at a time
    // through the ordinary strike and egress, and on a missile pass a salvo
    // spread over every listed target the weapon can reach from there (the
    // game's own multi-target launch, fed our list instead of its own picks),
    // at no more missiles per target than allowed. After each egress it takes
    // the next target that is still standing and not already covered by
    // missiles in the air; with everything covered it holds nearby and looks
    // again; when all are down, or it has nothing left that can hurt the
    // rest, it says so and resumes what it was doing.
    public static class StrikePlans
    {
        // ---- planning (the lead's list) ----------------------------------

        public static StrikeItem Plan(Flight flight, Unit target)
        {
            Flight lead = Wings.LeadOf(flight) ?? flight;
            if (lead == null || target == null) return null;
            StrikeItem existing = lead.StrikePlan.Find(i => i.Target == target);
            if (existing != null) return existing;
            var item = new StrikeItem { Target = target };
            lead.StrikePlan.Add(item);
            return item;
        }

        public static List<StrikeItem> PlanOf(Flight flight)
        {
            Flight lead = Wings.LeadOf(flight) ?? flight;
            if (lead == null) return new List<StrikeItem>();
            lead.StrikePlan.RemoveAll(i => Host.Dead(i.Target));
            return lead.StrikePlan;
        }

        public static void Remove(Flight flight, StrikeItem item) => PlanOf(flight).Remove(item);
        public static void Clear(Flight flight) => PlanOf(flight).Clear();

        // How many missiles the plan wants against what the wing carries --
        // missiles only; bombs, rockets and guns are one pass per target.
        public static void Needs(Flight flight, out int wanted, out int carried)
        {
            wanted = 0; carried = 0;
            var members = Wings.Group(Wings.LeadOf(flight) ?? flight);
            foreach (Flight member in members)
                foreach (WeaponStation station in FlightOrders.ArmedStations(member.Aircraft))
                    if (station.WeaponInfo.missile) carried += station.Ammo;
            foreach (StrikeItem item in PlanOf(flight))
            {
                WeaponStation station = StationFor(members.Count > 0 ? members[0] : flight, item);
                if (station != null && station.WeaponInfo.missile)
                    wanted += ShotDisciplinePatch.AllowedOn(members.Count > 0 ? members[0] : flight, item.Target, station.WeaponInfo);
            }
        }

        // The station this flight would use on the item: the chosen weapon if
        // it has rounds, else the best it has for the target.
        internal static WeaponStation StationFor(Flight flight, StrikeItem item)
        {
            if (flight?.Aircraft == null || item?.Target == null) return null;
            WeaponStation named = FlightOrders.NamedStation(flight.Aircraft, item.Weapon);
            if (named != null) return named;
            return string.IsNullOrEmpty(item.Weapon) ? FlightOrders.BestStationFor(flight.Aircraft, item.Target) : null;
        }

        // ---- authorising: shared over the wing ---------------------------

        public static string Authorize(Flight flight)
        {
            Flight lead = Wings.LeadOf(flight) ?? flight;
            List<StrikeItem> plan = new List<StrikeItem>(PlanOf(lead));
            if (plan.Count == 0) return (lead?.Name ?? "Flight") + " · no targets planned";
            List<Flight> members = Wings.Group(lead);
            var lists = new Dictionary<Flight, List<StrikeItem>>();
            var budget = new Dictionary<Flight, int>();
            foreach (Flight member in members)
            {
                lists[member] = new List<StrikeItem>();
                int rounds = 0;
                foreach (WeaponStation station in FlightOrders.ArmedStations(member.Aircraft)) rounds += station.WeaponInfo.missile ? station.Ammo : 2;
                budget[member] = rounds;
            }
            int skipped = 0;
            foreach (StrikeItem item in plan)
            {
                Flight best = null;
                int most = int.MinValue;
                foreach (Flight member in members)
                {
                    if (StationFor(member, item) == null) continue;
                    if (budget[member] > most) { most = budget[member]; best = member; }
                }
                if (best == null) { skipped++; continue; }
                lists[best].Add(item);
                WeaponStation station = StationFor(best, item);
                budget[best] -= station.WeaponInfo.missile ? ShotDisciplinePatch.AllowedOn(best, item.Target, station.WeaponInfo) : 1;
            }
            int flying = 0, targets = 0;
            foreach (Flight member in members)
            {
                if (lists[member].Count == 0) continue;
                flying++;
                targets += lists[member].Count;
                Run(member, lists[member]);
            }
            PlanOf(lead).Clear();
            if (flying == 0) return lead.Name + " · nothing aboard can attack the planned targets";
            string line = (lead.Wing ?? lead.Name) + " · strike authorised · " + targets + " target(s)" +
                (flying > 1 ? " over " + flying + " aircraft" : "") + (skipped > 0 ? " · " + skipped + " no weapon for" : "");
            Host.LogInfo("[flight] " + line);
            return line;
        }

        // ---- working down a list -----------------------------------------

        internal static bool Continuing;

        internal static void Run(Flight flight, List<StrikeItem> list)
        {
            flight.StrikeList.Clear();
            flight.StrikeList.AddRange(list);
            Next(flight, "starting");
        }

        // The next target worth a pass: standing, not already covered by
        // enough missiles in the air, and something aboard that can hit it.
        internal static void Next(Flight flight, string why)
        {
            flight.StrikeList.RemoveAll(i => Host.Dead(i.Target));
            if (flight.StrikeList.Count == 0)
            {
                Finish(flight, "all targets down");
                return;
            }
            FactionHQ hq = flight.Aircraft != null ? flight.Aircraft.NetworkHQ : null;
            bool covered = false, armed = false;
            foreach (StrikeItem item in flight.StrikeList)
            {
                WeaponStation station = StationFor(flight, item);
                if (station == null) continue;
                armed = true;
                if (station.WeaponInfo.missile && ShotDisciplinePatch.Closing(hq, item.Target) >= ShotDisciplinePatch.AllowedOn(flight, item.Target, station.WeaponInfo))
                { covered = true; continue; }
                Continuing = true;
                try { FlightOrders.Strike(flight, item.Target, item.Weapon); }
                finally { Continuing = false; }
                Tracing.Flight("[flight] " + flight.Name + " · strike list · " + why + " · " + ShipNames.Of(item.Target) + " · " + flight.StrikeList.Count + " left");
                return;
            }
            if (!armed) { Finish(flight, "nothing left aboard for the " + flight.StrikeList.Count + " still standing"); return; }
            // Every remaining target has missiles on the way: hold off and look
            // again when they have arrived.
            if (covered)
            {
                if (flight.Mode != FlightMode.Orbit) { flight.OrbitCentre = flight.Aircraft.GlobalPosition(); flight.Mode = FlightMode.Orbit; flight.Adopted = false; }
                flight.StrikeListCheck = Time.timeSinceLevelLoad + 4f;
            }
        }

        private static void Finish(Flight flight, string why)
        {
            flight.StrikeList.Clear();
            Host.Say(flight.Name + " · strike list complete · " + why);
            Host.LogInfo("[flight] " + flight.Name + " · strike list complete · " + why);
            FlightOrders.BreakOff(flight);
        }

        // From FlightOrders.Tick: a flight holding while its targets' missiles
        // arrive looks again.
        internal static void Tick(Flight flight)
        {
            if (flight.StrikeList.Count == 0 || flight.Mode != FlightMode.Orbit) return;
            if (Time.timeSinceLevelLoad < flight.StrikeListCheck) return;
            flight.StrikeListCheck = Time.timeSinceLevelLoad + 4f;
            Next(flight, "next");
        }

        // ---- the rest of the pass: other weapons, same run ----------------
        //
        // A salvo is one station's. With listed targets assigned other
        // weapons, and reachable from where the aircraft is now -- in range,
        // inside the cone, still wanting missiles -- the next station fires at
        // them a moment later on the same pass, before it turns away. True
        // while there is more to fire (or it is waiting its moment).
        private const float FollowUpGap = 0.4f, FollowUpFor = 8f;

        internal static bool FollowUp(Flight flight)
        {
            Aircraft aircraft = flight?.Aircraft;
            if (aircraft == null || flight.StrikeList.Count < 2 || aircraft.NetworkHQ == null) return false;
            float now = Time.timeSinceLevelLoad;
            if (flight.PassFirstShot <= 0f) flight.PassFirstShot = now;
            if (now - flight.PassFirstShot > FollowUpFor) return false;
            if (now < flight.NextFollowUp) return true;
            Pilot pilot = FlightOrders.FirstPilot(aircraft);
            if (pilot == null) return false;
            FactionHQ hq = aircraft.NetworkHQ;
            foreach (WeaponStation station in FlightOrders.ArmedStations(aircraft))
            {
                if (!station.WeaponInfo.missile || station.Ammo <= 0) continue;
                var targets = new List<Unit>();
                foreach (StrikeItem item in flight.StrikeList)
                {
                    if (Host.Dead(item.Target) || StationFor(flight, item) != station) continue;
                    if (!Reachable(aircraft, station.WeaponInfo, item.Target, out _)) continue;
                    int want = ShotDisciplinePatch.AllowedOn(flight, item.Target, station.WeaponInfo) - ShotDisciplinePatch.Closing(hq, item.Target);
                    for (int n = 0; n < want && targets.Count < station.Ammo; n++) targets.Add(item.Target);
                }
                if (targets.Count == 0 || !station.Ready() || station.SalvoInProgress) continue;
                aircraft.weaponManager.currentWeaponStation = station;
                List<Unit> list = aircraft.weaponManager.GetTargetList();
                list.Clear();
                list.AddRange(targets);
                aircraft.weaponManager.TargetListChanged();
                pilot.Fire();
                flight.NextFollowUp = now + FollowUpGap;
                Tracing.Flight("[flight] " + flight.Name + " · same pass · " + (station.WeaponInfo.weaponName ?? "missile") + " at " + targets.Count + " more");
                return true;
            }
            return false;
        }

        // Can this weapon be launched at the target from here?
        internal static bool Reachable(Aircraft aircraft, WeaponInfo info, Unit target, out float angle)
        {
            angle = 180f;
            if (aircraft?.NetworkHQ == null || !aircraft.NetworkHQ.TryGetKnownPosition(target, out GlobalPosition known)) return false;
            Vector3 to = known - aircraft.GlobalPosition();
            angle = Vector3.Angle(aircraft.transform.forward, to);
            float reach = info.targetRequirements.maxRange;
            float cone = Mathf.Min(info.targetRequirements.minAlignment > 0f ? info.targetRequirements.minAlignment : Tuning.MaxLaunchAngle, Tuning.MaxLaunchAngle);
            return (reach <= 0f || to.magnitude <= reach) && angle <= cone;
        }

        // ---- one pass or several? ----------------------------------------
        //
        // An estimate for the plan page: missile targets that can all be put
        // inside their weapons' cones and ranges from one launch point, short
        // of the targets on the line in from here, go in one pass; each target
        // left to a bomb, rocket or gun takes a pass of its own, as does any
        // missile target that will not fit the same launch point, and so does
        // a plan wanting more missiles than the wing carries.
        public static int Passes(Flight flight, out string why)
        {
            why = "";
            List<StrikeItem> plan = PlanOf(flight);
            Flight lead = Wings.LeadOf(flight) ?? flight;
            if (plan.Count == 0 || lead?.Aircraft == null) return 0;
            var missiles = new List<(StrikeItem item, WeaponInfo info, Vector3 at)>();
            int separate = 0, noWeapon = 0;
            FactionHQ hq = lead.Aircraft.NetworkHQ;
            foreach (StrikeItem item in plan)
            {
                WeaponStation station = null;
                foreach (Flight member in Wings.Group(lead)) if ((station = StationFor(member, item)) != null) break;
                if (station == null) { noWeapon++; continue; }
                if (!station.WeaponInfo.missile) { separate++; continue; }
                if (hq == null || !hq.TryGetKnownPosition(item.Target, out GlobalPosition known)) { separate++; continue; }
                missiles.Add((item, station.WeaponInfo, known.ToLocalPosition()));
            }
            int outside = 0;
            if (missiles.Count > 0)
            {
                Vector3 centre = Vector3.zero;
                foreach (var m in missiles) centre += m.at;
                centre /= missiles.Count;
                Vector3 inbound = centre - lead.Aircraft.transform.position;
                inbound.y = 0f;
                if (inbound.sqrMagnitude < 1f) inbound = lead.Aircraft.transform.forward;
                inbound.Normalize();
                float shortest = float.MaxValue;
                foreach (var m in missiles) if (m.info.targetRequirements.maxRange > 0f) shortest = Mathf.Min(shortest, m.info.targetRequirements.maxRange);
                if (shortest == float.MaxValue) shortest = 10000f;
                Vector3 launch = centre - inbound * shortest * 0.6f;
                foreach (var m in missiles)
                {
                    Vector3 to = m.at - launch;
                    float cone = Mathf.Min(m.info.targetRequirements.minAlignment > 0f ? m.info.targetRequirements.minAlignment : Tuning.MaxLaunchAngle, Tuning.MaxLaunchAngle);
                    float reach = m.info.targetRequirements.maxRange;
                    if ((reach > 0f && to.magnitude > reach) || Vector3.Angle(inbound, to) > cone * 0.9f) outside++;
                }
            }
            Needs(flight, out int wanted, out int carried);
            int passes = (missiles.Count - outside > 0 ? 1 : 0) + outside + separate;
            var reasons = new List<string>();
            if (separate > 0) reasons.Add(separate + " by bomb, rocket or gun, a pass each");
            if (outside > 0) reasons.Add(outside + " outside one launch point's cone or range");
            if (wanted > carried) { reasons.Add("wants " + wanted + " missiles, the wing carries " + carried); passes = Mathf.Max(passes, 2); }
            if (noWeapon > 0) reasons.Add(noWeapon + " nothing aboard can attack");
            why = string.Join(" · ", reasons.ToArray());
            return passes;
        }

        internal static void Cancel(Flight flight)
        {
            if (flight != null && !Continuing) flight.StrikeList.Clear();
        }
    }

    // A missile pass on a strike list: the salvo spread over every listed
    // target this weapon reaches from here -- in range, inside its launch
    // cone, known to the faction -- at what each is still allowed, rather than
    // the game's own picks round the one target. Nothing to add, and the
    // game's list stands.
    [HarmonyPatch(typeof(CombatAI), nameof(CombatAI.LookForMissileTargets))]
    internal static class StrikeListSalvoPatch
    {
        private const string Name = "Strike list salvo";

        private static void Postfix(Aircraft aircraft, Unit currentTarget, WeaponStation weaponStation, List<Unit> outTargets, ref int __result)
        {
            if (!Guard.Ok(Name)) return;
            try { Spread(aircraft, currentTarget, weaponStation, outTargets, ref __result); }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }

        private static void Spread(Aircraft aircraft, Unit primary, WeaponStation station, List<Unit> outTargets, ref int result)
        {
            Flight flight = aircraft != null ? FlightOrders.Of(aircraft) : null;
            if (flight == null || flight.StrikeList.Count < 2 || station?.WeaponInfo == null || outTargets == null) return;
            WeaponInfo info = station.WeaponInfo;
            FactionHQ hq = aircraft.NetworkHQ;
            if (hq == null) return;
            float cone = info.targetRequirements.minAlignment > 0f ? info.targetRequirements.minAlignment : Tuning.MaxLaunchAngle;
            cone = Mathf.Min(cone, Tuning.MaxLaunchAngle);
            float reach = info.targetRequirements.maxRange;
            var picked = new List<Unit>();
            int distinct = 0, room = station.Ammo;
            // The one being attacked first, then the rest in list order.
            var order = new List<StrikeItem>();
            StrikeItem first = flight.StrikeList.Find(i => i.Target == primary);
            if (first != null) order.Add(first);
            foreach (StrikeItem item in flight.StrikeList) if (item != first) order.Add(item);
            foreach (StrikeItem item in order)
            {
                Unit target = item.Target;
                if (Host.Dead(target) || room <= 0) continue;
                if (!string.IsNullOrEmpty(item.Weapon) && item.Weapon != info.name) continue;   // saved for its own weapon
                if (!hq.TryGetKnownPosition(target, out GlobalPosition known)) continue;
                Vector3 to = known - aircraft.GlobalPosition();
                if (reach > 0f && to.magnitude > reach) continue;
                if (Vector3.Angle(aircraft.transform.forward, to) > cone) continue;
                int count = ShotDisciplinePatch.AllowedOn(flight, target, info) - ShotDisciplinePatch.Closing(hq, target);
                count = Mathf.Min(count, room);
                if (count <= 0) continue;
                for (int n = 0; n < count; n++) picked.Add(target);
                room -= count;
                distinct++;
            }
            if (distinct == 0) return;
            outTargets.Clear();
            outTargets.AddRange(picked);
            result = distinct;
            if (distinct > 1)
                Tracing.Flight("[flight] " + flight.Name + " · salvo over " + distinct + " listed targets · " + picked.Count + " × " + (info.weaponName ?? "missile"));
        }
    }
}
