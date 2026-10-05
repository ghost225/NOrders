using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // One target in a strike plan, and the weapon chosen for it (a
    // WeaponKey -- the weapon, whatever pylon carries it; null lets the flight choose).
    public sealed class StrikeItem
    {
        public Unit Target;
        public string Weapon;
        // A saturation attack: every round of the chosen weapons (WeaponInfo
        // names; none chosen is every guided anti-surface weapon aboard), from
        // every aircraft of the wing carrying them, at this one target -- and,
        // Together, launched at once when the whole wing is in range.
        public bool Saturate;
        public bool Together = true;
        public readonly HashSet<string> Weapons = new HashSet<string>();

        // Whether this weapon is one the saturation fires.
        public bool Fires(WeaponInfo info) =>
            info != null && (info.missile || info.glideBomb) && !info.bomb &&
            (Weapons.Count > 0 ? Weapons.Contains(FlightOrders.WeaponKey(info)) : info.effectiveness.antiSurface > 0f || info.effectiveness.antiRadar > 0f);
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

        // Weapons fired by the salvo -- missiles and glide bombs -- go at a
        // number of rounds per target (ShotDisciplinePatch.AllowedOn, which
        // the flight's own per-target setting overrides); anything else is a
        // pass per target and its rounds are not counted.
        internal static bool Counted(WeaponInfo info) => info != null && (info.missile || info.glideBomb);

        internal static int RoundsFor(Flight flight, StrikeItem item, WeaponInfo info) =>
            Counted(info) ? ShotDisciplinePatch.AllowedOn(flight, item.Target, info) : 1;

        // Where the plan outruns the wing's racks, weapon by weapon.
        public sealed class Shortfall
        {
            public string Weapon;           // display name
            public int PerTarget, Wanted, Carried, Targets, Reached;
        }

        // The plan checked against what the wing carries: in list order, each
        // target takes its rounds of the weapon it would be attacked with;
        // once that weapon is spent, the targets after it are not reached.
        // Returns the shortfalls and fills the items that would go unattacked.
        public static List<Shortfall> Check(Flight flight, HashSet<StrikeItem> unreached = null)
        {
            var result = new List<Shortfall>();
            Flight lead = Wings.LeadOf(flight) ?? flight;
            List<Flight> members = Wings.Group(lead);
            if (members.Count == 0 && lead != null) members.Add(lead);
            var left = new Dictionary<string, int>();
            var byWeapon = new Dictionary<string, Shortfall>();
            foreach (Flight member in members)
                foreach (WeaponStation station in FlightOrders.ArmedStations(member.Aircraft))
                {
                    if (!Counted(station.WeaponInfo)) continue;
                    left.TryGetValue(FlightOrders.WeaponKey(station.WeaponInfo), out int n);
                    left[FlightOrders.WeaponKey(station.WeaponInfo)] = n + station.Ammo;
                }
            foreach (StrikeItem item in PlanOf(lead))
            {
                if (item.Saturate)
                {
                    bool fired = false;
                    foreach (Flight member in members)
                        foreach (WeaponStation st in FlightOrders.ArmedStations(member.Aircraft))
                            if (item.Fires(st.WeaponInfo) && left.ContainsKey(FlightOrders.WeaponKey(st.WeaponInfo)) && left[FlightOrders.WeaponKey(st.WeaponInfo)] > 0) { left[FlightOrders.WeaponKey(st.WeaponInfo)] = 0; fired = true; }
                    if (!fired) unreached?.Add(item);
                    continue;
                }
                WeaponStation station = null;
                Flight by = null;
                foreach (Flight member in members) if ((station = StationFor(member, item)) != null) { by = member; break; }
                if (station == null || !Counted(station.WeaponInfo)) continue;
                string key = FlightOrders.WeaponKey(station.WeaponInfo);
                if (!byWeapon.TryGetValue(key, out Shortfall f))
                {
                    left.TryGetValue(key, out int carried);
                    byWeapon[key] = f = new Shortfall { Weapon = station.WeaponInfo.weaponName ?? key, Carried = carried };
                }
                int per = RoundsFor(by, item, station.WeaponInfo);
                f.PerTarget = Mathf.Max(f.PerTarget, per);
                f.Wanted += per;
                f.Targets++;
                left.TryGetValue(key, out int remaining);
                if (remaining > 0) { f.Reached++; left[key] = remaining - per; }
                else unreached?.Add(item);
            }
            foreach (Shortfall f in byWeapon.Values) if (f.Wanted > f.Carried) result.Add(f);
            return result;
        }

        // One line for a shortfall: "GBM-500LR: 2 a target wants 8, the wing carries 4 · 2 of 4 targets reached".
        public static string Describe(Shortfall f) =>
            f.Weapon + ": " + f.PerTarget + " a target wants " + f.Wanted + ", the wing carries " + f.Carried +
            " · " + f.Reached + " of " + f.Targets + " target(s) reached";

        // Would one round a target cover every target? For the offer to drop
        // the per-target setting to 1.
        public static bool OneEachFits(Flight flight)
        {
            Flight lead = Wings.LeadOf(flight) ?? flight;
            List<Shortfall> now = Check(lead);
            if (now.Count == 0) return false;
            foreach (Shortfall f in now) if (f.Targets > f.Carried) return false;
            return true;
        }

        // How many rounds of salvo weapons the plan wants against what the
        // wing carries of them.
        public static void Needs(Flight flight, out int wanted, out int carried)
        {
            wanted = 0; carried = 0;
            var members = Wings.Group(Wings.LeadOf(flight) ?? flight);
            foreach (Flight member in members)
                foreach (WeaponStation station in FlightOrders.ArmedStations(member.Aircraft))
                    if (Counted(station.WeaponInfo)) carried += station.Ammo;
            foreach (StrikeItem item in PlanOf(flight))
            {
                if (item.Saturate) { foreach (int n in SaturationLoad(flight, item).Values) wanted += n; continue; }
                Flight by = members.Count > 0 ? members[0] : flight;
                WeaponStation station = null;
                foreach (Flight member in members) if ((station = StationFor(member, item)) != null) { by = member; break; }
                if (station != null && Counted(station.WeaponInfo)) wanted += RoundsFor(by, item, station.WeaponInfo);
            }
        }

        // The station this flight would use on the item: the chosen weapon if
        // it has rounds, else the best it has for the target.
        internal static WeaponStation StationFor(Flight flight, StrikeItem item)
        {
            if (flight?.Aircraft == null || item?.Target == null) return null;
            if (item.Saturate) return SaturationStation(flight.Aircraft, item);
            WeaponStation named = FlightOrders.NamedStation(flight.Aircraft, item.Weapon);
            if (named != null) return named;
            return string.IsNullOrEmpty(item.Weapon) ? FlightOrders.BestStationFor(flight.Aircraft, item.Target) : null;
        }

        // ---- saturation ---------------------------------------------------

        // The saturation item this flight is working on the target, if any.
        internal static StrikeItem SaturationOn(Flight flight, Unit target)
        {
            if (flight == null || target == null) return null;
            foreach (StrikeItem item in flight.StrikeList) if (item.Saturate && item.Target == target) return item;
            return null;
        }

        // Fired without the per-target limit: a saturation weapon at its target.
        internal static bool Saturating(Flight flight, Unit target, WeaponInfo info)
        {
            StrikeItem item = SaturationOn(flight, target);
            return item != null && item.Fires(info);
        }

        // The next of the chosen weapons to fire: rounds aboard, the longest
        // reach first, so the whole salvo can go from the first launch point.
        internal static WeaponStation SaturationStation(Aircraft aircraft, StrikeItem item)
        {
            WeaponStation best = null;
            foreach (WeaponStation station in FlightOrders.ArmedStations(aircraft))
            {
                if (station.Ammo <= 0 || !item.Fires(station.WeaponInfo)) continue;
                if (item.Weapons.Count == 0 && FlightOrders.BestStationFor(aircraft, item.Target) == null) continue;
                if (best == null || station.WeaponInfo.targetRequirements.maxRange > best.WeaponInfo.targetRequirements.maxRange) best = station;
            }
            return best;
        }

        // Rounds of the chosen weapons still aboard.
        internal static int SaturationRounds(Aircraft aircraft, StrikeItem item)
        {
            int rounds = 0;
            foreach (WeaponStation station in FlightOrders.ArmedStations(aircraft))
                if (item.Fires(station.WeaponInfo)) rounds += Mathf.Max(station.Ammo, 0);
            return rounds;
        }

        // Rounds of the chosen weapons across the wing, by weapon (display name).
        public static Dictionary<string, int> SaturationLoad(Flight flight, StrikeItem item)
        {
            var load = new Dictionary<string, int>();
            foreach (Flight member in Wings.Group(Wings.LeadOf(flight) ?? flight))
                foreach (WeaponStation station in FlightOrders.ArmedStations(member.Aircraft))
                {
                    if (station.Ammo <= 0 || !item.Fires(station.WeaponInfo)) continue;
                    string name = FlightOrders.WeaponKey(station.WeaponInfo);
                    load.TryGetValue(name, out int n);
                    load[name] = n + station.Ammo;
                }
            return load;
        }

        private sealed class Salvo
        {
            internal readonly Dictionary<Flight, float> InRange = new Dictionary<Flight, float>();
            internal float FirstReady = -1f;
            internal bool Released;
        }
        private static readonly Dictionary<StrikeItem, Salvo> salvos = new Dictionary<StrikeItem, Salvo>();
        private const float HoldAtMost = 90f;

        // In launch range on a Together saturation: hold until every aircraft
        // of the wing on it is in range too (or HoldAtMost seconds after the
        // first), then all fire. True while it must hold; says how many are in.
        internal static bool HoldForWing(Flight flight, StrikeItem item, out string waiting)
        {
            waiting = null;
            if (item == null || !item.Saturate || !item.Together) return false;
            if (!salvos.TryGetValue(item, out Salvo salvo)) salvos[item] = salvo = new Salvo();
            if (salvo.Released) return false;
            float now = Time.timeSinceLevelLoad;
            salvo.InRange[flight] = now;
            if (salvo.FirstReady < 0f) salvo.FirstReady = now;
            int on = 0, ready = 0;
            foreach (Flight other in FlightOrders.All())
            {
                if (other?.Aircraft == null || other.Aircraft.disabled || !other.StrikeList.Contains(item)) continue;
                if (other.Mode != FlightMode.Strike || other.Target != item.Target || SaturationRounds(other.Aircraft, item) <= 0) continue;
                on++;
                if (salvo.InRange.TryGetValue(other, out float at) && now - at < 3f) ready++;
            }
            if (ready >= on || now - salvo.FirstReady > HoldAtMost)
            {
                salvo.Released = true;
                string line = "saturation on " + ShipNames.Of(item.Target) + " · " + (ready >= on ? "all " + on + " in range" : ready + " of " + on + " in range after " + HoldAtMost.ToString("0") + " s") + " · launch";
                Host.LogInfo("[flight] " + (flight.Wing ?? flight.Name) + " · " + line);
                Host.Say((flight.Wing ?? flight.Name) + " · " + line);
                return false;
            }
            waiting = ready + " of " + on + " in range";
            return true;
        }

        // ---- authorising: shared over the wing ---------------------------

        public static string Authorize(Flight flight)
        {
            Flight lead = Wings.LeadOf(flight) ?? flight;
            List<StrikeItem> plan = new List<StrikeItem>(PlanOf(lead));
            if (plan.Count == 0) return (lead?.Name ?? "Flight") + " · no targets planned";
            List<Flight> members = Wings.Group(lead);
            var lists = new Dictionary<Flight, List<StrikeItem>>();
            // Rounds of each salvo weapon each aircraft has left to give; a
            // target goes to whoever has the most of the weapon it needs.
            var budget = new Dictionary<Flight, Dictionary<string, int>>();
            foreach (Flight member in members)
            {
                lists[member] = new List<StrikeItem>();
                var rounds = new Dictionary<string, int>();
                foreach (WeaponStation station in FlightOrders.ArmedStations(member.Aircraft))
                {
                    rounds.TryGetValue(FlightOrders.WeaponKey(station.WeaponInfo), out int n);
                    rounds[FlightOrders.WeaponKey(station.WeaponInfo)] = n + (Counted(station.WeaponInfo) ? station.Ammo : 2);
                }
                budget[member] = rounds;
            }
            List<Shortfall> short_ = Check(lead);
            int skipped = 0;
            foreach (StrikeItem item in plan)
            {
                // A saturation goes to every aircraft carrying its weapons,
                // and takes all of those rounds.
                if (item.Saturate)
                {
                    salvos.Remove(item);
                    bool any = false;
                    foreach (Flight member in members)
                    {
                        if (SaturationStation(member.Aircraft, item) == null) continue;
                        lists[member].Add(item);
                        any = true;
                        foreach (WeaponStation carried in FlightOrders.ArmedStations(member.Aircraft))
                            if (item.Fires(carried.WeaponInfo)) budget[member][FlightOrders.WeaponKey(carried.WeaponInfo)] = 0;
                    }
                    if (!any) skipped++;
                    continue;
                }
                // The rounds this target wants may be more than any one aircraft
                // carries -- four a target from a wing carrying one each: the
                // aircraft with the most of its weapon take it in turn until
                // the rounds are covered. Before, only the first did, and fired
                // its one round while the rest flew formation.
                var carriers = new List<(Flight member, WeaponStation station, int have)>();
                foreach (Flight member in members)
                {
                    WeaponStation candidate = StationFor(member, item);
                    if (candidate == null) continue;
                    budget[member].TryGetValue(FlightOrders.WeaponKey(candidate.WeaponInfo), out int have);
                    carriers.Add((member, candidate, have));
                }
                if (carriers.Count == 0) { skipped++; continue; }
                // Most rounds first; level on rounds, the one with the fewest
                // targets so far -- ties had all gone to the lead.
                carriers.Sort((a, b) => b.have != a.have ? b.have.CompareTo(a.have) : lists[a.member].Count.CompareTo(lists[b.member].Count));
                int need = RoundsFor(carriers[0].member, item, carriers[0].station.WeaponInfo);
                bool counted = Counted(carriers[0].station.WeaponInfo);
                int given = 0;
                foreach (var c in carriers)
                {
                    if (given > 0 && (c.have <= 0 || need <= 0)) break;
                    lists[c.member].Add(item);
                    given++;
                    int share = counted ? Mathf.Min(Mathf.Max(c.have, 0), need) : 1;
                    budget[c.member][FlightOrders.WeaponKey(c.station.WeaponInfo)] = c.have - share;
                    need -= counted ? Mathf.Max(share, 1) : need;
                }
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
            foreach (Shortfall f in short_) line += " · short: " + Describe(f);
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
                if (flight.Mode != FlightMode.Orbit)
                    Tracing.Flight("[flight] " + flight.Name + " · every target on the list already has missiles on the way · holding where it is");
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
                if (!(station.WeaponInfo.missile || station.WeaponInfo.glideBomb) || station.Ammo <= 0) continue;
                var targets = new List<Unit>();
                foreach (StrikeItem item in flight.StrikeList)
                {
                    if (Host.Dead(item.Target) || StationFor(flight, item) != station) continue;
                    if (station.WeaponInfo.glideBomb)
                    {
                        if (!aircraft.NetworkHQ.TryGetKnownPosition(item.Target, out GlobalPosition glideTo) || !NavalPilotState.GlideReach(aircraft, station.WeaponInfo, glideTo)) continue;
                    }
                    else if (!Reachable(aircraft, station.WeaponInfo, item.Target, out _)) continue;
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
                if (!Counted(station.WeaponInfo)) { separate++; continue; }
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
            int passes = (missiles.Count - outside > 0 ? 1 : 0) + outside + separate;
            var reasons = new List<string>();
            if (separate > 0) reasons.Add(separate + " by bomb, rocket or gun, a pass each");
            if (outside > 0) reasons.Add(outside + " outside one launch point's cone or range");
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
            if (flight == null || station?.WeaponInfo == null || outTargets == null) return;
            if (flight.StrikeList.Count < 2 && StrikePlans.SaturationOn(flight, primary) == null) return;
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
                if (item.Saturate ? !item.Fires(info) : !string.IsNullOrEmpty(item.Weapon) && item.Weapon != FlightOrders.WeaponKey(info)) continue;   // saved for its own weapon
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
