namespace Locked
{
    public enum PlayerRole : byte { Runner, Hunter }

    public enum MatchPhase : byte
    {
        Lobby,          // waiting for host to start
        HunterFreeze,   // 10 s: Hunters frozen, Runners scatter
        Hunt,           // main phase: tag + Lock (L) / Key (K), map pings every 25 s
        ChewingGum,     // "Proper Chewing Gum": roles reverse, Runners chase the Hunter
        RoundEnd        // short results screen, then losers become next Hunters
    }

    /// <summary>
    /// Marker interface. Put a component implementing this on the bot prefab variant.
    /// Movement/Interactor skip hardware input when it is present; the brain drives
    /// the same public input fields a human does (server-side only).
    /// </summary>
    public interface IBotBrain { }
}
