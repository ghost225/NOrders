using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace NOrders
{
    public enum FlightMode { Route, Orbit, Station, Strike, Jam, Cargo, Egress, Engage, ReturnToBase, Formation }

    public enum FlightRoe
    {
        Hold,      // never fight; evasion only
        Tight,     // fight back at whatever has shot at us
        Free       // engage hostiles in reach, then resume the task
    }

    public enum FlightThreat { None, Missile, Hostile }

    // A flight this ship launched and still commands. Aircraft are meant to
    // feel owned, like a deployed vehicle: they hold what they are given and do
    // not go hunting unless told.
    public sealed class Flight
    {
        public Aircraft Aircraft;
        // Where it launched from and belongs to: a carrier's deck or a land
        // field. Both are Airbases; Parent is the ship when the home is a deck.
        public Airbase Home;
        public Ship Parent => Airfields.ShipOf(Home);
        public FlightMode Mode = FlightMode.Orbit;
        public readonly List<GlobalPosition> Route = new List<GlobalPosition>();
        public GlobalPosition OrbitCentre;      // task area centre
        public float OrbitRadius = 3000f;       // task area radius, overwritten on adoption
        public bool ConfineToArea = true;       // fight only inside the area
        public float Altitude = 900f;
        public Vector3 StationOffset;
        // The ship it keeps company with on station: its own deck by default,
        // or another -- a task force's guide it has been sent to cover.
        public Ship StationShip;
        public Ship StationAnchor => StationShip != null && !StationShip.disabled ? StationShip : Parent;
        public bool Adopted;
        public Unit Target;                 // designated for a strike; for jamming, the first of JamTargets
        public readonly List<Unit> JamTargets = new List<Unit>();   // jamming: in priority order
        internal int SalvoLeft;             // missiles still to fire in a standoff launch before egress
        internal float LastLaunchAt = -10f;
        internal float InLaunchRangeSince = -1f;
        public string PreferredWeapon;      // WeaponInfo.name, or null for whatever suits best
        public bool WarnedAboutTrack;
        public float StrikeStarted;
        // Whether the run-in is flown: set up at a height and range the chosen
        // weapon can be released from, before the combat pilot takes over.
        public bool RunInDone;
        public float RunInStarted;
        public bool SettingUp;              // opening out toward friendly lines to come round for the run
        public int StrikeStartAmmo = -1;
        public FlightMode PreviousMode = FlightMode.Orbit;
        public FlightRoe Roe = FlightRoe.Tight;
        // Home at a land airfield: rearm, refuel and go back out to the task
        // (Turnaround), rather than park and stand down. Decks always park.
        public bool RearmAtHome = Tuning.RearmAtAirfields;
        // The task it had when it was sent home, to go back to after a turnaround.
        internal FlightMode? ResumeMode;
        internal GlobalPosition ResumeCentre;
        internal float ResumeRadius;
        internal readonly List<GlobalPosition> ResumeRoute = new List<GlobalPosition>();
        public int MissilesPerTarget;      // 0: the defaults in Tuning
        // Targets gathered for a strike not yet authorised (the lead's), and
        // the list this aircraft is working down once it is.
        public readonly List<StrikeItem> StrikePlan = new List<StrikeItem>();
        public readonly List<StrikeItem> StrikeList = new List<StrikeItem>();
        internal float StrikeListCheck, PassFirstShot, NextFollowUp;
        // Aircraft that have fired at this one, and when: what Weapons Tight
        // may fight back against.
        internal readonly Dictionary<Unit, float> Attackers = new Dictionary<Unit, float>();
        internal float TrackLostSince = -1f;
        public int AmmoAtAttack = -1;       // total rounds when the run began
        public GlobalPosition CargoPoint;
        public bool Airdrop;
        public float LastCargoPlan;
        internal bool CargoSeeded;          // the transport state knows this zone
        internal Ship SupplyShip;           // a naval supply run: the ship the container is for
        internal float CargoSearch;         // how far round its point it may look for ground; 0 is the default
        internal int CargoAtOrder;          // cargo aboard when the delivery was ordered
        internal bool RejoinAfterCargo;     // a wingman done with its drop, circling until the lead is done
        // An airdrop along a line: this aircraft's stretch of it, the point
        // it flies to first so it runs in along the line, and the gap between
        // items so its load is spread along the stretch.
        internal bool AbandonedOnGround;    // the pilot got out before it ever flew
        internal bool HasDropLine, LeadInPending;
        internal GlobalPosition DropLineStart, DropLineEnd, LeadIn;
        internal float DropSpacing;
        public GlobalPosition EgressPoint;
        public float EgressUntil;
        public float NextEgressPlan;
        // Egress after an air-to-air radar shot starts as a crank (see Crank):
        // targets held at the radar's edge until the missiles are on their own.
        public bool Cranking;
        public int CrankSide;
        public float CrankUntil, CrankFloor, CrankStarted;
        public FlightThreat Threat;
        public bool ThreatIsInfrared;       // flares matter, and it must be let in much closer
        public float ThreatRange = float.PositiveInfinity;
        public Missile ThreatMissile;       // the nearest shot at us
        public GlobalPosition? HomingVia;   // on the way home by a dogleg; the landing follows once it is reached
        public string LastThreat;           // the last shot at us, described; for the loss report
        public float LastThreatAt;
        public float LastBurstAt;           // IR defence: when the last string of flares ended
        // A heat-seeker inbound and the flight not on a run-in or in a fight
        // the native pilot is flying: our state flies the beam turn.
        // Shot at, but defending itself where it is -- pods on every radar
        // shot, flares for the rest -- so it keeps to its orders.
        public bool StandOn;
        public bool EvadingInfrared => !StandOn && Threat == FlightThreat.Missile && ThreatIsInfrared && ThreatMissile != null && !ThreatMissile.disabled;
        // A radar shot inbound and our state keeping the aircraft: beam, chaff, descend.
        public bool EvadingRadar => !StandOn && Tuning.OwnRadarEvasion && Threat == FlightThreat.Missile && !ThreatIsInfrared && ThreatMissile != null && !ThreatMissile.disabled;
        public float NextFlare;
        public float NextPreFlare;
        public int FlaresThisShot;
        public float ThrottleCutUntil;      // idle to cool the engines while the flares go
        public bool Interrupted;            // native pilot has it while it fights or evades
        public float ThreatClearedAt;

        // Its callsign when it has one -- which every flight launched through us
        // does -- and the airframe otherwise.
        public string Label;
        // The wing it launched with, by the wing's callsign ("Viper 1"); null
        // for a single aircraft.
        public string Wing;
        // The flight or wing this one escorts: it flies cover on that group's
        // lead, retaliates against whatever locks it, and intercepts missiles
        // fired at it. Set on an escort's lead.
        public Flight Escorting;
        public string Name => !string.IsNullOrEmpty(Label) ? Label : TypeName;
        public string TypeName => Aircraft != null ? (Aircraft.definition?.unitName ?? Aircraft.name) : "lost";

        // Where it came from, for display: flights from every deck are shown
        // together now, so which deck matters for reading the list.
        public string HomeName => Home != null ? Airfields.NameOf(Home) : "unknown";

        // Where "home" is right now -- a deck moves.
        public GlobalPosition HomePosition => Home != null ? Airfields.PositionOf(Home) : OrbitCentre;

        // 0-100. The constraint that actually governs carrier operations, and
        // until now it was invisible until the automatic recovery fired.
        //
        // Against the airframe's internal tanks, so external tanks read above
        // 100% while they hold fuel: a fully loaded aircraft with drop tanks
        // shows its real endurance, not a flat 100%.
        public float FuelPercent => Aircraft != null ? TakeoffCheck.FuelPercent(Aircraft) : 0f;

        // What it still has to fight with. A bare total is misleading: a strike
        // flight out of bombs but holding air-to-air rounds reads as armed
        // while being useless for the job it was sent to do. Group by role
        // instead, using the same effectiveness the game targets with.
        public int RoundsRemaining => FlightOrders.TotalAmmo(Aircraft);

        // Role label -> rounds now. Peak keeps what it ever carried, so a role
        // that has run out still shows as a zero rather than vanishing.
        internal readonly Dictionary<string, int> RoleStores = new Dictionary<string, int>();
        internal readonly Dictionary<string, int> RolePeak = new Dictionary<string, int>();

        private float storesAt = -100f;

        // Twice a second is plenty for a readout; it was every frame.
        internal void RefreshStores()
        {
            if (Time.unscaledTime - storesAt < 0.5f && Time.unscaledTime >= storesAt) return;
            storesAt = Time.unscaledTime;
            RoleStores.Clear();
            if (Aircraft == null || Aircraft.weaponStations == null) return;
            foreach (WeaponStation station in Aircraft.weaponStations)
            {
                if (station?.WeaponInfo == null || IsStore(station)) continue;
                string role = FlightOrders.RoleOf(station.WeaponInfo);
                // Anything that scores against nothing is filed as GUN; only a
                // real gun is one. Pods and other equipment -- jammers,
                // designators, radar pods -- are left out, or an empty pod
                // read as "GUN gone" on an aircraft that never had a gun.
                if (role == "GUN" && !station.WeaponInfo.gun) continue;
                int ammo = Mathf.Max(0, station.Ammo);
                RoleStores.TryGetValue(role, out int running);
                RoleStores[role] = running + ammo;
            }
            foreach (KeyValuePair<string, int> entry in RoleStores)
            {
                RolePeak.TryGetValue(entry.Key, out int peak);
                if (entry.Value > peak) RolePeak[entry.Key] = entry.Value;
            }
        }

        // Not a weapon: a drop tank or cargo. Counted as one, a jettisoned
        // empty tank read as a role run dry, and flagged the flight for
        // attention with nothing wrong.
        // The prefab search is kept per weapon type: this is asked for every
        // station of every flight, every frame.
        private static readonly Dictionary<WeaponInfo, bool> tankTypes = new Dictionary<WeaponInfo, bool>();

        internal static bool IsStore(WeaponStation station)
        {
            if (station.Cargo) return true;
            WeaponInfo info = station.WeaponInfo;
            if (info == null) return false;
            if (!tankTypes.TryGetValue(info, out bool tank))
            {
                GameObject prefab = info.weaponPrefab;
                tankTypes[info] = tank = prefab != null && prefab.GetComponentInChildren<FuelTank>(true) != null;
            }
            return tank;
        }

        // Why a flight needs attention, or null when it doesn't. Nothing here
        // stays up for good:
        //   missile inbound -- while a missile is on it;
        //   low fuel        -- while it is not already heading home (the
        //                      pilot turns for home on its own at 20%);
        //   out of weapons, or a weapon type run dry -- events, shown for a
        //                      minute or until its window is opened, never
        //                      while heading home; a type rearmed resets.
        private readonly Dictionary<string, float> eventAt = new Dictionary<string, float>();
        private readonly HashSet<string> acknowledged = new HashSet<string>();
        private const float EventSeconds = 60f;

        public string Attention
        {
            get
            {
                if (Threat == FlightThreat.Missile) return "missile inbound";
                bool heading = Mode == FlightMode.ReturnToBase;
                if (!heading && FuelPercent < Tuning.LowFuelAlert) return "low fuel";
                UpdateEvents();
                if (heading) return null;
                float now = Time.timeSinceLevelLoad;
                foreach (KeyValuePair<string, float> entry in eventAt)
                    if (!acknowledged.Contains(entry.Key) && now - entry.Value < EventSeconds) return entry.Key;
                return null;
            }
        }

        // Opening the flight's window has seen what it had to say.
        public void Acknowledge()
        {
            UpdateEvents();
            foreach (string key in eventAt.Keys) acknowledged.Add(key);
        }

        private void UpdateEvents()
        {
            var current = new HashSet<string>();
            if (RoundsRemaining <= 0 && RolePeak.Count > 0) current.Add("out of weapons");
            else
                foreach (KeyValuePair<string, int> entry in RolePeak)
                {
                    if (entry.Value <= 0) continue;
                    RoleStores.TryGetValue(entry.Key, out int now);
                    if (now <= 0) current.Add(entry.Key + " gone");
                }
            foreach (string key in current)
                if (!eventAt.ContainsKey(key)) eventAt[key] = Time.timeSinceLevelLoad;
            // Rearmed, or superseded: forget it, so it can be raised afresh.
            var gone = new List<string>();
            foreach (string key in eventAt.Keys) if (!current.Contains(key)) gone.Add(key);
            foreach (string key in gone) { eventAt.Remove(key); acknowledged.Remove(key); }
        }

        // Short enough for a chip: "A/G 0  A/A 4  GUN 240".
        public string StoresSummary
        {
            get
            {
                if (RolePeak.Count == 0) return "no stores";
                var parts = new List<string>();
                foreach (string role in FlightOrders.RoleOrder)
                {
                    if (!RolePeak.ContainsKey(role) || RolePeak[role] <= 0) continue;
                    RoleStores.TryGetValue(role, out int now);
                    parts.Add(role + " " + now);
                }
                return parts.Count == 0 ? "no stores" : string.Join("  ", parts.ToArray());
            }
        }

        // True when something it launched with has run out.
        public bool AnyRoleExhausted
        {
            get
            {
                foreach (KeyValuePair<string, int> entry in RolePeak)
                {
                    if (entry.Value <= 0) continue;
                    RoleStores.TryGetValue(entry.Key, out int now);
                    if (now <= 0) return true;
                }
                return false;
            }
        }

        public string Stores
        {
            get
            {
                if (Aircraft == null || Aircraft.weaponStations == null) return "";
                var parts = new List<string>();
                foreach (WeaponStation station in Aircraft.weaponStations)
                {
                    if (station?.WeaponInfo == null || station.Ammo <= 0) continue;
                    parts.Add(station.WeaponInfo.shortName is string shortName && shortName.Length > 0
                        ? shortName + " " + station.Ammo
                        : station.WeaponInfo.weaponName + " " + station.Ammo);
                }
                return parts.Count == 0 ? "no stores" : string.Join(", ", parts.ToArray());
            }
        }

        // Whatever the standing task is, what it is doing right now comes first.
        // What it is doing this moment, ahead of its standing task: shot at,
        // fighting back and at what, the stage of an attack, forming up.
        public string Status
        {
            get
            {
                if (Threat == FlightThreat.Missile) return StandOn ? "DEFENDING" : "EVADING";
                Unit fighting = NativeTarget();
                if (Interrupted)
                    return fighting == null ? "ENGAGING"
                        : (Attackers.ContainsKey(fighting) ? "ATTACKING ATTACKER · " : "ENGAGING · ") + ShipNames.Of(fighting);
                if (Mode == FlightMode.Strike && RunInDone && fighting != null) return "ATTACKING · " + ShipNames.Of(fighting) + ListProgress();
                if (activity != null && Time.timeSinceLevelLoad < activityUntil) return activity + (Mode == FlightMode.Strike ? ListProgress() : "");
                if (Mode == FlightMode.Egress) return (Cranking ? "CRANKING" : "EGRESSING") + ListProgress();
                if (Mode == FlightMode.Orbit && StrikeList.Count > 0) return "HOLDING · missiles on the way" + ListProgress();
                if (Aircraft != null && FlightOrders.FirstPilot(Aircraft)?.currentState is AIPilotLandingState landing)
                {
                    string phase = DeckWaveOff.Phase(landing);
                    if (phase != null) return "LANDING · " + phase;
                }
                if (Wings.FormingUp(this, out int waitingOn)) return "FORMING UP" + (waitingOn > 0 ? " · waiting for " + waitingOn : "");
                if (Wings.Joining(this, out float off)) return "JOINING · " + UnitConverter.DistanceReading(off);
                return null;
            }
        }

        private string activity;
        private float activityUntil;

        // Set by whatever is flying it, each frame it applies; lapses a moment
        // after it stops being said.
        internal void Doing(string what)
        {
            activity = what;
            activityUntil = Time.timeSinceLevelLoad + 1.5f;
        }

        private string ListProgress() => StrikeList.Count > 1 ? " · " + StrikeList.Count + " on the list" : "";

        private static readonly System.Reflection.FieldInfo CombatTarget = HarmonyLib.AccessTools.Field(typeof(AIPilotCombatModes), "currentTarget");
        private static readonly System.Reflection.FieldInfo HeloTarget = HarmonyLib.AccessTools.Field(typeof(AIHeloCombatState), "currentTarget");

        // What the game's combat pilot is going for, when it has the aircraft.
        private Unit NativeTarget()
        {
            PilotBaseState state = Aircraft != null ? FlightOrders.FirstPilot(Aircraft)?.currentState : null;
            try
            {
                if (state is AIPilotCombatModes && CombatTarget != null) return CombatTarget.GetValue(state) as Unit;
                if (state is AIHeloCombatState && HeloTarget != null) return HeloTarget.GetValue(state) as Unit;
            }
            catch { }
            return null;
        }

        public string Describe()
        {
            string now = Status;
            string task = Task();
            // Jamming carried through a strike: say so.
            if (Mode != FlightMode.Jam && JamTargets.Count > 0 && FlightOrders.KeepsJamming(Mode))
                task += " · jamming " + JamTargets.Count;
            if (now != null) return now + " · " + task;
            return task;
        }

        private string Task()
        {
            switch (Mode)
            {
                case FlightMode.Route: return Route.Count > 0 ? "Route · " + Route.Count + " leg(s)" : "Route complete";
                case FlightMode.Orbit: return "Station area · " + UnitConverter.DistanceReading(OrbitRadius) +
                    (ConfineToArea ? "" : " · unrestricted");
                case FlightMode.Station: return "Station on " + (StationShip != null ? ShipNames.Of(StationShip) : HomeName);
                case FlightMode.Strike: return !Host.Dead(Target)
                    ? "Strike · " + (Target.definition?.unitName ?? Target.name) : "Strike · target gone";
                case FlightMode.Egress: return Cranking ? "Cranking · guiding its missiles" : "Egressing · weapons away";
                case FlightMode.Cargo: return SupplyShip != null ? "Naval supply · " + ShipNames.Of(SupplyShip)
                    : (Airdrop ? "Airdrop" : "Delivery") + " · inbound to the zone";
                case FlightMode.Jam:
                {
                    var names = new List<string>();
                    foreach (Unit unit in JamTargets) if (unit != null && !unit.disabled) names.Add(unit.definition?.unitName ?? unit.name);
                    if (names.Count == 0 && !Host.Dead(Target)) names.Add(Target.definition?.unitName ?? Target.name);
                    return names.Count > 0 ? "Jamming · " + string.Join(", ", names) : "Jamming · target gone";
                }
                case FlightMode.Engage: return "Weapons free · AI engaging";
                case FlightMode.Formation:
                    Flight lead = Wings.LeadOf(this);
                    if (lead != this) return "Formation on " + lead.Name;
                    Flight escorted = Wings.EscortedLead(this);
                    return escorted != null ? "Escorting " + (escorted.Wing ?? escorted.Name) : "Formation · no lead";
                default: return "Returning to base";
            }
        }
    }

    public static class FlightOrders
    {
        private static readonly List<Flight> flights = new List<Flight>();

        // A launch is a request; the aircraft appears some time later. Watch for
        // it rather than guessing, and give up if it never arrives.
        private sealed class Pending
        {
            internal Airbase Field;
            internal AircraftDefinition Definition;
            internal NuclearOption.SavedMission.Loadout Loadout;   // identity of this launch
            internal string Callsign;
            internal string Wing;
            internal float ExpiresAt;
            internal float RequestedAt;
        }
        private static readonly List<Pending> pending = new List<Pending>();

        // Unit.ReportKilled pays an individual only when the crediting unit's
        // PersistentUnit carries a player, which an AI-flown aircraft does not.
        // Kills by aircraft we launched therefore paid the faction and nobody
        // else -- the same gap the commanded ship had. The game already fills
        // this field for owned ground vehicles.
        private static void CreditKills(Aircraft aircraft)
        {
            if (aircraft == null || !Host.CreditKillsToPlayer) return;
            if (!GameManager.GetLocalPlayer<Player>(out Player player) || player == null) return;
            if (player.HQ == null || aircraft.NetworkHQ != player.HQ) return;   // never another faction's kills
            if (!UnitRegistry.TryGetPersistentUnit(aircraft.persistentID, out PersistentUnit persistent)) return;
            if (persistent == null || persistent.player != null) return;     // never take another player's
            persistent.player = player;
        }

        internal static void ExpectLaunch(Airbase field, AircraftDefinition definition,
            NuclearOption.SavedMission.Loadout loadout, string callsign, string wing = null)
        {
            pending.Add(new Pending
            {
                Wing = wing,
                Field = field,
                Definition = definition,
                Loadout = loadout,
                Callsign = callsign,
                RequestedAt = Time.unscaledTime,
                ExpiresAt = Time.unscaledTime + 90f
            });
        }

        // Callsigns already spoken for: flying, or on a deck waiting to launch.
        internal static IEnumerable<string> LabelsInUse()
        {
            foreach (Flight flight in All())
                if (!string.IsNullOrEmpty(flight.Label)) yield return flight.Label;
            foreach (Pending request in pending)
                if (!string.IsNullOrEmpty(request.Callsign)) yield return request.Callsign;
            foreach (string queued in LaunchQueue.LabelsInUse()) yield return queued;
        }

        public static void Rename(Flight flight, string label)
        {
            if (flight == null) return;
            label = (label ?? "").Trim();
            if (label.Length == 0) return;
            flight.Label = label;
            Callsigns.Apply(flight.Aircraft, label);
        }

        // Claimed the moment the hangar builds it, matched on the loadout we
        // handed in, so a simultaneous AI launch of the same type cannot be
        // mistaken for ours.
        internal static Flight ClaimLaunch(NuclearOption.SavedMission.Loadout loadout, Aircraft aircraft)
        {
            if (loadout == null || aircraft == null) return null;
            for (int i = 0; i < pending.Count; i++)
            {
                if (!ReferenceEquals(pending[i].Loadout, loadout)) continue;
                Airbase home = pending[i].Field;
                string callsign = pending[i].Callsign;
                string wing = pending[i].Wing;
                pending.RemoveAt(i);
                if (home == null) return null;
                // Already adopted by the proximity fallback, under another
                // launch's name: this is the authoritative match, so it takes
                // this launch's callsign and wing instead of becoming a second
                // flight flying the same aircraft.
                Flight existing = Of(aircraft);
                if (existing != null)
                {
                    existing.Wing = wing;
                    Rename(existing, callsign ?? existing.Label);
                    Wings.Joined(existing);
                    Tracing.Deck("[deck] " + existing.Name + " · corrected from a proximity match");
                    return existing;
                }
                // Another mod's aircraft is not ours to adopt.
                if (!Ownership.Claim(aircraft)) return null;
                var flight = new Flight
                {
                    Aircraft = aircraft,
                    Home = home,
                    Wing = wing,
                    Mode = FlightMode.Orbit,
                    OrbitCentre = Airfields.PositionOf(home),
                    Altitude = Tuning.DefaultAltitude,
                    OrbitRadius = Tuning.DefaultAreaRadius
                };
                flights.Add(flight);
                CreditKills(aircraft);
                Rename(flight, callsign ?? Callsigns.Suggest(aircraft));
                Wings.Joined(flight);
                return flight;
            }
            return null;
        }

        // An aircraft that already exists -- placed by the mission, restored
        // from a save, spawned by a scenario event -- taken on as a flight of
        // ours as it is. On the ground it is treated as parked at its home;
        // in the air the tick installs our state as after any launch.
        public static Flight Adopt(Aircraft aircraft, Airbase home, string callsign, string wing = null)
        {
            if (aircraft == null || aircraft.disabled || aircraft.Player != null) return null;
            Pilot crew = FirstPilot(aircraft);
            if (crew != null && (crew.playerControlled || crew.currentState is PilotPlayerState)) return null;
            Flight existing = Of(aircraft);
            if (existing != null) return existing;
            if (!Ownership.Claim(aircraft)) return null;
            var flight = new Flight
            {
                Aircraft = aircraft,
                Home = home,
                Wing = wing,
                Mode = FlightMode.Orbit,
                OrbitCentre = home != null ? Airfields.PositionOf(home) : aircraft.GlobalPosition(),
                Altitude = Tuning.DefaultAltitude,
                OrbitRadius = Tuning.DefaultAreaRadius
            };
            flights.Add(flight);
            CreditKills(aircraft);
            Rename(flight, callsign ?? Callsigns.Suggest(aircraft));
            Wings.Joined(flight);
            return flight;
        }

        internal static void RenameWing(string wing, string name)
        {
            foreach (Pending request in pending)
            {
                if (request.Wing != wing) continue;
                request.Wing = name;
                int dash = request.Callsign != null ? request.Callsign.LastIndexOf('-') : -1;
                request.Callsign = name + (dash >= 0 ? request.Callsign.Substring(dash) : "");
            }
        }

        // Members of a wing asked of a deck but not yet off it.
        internal static int PendingInWing(string wing)
        {
            int count = 0;
            foreach (Pending request in Unseen()) if (request.Wing == wing) count++;
            return count;
        }

        // Launches requested but not yet seen on deck.
        internal static List<string> PendingNames(Airbase field)
        {
            var names = new List<string>();
            foreach (Pending request in Unseen())
                if (request.Field == field && request.Definition != null) names.Add(request.Definition.unitName);
            return names;
        }

        // Requests with no aircraft to show for them yet. One is already on the
        // deck when an aircraft of its type has come out of that field's
        // hangar since it was asked for and is not yet taken on as a flight --
        // the hook that claims a launch can miss, and the fallback match waits
        // twenty seconds, so four Vortexes taxiing were also listed as four
        // still in the hangar.
        private static List<Pending> Unseen()
        {
            var result = new List<Pending>();
            if (pending.Count == 0) return result;
            var used = new HashSet<Aircraft>();
            foreach (Pending request in pending)
            {
                bool seen = false;
                if (request.Field != null && request.Definition != null)
                    foreach (Unit unit in UnitRegistry.allUnits)
                    {
                        if (!(unit is Aircraft aircraft) || aircraft.disabled || used.Contains(aircraft)) continue;
                        if (aircraft.definition != request.Definition || Of(aircraft) != null) continue;
                        if (DeckTraffic.LaunchedAt(request.Field, aircraft) < request.RequestedAt - 1f) continue;
                        used.Add(aircraft);
                        seen = true;
                        break;
                    }
                if (!seen) result.Add(request);
            }
            return result;
        }

        internal static readonly string[] RoleOrder = { "A/G", "A/A", "ARM", "PD", "GUN" };

        // Which job this weapon is for, from the game's own effectiveness
        // profile rather than a list of weapon names.
        internal static string RoleOf(WeaponInfo info)
        {
            if (info == null) return "GUN";
            if (info.gun) return "GUN";
            RoleIdentity role = info.effectiveness;
            float best = role.antiSurface;
            string label = "A/G";
            if (role.antiAir > best) { best = role.antiAir; label = "A/A"; }
            if (role.antiRadar > best) { best = role.antiRadar; label = "ARM"; }
            if (role.antiMissile > best) { best = role.antiMissile; label = "PD"; }
            return best <= 0.001f ? "GUN" : label;
        }

        // Every flight we launched and still command, whichever deck it came
        // off. Filtering by the ship on the bridge made a flight vanish from
        // view the moment command moved to another ship, though it was still
        // flying its orders and still ours.
        public static List<Flight> All()
        {
            var result = new List<Flight>();
            foreach (Flight flight in flights)
                if (flight.Aircraft != null && !flight.Aircraft.disabled) result.Add(flight);
            return result;
        }

        public static Flight Of(Aircraft aircraft)
        {
            foreach (Flight flight in flights) if (flight.Aircraft == aircraft) return flight;
            return null;
        }

        internal static void Tick()
        {
            AssessThreats();
            Guard.Run("Missile jamming", MissileJamming.Tick);
            Guard.Run("Laser defence", LaserDefence.Tick);
            Guard.Run("Moving deck recovery", MovingDeckRecovery.Tick);
            Guard.Run("Deck wave-off", DeckWaveOff.Tick);
            Guard.Run("Handovers", Ownership.ServiceHandovers);
            Guard.Run("Cargo in one pass", CargoBurst.Tick);
            Guard.Run("Fixed-wing drops", FixedWingDrops.Forget);
            Guard.Run("Ejection", EjectionCheck.Tick);
            Guard.Run("Turnaround", Turnaround.Tick);
            Guard.Run("Recovery queue", RecoveryQueue.Tick);
            for (int i = flights.Count - 1; i >= 0; i--)
                if (flights[i].Aircraft == null || flights[i].Aircraft.disabled) flights.RemoveAt(i);
            foreach (Flight flight in flights)
            {
                flight.RefreshStores();
                // A dogleg home reached (the route ends in an orbit at its last
                // leg), or the flight retasked meanwhile: the landing, or nothing.
                if (flight.HomingVia.HasValue)
                {
                    if (flight.Mode != FlightMode.Route && flight.Mode != FlightMode.Orbit) flight.HomingVia = null;
                    else if (flight.Mode == FlightMode.Orbit || (flight.Aircraft != null &&
                             FastMath.InRange(flight.Aircraft.GlobalPosition(), flight.HomingVia.Value, 2500f)))
                    {
                        flight.HomingVia = null;
                        flight.Mode = FlightMode.ReturnToBase;
                    }
                }
                // The burner for the native pilot too: it sets full throttle in
                // a fight and never touches the axis that lights the afterburner
                // on airframes with parasitic thrust loss, so a mod fighter
                // fought at a fraction of its thrust and died slow.
                if (flight.Interrupted && flight.Aircraft.autopilot is AutopilotPlane && !Host.IsFlownByPlayer(flight))
                {
                    AuxAxis.Apply(flight.Aircraft, flight.Aircraft.GetInputs());
                }
            }

            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Pending request = pending[i];
                if (request.Field == null || Time.unscaledTime > request.ExpiresAt) { pending.RemoveAt(i); continue; }
                // The spawn hook normally claims the aircraft outright; this
                // only covers a build where that hook failed to bind. Give the
                // hook time first: matching by proximity straight away grabbed
                // an aircraft the hook was about to claim for another launch,
                // and scrambled a wing's callsigns and leads.
                if (Time.unscaledTime - request.RequestedAt < 20f) continue;
                Aircraft found = FindNew(request);
                if (found == null) continue;
                Pilot matched = FirstPilot(found);
                Host.LogWarning("[deck] launch matched by proximity, not by loadout · " +
                    (request.Definition?.unitName ?? "aircraft") + " · state " + (matched?.currentState?.GetType().Name ?? "none") +
                    " · alt " + found.radarAlt.ToString("0") + " m · speed " + found.speed.ToString("0") + " m/s");
                pending.RemoveAt(i);
                if (!Ownership.Claim(found)) continue;
                var flight = new Flight
                {
                    Aircraft = found,
                    Home = request.Field,
                    Wing = request.Wing,
                    Mode = FlightMode.Orbit,
                    OrbitCentre = Airfields.PositionOf(request.Field),
                    Altitude = Tuning.DefaultAltitude,
                    OrbitRadius = Tuning.DefaultAreaRadius
                };
                flights.Add(flight);
                CreditKills(found);
                Rename(flight, request.Callsign ?? Callsigns.Suggest(found));
                Wings.Joined(flight);
                Host.LogInfo("[flight] adopted " + flight.Name + " from " + Airfields.NameOf(request.Field) + TakeoffCheck.Actual(found));
            }

            // Install our state once the aircraft is actually flying: taking it
            // over during taxi or takeoff would fight the native sequence.
            foreach (Flight flight in flights)
            {
                if (flight.Aircraft != null && !flight.Aircraft.disabled) CargoProgress(flight);

                if (flight.Mode == FlightMode.Jam)
                {
                    // A target gone leaves the rest; the last one gone ends the task.
                    int before = flight.JamTargets.Count;
                    flight.JamTargets.RemoveAll(u => u == null || u.disabled);
                    if (flight.JamTargets.Count == 0 && !Host.Dead(flight.Target))
                        flight.JamTargets.Add(flight.Target);
                    if (flight.JamTargets.Count == 0)
                    {
                        Host.LogInfo("[flight] " + flight.Name + " · jamming target gone");
                        BreakOff(flight);
                    }
                    else
                    {
                        if (flight.JamTargets.Count < before) Host.Say(flight.Name + " · a jamming target is gone · " + flight.JamTargets.Count + " left");
                        flight.Target = flight.JamTargets[0];
                    }
                }

                // The strike's target lost to the faction's picture for half a
                // minute: end it (or go on down the list) rather than leave the
                // combat pilot hunting whatever it finds.
                if (flight.Mode == FlightMode.Strike && !Host.Dead(flight.Target) && flight.Aircraft.NetworkHQ != null)
                {
                    if (flight.Aircraft.NetworkHQ.TryGetKnownPosition(flight.Target, out _)) flight.TrackLostSince = -1f;
                    else if (flight.TrackLostSince < 0f) flight.TrackLostSince = Time.timeSinceLevelLoad;
                    else if (Time.timeSinceLevelLoad - flight.TrackLostSince > 30f)
                    {
                        flight.TrackLostSince = -1f;
                        Host.LogInfo("[flight] " + flight.Name + " · lost the track on " + ShipNames.Of(flight.Target) + ", ending the strike");
                        if (flight.StrikeList.Count > 0) { flight.StrikeList.RemoveAll(i => i.Target == flight.Target); StrikePlans.Next(flight, "track lost"); }
                        else BreakOff(flight);
                    }
                }

                if (flight.Mode == FlightMode.Strike && (Host.Dead(flight.Target)))
                {
                    if (flight.StrikeList.Count > 0) StrikePlans.Next(flight, "target down");
                    else
                    {
                        Host.LogInfo("[flight] " + flight.Name + " · target destroyed, breaking off");
                        BreakOff(flight);
                    }
                }
                StrikePlans.Tick(flight);

                // Pressing an attack that never produces a release. The usual
                // cause is bombs against a track the AI will not drop on, but
                // the symptom is the same whatever the reason: passes without
                // rounds leaving. Give it time to arrive and acquire first,
                // then try something else, then give up honestly.
                if (flight.Mode == FlightMode.Strike && flight.RunInDone && flight.StrikeStartAmmo >= 0 &&
                    Time.timeSinceLevelLoad - flight.StrikeStarted > Tuning.StrikePatience &&
                    TotalAmmo(flight.Aircraft) >= flight.StrikeStartAmmo)
                {
                    WeaponStation alternative = null;
                    foreach (WeaponStation station in ArmedStations(flight.Aircraft))
                    {
                        WeaponInfo info = station.WeaponInfo;
                        if (info.bomb || info.glideBomb) continue;          // the likely culprit
                        if (info.name == flight.PreferredWeapon) continue;  // already tried
                        if (WeaponOrders.Opportunity(info, flight.Target) <= 0.01f && !info.gun) continue;
                        alternative = station;
                        break;
                    }

                    if (alternative != null)
                    {
                        Tracing.Flight("[flight] " + flight.Name + " · no release after " +
                            Tuning.StrikePatience.ToString("0") + " s, switching to " +
                            alternative.WeaponInfo.weaponName);
                        Strike(flight, flight.Target, alternative.WeaponInfo.name);
                    }
                    else
                    {
                        Host.LogWarning("[flight] " + flight.Name +
                            " · cannot get a release on this target, breaking off");
                        BreakOff(flight);
                    }
                }

                // A shot has left the aircraft: stop pressing. Not in an air
                // fight, where turning away only hands the enemy the shot.
                // A standoff launch leaves once its whole salvo is away.
                if (flight.Mode == FlightMode.Strike && flight.AmmoAtAttack >= 0 && !IsAirTarget(flight.Target) && flight.SalvoLeft <= 0)
                {
                    int now = TotalAmmo(flight.Aircraft);
                    // On a strike list, the other weapons' targets reachable
                    // from here go on the same pass before it turns away.
                    if (now >= 0 && now < flight.AmmoAtAttack && !StrikePlans.FollowUp(flight)) Egress(flight);
                }

                if (flight.Mode == FlightMode.Egress && flight.Cranking)
                {
                    // Cold once nothing is left to guide, or after long enough.
                    int guided = Time.timeSinceLevelLoad < flight.NextEgressPlan ? 1 : Crank.Supported(flight.Aircraft, null);
                    if (Time.timeSinceLevelLoad >= flight.NextEgressPlan) flight.NextEgressPlan = Time.timeSinceLevelLoad + 0.5f;
                    if ((guided == 0 && Time.timeSinceLevelLoad - flight.CrankStarted > 3f) || Time.timeSinceLevelLoad >= flight.CrankUntil)
                    {
                        flight.Cranking = false;
                        flight.EgressUntil = Time.timeSinceLevelLoad + Crank.ColdSeconds;
                        PlanEgress(flight);
                        Tracing.Flight("[flight] " + flight.Name + " · " + (guided == 0 ? "missiles on their own" : "crank timed out") + ", going cold");
                    }
                }
                else if (flight.Mode == FlightMode.Egress)
                {
                    // The threat picture moves; so should the escape route.
                    if (Time.timeSinceLevelLoad >= flight.NextEgressPlan)
                    {
                        flight.NextEgressPlan = Time.timeSinceLevelLoad + 2f;
                        PlanEgress(flight);
                    }
                    // Against an air target, the time cold is the egress:
                    // range from a fighter says little about being safe from it.
                    bool clear = Host.Dead(flight.Target) || (!IsAirTarget(flight.Target) &&
                        FastMath.Distance(flight.Aircraft.GlobalPosition(), flight.Target.GlobalPosition())
                            >= Tuning.StandoffMetres);
                    if ((clear || Time.timeSinceLevelLoad >= flight.EgressUntil) && flight.StrikeList.Count > 0)
                        StrikePlans.Next(flight, "after the pass");
                    else if (clear || Time.timeSinceLevelLoad >= flight.EgressUntil)
                    {
                        // Out of danger. Press again only with something left to
                        // press with, and only if the target is still there.
                        // An aircraft gets one salvo per pass: the missiles
                        // take time to arrive, and a wing re-attacking a lone
                        // helicopter every egress emptied eight racks at it.
                        bool rearmed = !Host.Dead(flight.Target) &&
                            !(flight.Target is Aircraft) &&
                            (NamedStation(flight.Aircraft, flight.PreferredWeapon) ?? BestStationFor(flight.Aircraft, flight.Target)) != null &&
                            Tuning.ReattackAfterEgress;
                        if (rearmed)
                        {
                            Tracing.Flight("[flight] " + flight.Name + " · re-attacking");
                            Strike(flight, flight.Target);
                        }
                        else
                        {
                            Tracing.Flight("[flight] " + flight.Name + " · clear of the target, resuming");
                            BreakOff(flight);
                        }
                    }
                }
                Pilot crew = FirstPilot(flight.Aircraft);
                if (crew != null && !crew.playerControlled && flight.Adopted)
                {
                    bool yield = ShouldYield(flight);
                    if (yield && !flight.Interrupted && crew.currentState is NavalPilotState)
                    {
                        // Hand it over: the native pilot evades and fights far
                        // better than a navigation loop ever will.
                        flight.Interrupted = true;
                        PilotBaseState combat = CombatStateFor(crew);
                        if (combat != null) { crew.SwitchStateNew(combat); NativePilot.Wake(combat, flight.Aircraft); }
                        Tracing.Flight("[flight] " + flight.Name + " · " +
                            (flight.Threat == FlightThreat.Missile ? "evading" : "engaging"));
                    }
                    else if (!yield && flight.Interrupted &&
                             Time.unscaledTime - flight.ThreatClearedAt > Tuning.ThreatSettleSeconds)
                    {
                        // Settle before taking it back, or it yo-yos between
                        // states every time a threat flickers in and out.
                        Tracing.Flight("[flight] " + flight.Name + " · clear, resuming task");
                        Reclaim(flight);
                    }
                }

                if (flight.Adopted || flight.Aircraft == null) continue;
                Pilot pilot = FirstPilot(flight.Aircraft);
                if (pilot == null || pilot.playerControlled) continue;
                // A strike or weapons-free order wants the native combat pilot,
                // and can be handed over from our own state as well as from
                // the native one -- gating it on the native state meant the
                // handoff never happened once we already had the aircraft.
                if (flight.Mode == FlightMode.Cargo)
                {
                    // Not while it is still getting off the deck, and not once
                    // a supply run has dropped its container: that one is
                    // being sent home.
                    if (StillLeaving(pilot)) continue;
                    if (flight.SupplyShip != null && Replenishment.Delivered(flight)) continue;
                    // An aeroplane: a mod's fixed-wing transport state, pointed
                    // at our drop.
                    if (pilot.pilotType == Pilot.PilotType.Plane)
                    {
                        if (!FixedWingDrops.Start(pilot))
                        {
                            Host.LogWarning("[flight] " + flight.Name + " cannot fly an airdrop; returning it to its previous task");
                            Host.Say(flight.Name + " · cannot fly an airdrop");
                            BreakOff(flight);
                            continue;
                        }
                        Host.LogInfo("[flight] " + flight.Name + " · airdropping cargo");
                        flight.Adopted = true;
                        continue;
                    }
                    // The game builds this state lazily, inside the helo combat
                    // state, the first time an aircraft notices cargo aboard.
                    // A flight we took under command on the climb-out has never
                    // been through there, so the field is usually still null --
                    // and leaving the aircraft in our own state with a mode we
                    // do not fly is what dropped one into the sea. Build it the
                    // same way the game does.
                    if (pilot.AIHeloTransportState == null && CanDeliver(flight.Aircraft))
                        pilot.AIHeloTransportState = new AIHeloTransportState(flight.Aircraft);

                    if (pilot.AIHeloTransportState == null)
                    {
                        Host.LogWarning("[flight] " + flight.Name +
                            " cannot fly a delivery; returning it to its previous task");
                        Host.Say(flight.Name + " · cannot fly a delivery");
                        BreakOff(flight);
                        continue;
                    }

                    if (!(pilot.currentState is AIHeloTransportState))
                    {
                        pilot.SwitchStateNew(pilot.AIHeloTransportState);
                        Host.LogInfo("[flight] " + flight.Name + " · " +
                            (flight.Airdrop ? "airdropping" : "delivering") + " cargo");
                    }
                    flight.Adopted = true;
                    continue;
                }

                // A fixed-wing strike is set up by us first -- the run-in, in
                // our own state -- and handed to the combat pilot from there.
                // An air target goes straight to the combat pilot, unless it is
                // one for a radar missile: that intercept is flown by us first.
                if (flight.Mode == FlightMode.Strike && IsAirTarget(flight.Target) &&
                    !NavalPilotState.Bvr(flight, NamedStation(flight.Aircraft, flight.PreferredWeapon) ?? BestStationFor(flight.Aircraft, flight.Target)))
                    flight.RunInDone = true;
                if (flight.Mode == FlightMode.Strike && !flight.RunInDone && !IsRotary(pilot) &&
                    NavalPilotState.CanBeFlown(flight.Aircraft))
                {
                    if (StillLeaving(pilot)) continue;
                    if (!(pilot.currentState is NavalPilotState)) NavalPilotState.Install(pilot, flight);
                    flight.Adopted = true;
                    Host.LogInfo("[flight] " + flight.Name + " · setting up to strike " +
                        (flight.Target?.definition?.unitName ?? "target"));
                    continue;
                }

                if (flight.Mode == FlightMode.Strike || flight.Mode == FlightMode.Engage)
                {
                    if (StillLeaving(pilot)) continue;          // it goes to combat by itself once clear
                    PilotBaseState combat = CombatStateFor(pilot);
                    if (combat != null && !ReferenceEquals(pilot.currentState, combat))
                        pilot.SwitchStateNew(combat);
                        NativePilot.Wake(combat, flight.Aircraft);
                    flight.Adopted = true;
                    Host.LogInfo("[flight] " + flight.Name + " · " +
                        (flight.Mode == FlightMode.Strike
                            ? "striking " + (flight.Target?.definition?.unitName ?? "target")
                            : "weapons free"));
                    continue;
                }

                // Wait until it is actually flying, but do not name the state it
                // must be in: a helicopter goes to AIHeloCombatState and never
                // to AIPilotCombatModes, so testing for the latter meant rotary
                // flights were never taken under command at all. And flying in
                // fact, not just by its pilot's state: off the deck, or fast.
                if (StillLeaving(pilot) || !Airborne(flight.Aircraft)) continue;
                if (!NavalPilotState.CanBeFlown(flight.Aircraft))
                {
                    // Better the native AI than an aircraft nobody is flying.
                    Host.LogWarning("[flight] " + flight.Name +
                        " has no usable autopilot; leaving it to the native AI");
                    flight.Mode = FlightMode.Engage;
                    flight.Adopted = true;
                    continue;
                }
                NavalPilotState.Install(pilot, flight);
                flight.Adopted = true;
            }
        }

        // Aircraft carry a pilots array rather than a single accessor; the
        // first live one flies the thing.
        internal static Pilot FirstPilot(Aircraft aircraft)
        {
            if (aircraft == null || aircraft.pilots == null) return null;
            foreach (Pilot pilot in aircraft.pilots)
                if (pilot != null && !pilot.dead && !pilot.ejected) return pilot;
            return null;
        }

        // On the deck, taxiing, or climbing out: taking over now would fight
        // the native launch sequence.
        internal static bool Airborne(Aircraft aircraft)
        {
            float takeoff = aircraft.definition?.aircraftParameters != null ? aircraft.definition.aircraftParameters.takeoffSpeed : 0f;
            return aircraft.radarAlt > 5f || aircraft.speed > Mathf.Max(takeoff * 0.9f, 20f);
        }

        internal static bool StillLeaving(Pilot pilot) =>
            pilot.currentState is PilotParkedState ||
            pilot.currentState is AIPilotTaxiState ||
            pilot.currentState is AIPilotTakeoffState ||
            pilot.currentState is AIHeloTakeoffState ||
            ModdedDeparture(pilot.currentState);

        // Another mod's own way off the deck -- Aryx's catapult has
        // AryxAIPilotCatapultTaxiState and AryxAIPilotCatapultTakeoffState,
        // which hold the aircraft on the shuttle by its nose wheel. Taken over
        // mid-stroke, the gear came up on the catapult and was torn off.
        // Those states hand over to the combat state themselves once clear.
        internal static bool ModdedDeparture(PilotBaseState state)
        {
            if (state == null) return false;
            string name = state.GetType().Name;
            return name.IndexOf("Takeoff", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Taxi", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Catapult", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool IsRotary(Pilot pilot) => pilot != null && pilot.AIHeloCombatState != null;

        // The combat state that suits this airframe.
        internal static PilotBaseState CombatStateFor(Pilot pilot) =>
            IsRotary(pilot) && pilot.AIHeloCombatState != null
                ? (PilotBaseState)pilot.AIHeloCombatState : pilot.AICombatState;

        private static Aircraft FindNew(Pending request)
        {
            FactionHQ hq = request.Field.CurrentHQ;
            if (hq == null) return null;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Aircraft aircraft) || aircraft.disabled) continue;
                if (aircraft.NetworkHQ != hq) continue;
                if (request.Definition != null && aircraft.definition != request.Definition) continue;
                if (Of(aircraft) != null) continue;
                // Only one this field's hangar has just built. A parked or
                // abandoned airframe of the same type on the deck matched
                // before, and was flown -- crewless -- until it was cleared.
                if (!DeckTraffic.CameFrom(request.Field, aircraft)) continue;
                Pilot crew = FirstPilot(aircraft);
                if (crew == null || crew.playerControlled) continue;
                // Never a player's aircraft: one rolling for take-off from the
                // same field as our launch, same type, was matched and flown
                // out from under the player. The flag above is not yet set at
                // that moment; the player reference and the state are.
                if (aircraft.Player != null || crew.currentState is PilotPlayerState) continue;
                // Close by: it came off this field rather than another one.
                if (FastMath.Distance(aircraft.GlobalPosition(), Airfields.PositionOf(request.Field)) >
                    Mathf.Max(1200f, request.Field.GetRadius())) continue;
                return aircraft;
            }
            return null;
        }

        // ---- orders --------------------------------------------------------

        // Still jamming through a strike or weapons free, and back to it after;
        // an order that sends the flight somewhere else ends it.
        // A jam list is worked alongside whatever else the flight is doing --
        // a route, an area, a strike, a delivery -- until it is stopped or the
        // flight goes home.
        internal static bool KeepsJamming(FlightMode mode) => mode != FlightMode.ReturnToBase;

        // Off the jamming station, list kept: the pods go on jamming from
        // wherever the new task takes it.
        internal static void LeaveJamStation(Flight flight)
        {
            if (flight == null) return;
            if (flight.Mode == FlightMode.Jam) { flight.Mode = FlightMode.Orbit; flight.Adopted = false; }
            if (flight.PreviousMode == FlightMode.Jam) flight.PreviousMode = FlightMode.Orbit;
        }

        // Jam this too, and change nothing else: onto the list, no move.
        public static bool JamAlong(Flight flight, Unit target)
        {
            if (flight == null || target == null) return false;
            flight.JamTargets.RemoveAll(u => u == null || u.disabled);
            if (flight.JamTargets.Contains(target)) return true;
            if (flight.JamTargets.Count >= JamCapacity(flight)) return false;
            flight.JamTargets.Add(target);
            if (flight.Mode == FlightMode.Jam) flight.Target = flight.JamTargets[0];
            return true;
        }

        public static void Unjam(Flight flight, Unit target)
        {
            if (flight == null) return;
            flight.JamTargets.Remove(target);
            if (flight.JamTargets.Count == 0) { StopJamming(flight); return; }
            if (flight.Mode == FlightMode.Jam) flight.Target = flight.JamTargets[0];
        }

        // Every jam order stops, and a jamming station is given up.
        public static void StopJamming(Flight flight)
        {
            if (flight == null) return;
            flight.JamTargets.Clear();
            if (flight.Mode == FlightMode.Jam) { flight.Mode = FlightMode.Orbit; flight.Adopted = false; }
            if (flight.PreviousMode == FlightMode.Jam) flight.PreviousMode = FlightMode.Orbit;
        }

        public static void SetRoute(Flight flight, GlobalPosition point, bool append)
        {
            if (flight == null) return;
            TakeBack(flight);
            StrikePlans.Cancel(flight);
            LeaveJamStation(flight);
            if (!append) flight.Route.Clear();
            // A leg added to a flight working an area runs on from that area:
            // the area's centre is the route's first point. Starting over from
            // wherever the aircraft happened to be dropped the waypoint just
            // placed and flew straight to the new one.
            else if (flight.Mode == FlightMode.Orbit && flight.Route.Count == 0)
                flight.Route.Add(flight.OrbitCentre);
            flight.Route.Add(point);
            flight.Mode = FlightMode.Route;
        }

        public static void Orbit(Flight flight, GlobalPosition centre)
        {
            if (flight == null) return;
            StrikePlans.Cancel(flight);
            flight.OrbitCentre = centre;
            flight.Route.Clear();
            flight.Mode = FlightMode.Orbit;
        }

        // A task area is where the flight works: it holds inside it and, unless
        // released, will not prosecute anything outside it. That is the
        // difference between a patrol and an aircraft that wanders off after the
        // first contact it sees.
        // A movement order flown by us, wherever the aircraft is: the game's
        // combat pilot, given it for a strike or a fight, kept it -- a lead
        // sent to a new area after a missile strike flew straight on with its
        // target dead, the order noted and nobody flying it. Taken back unless
        // ours already has it; a threat still hands it over again as usual.
        private static void TakeBack(Flight flight)
        {
            if (flight?.Aircraft == null) return;
            Pilot pilot = FirstPilot(flight.Aircraft);
            if (pilot == null || pilot.playerControlled || pilot.currentState is NavalPilotState) return;
            if (StillLeaving(pilot)) return;                 // off the deck first; adoption follows
            if (flight.Mode == FlightMode.Strike || flight.Mode == FlightMode.Egress || flight.Mode == FlightMode.Engage) flight.Target = null;
            flight.Adopted = false;
            flight.Interrupted = false;
        }

        public static void SetArea(Flight flight, GlobalPosition centre, float radius)
        {
            if (flight == null) return;
            TakeBack(flight);
            StrikePlans.Cancel(flight);
            flight.OrbitCentre = centre;
            flight.OrbitRadius = Mathf.Clamp(radius, 500f, 60000f);
            flight.Route.Clear();
            // Jamming, an area moves where it jams from rather than ending it --
            // used if every target is in reach from all of it, moved if not.
            if (flight.Mode == FlightMode.Jam) return;
            flight.Mode = FlightMode.Orbit;
        }

        public static void SetConfined(Flight flight, bool confined)
        {
            if (flight != null) flight.ConfineToArea = confined;
        }

        // Does this flight have an area that limits where it may fight?
        internal static bool HasArea(Flight flight) =>
            flight != null && flight.ConfineToArea &&
            (flight.Mode == FlightMode.Orbit || flight.Mode == FlightMode.Station);

        internal static GlobalPosition AreaCentre(Flight flight) =>
            flight.Mode == FlightMode.Station && flight.StationAnchor != null
                ? flight.StationAnchor.GlobalPosition()
                : flight.Mode == FlightMode.Station && flight.Home != null ? flight.HomePosition : flight.OrbitCentre;

        public static void Station(Flight flight, Ship on = null)
        {
            if (flight == null || (flight.Home == null && on == null)) return;
            TakeBack(flight);
            StrikePlans.Cancel(flight);
            LeaveJamStation(flight);
            flight.StationShip = on;
            flight.Route.Clear();
            // Abeam and slightly ahead: clear of the ship, still close aboard.
            flight.StationOffset = new Vector3(2200f, 0f, 1200f);
            flight.Mode = FlightMode.Station;
        }

        // Designating a target hands the flight to the native combat pilot,
        // which knows how to run an attack, while a patch pins its target to
        // ours. When the target dies the flight comes back under command rather
        // than wandering off hunting.
        public static void Strike(Flight flight, Unit target, string preferredWeapon = null)
        {
            if (flight == null || target == null) return;
            StrikePlans.Cancel(flight);            // a plain strike replaces any list
            flight.PreferredWeapon = preferredWeapon;
            flight.WarnedAboutTrack = false;
            flight.StrikeStarted = Time.timeSinceLevelLoad;
            flight.StrikeStartAmmo = TotalAmmo(flight.Aircraft);
            flight.RunInDone = false;
            flight.SettingUp = false;
            flight.RunInStarted = Time.timeSinceLevelLoad;
            flight.SalvoLeft = 0;
            flight.PassFirstShot = 0f;
            flight.InLaunchRangeSince = -1f;
            if (flight.Mode != FlightMode.Strike && flight.Mode != FlightMode.Egress) flight.PreviousMode = flight.Mode;
            flight.Target = target;
            flight.Cranking = false;
            flight.AmmoAtAttack = TotalAmmo(flight.Aircraft);
            flight.Route.Clear();
            flight.Mode = FlightMode.Strike;
            flight.Adopted = false;                 // let Tick hand it to the combat state
        }

        internal static int TotalAmmo(Aircraft aircraft)
        {
            if (aircraft == null || aircraft.weaponStations == null) return -1;
            int total = 0;
            foreach (WeaponStation station in aircraft.weaponStations)
                if (station != null && station.WeaponInfo != null && !Flight.IsStore(station)) total += Mathf.Max(0, station.Ammo);
            return total;
        }

        // Weapons away: get out rather than keep closing.
        //
        // The native pilot presses an attack for as long as it holds the target,
        // and pinning that target on every search means it never re-evaluates
        // and never disengages -- so it flies down the throat of whatever is
        // defending and dies there. Take the aircraft back once a shot is off
        // and fly it out to standoff before deciding what to do next.
        private static void Egress(Flight flight)
        {
            flight.Cranking = false;
            flight.Mode = FlightMode.Egress;
            flight.EgressUntil = Time.timeSinceLevelLoad + Tuning.EgressSeconds;
            PlanEgress(flight);
            flight.Adopted = false;                 // take it back off the native pilot
            flight.Interrupted = false;
            Host.LogInfo("[flight] " + flight.Name + " · weapons away, egressing");
        }

        // After an air-to-air radar shot: crank while missiles are flying on
        // this aircraft's radar. False with none such (a heat-seeker, or an
        // active round already on its own): the caller carries on as before.
        internal static bool StartCrank(Flight flight, WeaponInfo fired)
        {
            Aircraft aircraft = flight?.Aircraft;
            // The round just fired may not be in the registry this frame.
            if (aircraft == null || (!Crank.RadarGuided(fired) && Crank.Supported(aircraft, null) == 0)) return false;
            GlobalPosition here = aircraft.GlobalPosition();
            float ground = here.y - aircraft.radarAlt;
            flight.Mode = FlightMode.Egress;
            flight.Cranking = true;
            flight.CrankSide = 0;
            flight.CrankUntil = Time.timeSinceLevelLoad + Crank.MaxSeconds;
            flight.CrankStarted = Time.timeSinceLevelLoad;
            flight.CrankFloor = Mathf.Min(here.y, Mathf.Max(ground + Crank.FloorAboveGround, here.y - Crank.Descent));
            flight.EgressUntil = flight.CrankUntil + Crank.ColdSeconds;
            flight.RunInDone = true;
            flight.Adopted = false;
            flight.Interrupted = false;
            Host.LogInfo("[flight] " + flight.Name + " · missiles away, cranking");
            return true;
        }

        // Weapons away at something that cannot chase (a helicopter): leave
        // as from a ground target, rather than hand to the combat pilot.
        internal static void EgressNow(Flight flight)
        {
            if (flight == null) return;
            flight.RunInDone = true;
            Egress(flight);
        }

        // Away from the threat, never through it.
        //
        // Steering toward home is wrong whenever home lies beyond the target:
        // it takes the aircraft directly over what it just attacked, and over
        // whatever is defending it. Push away from every hostile close enough to
        // matter, weighted by how close it is, and only lean toward home when
        // that does not turn the aircraft back into them.
        private static void PlanEgress(Flight flight)
        {
            Aircraft aircraft = flight.Aircraft;
            if (aircraft == null) return;
            GlobalPosition here = aircraft.GlobalPosition();
            float reach = Tuning.StandoffMetres * 2f;

            Vector3 away = Vector3.zero;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || unit is Missile) continue;
                if (unit.NetworkHQ == null || unit.NetworkHQ == aircraft.NetworkHQ) continue;
                Vector3 from = here - unit.GlobalPosition();
                from.y = 0f;
                float distance = from.magnitude;
                if (distance < 1f || distance > reach) continue;
                // Nearer things push harder.
                away += from / distance * (1f - distance / reach);
            }

            // Nothing close enough to weigh: just leave the target behind.
            if (away.sqrMagnitude < 0.0001f && flight.Target != null)
            {
                away = here - flight.Target.GlobalPosition();
                away.y = 0f;
            }
            if (away.sqrMagnitude < 0.0001f) away = aircraft.transform.forward;
            away.y = 0f;
            away.Normalize();

            GlobalPosition home = flight.HomePosition;
            Vector3 toHome = home - here;
            toHome.y = 0f;
            if (toHome.sqrMagnitude > 1f)
            {
                toHome.Normalize();
                // Out toward friendly lines, pushed off whatever is close. Only
                // when home lies straight back through the threats does getting
                // away from them come first, angled toward home.
                away = Vector3.Dot(toHome, away) > -0.5f
                    ? (toHome * 0.65f + away * 0.35f).normalized
                    : (away * 0.7f + toHome * 0.3f).normalized;
            }

            flight.EgressPoint = here + away * Tuning.StandoffMetres;
        }

        // A jamming pod is a weapon, so the aircraft carrying one can be sent
        // to suppress a specific emitter. Unlike a strike this never closes:
        // the flight holds at standoff and keeps the pod on the target, which
        // is the whole point of sending it rather than something with bombs.
        // How many targets a flight can jam as its task: one per pod, less one
        // kept free for missiles fired at it (none kept with a single pod).
        public static int JamCapacity(Flight flight)
        {
            int pods = MissileJamming.Pods(flight?.Aircraft).Count;
            return pods >= 2 ? pods - 1 : pods;
        }

        // Jam this target: in place of what the flight was jamming, or added
        // to it, up to its capacity. False when there is no room to add.
        public static bool Jam(Flight flight, Unit target, bool add = false)
        {
            if (flight == null || target == null) return false;
            flight.JamTargets.RemoveAll(u => u == null || u.disabled);
            if (add && flight.Mode == FlightMode.Jam)
            {
                if (flight.JamTargets.Contains(target)) return true;
                if (flight.JamTargets.Count >= JamCapacity(flight)) return false;
                flight.JamTargets.Add(target);
                flight.Target = flight.JamTargets[0];
                return true;
            }
            if (flight.Mode != FlightMode.Jam) flight.PreviousMode = flight.Mode;
            // At standoff, for the whole list and this: the station is chosen to
            // reach all of them. Full, this one takes the oldest one's place.
            if (!flight.JamTargets.Contains(target))
            {
                if (flight.JamTargets.Count >= Mathf.Max(1, JamCapacity(flight))) flight.JamTargets.RemoveAt(0);
                flight.JamTargets.Add(target);
            }
            flight.Target = flight.JamTargets[0];
            flight.Route.Clear();
            flight.Mode = FlightMode.Jam;
            flight.Adopted = false;                 // ours to fly, not the combat pilot's
            return true;
        }

        internal static WeaponStation JammerOn(Aircraft aircraft)
        {
            if (aircraft == null || aircraft.weaponStations == null) return null;
            foreach (WeaponStation station in aircraft.weaponStations)
            {
                if (station == null || station.Weapons == null) continue;
                if (station.WeaponInfo != null && station.WeaponInfo.jammer) return station;
                foreach (Weapon weapon in station.Weapons)
                    if (weapon is JammingPod) return station;
            }
            return null;
        }

        public static List<Flight> Jammers()
        {
            var result = new List<Flight>();
            foreach (Flight flight in All())
                if (JammerOn(flight.Aircraft) != null) result.Add(flight);
            return result;
        }

        // The native transport state flies the delivery; we only tell it where.
        // An aeroplane can only airdrop, whatever was asked for.
        public static void Deliver(Flight flight, GlobalPosition where, bool airdrop)
        {
            if (flight == null) return;
            if (!CanDeliver(flight.Aircraft))
            {
                Host.Say(flight.Name + " · " + WhyNoDelivery(flight.Aircraft));
                return;
            }
            if (FixedWingDrops.CanAirdrop(flight.Aircraft)) airdrop = true;
            // A drop along a line keeps its line; any other delivery has none.
            if (!deliveringAlong) { flight.HasDropLine = false; flight.LeadInPending = false; }
            LeaveJamStation(flight);
            if (flight.Mode != FlightMode.Cargo) flight.PreviousMode = flight.Mode;
            flight.CargoPoint = where;
            flight.Airdrop = airdrop;
            flight.LastCargoPlan = 0f;             // solve the approach at once
            flight.CargoSeeded = false;            // and from our zone, not its own
            flight.CargoSearch = 0f;
            flight.CargoAtOrder = CargoAboard(flight.Aircraft);
            flight.RejoinAfterCargo = false;
            flight.Route.Clear();
            flight.Mode = FlightMode.Cargo;
            flight.Adopted = false;
        }

        // An airdrop along a stretch of line: out to a lead-in point short of
        // its start first, so the run comes in along the line, then the drop
        // from its start, the load spread along it. Any other order replaces
        // the route, and the pending drop with it.
        public static void DeliverAlong(Flight flight, GlobalPosition start, GlobalPosition end)
        {
            if (flight?.Aircraft == null) return;
            if (!CanDeliver(flight.Aircraft))
            {
                Host.Say(flight.Name + " · " + WhyNoDelivery(flight.Aircraft));
                return;
            }
            Vector3 along = end - start;
            along.y = 0f;
            float length = along.magnitude;
            Vector3 dir = length > 1f ? along / length : Vector3.forward;
            bool plane = FixedWingDrops.CanAirdrop(flight.Aircraft);
            int items = Mathf.Max(1, CargoAboard(flight.Aircraft));
            flight.HasDropLine = true;
            flight.DropLineStart = start;
            flight.DropLineEnd = end;
            flight.DropSpacing = items > 1 ? length / (items - 1) : 0f;
            flight.LeadIn = start - dir * (plane ? 6000f : 1500f);
            flight.LeadInPending = true;
            LeaveJamStation(flight);
            if (flight.Mode != FlightMode.Cargo && flight.Mode != FlightMode.Route) flight.PreviousMode = flight.Mode;
            flight.Route.Clear();
            flight.Route.Add(flight.LeadIn);
            flight.Mode = FlightMode.Route;
            flight.Adopted = false;
            Host.LogInfo("[flight] " + flight.Name + " · airdrop along " + length.ToString("0") + " m, " + items +
                " item(s) " + flight.DropSpacing.ToString("0") + " m apart · leading in from " + (plane ? "6" : "1.5") + " km");
        }

        // The lead-in reached: the drop proper, from the start of its stretch.
        // For a Chimera-type state the point is the middle of the stretch, and
        // it lets the whole load go on the one pass.
        internal static bool LeadInReached(Flight flight, GlobalPosition leg)
        {
            if (flight == null || !flight.LeadInPending || FastMath.Distance(leg, flight.LeadIn) > 50f) return false;
            flight.LeadInPending = false;
            bool plane = FixedWingDrops.CanAirdrop(flight.Aircraft);
            GlobalPosition point = plane
                ? flight.DropLineStart + (flight.DropLineEnd - flight.DropLineStart) * 0.5f
                : flight.DropLineStart;
            FlightMode previous = flight.PreviousMode;
            deliveringAlong = true;
            try { Deliver(flight, point, true); }
            finally { deliveringAlong = false; }
            flight.PreviousMode = previous;
            return true;
        }

        private static bool deliveringAlong;

        // Containers, pallets, troops: whatever the cargo stations still hold.
        internal static int CargoAboard(Aircraft aircraft)
        {
            int count = 0;
            if (aircraft?.weaponStations == null) return 0;
            foreach (WeaponStation station in aircraft.weaponStations)
                if (station != null && (station.Cargo || (station.WeaponInfo != null && (station.WeaponInfo.cargo || station.WeaponInfo.troops))))
                    count += Mathf.Max(0, station.Ammo);
            return count;
        }

        // A delivery is done when what it set out with is gone. The transport
        // state then hands the aircraft to the combat or takeoff state, and
        // without this it was forced straight back into delivering nothing.
        // A wingman circles the zone until its lead is done too -- forming up
        // on a lead still hovering over a landing zone is how rotors meet --
        // and everyone else takes up what it was doing before.
        private static void CargoProgress(Flight flight)
        {
            if (flight.RejoinAfterCargo)
            {
                Flight lead = Wings.LeadOf(flight);
                if (lead == null || lead == flight || lead.Mode != FlightMode.Cargo)
                {
                    flight.RejoinAfterCargo = false;
                    flight.Mode = lead != null && lead != flight ? FlightMode.Formation : FlightMode.Orbit;
                    flight.Adopted = false;
                }
                return;
            }
            if (flight.Mode != FlightMode.Cargo || flight.SupplyShip != null || flight.CargoAtOrder <= 0) return;
            if (CargoAboard(flight.Aircraft) > 0) return;

            bool wingman = Wings.IsWingman(flight) && Wings.LeadOf(flight) != flight;
            Flight leader = wingman ? Wings.LeadOf(flight) : null;
            if (wingman && leader != null && leader.Mode == FlightMode.Cargo)
            {
                flight.RejoinAfterCargo = true;
                flight.Mode = FlightMode.Orbit;
                flight.OrbitCentre = flight.CargoPoint;
                flight.OrbitRadius = 1500f;
            }
            else if (wingman) flight.Mode = FlightMode.Formation;
            else
            {
                flight.Mode = flight.PreviousMode == FlightMode.Cargo ? FlightMode.Orbit : flight.PreviousMode;
                if (flight.Mode == FlightMode.Orbit && flight.OrbitRadius <= 0f) flight.OrbitRadius = Tuning.DefaultAreaRadius;
            }
            flight.CargoAtOrder = 0;
            flight.Adopted = false;
            Host.Say(flight.Name + " · cargo delivered" + (flight.RejoinAfterCargo ? ", circling for the wing" : wingman ? ", rejoining" : ""));
            Host.LogInfo("[flight] " + flight.Name + " · cargo delivered");
        }

        // Only the transport state knows how to run an approach, pick usable
        // ground and unload, and the game only ever gives it to something that
        // can hover. A fixed-wing aircraft carrying a container has no way to
        // deliver it, so the order is refused rather than accepted into a mode
        // nothing can fly.
        //
        // The exception is an aeroplane with a fixed-wing transport state
        // loaded by a mod (FixedWingDrops): that one can airdrop.
        internal static bool CanDeliver(Aircraft aircraft)
        {
            Pilot crew = FirstPilot(aircraft);
            if (crew == null) return false;
            return crew.pilotType != Pilot.PilotType.Plane || FixedWingDrops.CanAirdrop(aircraft);
        }

        public static string WhyNoDelivery(Aircraft aircraft)
        {
            Pilot crew = FirstPilot(aircraft);
            if (crew != null && crew.pilotType == Pilot.PilotType.Plane && CargoAboard(aircraft) > 0)
                return "cannot fly a delivery; no fixed-wing airdrop mod is loaded";
            if (CargoAboard(aircraft) <= 0) return "has no cargo aboard";
            return "cannot fly a delivery";
        }

        public static List<Flight> Carriers()
        {
            var result = new List<Flight>();
            foreach (Flight flight in All())
                if (CargoMissions.CanCarry(flight.Aircraft)) result.Add(flight);
            return result;
        }

        public static void BreakOff(Flight flight)
        {
            if (flight == null) return;
            flight.Target = null;
            flight.HasDropLine = flight.LeadInPending = false;
            // Back to what it was doing, where it was doing it: the task area
            // it was sent to, not wherever the attack or the egress ended. Only
            // a flight with no earlier task holds where it is.
            bool noTask = flight.PreviousMode == FlightMode.Strike;
            flight.Mode = noTask ? FlightMode.Orbit : flight.PreviousMode;
            if (noTask && flight.Aircraft != null)
                flight.OrbitCentre = flight.Aircraft.GlobalPosition();
            flight.Adopted = false;                 // reclaim on the next tick
        }

        // Every flight that could usefully be sent at this contact.
        public static List<Flight> CapableOf(Unit target)
        {
            var result = new List<Flight>();
            if (target == null) return result;
            foreach (Flight flight in All())
            {
                if (flight.Aircraft == null) continue;
                if (BestStationFor(flight.Aircraft, target) != null) result.Add(flight);
            }
            return result;
        }

        // The fitted station best suited to this target, falling back to a gun.
        // A gun will hurt almost anything given the chance, so "no dedicated
        // weapon for this" should not mean "cannot attack at all" -- but a
        // flight with nothing at all still has to be told no rather than sent.
        // Stations with something left on them, for choosing by hand.
        public static List<WeaponStation> ArmedStations(Aircraft aircraft)
        {
            var result = new List<WeaponStation>();
            if (aircraft == null || aircraft.weaponStations == null) return result;
            foreach (WeaponStation station in aircraft.weaponStations)
                if (station?.WeaponInfo != null && station.Ammo > 0) result.Add(station);
            return result;
        }

        internal static WeaponStation NamedStation(Aircraft aircraft, string weapon)
        {
            if (aircraft == null || string.IsNullOrEmpty(weapon)) return null;
            foreach (WeaponStation station in ArmedStations(aircraft))
                if (station.WeaponInfo.name == weapon) return station;
            return null;
        }

        // Whether a bomb could be released on this target *right now*. The AI
        // will not enter its bombing mode without a track good to fifty metres.
        //
        // This is a live condition, not a property of the order: an aircraft
        // forty kilometres out has no eyes on anything, and acquires the target
        // when it arrives -- its own sensors feed the same faction picture this
        // asks about. So it is a hint for choosing between stores, never a
        // reason to refuse the mission.
        internal static bool CanReleaseNow(Aircraft aircraft, WeaponInfo info, Unit target)
        {
            if (info == null || target == null) return false;
            if (!info.bomb && !info.glideBomb) return true;
            FactionHQ hq = aircraft != null ? aircraft.NetworkHQ : null;
            return hq != null && hq.IsTargetPositionAccurate(target, 50f);
        }

        // The height and range to hand a strike to the combat pilot from, for
        // this weapon; false when the combat pilot is better left to set up
        // the attack itself.
        //
        // The combat pilot takes its target height from wherever the aircraft
        // is when it takes over and moves it about ten metres a second, and
        // while the target is more than 20 degrees off the nose it follows the
        // terrain at that height. Handed an aircraft at 6,000 m, it dives at the
        // target, is pulled back up, overflies, and circles -- never getting
        // the nose inside the weapon's alignment limit. From the right height
        // the target is already nearly in front of it, and it fires.
        //
        // Level bombs need a straight run as well as a sane height: the combat
        // pilot times the release on a drag-free fall and only drops with its
        // track within 10 degrees of the target, breaking off for another lap
        // if it gets close still turning. From 6,000 m the fall is long and it
        // lines up late -- over the target, then a release that misses. So a
        // level bomb is taken down to bombing height and handed over only once
        // lined up, from far enough out to settle.
        // In the air, not parked or landed: an air-to-air fight, which the
        // combat pilot flies well from wherever it is. No run-in, and no
        // egress after a shot -- that is for leaving a defended ground target,
        // not for turning away from an enemy aircraft.
        internal static bool IsAirTarget(Unit target) =>
            target is Missile || (target is Aircraft aircraft && (aircraft.radarAlt > 10f || aircraft.speed > 25f));

        internal static bool RunInFor(WeaponInfo info, out float height, out float range, out bool straight)
        {
            height = 0f;
            range = 0f;
            straight = false;
            if (info == null) return false;
            float reach = info.targetRequirements.maxRange;
            if (info.gun)
            {
                height = 600f;
                range = Mathf.Max(reach * 2f, 3000f);
                return true;
            }
            if (info.bomb && !info.glideBomb && !info.laserGuided)
            {
                height = Tuning.BombingHeight;
                range = 7000f;
                straight = true;
                return true;
            }
            if (!(info.missile || info.laserGuided) || reach <= 0f) return false;   // glide bombs set up their own
            // Low enough that at release range the target sits within half the
            // weapon's alignment limit below the nose.
            float align = Mathf.Clamp(info.targetRequirements.minAlignment, 5f, 60f);
            range = reach * 0.85f;
            height = Mathf.Clamp(range * Mathf.Tan(align * 0.5f * Mathf.Deg2Rad), 300f, 5000f);
            return true;
        }

        // The best store aboard for the target. Guns only when allowed: a
        // flight is not sent at a target with nothing but its gun unless the
        // gun was chosen for it -- a wingman with no air-to-air missiles went
        // after an aircraft on the strength of its cannon. A gun picked by
        // name (NamedStation) is always honoured; weapons free may use one.
        internal static WeaponStation BestStationFor(Aircraft aircraft, Unit target, bool allowGun = false)
        {
            if (aircraft == null || target == null || aircraft.weaponStations == null) return null;
            WeaponStation best = null, gun = null;
            float bestScore = 0.01f;
            foreach (WeaponStation station in aircraft.weaponStations)
            {
                if (station == null || station.WeaponInfo == null || station.Ammo <= 0) continue;
                if (station.WeaponInfo.gun)
                {
                    if (allowGun && gun == null) gun = station;
                    continue;
                }

                float score = WeaponOrders.Opportunity(station.WeaponInfo, target);
                if (score <= bestScore) continue;
                bestScore = score;
                best = station;
            }
            return best ?? gun;
        }

        public static void Engage(Flight flight)
        {
            if (flight == null) return;
            StrikePlans.Cancel(flight);
            flight.Mode = FlightMode.Engage;
        }

        public static void SetRoe(Flight flight, FlightRoe roe)
        {
            if (flight == null) return;
            flight.Roe = roe;
            // Tightening while the native pilot has it takes control straight back.
            if (roe == FlightRoe.Hold && flight.Interrupted && flight.Threat != FlightThreat.Missile)
                Reclaim(flight);
        }

        public static string Describe(FlightRoe roe) =>
            roe == FlightRoe.Hold ? "Weapons Hold"
            : roe == FlightRoe.Tight ? "Weapons Tight" : "Weapons Free";

        private static void Reclaim(Flight flight)
        {
            flight.Interrupted = false;
            flight.Adopted = false;          // Tick reinstalls our state
        }

        // ---- threats -------------------------------------------------------

        private static float nextThreatScan;

        // Heat-seeking, as the game itself reads it (the seeker's type
        // string, which its countermeasure stations match on), or by the
        // seeker component when the string is not available yet.
        public static bool IsHeatSeeker(Missile missile)
        {
            if (missile == null) return false;
            try { if (missile.GetSeekerType() == "IR") return true; } catch { }
            return missile.GetComponent<IRSeeker>() != null || missile.GetComponentInChildren<IRSeeker>(true) != null;
        }

        private static void AssessThreats()
        {
            if (Time.unscaledTime < nextThreatScan) return;
            nextThreatScan = Time.unscaledTime + 0.25f;

            foreach (Flight flight in flights)
            {
                Aircraft aircraft = flight.Aircraft;
                if (aircraft == null || aircraft.disabled) continue;
                FlightThreat threat = FlightThreat.None;

                float nearestShot = float.PositiveInfinity, nearestHeat = float.PositiveInfinity;
                bool infrared = false;
                Missile nearestMissile = null, nearestHeatMissile = null;

                foreach (Unit unit in UnitRegistry.allUnits)
                {
                    if (unit == null || unit.disabled || unit.NetworkHQ == null) continue;
                    if (unit.NetworkHQ == aircraft.NetworkHQ) continue;

                    // Anything already in the air at us outranks every order.
                    if (unit is Missile missile)
                    {
                        if (missile.targetID != aircraft.persistentID) continue;
                        threat = FlightThreat.Missile;
                        if (missile.owner is Aircraft shooter && !shooter.disabled) flight.Attackers[shooter] = Time.timeSinceLevelLoad;
                        float shotRange = FastMath.Distance(aircraft.GlobalPosition(), missile.GlobalPosition());
                        if (shotRange < nearestShot) { nearestShot = shotRange; nearestMissile = missile; }
                        // A heat-seeker, by the game's own reading of the seeker
                        // (the same string its countermeasure stations match
                        // on): a component check alone read every shot as
                        // radar, and an F-16 burned at full power with its
                        // flares untouched.
                        if (IsHeatSeeker(missile) && shotRange < nearestHeat) { nearestHeat = shotRange; nearestHeatMissile = missile; }
                        continue;
                    }
                    if (threat != FlightThreat.None) continue;
                    if (flight.Roe != FlightRoe.Free) continue;
                    // Weapons free inside the task area: something we can reach
                    // and hurt, that is also somewhere we were sent to fight.
                    Flight area = flight.Mode == FlightMode.Formation ? Wings.LeadOf(flight) : flight;
                    if (HasArea(area) &&
                        FastMath.Distance(unit.GlobalPosition(), AreaCentre(area)) > area.OrbitRadius) continue;
                    WeaponStation station = BestStationFor(aircraft, unit, allowGun: true);
                    if (station == null) continue;
                    float range = FastMath.Distance(aircraft.GlobalPosition(), unit.GlobalPosition());
                    if (range > station.WeaponInfo.targetRequirements.maxRange) continue;
                    if (Host.AvoidEngaging(flight, unit)) continue;      // under their missiles: not worth it
                    threat = FlightThreat.Hostile;
                }

                // With shots of both kinds inbound the heat-seeker sets the
                // defence once it is within twice the flaring range: flares and
                // a cold engine cost a radar shot nothing, while full power
                // with the burner lit feeds the heat-seeker.
                if (nearestHeatMissile != null && (nearestHeatMissile == nearestMissile || nearestHeat <= Tuning.IrBurstRange * 2f))
                { infrared = true; nearestMissile = nearestHeatMissile; nearestShot = nearestHeat; }

                // Weapons tight fights back at whoever actually shot at us, which
                // is exactly the missile case above.
                if (threat == FlightThreat.None && flight.Threat != FlightThreat.None)
                    flight.ThreatClearedAt = Time.unscaledTime;
                flight.Threat = threat;
                flight.ThreatIsInfrared = infrared;
                flight.ThreatRange = nearestShot;
                flight.ThreatMissile = threat == FlightThreat.Missile ? nearestMissile : null;
                string cover = "";
                bool standOn = threat == FlightThreat.Missile && Tuning.StandOnWhenCovered && !Host.IsFlownByPlayer(flight) &&
                    Covered(aircraft, out cover);
                if (standOn != flight.StandOn)
                {
                    flight.StandOn = standOn;
                    if (standOn) Tracing.Flight("[flight] " + flight.Name + " · shot at, covered · " + cover + " · holding its orders");
                    else if (threat == FlightThreat.Missile) Tracing.Flight("[flight] " + flight.Name + " · no longer covered, evading");
                }
                if (nearestMissile != null)
                {
                    flight.LastThreat = (nearestMissile.GetWeaponInfo()?.weaponName ?? nearestMissile.name) + (infrared ? " (heat-seeking)" : " (radar)") +
                        " at " + UnitConverter.DistanceReading(nearestShot) + " · " + Describe(flight);
                    flight.LastThreatAt = Time.timeSinceLevelLoad;
                }
                IrDefence.Defend(flight, aircraft, threat == FlightThreat.Missile, infrared, nearestShot);
            }
        }

        // How the aircraft stood against a shot: who was flying it, throttle,
        // flares, height. For the record of a loss.
        internal static string Describe(Flight flight)
        {
            Aircraft aircraft = flight.Aircraft;
            if (aircraft == null) return "gone";
            Pilot pilot = FirstPilot(aircraft);
            ControlInputs inputs = aircraft.GetInputs();
            return flight.Mode + (flight.Interrupted ? " (native pilot" + (pilot?.currentState != null ? ": " + pilot.currentState.GetType().Name : "") + ")" : " (ours)") +
                " · throttle " + (inputs != null ? (inputs.throttle * 100f).ToString("0") + "%" : "?") +
                " · flares " + (IrDefence.FlareFraction(aircraft) * 100f).ToString("0") + "%" +
                " · alt " + aircraft.radarAlt.ToString("0") + " m · " + aircraft.speed.ToString("0") + " m/s";
        }

        // Does the flight's ROE let the native pilot take it right now?
        // Can this aircraft defend itself against everything coming at it
        // without leaving its task? Every radar-guided shot within its pods'
        // reach is one they can take -- tracked by our side, and no more of
        // them than pods; those still beyond reach are not yet a reason to
        // turn. Every heat-seeker needs flares left. And a radar shot that is
        // still coming inside 2.5 km is evaded whatever the pods are doing.
        private const float StandOnLastDitch = 2500f;

        private static bool Covered(Aircraft aircraft, out string cover)
        {
            cover = "";
            List<MissileJamming.Pod> pods = MissileJamming.Pods(aircraft);
            if (pods.Count == 0) return false;             // only an aircraft that can jam holds on
            float reach = pods.Count > 0 && pods[0].Station?.WeaponInfo != null && pods[0].Station.WeaponInfo.targetRequirements.maxRange > 0f
                ? pods[0].Station.WeaponInfo.targetRequirements.maxRange : 20000f;
            FactionHQ hq = aircraft.NetworkHQ;
            int radarInReach = 0, radarFar = 0, heat = 0;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Missile missile) || missile.disabled || missile.targetID != aircraft.persistentID) continue;
                float range = Vector3.Distance(missile.transform.position, aircraft.transform.position);
                if (IsHeatSeeker(missile))
                {
                    if (IrDefence.FlareFraction(aircraft) <= 0f) return false;
                    heat++;
                    continue;
                }
                if (range < StandOnLastDitch) return false;
                string seeker = missile.GetSeekerType();
                bool jammable = seeker == "ARH" || seeker == "SARH";
                if (range > reach) { radarFar++; continue; }
                if (!jammable) return false;
                if (hq != null && !hq.IsTargetBeingTracked(missile)) return false;
                radarInReach++;
            }
            if (radarInReach > pods.Count) return false;
            cover = (radarInReach > 0 ? radarInReach + " radar shot(s) jammed" : "") +
                (radarFar > 0 ? (radarInReach > 0 ? ", " : "") + radarFar + " still beyond pod reach" : "") +
                (heat > 0 ? ((radarInReach + radarFar) > 0 ? ", " : "") + heat + " heat-seeker(s) flared" : "");
            return true;
        }

        private static bool ShouldYield(Flight flight)
        {
            // Covered: the shot is its pods' and flares' business, not a
            // reason to hand the aircraft to the game's evasion.
            if (flight.StandOn && flight.Threat == FlightThreat.Missile)
                return flight.Mode == FlightMode.Engage || (flight.Mode == FlightMode.Strike && flight.RunInDone);
            // Once the combat pilot has the attack, it keeps it. On the run in,
            // a radar shot is evaded, but a heat-seeker is flared off without
            // leaving the run -- breaking away throws the attack away.
            if (flight.Mode == FlightMode.Strike)
                return flight.RunInDone || (flight.Threat == FlightThreat.Missile && !flight.ThreatIsInfrared && !Tuning.OwnRadarEvasion);
            if (flight.Mode == FlightMode.Engage) return true;
            if (flight.Mode == FlightMode.Cargo) return false;       // the transport state has it
            // Jamming holds station; only an actual shot takes it off the job.
            if (flight.Mode == FlightMode.Jam) return flight.Threat == FlightThreat.Missile && !flight.ThreatIsInfrared && !Tuning.OwnRadarEvasion;

            // Leaving outranks evading, up to a point. Turning to fight a shot
            // that is still thirty kilometres away just keeps the aircraft in
            // the threat envelope; running until it is genuinely close, then
            // handing to the native pilot, gets it out alive. Heat-seekers are
            // let in much closer than radar shots because flares work and the
            // endgame is short.
            if (flight.Mode == FlightMode.Egress)
            {
                if (flight.Threat != FlightThreat.Missile) return false;
                if (flight.ThreatIsInfrared || Tuning.OwnRadarEvasion) return false;   // flown off on the beam by our state
                return flight.ThreatRange <= Tuning.RadarHandover;
            }

            // Evasion is never a choice: being shot at overrides Weapons Hold.
            // A radar shot goes to the native pilot, who notches and dives; a
            // heat-seeker is ours: idle, beam, flares (see IrDefence).
            if (flight.Threat == FlightThreat.Missile) return !flight.ThreatIsInfrared && !Tuning.OwnRadarEvasion;
            if (flight.Roe == FlightRoe.Hold) return false;
            return flight.Threat == FlightThreat.Hostile;
        }

        public static void ReturnToBase(Flight flight)
        {
            if (flight == null) return;
            StrikePlans.Cancel(flight);
            Remember(flight);
            flight.JamTargets.Clear();
            // Round the missiles first, if the host mod knows a way.
            if (flight.HomingVia == null && flight.Aircraft != null && !flight.Aircraft.disabled)
            {
                GlobalPosition? via = null;
                try { via = Host.DoglegHome(flight); } catch (System.Exception ex) { Host.LogWarning("[flight] dogleg: " + ex.Message); }
                if (via.HasValue)
                {
                    flight.HomingVia = via;
                    SetRoute(flight, via.Value, false);
                    Host.LogInfo("[flight] " + flight.Name + " · home round the missiles via a dogleg");
                    return;
                }
            }
            flight.HomingVia = null;
            flight.Mode = FlightMode.ReturnToBase;
        }

        // The task in hand as it is sent home, for a turnaround to go back to.
        // Only the first time: the dogleg home is itself a route.
        private static void Remember(Flight flight)
        {
            if (flight.Mode == FlightMode.ReturnToBase || flight.HomingVia.HasValue) return;
            flight.ResumeMode = flight.Mode;
            flight.ResumeCentre = flight.OrbitCentre;
            flight.ResumeRadius = flight.OrbitRadius;
            flight.ResumeRoute.Clear();
            flight.ResumeRoute.AddRange(flight.Route);
        }

        // Back out after a turnaround: to the task it had when sent home, or
        // the one given while it was on the ground. A strike, a jamming run
        // or a delivery is not picked up again -- the target may be gone, the
        // cargo is -- so it goes back to that task's area. Returns what it is
        // going to, for the log.
        internal static string Resume(Flight flight)
        {
            if (flight.Mode != FlightMode.ReturnToBase) { flight.ResumeMode = null; return Describe(flight); }
            FlightMode mode = flight.ResumeMode ?? FlightMode.Orbit;
            flight.ResumeMode = null;
            flight.OrbitCentre = flight.ResumeCentre;
            if (flight.ResumeRadius > 0f) flight.OrbitRadius = flight.ResumeRadius;
            flight.Route.Clear();
            switch (mode)
            {
                case FlightMode.Route:
                    flight.Route.AddRange(flight.ResumeRoute);
                    flight.Mode = flight.Route.Count > 0 ? FlightMode.Route : FlightMode.Orbit;
                    break;
                case FlightMode.Engage:
                case FlightMode.Station:
                case FlightMode.Formation:
                    flight.Mode = mode;
                    break;
                default:
                    flight.Mode = FlightMode.Orbit;
                    break;
            }
            return Describe(flight);
        }

        // Recovering now, rather than when the fuel says so. The landing state
        // picks the nearest field it can use, which from a sortie flown off a
        // ship at sea is that ship.
        public static void RecoverToShip(Flight flight)
        {
            if (flight == null) return;
            flight.Mode = FlightMode.ReturnToBase;
        }

        public static void SetAltitude(Flight flight, float metres)
        {
            if (flight != null) flight.Altitude = Mathf.Clamp(metres, 60f, 12000f);
        }

        public static void SetOrbitRadius(Flight flight, float metres)
        {
            if (flight != null) flight.OrbitRadius = Mathf.Clamp(metres, 500f, 30000f);
        }
    }
}
