using System;
using Unity.Netcode;
using UnityEngine;

namespace Locked
{
    /// <summary>
    /// The Lock & Key loop. Clients only send intent ("I pressed L"); the server picks the
    /// target and validates range, facing, line of sight, role and phase.
    ///
    ///   Hunt phase, Hunter:  Tag (LMB) a Runner → Runner is "tagged" (slowed) for tagWindowSeconds
    ///                        → press L while in lockRange to Lock them.
    ///   Hunt phase, Runner:  Hold K next to a Locked teammate to free them.
    ///   Chewing Gum, Runner: Tag (LMB) the fleeing Hunter to become safe.
    ///
    /// Bots call the public Server* methods directly instead of reading keys.
    /// </summary>
    [RequireComponent(typeof(PlayerState))]
    public class PlayerInteractor : NetworkBehaviour
    {
        [SerializeField] KeyCode tagKey = KeyCode.Mouse0;
        [SerializeField] KeyCode lockKey = KeyCode.L;
        [SerializeField] KeyCode keyKey = KeyCode.K;
        [SerializeField] LayerMask lineOfSightBlockers = ~0; // exclude the Player layer

        PlayerState state;
        GameConfig Config => state.Config;

        // server-only
        float nextTagTime;
        bool unlockHeld;

        public event Action<PlayerState> ServerTagLanded; // hook for SFX / stats / bot learning

        void Awake() => state = GetComponent<PlayerState>();

        void Update()
        {
            if (!IsSpawned) return;
            if (IsOwner && !state.IsBot) PollHumanInput();
            if (IsServer) ServerTickUnlock(Time.deltaTime);
        }

        void PollHumanInput()
        {
            if (Input.GetKeyDown(tagKey)) TagRpc();
            if (Input.GetKeyDown(lockKey)) LockRpc();
            if (Input.GetKeyDown(keyKey)) SetUnlockHeldRpc(true);
            if (Input.GetKeyUp(keyKey)) SetUnlockHeldRpc(false);
        }

        void OnApplicationFocus(bool focused)
        {
            if (!focused && IsSpawned && IsOwner && !state.IsBot) SetUnlockHeldRpc(false);
        }

        [Rpc(SendTo.Server, RequireOwnership = true)] void TagRpc() => ServerTryTag();
        [Rpc(SendTo.Server, RequireOwnership = true)] void LockRpc() => ServerTryLock();
        [Rpc(SendTo.Server, RequireOwnership = true)] void SetUnlockHeldRpc(bool held) => unlockHeld = held;

        // ---------------- Server logic (also the bot API) ----------------

        public bool ServerTryTag()
        {
            var mm = MatchManager.Instance;
            if (!IsServer || mm == null || !state.CanMove) return false;
            if (Time.time < nextTagTime) return false;
            nextTagTime = Time.time + Config.tagCooldownSeconds;

            float range = Config.tagRange * state.TagRangeMultiplier;

            if (mm.Phase.Value == MatchPhase.Hunt && state.Role.Value == PlayerRole.Hunter)
            {
                var target = FindTarget(p => p.Role.Value == PlayerRole.Runner && !p.IsLocked.Value && !p.IsImmune,
                                        range, requireFacing: true);
                if (target == null) return false;
                target.ServerTag(state, Config.tagWindowSeconds);
                ServerTagLanded?.Invoke(target);
                return true;
            }

            if (mm.Phase.Value == MatchPhase.ChewingGum && state.Role.Value == PlayerRole.Runner && !state.SafeInGum.Value)
            {
                var hunter = mm.GumHunter;
                if (hunter == null || FindTarget(p => p == hunter, range, requireFacing: true) == null) return false;
                mm.ServerRegisterGumTag(state);
                ServerTagLanded?.Invoke(hunter);
                return true;
            }
            return false;
        }

        public bool ServerTryLock()
        {
            var mm = MatchManager.Instance;
            if (!IsServer || mm == null || !state.CanMove) return false;
            if (mm.Phase.Value != MatchPhase.Hunt || state.Role.Value != PlayerRole.Hunter) return false;

            ulong myId = NetworkObjectId;
            var target = FindTarget(p => p.Role.Value == PlayerRole.Runner && !p.IsLocked.Value &&
                                         p.IsTagged && p.TaggedBy.Value == myId,
                                    Config.lockRange, requireFacing: false);
            if (target == null) return false;
            mm.ServerOnLock(state, target);
            return true;
        }

        public void ServerSetUnlockHeld(bool held) { if (IsServer) unlockHeld = held; }

        void ServerTickUnlock(float dt)
        {
            var mm = MatchManager.Instance;
            if (!unlockHeld || mm == null || mm.Phase.Value != MatchPhase.Hunt) return;
            if (state.Role.Value != PlayerRole.Runner || !state.CanMove) return;

            var target = FindTarget(p => p.Role.Value == PlayerRole.Runner && p.IsLocked.Value,
                                    Config.keyRange, requireFacing: false);
            if (target == null) return;

            float amount = dt / Config.unlockSeconds * state.UnlockSpeedMultiplier;
            if (target.ServerAddUnlockProgress(amount)) mm.ServerOnUnlock(state, target);
        }

        // ---------------- Targeting ----------------

        PlayerState FindTarget(Func<PlayerState, bool> filter, float range, bool requireFacing)
        {
            PlayerState best = null;
            float bestSqr = range * range;

            foreach (var p in PlayerState.All)
            {
                if (p == state || !filter(p)) continue;

                Vector3 to = p.transform.position - transform.position;
                float sqr = to.sqrMagnitude;
                if (sqr > bestSqr) continue;

                if (requireFacing)
                {
                    Vector3 flat = new Vector3(to.x, 0f, to.z);
                    if (flat.sqrMagnitude > 0.01f &&
                        Vector3.Angle(transform.forward, flat) > Config.tagConeAngle * 0.5f) continue;
                }
                if (!HasLineOfSight(p)) continue;

                best = p;
                bestSqr = sqr;
            }
            return best;
        }

        bool HasLineOfSight(PlayerState other)
        {
            Vector3 from = transform.position + Vector3.up * 1.0f;
            Vector3 to = other.transform.position + Vector3.up * 0.8f; // chest-ish; still visible when crouched
            return !Physics.Linecast(from, to, lineOfSightBlockers, QueryTriggerInteraction.Ignore);
        }
    }
}
