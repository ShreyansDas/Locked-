using UnityEngine;

namespace Locked
{
    /// <summary>
    /// A cosmetic skin plus its passive ability, expressed as stat multipliers (1 = no effect).
    /// Keep passives small (5–15%) — they must never decide a match on their own.
    /// </summary>
    [CreateAssetMenu(menuName = "Locked/Skin", fileName = "Skin_")]
    public class SkinDefinition : ScriptableObject
    {
        public string displayName = "Default";
        [TextArea] public string passiveDescription;
        public GameObject visualPrefab;

        [Header("Passive multipliers")]
        public float moveSpeed = 1f;    // e.g. "Sprinter"  1.08
        public float climbSpeed = 1f;   // e.g. "Gecko"     1.30
        public float tagRange = 1f;     // e.g. "Long Arms" 1.15 (Hunter-relevant)
        public float unlockSpeed = 1f;  // e.g. "Medic"     1.40 (Runner-relevant)
    }
}
