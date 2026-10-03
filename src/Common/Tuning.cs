namespace NOrders
{
    // The knobs shared code reads, as plain values with their defaults. A mod
    // with a settings file copies its entries in here at startup and again
    // whenever one changes; a mod without one gets these defaults.
    public static class Tuning
    {
        // Flights
        public static float DefaultFuel = 1f;
        public static float DefaultAltitude = 600f;
        public static float DefaultHelicopterAltitude = 600f;
        public static float DefaultHelicopterAreaRadius = 4000f;
        public static float DefaultAreaRadius = 3000f;
        public static float MinimumClearance = 55f;
        public static float ThreatSettleSeconds = 8f;
        public static float StandoffMetres = 12000f;
        public static float EgressSeconds = 45f;
        public static bool ReattackAfterEgress = true;
        public static bool LaunchCostFromAllocation = true;
        public static bool SortieBonusOnRecovery = true;
        public static bool CarrierApproachFix = true;
        public static float CarrierApproachFactor = 0.75f;
        public static float StrikePatience = 120f;
        public static float JammingStandoff = 18000f;
        public static float EgressAltitude = 200f;
        public static float RadarHandover = 15000f;
        public static float InfraredHandover = 2000f;
        public static float LowFuelAlert = 25f;
        public static float BombingHeight = 1500f;
        public static float CruiseThrottle = 0.8f;
        // A flight's default for home at a land airfield: rearm and go back
        // out (Turnaround), or park. Decks always park.
        public static bool RearmAtAirfields = false;
        public static float TurnaroundSeconds = 60f;
        // A missile is not launched more than this far off the nose, whatever
        // its definition allows: an A-19 put optical-homing AGM-48s out at
        // ninety degrees and they went nowhere. Weapons whose own alignment
        // limit is tighter keep theirs; a seeker that must see the target at
        // launch (optical, laser) gets the tighter cone below.
        // Missiles of ours allowed closing on one target at once
        // (ShotDiscipline): an aircraft, and anything else -- 0 there is the
        // game's own estimate of the hits it needs, at most four. A flight's
        // own MissilesPerTarget, when set, overrides both.
        public static int MissilesPerAirTarget = 2;
        public static int MissilesPerSurfaceTarget = 0;
        public static float MaxLaunchAngle = 70f;           // radar, anti-radiation and heat-seeking missiles
        public static float MaxLaunchAngleOptical = 45f;    // optical and laser seekers: the target in the seeker's view
        public static float MaxLaunchAngleRocket = 15f;     // unguided rockets: pointed at it
        public static float IrBurstRange = 3000f;
        public static int IrBurstFlares = 4;
        public static float IrBurstPause = 1.5f;      // between strings while the shot keeps coming
        // Radar shots flown off by our own state (beam, chaff, a gentle descent
        // at full power) instead of the native pilot's dive to the deck, which
        // put heavy airframes into the sea and handed them back stalled.
        public static bool OwnRadarEvasion = true;
        public static bool StandOnWhenCovered = true;
        public static float RadarEvasionFloor = 250f;   // metres above ground the descent stops at
        public static bool PreFlare = true;
        public static float PreFlareInterval = 2f;
        public static float FlareReserve = 0.3f;
        public static float FlareInterval = 1.5f;

        // Wings
        public static float CloseSpacing = 200f;
        public static float CombatSpacing = 1600f;
        public static bool EscortRetaliate = true;
        public static bool EscortIntercept = true;

        // Ships
        public static int DamageControlRate = 5;
        public static bool DamageControlPreserveCapacity = true;
        public static int DamageControlConcentration = 6;
        public static float DamageControlRestock = 0.2f;   // of full capacity, per resupply delivered
        public static bool NameShips = true;

        // Diagnostics
        public static bool DeckTrace;
        public static bool FlightTrace;
        public static bool NavigationTrace;
        public static bool InterfaceTrace;
    }
}
