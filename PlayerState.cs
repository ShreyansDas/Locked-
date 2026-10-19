using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Locked
{
    /// <summary>
    /// Replicated, server-authoritative state for one participant (human or bot).
    /// Clients only READ these values; every mutation goes through a Server* method.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerState : NetworkBehaviour
    {
        public static readonly List<PlayerState> All = new List<PlayerState>();

        [SerializeField] GameConfig config;

        public readonly NetworkVariable<PlayerRole> Role = new(PlayerRole.Runner);
        public readonly NetworkVariable<bool> IsLocked = new();
        public readonly NetworkVariable<bool> IsFrozen = new();          // phase freezes (countdown, gum, round end)
        public readonly NetworkVariable<double> TaggedUntil = new();     // server time; L must land before this
        public readonly NetworkVariable<ulong> TaggedBy = new(ulong.MaxValue);
        public readonly NetworkVariable<double> ImmuneUntil = new();
        public readonly NetworkVariable<int> LocksThisRound = new();
        public readonly NetworkVariable<bool> SafeInGum = new();         // tagged the Hunter during Chewing Gum
        public readonly NetworkVariable<float> UnlockProgress = new();   // 0..1, drives the rescue UI ring
        public readonly NetworkVariable<float> SpeedBuff = new(1f);      // power-ups / gum hunter boost
        public readonly NetworkVariable<int> SkinIndex = new();

        public GameConfig Config => config;
        public PlayerMovement Movement { get; private set; }
        public bool IsBot { get; private set; }

        double Now => NetworkManager.ServerTime.Time;
        public bool CanMove => !IsLocked.Value && !IsFrozen.Value;
        public bool IsTagged => TaggedUntil.Value > Now;
        public bool IsImmune => ImmuneUntil.Value > Now;

        // ---- Stats (skin passive × power-up buffs × status) ----
        public SkinDefinition Skin => config ? config.GetSkin(SkinIndex.Value) : null;
        public float MoveSpeedMultiplier =>
            (Skin ? Skin.moveSpeed : 1f) * SpeedBuff.Value * (IsTagged ? config.taggedSlowMultiplier : 1f);
        public float ClimbSpeedMultiplier => Skin ? Skin.climbSpeed : 1f;
        public float TagRangeMultiplier => Skin ? Skin.tagRange : 1f;
        public float UnlockSpeedMultiplier => Skin ? Skin.unlockSpeed : 1f;

        float lastUnlockProgressTime;

        void Awake()
        {
            Movement = GetComponent<PlayerMovement>();
            IsBot = GetComponent<IBotBrain>() != null;
        }

        public override void OnNetworkSpawn() => All.Add(this);
        public override void OnNetworkDespawn() => All.Remove(this);

        void Update()
        {
            // Rescue progress bleeds away if nobody is holding K on this player.
            if (IsServer && IsLocked.Value && UnlockProgress.Value > 0f &&
                Time.time - lastUnlockProgressTime > 0.15f)
            {
                UnlockProgress.Value = Mathf.MoveTowards(UnlockProgress.Value, 0f, Time.deltaTime * 0.5f);
            }
        }

        // ---------------- Server-only mutators ----------------

        public void ServerResetForRound(PlayerRole role)
        {
            if (!IsServer) return;
            Role.Value = role;
            IsLocked.Value = false;
            IsFrozen.Value = false;
            TaggedUntil.Value = 0;
            TaggedBy.Value = ulong.MaxValue;
            ImmuneUntil.Value = 0;
            LocksThisRound.Value = 0;
            SafeInGum.Value = false;
            UnlockProgress.Value = 0f;
            SpeedBuff.Value = 1f;
        }

        public void ServerTag(PlayerState hunter, float window)
        {
            if (!IsServer) return;
            TaggedUntil.Value = Now + window;
            TaggedBy.Value = hunter.NetworkObjectId;
        }

        public void ServerLock()
        {
            if (!IsServer) return;
            IsLocked.Value = true;
            TaggedUntil.Value = 0;
            TaggedBy.Value = ulong.MaxValue;
            UnlockProgress.Value = 0f;
        }

        public void ServerUnlock(float immunitySeconds)
        {
            if (!IsServer) return;
            IsLocked.Value = false;
            UnlockProgress.Value = 0f;
            ImmuneUntil.Value = Now + immunitySeconds;
        }

        /// <returns>true when the rescue completes.</returns>
        public bool ServerAddUnlockProgress(float amount)
        {
            if (!IsServer || !IsLocked.Value) return false;
            lastUnlockProgressTime = Time.time;
            UnlockProgress.Value = Mathf.Min(1f, UnlockProgress.Value + amount);
            return UnlockProgress.Value >= 1f;
        }

        // ---------------- Client → server ----------------

        [Rpc(SendTo.Server, RequireOwnership = true)]
        public void SelectSkinRpc(int index)
        {
            var phase = MatchManager.Instance ? MatchManager.Instance.Phase.Value : MatchPhase.Lobby;
            bool canChange = phase == MatchPhase.Lobby || phase == MatchPhase.RoundEnd;
            if (canChange && config.GetSkin(index) != null) SkinIndex.Value = index;
        }
    }
}
