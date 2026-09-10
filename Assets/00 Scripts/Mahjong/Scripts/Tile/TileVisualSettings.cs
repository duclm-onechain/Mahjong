using UnityEngine;

namespace MahjongOut3D.TileSystem
{
    /// <summary>
    /// Stores presentation tuning values shared by Mahjong tile visuals.
    /// </summary>
    [CreateAssetMenu(menuName = "Mahjong Out 3D/Tile/Tile Visual Settings", fileName = "TileVisualSettings")]
    public sealed class TileVisualSettings : ScriptableObject
    {
        [field: Header("Scale")]
        [field: SerializeField, Min(0.1f)]
        public float VisibleScaleMultiplier { get; private set; } = 1f;

        [field: SerializeField, Min(0.1f)]
        public float SelectedScaleMultiplier { get; private set; } = 1.05f;

        [field: SerializeField, Min(0.1f)]
        public float MatchedScaleMultiplier { get; private set; } = 1.02f;

        [field: SerializeField, Min(0.01f)]
        public float ScaleSmoothing { get; private set; } = 0.06f;

        [field: Header("Color")]
        [field: SerializeField]
        public Color SelectedTintColor { get; private set; } = new Color(1f, 0.95f, 0.8f, 1f);

        [field: SerializeField]
        public Color SelectedEmissionColor { get; private set; } = new Color(0.95f, 0.8f, 0.35f, 1f);

        [field: SerializeField]
        public Color MatchedEmissionColor { get; private set; } = new Color(0.45f, 1f, 0.8f, 1f);

        [field: SerializeField]
        public Color HintTintColor { get; private set; } = new Color(0.35f, 1f, 0.75f, 1f);

        [field: SerializeField]
        public Color HintEmissionColor { get; private set; } = new Color(0.15f, 1f, 0.85f, 1f);

        [field: SerializeField]
        public Color BlockedTintColor { get; private set; } = new Color(0f, 0f, 0f, 1f);

        [field: SerializeField, Range(0f, 4f)]
        public float SelectedTintStrength { get; private set; } = 0.18f;

        [field: SerializeField, Range(0f, 8f)]
        public float SelectedEmissionIntensity { get; private set; } = 1.35f;

        [field: SerializeField, Range(0f, 8f)]
        public float MatchedEmissionIntensity { get; private set; } = 1.75f;

        [field: SerializeField, Range(0f, 4f)]
        public float HintTintStrength { get; private set; } = 0.7f;

        [field: SerializeField, Range(0f, 8f)]
        public float HintEmissionIntensity { get; private set; } = 2.2f;

        [field: SerializeField, Range(0f, 1f)]
        public float BlockedTintStrength { get; private set; } = 0.6f;

        [field: SerializeField, Min(0.05f)]
        public float UnblockFadeDuration { get; private set; } = 0.35f;

        [field: Header("Shader Properties")]
        [field: SerializeField]
        public string BaseColorProperty { get; private set; } = "_BaseColor";

        [field: SerializeField]
        public string SecondaryBaseColorProperty { get; private set; } = "_Color";

        [field: SerializeField]
        public string EmissionColorProperty { get; private set; } = "_EmissionColor";

        [field: Header("Timing")]
        [field: SerializeField]
        public bool UseUnscaledTime { get; private set; } = true;
    }
}
