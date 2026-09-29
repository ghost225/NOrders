namespace NOrders
{
    // Diagnostic logging, each kind behind its own Tuning flag (off by
    // default). The plain log keeps startup, warnings, and the events a player
    // also sees on screen.
    public static class Tracing
    {
        // Launch and recovery, taking the controls, cockpit displays and the
        // targeting camera.
        public static void Deck(string line) { if (Tuning.DeckTrace) Host.LogInfo(line); }

        // How a flight flies its tasks: run-ins, re-attacks, join-ups, flares.
        public static void Flight(string line) { if (Tuning.FlightTrace) Host.LogInfo(line); }

        // Ships under way and task-force station keeping.
        public static void Nav(string line) { if (Tuning.NavigationTrace) Host.LogInfo(line); }

        // The interface: map docking, night vision, ship naming.
        public static void Ui(string line) { if (Tuning.InterfaceTrace) Host.LogInfo(line); }
    }
}
