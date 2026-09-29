using System.Collections.Generic;
using UnityEngine;

namespace NOrders
{
    internal enum TakeoffVerdict { Unknown, Ok, Marginal, TooHeavy, OverMax }

    internal sealed class TakeoffEstimate
    {
        internal float Gross, MaxWeight, ThrustToWeight;
        internal float Roll, Runway;            // metres; Roll 0 when thrust alone lifts it
        internal bool SkiJump, Deck, Vtol;
        internal float DeckWind;                // m/s of the ship's own speed over the deck
        internal float Scaled;                  // the take-off distance scaled by weight, for comparison
        internal TakeoffVerdict Verdict;
        internal string Line;
    }

    // Will it get off this deck with this load?
    //
    // The game's own loadout screen shows gross weight against the airframe's
    // maximum and a thrust-to-weight ratio, and nothing about where it is
    // taking off from. That matters most at sea: the AI's takeoff is a rolling
    // run down whatever runway the field has -- a VTOL airframe with its
    // nozzles part-way down, but still a run -- and a carrier's is a short
    // deck. Too heavy to reach flying speed by the end of it, the aircraft goes
    // into the water. A load that flies off a land runway may not fly off a
    // ship.
    //
    // The estimate: gross weight the loadout screen's way -- the airframe's
    // parts, the fuel, and what each mount really adds; then the run to
    // flying speed at full reheat (flying speed with the root of weight, less
    // the ship's own speed over the deck), or failing a thrust figure the
    // airframe's take-off distance scaled by weight squared; shortened by a
    // ski jump, and for a VTOL airframe by the share of its weight its thrust
    // carries. Thrust above weight lifts it off anywhere.
    // It is an estimate, and it says so; the numbers are logged at launch so
    // they can be checked against what actually happens.
    internal static class TakeoffCheck
    {
        private const float Gravity = 9.81f;

        internal static TakeoffEstimate Estimate(LoadoutPlan plan, Airbase field)
        {
            var result = new TakeoffEstimate();
            AircraftDefinition definition = plan?.Definition;
            if (definition == null || definition.aircraftInfo == null || definition.aircraftParameters == null) return result;
            var prefab = definition.unitPrefab != null ? definition.unitPrefab.GetComponent<Aircraft>() : null;
            if (prefab == null) return result;

            result.MaxWeight = definition.aircraftInfo.maxWeight;
            // The same sum the game's loadout screen shows: the airframe's
            // parts, then what fuel and each mount add to them. A tank's fuel
            // is added to its part's mass when it is filled, so a drop tank's
            // mount lists only the empty tank: its fuel goes on too.
            float empty = prefab.GetPrefabMass();
            if (empty <= 0f) empty = definition.aircraftInfo.emptyWeight;
            result.Gross = empty +
                (Fuel(prefab) + ExternalFuel(plan)) * Mathf.Clamp01(plan.Fuel) + Stores(prefab, plan);
            float thrust = MaxThrust(prefab);
            result.ThrustToWeight = thrust > 0f && result.Gross > 0f ? thrust / (result.Gross * Gravity) : 0f;
            result.Vtol = definition.aircraftParameters.verticalLanding;
            result.Deck = field != null && field.AttachedAirbase;
            if (field != null && field.runways != null)
                foreach (Airbase.Runway runway in field.runways)
                    if (runway != null && runway.Takeoff && runway.Length > result.Runway)
                    {
                        result.Runway = runway.Length;
                        result.SkiJump = runway.SkiJump;
                    }

            string weight = UnitConverter.WeightReading(result.Gross) + " of " + UnitConverter.WeightReading(result.MaxWeight) + " max";
            string twr = result.ThrustToWeight > 0f ? "  ·  T/W " + result.ThrustToWeight.ToString("0.00") : "";
            if (result.MaxWeight > 0f && result.Gross > result.MaxWeight)
            {
                result.Verdict = TakeoffVerdict.OverMax;
                result.Line = "OVER MAXIMUM WEIGHT  ·  " + weight + twr;
                return result;
            }

            // A margin, not the bare ratio: this is an estimate, and 1.03 is too
            // close to call.
            if (result.Vtol && result.ThrustToWeight >= 1.1f)
            {
                result.Verdict = TakeoffVerdict.Ok;
                result.Line = "Takeoff OK  ·  thrust exceeds weight  ·  " + weight + twr;
                return result;
            }

            float reference = definition.aircraftParameters.takeoffDistance;
            if (reference <= 0f || result.Runway <= 0f || result.MaxWeight <= 0f)
            {
                result.Verdict = TakeoffVerdict.Unknown;
                result.Line = weight + twr;
                return result;
            }
            float share = result.Gross / result.MaxWeight;
            // A ship under way puts its own speed over the deck as a headwind:
            // the run only has to make up the rest of flying speed.
            Ship ship = result.Deck ? Airfields.ShipOf(field) : null;
            float takeoffSpeed = definition.aircraftParameters.takeoffSpeed;
            if (ship != null && ship.rb != null && takeoffSpeed > 1f)
                result.DeckWind = Mathf.Clamp(Vector3.Dot(ship.rb.velocity, ship.transform.forward), 0f, takeoffSpeed * 0.8f);

            // From the airframe's own take-off distance, by weight squared --
            // what the estimate used to be, kept in the log to compare.
            float scaled = reference * share * share;
            if (result.DeckWind > 0f) { float rest = 1f - result.DeckWind / takeoffSpeed; scaled *= rest * rest; }
            result.Scaled = scaled;

            // From thrust, when it is known: the AI takes off in full reheat, and
            // the thrust read here includes it. Flying speed goes with the root
            // of weight; static thrust is taken at four fifths for what a jet
            // loses as it gathers speed, less rolling friction.
            float roll = scaled;
            float accel = Gravity * (result.ThrustToWeight * 0.8f - 0.04f);
            if (result.ThrustToWeight > 0f && takeoffSpeed > 1f)
            {
                float needed = Mathf.Max(takeoffSpeed * Mathf.Sqrt(share) - result.DeckWind, 0f);
                roll = accel > 0.1f ? needed * needed / (2f * accel) : float.MaxValue;
            }
            if (result.SkiJump) roll *= 0.7f;
            if (result.Vtol && result.ThrustToWeight > 0f) roll *= Mathf.Max(0.15f, 1f - result.ThrustToWeight);
            result.Roll = roll;

            string where = result.Deck ? "deck" : "runway";
            string run = "run ~" + roll.ToString("0") + " m of " + result.Runway.ToString("0") + " m " + where;
            if (roll <= result.Runway * 0.9f)
            {
                result.Verdict = TakeoffVerdict.Ok;
                result.Line = "Takeoff OK  ·  " + run + "  ·  " + weight + twr;
            }
            else if (roll <= result.Runway * 1.15f)
            {
                result.Verdict = TakeoffVerdict.Marginal;
                result.Line = "MARGINAL  ·  " + run + "  ·  less fuel or fewer stores";
            }
            else
            {
                result.Verdict = TakeoffVerdict.TooHeavy;
                result.Line = "TOO HEAVY FOR THIS " + where.ToUpperInvariant() + "  ·  " + run +
                    (result.Deck ? "  ·  likely to go in the water" : "");
            }
            return result;
        }

        // What a spawned aircraft really weighs, the loadout screen's way, to
        // set against the estimate logged at launch.
        internal static string Actual(Aircraft aircraft)
        {
            if (aircraft == null) return "";
            float gross = 0f;
            foreach (UnitPart part in aircraft.GetAllParts()) if (part != null) gross += part.mass;
            return " · gross " + gross.ToString("0") + " kg of " +
                (aircraft.definition?.aircraftInfo != null ? aircraft.definition.aircraftInfo.maxWeight.ToString("0") : "?") + " max";
        }

        internal static string Trace(TakeoffEstimate e) =>
            "takeoff estimate · gross " + e.Gross.ToString("0") + " kg / max " + e.MaxWeight.ToString("0") +
            " · T/W " + e.ThrustToWeight.ToString("0.00") + (e.Vtol ? " (VTOL)" : "") +
            " · run " + e.Roll.ToString("0") + " m vs " + e.Runway.ToString("0") + " m" +
            (e.DeckWind > 0f ? " with " + e.DeckWind.ToString("0") + " m/s over the deck" : "") +
            " (by weight alone " + e.Scaled.ToString("0") + " m)" +
            (e.SkiJump ? " ski jump" : "") + (e.Deck ? " deck" : "") + " · " + e.Verdict;

        // Fuel aboard against internal capacity. The game's own fuel level is
        // everything aboard over everything's capacity, drop tanks included,
        // so full is 100% with or without them. Internal capacity comes from
        // the airframe's prefab, whose tanks are all internal; external tanks
        // -- vanilla or a mod's, as long as they register as fuel tanks -- are
        // in the aircraft's fuel on board and so push the reading above 100%.
        private static readonly Dictionary<AircraftDefinition, float> internalCapacity =
            new Dictionary<AircraftDefinition, float>();

        internal static float InternalCapacity(AircraftDefinition definition)
        {
            if (definition == null) return 0f;
            if (internalCapacity.TryGetValue(definition, out float known)) return known;
            var prefab = definition.unitPrefab != null ? definition.unitPrefab.GetComponent<Aircraft>() : null;
            float capacity = prefab != null ? Fuel(prefab) : 0f;
            internalCapacity[definition] = capacity;
            return capacity;
        }

        // Fuel the plan's external tanks hold when full, on every hardpoint of
        // their station.
        internal static float ExternalFuel(LoadoutPlan plan)
        {
            if (plan?.Definition == null) return 0f;
            var prefab = plan.Definition.unitPrefab != null ? plan.Definition.unitPrefab.GetComponent<Aircraft>() : null;
            HardpointSet[] sets = prefab != null && prefab.weaponManager != null ? prefab.weaponManager.hardpointSets : null;
            float capacity = 0f;
            foreach (LoadoutStation station in plan.Stations)
            {
                if (station.Selected?.prefab == null) continue;
                float tank = 0f;
                foreach (FuelTank fuel in station.Selected.prefab.GetComponentsInChildren<FuelTank>(true)) tank += fuel.GetCapacity();
                if (tank <= 0f) continue;
                int points = sets != null && station.Index >= 0 && station.Index < sets.Length && sets[station.Index]?.hardpoints != null
                    ? Mathf.Max(1, sets[station.Index].hardpoints.Count) : 1;
                capacity += tank * points;
            }
            return capacity;
        }

        internal static float FuelPercent(Aircraft aircraft)
        {
            float capacity = InternalCapacity(aircraft.definition);
            if (capacity <= 0f) return Mathf.Clamp01(aircraft.GetFuelLevel()) * 100f;
            return aircraft.GetFuelQuantity() / capacity * 100f;
        }

        private static float Fuel(Aircraft prefab)
        {
            float capacity = 0f;
            foreach (FuelTank tank in prefab.GetComponentsInChildren<FuelTank>(true)) capacity += tank.GetCapacity();
            return capacity;
        }

        // Each station's mount, loaded, on every hardpoint of the set.
        private static float Stores(Aircraft prefab, LoadoutPlan plan)
        {
            HardpointSet[] sets = prefab.weaponManager != null ? prefab.weaponManager.hardpointSets : null;
            if (sets == null) return 0f;
            float mass = 0f;
            foreach (LoadoutStation station in plan.Stations)
            {
                if (station.Selected == null || station.Index < 0 || station.Index >= sets.Length) continue;
                int points = sets[station.Index]?.hardpoints != null ? Mathf.Max(1, sets[station.Index].hardpoints.Count) : 1;
                mass += MountMass(station.Selected) * points;
            }
            return mass;
        }

        // What a mount adds to its hardpoint's part when it is fitted: its own
        // empty mass, then each weapon in it -- a missile's mass, a gun's full
        // ammunition, a vehicle's whole prefab. Worked out the way the game
        // applies it rather than read from the mount's mass field, which for
        // cargo gains the vehicle's mass again every time the mount is
        // initialised -- how a normal helicopter load came out tons over.
        private static readonly Dictionary<WeaponMount, float> mountMass = new Dictionary<WeaponMount, float>();

        internal static float MountMass(WeaponMount mount)
        {
            if (mount == null) return 0f;
            if (mountMass.TryGetValue(mount, out float known)) return known;
            float mass = mount.emptyMass;
            bool vehicle = false;
            if (mount.prefab != null)
                foreach (Weapon weapon in mount.prefab.GetComponentsInChildren<Weapon>(true))
                {
                    if (weapon is MountedCargo cargo)
                    {
                        Unit unit = cargo.cargo != null && cargo.cargo.unitPrefab != null ? cargo.cargo.unitPrefab.GetComponent<Unit>() : null;
                        if (unit != null) { mass += unit.GetPrefabMass(); vehicle = true; }
                    }
                    else if (weapon.info != null) mass += weapon.info.massPerRound * Mathf.Max(1, weapon.GetFullAmmo());
                }
            // A cargo mount carrying units some other way: their mass, counted once.
            if (mount.Cargo && !vehicle && mount.prefab != null)
                foreach (Unit unit in mount.prefab.GetComponentsInChildren<Unit>(true)) mass += unit.GetMass();
            mountMass[mount] = mass;
            return mass;
        }

        private static float MaxThrust(Aircraft prefab)
        {
            var ducted = prefab.GetComponent<DuctedThrustSystem>();
            if (ducted != null) return ducted.GetMaxThrust();
            float thrust = 0f;
            foreach (MonoBehaviour behaviour in prefab.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour is IThrustSource source) thrust += source.GetMaxThrust();
            return thrust;
        }
    }
}
