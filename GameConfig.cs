using UnityEngine;

namespace Locked
{
    /// <summary>
    /// Every gameplay tunable lives here so designers can balance without touching code.
    /// Create via Assets > Create > Locked > Game Config.
    /// </summary>
    [CreateAssetMenu(menuName = "Locked/Game Config", fileName = "GameConfig")]
    public class GameConfig : ScriptableObject
    {
        [Header("Roles")]
        [Range(1, 3)] public int initialHunters = 1;
        [Range(1, 3)] public int maxHunters = 3;

        [Header("Phase timing (seconds)")]
        public float hunterFreezeSeconds = 10f;   // spec: 10 s countdown
        public float huntSeconds = 240f;          // safety cap so a round can't stall forever
        public float pingIntervalSeconds = 25f;   // spec: reveal every 25 s
        public float pingDisplaySeconds = 3f;     // how long UI shows the blips
        public float roundEndSeconds = 6f;

        [Header("Tag → Lock (L) → Key (K)")]
        public float tagRange = 2f;
        [Range(10f, 180f)] public float tagConeAngle = 90f;
        public float tagCooldownSeconds = 0.75f;  // whiffs are punished
        public float tagWindowSeconds = 2.5f;     // time the Hunter has to press L after a tag
        [Range(0.3f, 1f)] public float taggedSlowMultiplier = 0.75f;
        public float lockRange = 3f;
        public float keyRange = 2f;
        public float unlockSeconds = 1.5f;        // hold K this long (2 rescuers = 2x faster)
        public float unlockImmunitySeconds = 3f;  // freed Runner can't be re-tagged instantly

        [Header("Proper Chewing Gum")]
        public int locksForChewingGum = 3;        // spec: 3 locks unlocks the phase
        public float gumSeconds = 60f;
        public float gumRunnerDelaySeconds = 3f;  // Hunter's head start
        public float gumHunterSpeedMultiplier = 1.2f;

        [Header("Skins (index = SkinIndex on PlayerState)")]
        public SkinDefinition[] skins;

        public SkinDefinition GetSkin(int index) =>
            skins != null && index >= 0 && index < skins.Length ? skins[index] : null;
    }
}
