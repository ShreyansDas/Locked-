using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

namespace Locked
{
    /// <summary>
    /// Third-person CharacterController movement: walk, sprint, jump, crouch (cover) and climb.
    /// Runs only on the owner: the local client for humans, the server for bots.
    /// Pivot must be at the feet. Pair with ClientNetworkTransform for replication.
    /// </summary>
    [RequireComponent(typeof(CharacterController), typeof(PlayerState))]
    public class PlayerMovement : NetworkBehaviour
    {
        [Header("Speeds (m/s)")]
        [SerializeField] float walkSpeed = 4.5f;
        [SerializeField] float sprintSpeed = 7f;
        [SerializeField] float crouchSpeed = 2.4f;
        [SerializeField] float climbSpeed = 3f;
        [SerializeField] float acceleration = 40f;
        [SerializeField, Range(0f, 1f)] float airControl = 0.35f;
        [SerializeField] float turnSpeedDegrees = 720f;
        [SerializeField] float jumpHeight = 1.2f;
        [SerializeField] float gravity = -22f;

        [Header("Crouch / cover")]
        [SerializeField] float standHeight = 1.8f;
        [SerializeField] float crouchHeight = 1.0f;
        [SerializeField] float crouchTransitionSpeed = 10f;
        [SerializeField] LayerMask environmentMask = ~0; // exclude the Player layer

        [Header("Climbing")]
        [SerializeField] LayerMask climbableMask;          // put climbable walls/ledges on a "Climbable" layer
        [SerializeField] float climbProbeDistance = 0.6f;
        [SerializeField] float mantleForwardBoost = 2.5f;

        // ---- Input: written by local hardware (humans) or an IBotBrain (server) ----
        [HideInInspector] public Vector2 MoveInput;        // humans: camera-relative; bots: world X/Z
        [HideInInspector] public bool SprintHeld, CrouchHeld, JumpPressed;

        // Replicated for animation, AI vision (crouched = harder to spot) and hitbox height.
        public readonly NetworkVariable<bool> IsCrouched =
            new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        public readonly NetworkVariable<bool> IsClimbing =
            new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public float PlanarSpeed { get; private set; }

        CharacterController cc;
        PlayerState state;
        Camera cam;
        Vector3 planarVelocity;
        float verticalVelocity;

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            state = GetComponent<PlayerState>();
        }

        public override void OnNetworkSpawn()
        {
            if (IsOwner && !state.IsBot) cam = Camera.main; // hook Cinemachine to this player here
        }

        void Update()
        {
            if (!IsSpawned || !IsOwner) return;
            if (!state.IsBot) ReadHumanInput();

            float dt = Time.deltaTime;
            UpdateCrouch(dt);

            if (!state.CanMove)
            {
                planarVelocity = Vector3.zero;
                SetClimbing(false);
                ApplyVertical(dt, allowJump: false);
                cc.Move(Vector3.up * verticalVelocity * dt);
            }
            else if (!TryClimb(dt))
            {
                Move(dt);
            }
            JumpPressed = false;
        }

        void ReadHumanInput()
        {
            MoveInput = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            SprintHeld = Input.GetKey(KeyCode.LeftShift);
            CrouchHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C);
            if (Input.GetKeyDown(KeyCode.Space)) JumpPressed = true;
        }

        Vector3 WishDirection()
        {
            var input = Vector3.ClampMagnitude(new Vector3(MoveInput.x, 0f, MoveInput.y), 1f);
            if (state.IsBot || cam == null) return input;

            Vector3 fwd = cam.transform.forward; fwd.y = 0f; fwd.Normalize();
            Vector3 right = cam.transform.right; right.y = 0f; right.Normalize();
            return Vector3.ClampMagnitude(fwd * input.z + right * input.x, 1f);
        }

        void Move(float dt)
        {
            Vector3 wish = WishDirection();
            bool grounded = cc.isGrounded;

            float speed = IsCrouched.Value ? crouchSpeed : (SprintHeld ? sprintSpeed : walkSpeed);
            speed *= state.MoveSpeedMultiplier;

            float accel = grounded ? acceleration : acceleration * airControl;
            planarVelocity = Vector3.MoveTowards(planarVelocity, wish * speed, accel * dt);

            ApplyVertical(dt, allowJump: grounded && !IsCrouched.Value);
            cc.Move((planarVelocity + Vector3.up * verticalVelocity) * dt);

            if (wish.sqrMagnitude > 0.01f)
            {
                var target = Quaternion.LookRotation(wish, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeedDegrees * dt);
            }
            PlanarSpeed = new Vector3(cc.velocity.x, 0f, cc.velocity.z).magnitude;
        }

        void ApplyVertical(float dt, bool allowJump)
        {
            if (cc.isGrounded && verticalVelocity < 0f) verticalVelocity = -2f; // stick to slopes
            if (allowJump && JumpPressed) verticalVelocity = Mathf.Sqrt(2f * jumpHeight * -gravity);
            verticalVelocity += gravity * dt;
        }

        // ---------------- Crouch (cover) ----------------

        void UpdateCrouch(float dt)
        {
            bool want = CrouchHeld && !IsClimbing.Value;
            if (!want && IsCrouched.Value && !HasHeadroom()) want = true; // can't stand up under a table
            if (want != IsCrouched.Value) IsCrouched.Value = want;

            float targetHeight = want ? crouchHeight : standHeight;
            cc.height = Mathf.MoveTowards(cc.height, targetHeight, crouchTransitionSpeed * dt);
            cc.center = new Vector3(0f, cc.height * 0.5f, 0f);
        }

        bool HasHeadroom()
        {
            float r = cc.radius * 0.95f;
            Vector3 origin = transform.position + Vector3.up * (cc.height - r);
            float distance = standHeight - cc.height + 0.05f;
            return !Physics.SphereCast(origin, r, Vector3.up, out _, distance, environmentMask,
                                       QueryTriggerInteraction.Ignore);
        }

        // ---------------- Climbing (verticality) ----------------

        bool TryClimb(float dt)
        {
            Vector3 wish = WishDirection();
            if (wish.sqrMagnitude < 0.04f || IsCrouched.Value) { SetClimbing(false); return false; }

            Vector3 dir = wish.normalized;
            Vector3 chest = transform.position + Vector3.up * (cc.height * 0.6f);
            bool hitWall = Physics.Raycast(chest, dir, out RaycastHit hit, cc.radius + climbProbeDistance,
                                           climbableMask, QueryTriggerInteraction.Ignore);

            if (hitWall && Vector3.Dot(-hit.normal, dir) > 0.5f)
            {
                SetClimbing(true);
                planarVelocity = Vector3.zero;
                verticalVelocity = 0f;

                Vector3 faceWall = -hit.normal; faceWall.y = 0f;
                if (faceWall.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(faceWall);

                Vector3 climb = Vector3.up * climbSpeed * state.ClimbSpeedMultiplier + faceWall * 0.5f;
                cc.Move(climb * dt);
                return true;
            }

            if (IsClimbing.Value)
            {
                // Chest cleared the ledge: mantle over it with a small forward hop.
                SetClimbing(false);
                planarVelocity = dir * mantleForwardBoost;
                verticalVelocity = 2f;
            }
            return false;
        }

        void SetClimbing(bool value)
        {
            if (IsClimbing.Value != value) IsClimbing.Value = value;
        }

        // ---------------- Server → owner ----------------

        /// <summary>Owner-authoritative transform, so the server asks the owner to move itself.</summary>
        [Rpc(SendTo.Owner)]
        public void TeleportRpc(Vector3 position, Quaternion rotation)
        {
            cc.enabled = false;
            if (TryGetComponent(out NetworkTransform nt)) nt.Teleport(position, rotation, transform.localScale);
            else transform.SetPositionAndRotation(position, rotation);
            cc.enabled = true;
            planarVelocity = Vector3.zero;
            verticalVelocity = 0f;
        }
    }
}
