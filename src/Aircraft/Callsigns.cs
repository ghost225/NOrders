using System.Collections.Generic;

namespace NOrders
{
    // Every flight gets a callsign, so a list of six Vortexes is six things you
    // can tell apart and say out loud.
    //
    // A callsign is a word for the airframe type and a number: the first type
    // launched this session gets the first word, the next type the next, and
    // the number is the lowest one not already flying or waiting on a deck.
    // Numbers are reused once a flight is gone, the way a squadron reuses them.
    internal static class Callsigns
    {
        private static readonly string[] Words =
        {
            "Viper", "Raven", "Cobra", "Hawk", "Falcon", "Talon", "Spectre", "Lancer",
            "Reaper", "Ghost", "Jester", "Havoc", "Mako", "Nomad", "Shadow", "Titan"
        };

        private static readonly Dictionary<AircraftDefinition, string> wordFor =
            new Dictionary<AircraftDefinition, string>();

        internal static string Suggest(AircraftDefinition definition)
        {
            string word = WordFor(definition);
            var taken = new HashSet<string>();
            foreach (string label in FlightOrders.LabelsInUse())
            {
                taken.Add(label);
                // "Viper 1-3" belongs to wing "Viper 1", which holds that number.
                int dash = label.LastIndexOf('-');
                if (dash > 0) taken.Add(label.Substring(0, dash));
            }
            for (int n = 1; n < 100; n++)
            {
                string candidate = word + " " + n;
                if (!taken.Contains(candidate)) return candidate;
            }
            return word;
        }

        private static string WordFor(AircraftDefinition definition)
        {
            if (definition == null) return "Flight";
            if (wordFor.TryGetValue(definition, out string word)) return word;
            word = Words[wordFor.Count % Words.Length];
            wordFor[definition] = word;
            return word;
        }

        // ---- by faction and job ---------------------------------------------------
        //
        // High Command's scheme, shared: a registry per side, after the
        // faction's own vehicle names -- plain engineering English for
        // Boscali, Greek and Near-Eastern for Primeva -- grouped by what the
        // flight is for, so a Boscali ground-attack wing is Hammer and a
        // Primeva fighter is Aetos. What it is for is read from what it
        // carries, then the airframe. A name in use is skipped; the pools
        // start at a random place and rotate, so each suggestion differs.
        // Factions with no registry get the plain word-and-number above.

        internal enum Kind { Fighter, Strike, Sead, Cas, Recon, Helicopter, Ew, Transport, Bomber }

        private static readonly Dictionary<Kind, string[]> Boscali = new Dictionary<Kind, string[]>
        {
            // Blades and birds of prey: what goes up to meet the enemy's air.
            [Kind.Fighter] = new[] { "Viper", "Sabre", "Rapier", "Falcon", "Kestrel", "Merlin", "Goshawk", "Lancer", "Javelin", "Dagger", "Cutlass", "Talon", "Warden", "Spear", "Peregrine", "Sparrowhawk" },
            // Heavy tools and weather: what breaks things at range.
            [Kind.Strike] = new[] { "Thunder", "Tempest", "Trident", "Pike", "Halberd", "Longbow", "Ballista", "Onager", "Broadsword", "Claymore", "Mace", "Flail", "Bolt", "Warhead", "Lance", "Culverin" },
            // Small, quick and vicious: the radar hunters.
            [Kind.Sead] = new[] { "Ferret", "Weasel", "Magpie", "Crow", "Raven", "Jackdaw", "Rook", "Mongoose", "Badger", "Stoat", "Polecat", "Marten", "Wolverine", "Lynx", "Ocelot", "Serval" },
            // The workshop: what hammers the ground for the troops.
            [Kind.Cas] = new[] { "Hammer", "Sledge", "Mallet", "Maul", "Pickaxe", "Crowbar", "Wrench", "Chisel", "Rivet", "Grinder", "Ripsaw", "Buzzsaw", "Hatchet", "Cleaver", "Blowtorch", "Anvil" },
            // Eyes.
            [Kind.Recon] = new[] { "Spyglass", "Lookout", "Lantern", "Prism", "Lens", "Signal", "Semaphore", "Watchman", "Outrider", "Pathfinder", "Tracker", "Scout", "Beacon", "Sentry", "Vedette", "Picket" },
            // Small flying things.
            [Kind.Helicopter] = new[] { "Wasp", "Mayfly", "Damsel", "Sawfly", "Gadfly", "Horsefly", "Lacewing", "Skimmer", "Whirligig", "Rotor", "Turbine", "Vane", "Hornet", "Cricket", "Katydid", "Midge" },
            // What you hear when the jamming is on.
            [Kind.Ew] = new[] { "Static", "Hiss", "Whisper", "Murmur", "Echo", "Rumour", "Snowstorm", "Blizzard", "Haze", "Fog", "Mumble", "Crackle" },
            // What carries.
            [Kind.Transport] = new[] { "Wagon", "Cart", "Barrow", "Pallet", "Crate", "Hopper", "Bucket", "Skip", "Tote", "Ferry", "Shuttle", "Porter", "Hauler", "Drayman", "Mule", "Packhorse" },
            // What falls on you.
            [Kind.Bomber] = new[] { "Landslide", "Avalanche", "Rockfall", "Quake", "Tremor", "Thunderclap", "Deluge", "Torrent", "Cataract", "Downpour", "Hailstorm", "Cloudburst" }
        };

        private static readonly Dictionary<Kind, string[]> Primeva = new Dictionary<Kind, string[]>
        {
            // Birds of prey, Greek and Arabic, and the gods of war.
            [Kind.Fighter] = new[] { "Aetos", "Hierax", "Saqr", "Shahin", "Baz", "Kirkos", "Gryps", "Sphinx", "Lamia", "Nike", "Ares", "Enyo", "Phobos", "Deimos", "Uqab", "Gyps" },
            // Lightning and spears.
            [Kind.Strike] = new[] { "Keraunos", "Astrape", "Bronte", "Sarissa", "Xiphos", "Kopis", "Dory", "Akontion", "Toxon", "Belos", "Rumh", "Saif", "Khanjar", "Nasl", "Qaws", "Harba" },
            // Foxes, jackals and weasels.
            [Kind.Sead] = new[] { "Alopex", "Thaalab", "Ichneumon", "Nims", "Galee", "Lykos", "Dhib", "Hyaina", "Dabu", "Kalb", "Ibn Awa", "Enydris", "Wawi", "Fahd", "Namir", "Basileus" },
            // Hammers, axes and saws in Greek and Arabic.
            [Kind.Cas] = new[] { "Sfyra", "Pelekys", "Mitraqa", "Fas", "Skeparni", "Balta", "Minshar", "Prioni", "Kalemi", "Izmil", "Lostos", "Sfina", "Sfyri", "Kopanos", "Matraq", "Tsekouri" },
            // Watchers.
            [Kind.Recon] = new[] { "Skopos", "Talaia", "Raqib", "Rasid", "Ain", "Nazar", "Basira", "Kataskopos", "Lykaon", "Aigithos", "Ophthalmos", "Phylax", "Haris", "Diyar", "Manara", "Pharos" },
            // Bees, wasps, cicadas, locusts.
            [Kind.Helicopter] = new[] { "Melissa", "Nahla", "Sphex", "Zunbur", "Tettix", "Sirsir", "Akris", "Jarad", "Farasha", "Pyralis", "Dabbur", "Empis", "Konops", "Baouda", "Myia", "Dhubab" },
            // Silence, fog and whispers.
            [Kind.Ew] = new[] { "Sigi", "Samt", "Psithyros", "Hams", "Omichli", "Dabab", "Nephele", "Sahab", "Skotos", "Zalam", "Ekho", "Sada" },
            // Porters and pack animals.
            [Kind.Transport] = new[] { "Hamal", "Jamal", "Kamila", "Onos", "Himar", "Amaxa", "Araba", "Naql", "Phortion", "Skaphe", "Baghl", "Hemionos", "Faras", "Hippos", "Qafila", "Karavani" },
            // Earthquake and flood.
            [Kind.Bomber] = new[] { "Seismos", "Zilzal", "Kataklysmos", "Tufan", "Lailaps", "Prester", "Sawaiq", "Skepasma", "Katigis", "Ramla", "Faydan", "Chalaza" }
        };

        private static readonly Dictionary<(string, Kind), int> cursors = new Dictionary<(string, Kind), int>();

        // What the flight is for: the airframe first where it settles it
        // (helicopter, jammer, transport, bomber), then what it carries --
        // anti-radiation rounds are SEAD, heavy or long-range ground rounds a
        // strike, light ones close support, air-to-air alone a fighter.
        // Nothing aboard: a transport if it has no combat role, else recon.
        internal static Kind KindOf(AircraftDefinition def, IEnumerable<WeaponInfo> weapons)
        {
            var prefab = def?.unitPrefab != null ? def.unitPrefab.GetComponent<Aircraft>() : null;
            RoleIdentity r = def != null ? def.roleIdentity : default;
            if (prefab?.autopilot is AutopilotHelo) return Kind.Helicopter;
            if ((def?.unitName ?? "").StartsWith("EW") || r.antiRadar > 0.6f && r.antiAir < 0.5f) return Kind.Ew;
            bool combatAirframe = !(r.antiAir < 0.2f && r.antiSurface < 0.3f);
            bool heavy = def != null && def.mass >= 30000f && r.antiAir < 0.8f;

            bool air = false, ground = false, strike = false, arm = false, jam = false, carry = false;
            if (weapons != null)
                foreach (WeaponInfo info in weapons)
                {
                    if (info == null || info.gun) continue;
                    if (info.jammer) { jam = true; continue; }
                    if (info.cargo || info.troops || info.sling) { carry = true; continue; }
                    switch (FlightOrders.RoleOf(info))
                    {
                        case "ARM": arm = true; break;
                        case "A/A": air = true; break;
                        case "A/G":
                            ground = true;
                            if (info.glideBomb || info.overHorizon || info.strategic || info.nuclear ||
                                (info.bomb || info.missile) && info.massPerRound >= 300f) strike = true;
                            break;
                    }
                }
            if (jam) return Kind.Ew;
            if (arm) return Kind.Sead;
            if (ground) return heavy ? Kind.Bomber : strike ? Kind.Strike : Kind.Cas;
            if (air) return Kind.Fighter;
            if (carry || !combatAirframe) return Kind.Transport;
            return heavy ? Kind.Bomber : Kind.Recon;
        }

        // A wing name for this faction and what the flight carries.
        internal static string Suggest(FactionHQ hq, AircraftDefinition def, IEnumerable<WeaponInfo> weapons)
        {
            string faction = hq?.faction?.factionName ?? "";
            Dictionary<Kind, string[]> pools = faction == FactionHelper.Boscali ? Boscali : faction == FactionHelper.Primeva ? Primeva : null;
            if (pools == null) return Suggest(def);
            Kind kind = KindOf(def, weapons);
            string[] pool = pools[kind];
            var inUse = new HashSet<string>();
            foreach (string label in FlightOrders.LabelsInUse())
            {
                if (string.IsNullOrEmpty(label)) continue;
                inUse.Add(label);
                int dash = label.LastIndexOf('-');
                if (dash > 0) inUse.Add(label.Substring(0, dash));
            }
            foreach (Flight flight in FlightOrders.All()) if (flight.Wing != null) inUse.Add(flight.Wing);
            if (!cursors.TryGetValue((faction, kind), out int start)) start = UnityEngine.Random.Range(0, pool.Length);
            for (int i = 0; i < pool.Length; i++)
            {
                string name = pool[(start + i) % pool.Length];
                if (inUse.Contains(name)) continue;
                cursors[(faction, kind)] = (start + i + 1) % pool.Length;
                return name;
            }
            // Every name of the kind is up: the second of the name.
            for (int n = 2; ; n++)
            {
                string name = pool[start % pool.Length] + " " + n;
                if (!inUse.Contains(name)) { cursors[(faction, kind)] = (start + 1) % pool.Length; return name; }
            }
        }

        // For a loadout being planned on a deck.
        internal static string Suggest(FactionHQ hq, LoadoutPlan plan)
        {
            var weapons = new List<WeaponInfo>();
            if (plan != null) foreach (LoadoutStation station in plan.Stations) if (station.Selected?.info != null) weapons.Add(station.Selected.info);
            return Suggest(hq, plan?.Definition, weapons);
        }

        // A plan not named by hand takes a name for what it now carries, and
        // keeps it while that stays the same.
        internal static void Refresh(FactionHQ hq, LoadoutPlan plan)
        {
            if (plan == null || plan.CallsignChosen || hq == null) return;
            var weapons = new List<WeaponInfo>();
            foreach (LoadoutStation station in plan.Stations) if (station.Selected?.info != null) weapons.Add(station.Selected.info);
            Kind kind = KindOf(plan.Definition, weapons);
            if (plan.NamedFor == kind) return;
            plan.NamedFor = kind;
            plan.Callsign = Suggest(hq, plan.Definition, weapons);
        }

        // For an aircraft already flying: its side and what it carries.
        internal static string Suggest(Aircraft aircraft)
        {
            var weapons = new List<WeaponInfo>();
            if (aircraft?.weaponStations != null)
                foreach (WeaponStation station in aircraft.weaponStations) if (station?.WeaponInfo != null) weapons.Add(station.WeaponInfo);
            return Suggest(aircraft?.NetworkHQ, aircraft?.definition, weapons);
        }

        // The game's own name for the aircraft, written the way it writes a
        // player's: callsign first, airframe in brackets. The hover card, the
        // kill feed and the target lists all read this, so a flight is named
        // wherever the game names anything.
        internal static void Apply(Aircraft aircraft, string label)
        {
            if (aircraft == null || string.IsNullOrEmpty(label)) return;
            string name = label + " [" + (aircraft.definition?.unitName ?? aircraft.name) + "]";
            aircraft.NetworkunitName = name;
            if (UnitRegistry.TryGetPersistentUnit(aircraft.persistentID, out PersistentUnit persistent) && persistent != null)
                persistent.unitName = name;
        }
    }
}
