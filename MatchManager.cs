using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

namespace Locked
{
    /// <summary>
    /// Server-authoritative match state machine:
    ///   Lobby → HunterFreeze (10 s) → Hunt (pings every 25 s)
    ///         → ChewingGum (after 3 locks) → RoundEnd → HunterFreeze (losers now hunt) …
    /// Clients read Phase / PhaseEndsAt for HUD and subscribe to the events.
    /// Singleplayer is the same code running as Host with server-owned bot players.
    /// </summary>
    public class MatchManager : NetworkBehaviour
    {
        public static MatchManager Instance { get; private set; }

        [SerializeField] GameConfig config;
        [SerializeField] Transform[] hunterSpawns;
        [SerializeField] Transform[] runnerSpawns;

        public readonly NetworkVariable<MatchPhase> Phase = new(MatchPhase.Lobby);
        public readonly NetworkVariable<double> PhaseEndsAt = new();
        public readonly NetworkVariable<double> NextPingAt = new();
        public readonly NetworkVariable<ulong> GumHunterId = new(ulong.MaxValue);
        public readonly NetworkVariable<int> RoundNumber = new();

        /// <summary>Fires on every peer.</summary>
        public event Action<MatchPhase> PhaseChanged;
        /// <summary>Fires on every peer; the map UI decides who sees the blips (default: Hunters).</summary>
        public event Action<Vector3[]> RunnersPinged;
        /// <summary>Fires on every peer with the NetworkObjectIds of the next round's Hunters.</summary>
        public event Action<ulong[]> RoundEnded;

        public GameConfig Config => config;
        public PlayerState GumHunter => FindPlayer(GumHunterId.Value);
        public float PhaseTimeRemaining => Mathf.Max(0f, (float)(PhaseEndsAt.Value - NetworkManager.ServerTime.Time));
        double Now => NetworkManager.ServerTime.Time;

        // ---- server-only ----
        readonly List<PlayerState> nextHunters = new();
        readonly List<PlayerState> gumTagOrder = new();
        double gumRunnersReleaseAt;
        int huntersThisRound = 1;

        void Awake() => Instance = this;
        public override void OnDestroy() { if (Instance == this) Instance = null; base.OnDestroy(); }

        public override void OnNetworkSpawn() => Phase.OnValueChanged += OnPhaseValueChanged;
        public override void OnNetworkDespawn() => Phase.OnValueChanged -= OnPhaseValueChanged;
        void OnPhaseValueChanged(MatchPhase _, MatchPhase now) => PhaseChanged?.Invoke(now);

        // ================= Match flow =================

        /// <summary>Called by the host's lobby UI once everyone (and every bot) is spawned.</summary>
        public void ServerStartMatch()
        {
            if (!IsServer || Phase.Value != MatchPhase.Lobby) return;
            var players = PlayerState.All;
            if (players.Count < 2) { Debug.LogWarning("[Locked] Need at least 2 players/bots."); return; }

            int hunters = Mathf.Clamp(config.initialHunters, 1, Mathf.Min(config.maxHunters, players.Count - 1));
            nextHunters.Clear();
            nextHunters.AddRange(players.OrderBy(_ => UnityEngine.Random.value).Take(hunters));
            RoundNumber.Value = 0;
            StartRound();
        }

        void StartRound()
        {
            nextHunters.RemoveAll(p => p == null || !PlayerState.All.Contains(p)); // disconnected losers
            if (nextHunters.Count == 0)
                nextHunters.Add(PlayerState.All[UnityEngine.Random.Range(0, PlayerState.All.Count)]);

            RoundNumber.Value++;
            GumHunterId.Value = ulong.MaxValue;
            gumTagOrder.Clear();

            int h = 0, r = 0;
            foreach (var p in PlayerState.All)
            {
                bool isHunter = nextHunters.Contains(p);
                p.ServerResetForRound(isHunter ? PlayerRole.Hunter : PlayerRole.Runner);
                var spawn = isHunter ? PickSpawn(hunterSpawns, h++) : PickSpawn(runnerSpawns, r++);
                if (spawn != null && p.Movement != null) p.Movement.TeleportRpc(spawn.position, spawn.rotation);
                if (isHunter) p.IsFrozen.Value = true; // spec: 10 s Hunter freeze
            }
            huntersThisRound = h;
            SetPhase(MatchPhase.HunterFreeze, config.hunterFreezeSeconds);
        }

        void Update()
        {
            if (!IsServer) return;

            switch (Phase.Value)
            {
                case MatchPhase.HunterFreeze:
                    if (Now >= PhaseEndsAt.Value)
                    {
                        foreach (var p in Hunters()) p.IsFrozen.Value = false;
                        SetPhase(MatchPhase.Hunt, config.huntSeconds);
                        NextPingAt.Value = Now + config.pingIntervalSeconds;
                    }
                    break;

                case MatchPhase.Hunt:
                    if (Now >= NextPingAt.Value)
                    {
                        NextPingAt.Value += config.pingIntervalSeconds;
                        ServerPing();
                    }
                    if (Now >= PhaseEndsAt.Value) EndRoundRunnersSurvived();
                    break;

                case MatchPhase.ChewingGum:
                    if (gumRunnersReleaseAt > 0 && Now >= gumRunnersReleaseAt)
                    {
                        gumRunnersReleaseAt = 0;
                        foreach (var p in Runners()) p.IsFrozen.Value = false;
                    }
                    if (GumHunter == null) EndRoundRunnersSurvived();        // gum Hunter disconnected
                    else if (Now >= PhaseEndsAt.Value) ResolveGumTimeout();
                    break;

                case MatchPhase.RoundEnd:
                    if (Now >= PhaseEndsAt.Value) StartRound();
                    break;
            }
        }

        // ================= Tracking: map ping =================

        void ServerPing()
        {
            var positions = Runners().Where(p => !p.IsLocked.Value)
                                     .Select(p => p.transform.position).ToArray();
            PingRpc(positions);
        }

        [Rpc(SendTo.Everyone)]
        void PingRpc(Vector3[] positions) => RunnersPinged?.Invoke(positions);

        // ================= Lock & Key callbacks =================

        public void ServerOnLock(PlayerState hunter, PlayerState runner)
        {
            if (!IsServer || Phase.Value != MatchPhase.Hunt) return;

            runner.ServerLock();
            hunter.LocksThisRound.Value++;

            var runners = Runners().ToList();
            int threshold = Mathf.Min(config.locksForChewingGum, runners.Count); // small lobbies
            if (hunter.LocksThisRound.Value >= threshold)
            {
                StartChewingGum(hunter);
            }
            else if (runners.All(p => p.IsLocked.Value))
            {
                // Several Hunters shared the locks and nobody reached 3: top locker gets the gum phase.
                StartChewingGum(Hunters().OrderByDescending(p => p.LocksThisRound.Value).First());
            }
        }

        public void ServerOnUnlock(PlayerState rescuer, PlayerState runner)
        {
            if (!IsServer || Phase.Value != MatchPhase.Hunt) return;
            runner.ServerUnlock(config.unlockImmunitySeconds);
            // TODO(stats): credit rescuer for end-of-round summary / XP.
        }

        // ================= Proper Chewing Gum =================

        void StartChewingGum(PlayerState gumHunter)
        {
            GumHunterId.Value = gumHunter.NetworkObjectId;
            gumTagOrder.Clear();

            foreach (var p in PlayerState.All)
            {
                if (p == gumHunter)
                {
                    p.IsFrozen.Value = false;
                    p.SpeedBuff.Value = config.gumHunterSpeedMultiplier;
                }
                else if (p.Role.Value == PlayerRole.Hunter)
                {
                    p.IsFrozen.Value = true; // other Hunters sit this phase out (design knob)
                }
                else
                {
                    if (p.IsLocked.Value) p.ServerUnlock(0f);
                    p.TaggedUntil.Value = 0;
                    p.SafeInGum.Value = false;
                    p.IsFrozen.Value = true; // released after the Hunter's head start
                }
            }

            gumRunnersReleaseAt = Now + config.gumRunnerDelaySeconds;
            SetPhase(MatchPhase.ChewingGum, config.gumRunnerDelaySeconds + config.gumSeconds);
        }

        /// <summary>A Runner tagged the gum Hunter and is now safe.</summary>
        public void ServerRegisterGumTag(PlayerState runner)
        {
            if (!IsServer || Phase.Value != MatchPhase.ChewingGum || runner.SafeInGum.Value) return;

            runner.SafeInGum.Value = true;
            gumTagOrder.Add(runner);

            var runners = Runners().ToList();
            var stillUnsafe = runners.Where(p => !p.SafeInGum.Value).ToList();
            int losersNeeded = LosersNeeded(runners.Count);

            // "Last Runner to tag loses": once only the losers are left un-safe, the outcome is decided —
            // no need to make them actually tag. If too few remain (tiny lobbies), the latest taggers fill in.
            if (stillUnsafe.Count <= losersNeeded)
            {
                var losers = new List<PlayerState>(stillUnsafe);
                for (int i = gumTagOrder.Count - 1; i >= 0 && losers.Count < losersNeeded; i--)
                    losers.Add(gumTagOrder[i]);
                EndRound(losers);
            }
        }

        void ResolveGumTimeout()
        {
            var runners = Runners().ToList();
            var stillUnsafe = runners.Where(p => !p.SafeInGum.Value)
                                     .OrderBy(_ => UnityEngine.Random.value).ToList();
            int cap = Mathf.Clamp(stillUnsafe.Count, 1, Mathf.Min(config.maxHunters, PlayerState.All.Count - 1));
            var losers = stillUnsafe.Take(cap).ToList();
            if (losers.Count == 0) losers.Add(runners.Count > 0 ? runners[0] : GumHunter);
            EndRound(losers);
        }

        int LosersNeeded(int runnerCount) =>
            Mathf.Clamp(huntersThisRound, 1, Mathf.Max(1, runnerCount - 1));

        // ================= Round end =================

        void EndRoundRunnersSurvived()
        {
            // Design choice: Hunters failed to reach Chewing Gum → rotate in fresh random Hunters.
            var candidates = Runners().OrderBy(_ => UnityEngine.Random.value).ToList();
            if (candidates.Count == 0) candidates = PlayerState.All.ToList();
            EndRound(candidates.Take(Mathf.Max(1, huntersThisRound)).ToList());
        }

        void EndRound(List<PlayerState> losers)
        {
            nextHunters.Clear();
            nextHunters.AddRange(losers);
            foreach (var p in PlayerState.All) p.IsFrozen.Value = true;
            RoundEndedRpc(losers.Select(p => p.NetworkObjectId).ToArray());
            SetPhase(MatchPhase.RoundEnd, config.roundEndSeconds);
        }

        [Rpc(SendTo.Everyone)]
        void RoundEndedRpc(ulong[] nextHunterIds) => RoundEnded?.Invoke(nextHunterIds);

        // ================= Helpers =================

        void SetPhase(MatchPhase phase, float duration)
        {
            PhaseEndsAt.Value = Now + duration;
            Phase.Value = phase;
        }

        static IEnumerable<PlayerState> Hunters() => PlayerState.All.Where(p => p.Role.Value == PlayerRole.Hunter);
        static IEnumerable<PlayerState> Runners() => PlayerState.All.Where(p => p.Role.Value == PlayerRole.Runner);

        static Transform PickSpawn(Transform[] spawns, int i) =>
            spawns != null && spawns.Length > 0 ? spawns[i % spawns.Length] : null;

        public static PlayerState FindPlayer(ulong networkObjectId) =>
            PlayerState.All.FirstOrDefault(p => p.NetworkObjectId == networkObjectId);
    }
}
