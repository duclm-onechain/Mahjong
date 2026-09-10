using System.Collections.Generic;
using UnityEngine;

namespace MahjongOut3D.TileSystem
{
    /// <summary>
    /// Drives scale, tint, glow and visibility feedback for a Mahjong tile.
    /// </summary>
    public sealed class TileVisualController : MonoBehaviour
    {
        private const string MatchIndicatorObjectName = "Quad";
        private const string GeneratedOutlineObjectName = "InvertedHullOutlineRuntime";

        [SerializeField] private TileVisualSettings settings;
        [SerializeField] private MeshRenderer[] targetRenderers;
        [SerializeField] private TileOutlinePresenter outlinePresenter;
        [SerializeField] private Transform scaleRoot;

        private bool hasInitializedScaleState;
        private Color[] cachedPrimaryBaseColors;
        private Color[] cachedSecondaryBaseColors;
        private bool[] cachedHasPrimaryBaseColor;
        private bool[] cachedHasSecondaryBaseColor;
        private MaterialPropertyBlock propertyBlock;
        private Vector3 baseScale;
        private Vector3 currentScale;
        private Vector3 targetScale;
        private float scaleXSmoothVelocity;
        private float scaleYSmoothVelocity;
        private float scaleZSmoothVelocity;
        private bool hasRuntimeBaseColorOverride;
        private Color runtimeBaseColorOverride = Color.white;
        private bool isHintHighlighted;
        private bool isSelectionBlocked;
        private bool hasResolvedTargetRenderers;
        private float currentBlockedWeight;
        private float targetBlockedWeight;
        private TileState currentState = TileState.Visible;

        /// <summary>
        /// Enables or disables the temporary hint highlight visual.
        /// </summary>
        /// <param name="isHighlighted">True to show the hint highlight; otherwise false.</param>
        public void SetHintHighlighted(bool isHighlighted)
        {
            isHintHighlighted = isHighlighted;

            if (outlinePresenter == null)
            {
                outlinePresenter = GetComponentInChildren<TileOutlinePresenter>(true);
            }

            if (outlinePresenter != null)
            {
                outlinePresenter.SetHintHighlighted(isHighlighted);
            }
        }

        /// <summary>
        /// Enables or disables the dimmed visual used for tiles that cannot currently be selected.
        /// </summary>
        /// <param name="isBlocked">True to darken the tile; otherwise false.</param>
        /// <param name="instant">True to snap color immediately; false to fade smoothly on unblock.</param>
        public void SetSelectionBlocked(bool isBlocked, bool instant = false)
        {
            bool wasBlocked = isSelectionBlocked;
            isSelectionBlocked = isBlocked;
            targetBlockedWeight = isBlocked ? 1f : 0f;

            if (instant || !isActiveAndEnabled || !Application.isPlaying || currentState != TileState.Visible)
            {
                currentBlockedWeight = targetBlockedWeight;
            }
            else if (isBlocked && !wasBlocked)
            {
                currentBlockedWeight = 1f;
            }
        }

        /// <summary>
        /// Applies visual feedback for the specified tile state.
        /// </summary>
        /// <param name="state">New tile state.</param>
        /// <param name="instant">True to snap immediately; otherwise smooth scale transitions.</param>
        public void ApplyState(TileState state, bool instant)
        {
            EnsureInitialized();
            currentState = state;

            MahjongTile mahjongTile = GetComponent<MahjongTile>();
            bool isBuffered = mahjongTile != null && mahjongTile.IsBufferedSelection;
            bool shouldRender = state != TileState.Hidden && state != TileState.Removed && state != TileState.Matched && !isBuffered;
            SetRenderersVisible(shouldRender);
            SetOutlineVisible(shouldRender);

            targetScale = GetTargetScale(state);
            if (instant)
            {
                currentScale = targetScale;
                ApplyScale(currentScale);
                if (state != TileState.Visible)
                {
                    currentBlockedWeight = isSelectionBlocked ? 1f : 0f;
                }
            }

            ApplyMaterialFeedback(state);
        }

        /// <summary>
        /// Gets the primary renderer used by the tile visual.
        /// </summary>
        /// <returns>Primary renderer when available; otherwise null.</returns>
        public MeshRenderer GetPrimaryRenderer()
        {
            EnsureInitialized();
            return targetRenderers != null && targetRenderers.Length > 0 ? targetRenderers[0] : null;
        }

        /// <summary>
        /// Gets the outline presenter used by this tile visual.
        /// </summary>
        /// <returns>Outline presenter when available; otherwise null.</returns>
        public TileOutlinePresenter GetOutlinePresenter()
        {
            return outlinePresenter;
        }

        /// <summary>
        /// Applies a runtime base scale override used for generated tile face tuning.
        /// </summary>
        /// <param name="scale">Base scale to use for this tile instance.</param>
        public void SetRuntimeBaseScale(Vector3 scale)
        {
            EnsureInitialized();

            baseScale = scale;
            currentScale = scale;
            targetScale = scale;
            scaleXSmoothVelocity = 0f;
            scaleYSmoothVelocity = 0f;
            scaleZSmoothVelocity = 0f;
            hasInitializedScaleState = true;
            ApplyScale(scale);
        }

        /// <summary>
        /// Applies a runtime base color override used for debugging or lightweight theming.
        /// </summary>
        /// <param name="color">Base color to use for this tile instance.</param>
        public void SetRuntimeBaseColor(Color color)
        {
            hasRuntimeBaseColorOverride = true;
            runtimeBaseColorOverride = color;
        }

        /// <summary>
        /// Clears any runtime base color override and restores material-driven colors.
        /// </summary>
        public void ClearRuntimeBaseColor()
        {
            hasRuntimeBaseColorOverride = false;
            runtimeBaseColorOverride = Color.white;
        }

        /// <summary>
        /// Caches references and base material colors once the component wakes.
        /// </summary>
        private void Awake()
        {
            EnsureInitialized();
        }

        /// <summary>
        /// Smooths scale feedback every frame.
        /// </summary>
        private void LateUpdate()
        {
            EnsureInitialized();

            float deltaTime = GetDeltaTime();
            currentScale = new Vector3(
                Mathf.SmoothDamp(currentScale.x, targetScale.x, ref scaleXSmoothVelocity, GetScaleSmoothing(), Mathf.Infinity, deltaTime),
                Mathf.SmoothDamp(currentScale.y, targetScale.y, ref scaleYSmoothVelocity, GetScaleSmoothing(), Mathf.Infinity, deltaTime),
                Mathf.SmoothDamp(currentScale.z, targetScale.z, ref scaleZSmoothVelocity, GetScaleSmoothing(), Mathf.Infinity, deltaTime));

            Vector3 displayScale = currentScale;
            if (isHintHighlighted)
            {
                displayScale *= GetHintPulseScaleMultiplier();
            }

            ApplyScale(displayScale);

            if (Mathf.Abs(currentBlockedWeight - targetBlockedWeight) > 0.001f)
            {
                float fadeDuration = GetUnblockFadeDuration();
                float fadeSpeed = 1f / Mathf.Max(0.05f, fadeDuration);
                currentBlockedWeight = Mathf.MoveTowards(currentBlockedWeight, targetBlockedWeight, deltaTime * fadeSpeed);
                ApplyMaterialFeedback(currentState);
            }
        }

        /// <summary>
        /// Auto-caches editor references when values change in the inspector.
        /// </summary>
        private void OnValidate()
        {
            if (scaleRoot == null)
            {
                scaleRoot = transform;
            }

            if (outlinePresenter == null)
            {
                outlinePresenter = GetComponentInChildren<TileOutlinePresenter>(true);
            }

            if (outlinePresenter != null)
            {
                outlinePresenter.SetHintHighlighted(isHintHighlighted);
            }

            if (targetRenderers == null || targetRenderers.Length == 0)
            {
                RefreshTargetRenderers();
            }
        }

        /// <summary>
        /// Ensures cached renderers, colors and scale state are available.
        /// </summary>
        private void EnsureInitialized()
        {
            if (scaleRoot == null)
            {
                scaleRoot = transform;
            }

            if (propertyBlock == null)
            {
                propertyBlock = new MaterialPropertyBlock();
            }

            if (!hasResolvedTargetRenderers || HasMissingTargetRenderer())
            {
                RefreshTargetRenderers();
            }

            if (outlinePresenter == null)
            {
                outlinePresenter = GetComponentInChildren<TileOutlinePresenter>(true);
            }

            if (!hasInitializedScaleState)
            {
                baseScale = scaleRoot.localScale;
                currentScale = baseScale;
                targetScale = baseScale;
                hasInitializedScaleState = true;
            }

            if (cachedPrimaryBaseColors == null || cachedPrimaryBaseColors.Length != targetRenderers.Length)
            {
                cachedPrimaryBaseColors = new Color[targetRenderers.Length];
                cachedSecondaryBaseColors = new Color[targetRenderers.Length];
                cachedHasPrimaryBaseColor = new bool[targetRenderers.Length];
                cachedHasSecondaryBaseColor = new bool[targetRenderers.Length];

                for (int index = 0; index < targetRenderers.Length; index++)
                {
                    MeshRenderer renderer = targetRenderers[index];
                    cachedHasPrimaryBaseColor[index] = TryResolveBaseColor(renderer, GetBaseColorProperty(), out cachedPrimaryBaseColors[index]);
                    cachedHasSecondaryBaseColor[index] = TryResolveBaseColor(renderer, GetSecondaryBaseColorProperty(), out cachedSecondaryBaseColors[index]);
                }
            }
        }

        /// <summary>
        /// Refreshes the renderer list so every visible tile mesh is driven by the state machine.
        /// </summary>
        private void RefreshTargetRenderers()
        {
            MeshRenderer[] discoveredRenderers = GetComponentsInChildren<MeshRenderer>(true);
            List<MeshRenderer> mergedRenderers = new List<MeshRenderer>(discoveredRenderers.Length);

            AddRenderers(mergedRenderers, targetRenderers);
            AddRenderers(mergedRenderers, discoveredRenderers);

            targetRenderers = mergedRenderers.ToArray();
            hasResolvedTargetRenderers = targetRenderers.Length > 0;
        }

        /// <summary>
        /// Returns true when the cached renderer list contains missing references.
        /// </summary>
        private bool HasMissingTargetRenderer()
        {
            if (targetRenderers == null || targetRenderers.Length == 0)
            {
                return true;
            }

            for (int index = 0; index < targetRenderers.Length; index++)
            {
                if (targetRenderers[index] == null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Adds valid tile renderers while excluding match-indicator and generated-outline helpers.
        /// </summary>
        private static void AddRenderers(List<MeshRenderer> destination, MeshRenderer[] source)
        {
            if (source == null)
            {
                return;
            }

            for (int index = 0; index < source.Length; index++)
            {
                MeshRenderer renderer = source[index];
                if (renderer == null || destination.Contains(renderer))
                {
                    continue;
                }

                string rendererObjectName = renderer.gameObject.name;
                if (rendererObjectName == MatchIndicatorObjectName || rendererObjectName == GeneratedOutlineObjectName)
                {
                    continue;
                }

                destination.Add(renderer);
            }
        }

        /// <summary>
        /// Resolves the scale multiplier associated with a tile state.
        /// </summary>
        /// <param name="state">Tile state to evaluate.</param>
        /// <returns>Target local scale vector.</returns>
        private Vector3 GetTargetScale(TileState state)
        {
            float scaleMultiplier = GetVisibleScaleMultiplier();
            if (state == TileState.Matched)
            {
                scaleMultiplier = GetMatchedScaleMultiplier();
            }

            return baseScale * scaleMultiplier;
        }

        /// <summary>
        /// Applies state-based tint and glow using material property blocks.
        /// </summary>
        /// <param name="state">Current tile state.</param>
        private void ApplyMaterialFeedback(TileState state)
        {
            for (int index = 0; index < targetRenderers.Length; index++)
            {
                MeshRenderer renderer = targetRenderers[index];
                if (renderer == null)
                {
                    continue;
                }

                renderer.GetPropertyBlock(propertyBlock);
                Color emissionColor = Color.black;

                if (hasRuntimeBaseColorOverride || cachedHasPrimaryBaseColor[index])
                {
                    Color primaryBaseColor = hasRuntimeBaseColorOverride ? runtimeBaseColorOverride : cachedPrimaryBaseColors[index];
                    Color primaryTintColor = ResolveTintColor(primaryBaseColor, state);
                    SetColorPropertyOnCurrentBlock(renderer.sharedMaterial, GetBaseColorProperty(), primaryTintColor);
                }

                if (hasRuntimeBaseColorOverride || cachedHasSecondaryBaseColor[index])
                {
                    Color secondaryBaseColor = hasRuntimeBaseColorOverride ? runtimeBaseColorOverride : cachedSecondaryBaseColors[index];
                    Color secondaryTintColor = ResolveTintColor(secondaryBaseColor, state);
                    SetColorPropertyOnCurrentBlock(renderer.sharedMaterial, GetSecondaryBaseColorProperty(), secondaryTintColor);
                }

                if (isHintHighlighted)
                {
                    emissionColor = GetHintEmissionColor() * GetHintEmissionIntensity();
                }
                else if (state == TileState.Matched)
                {
                    emissionColor = GetMatchedEmissionColor() * GetMatchedEmissionIntensity();
                }

                SetColorPropertyOnCurrentBlock(renderer.sharedMaterial, GetEmissionColorProperty(), emissionColor);
                renderer.SetPropertyBlock(propertyBlock);
            }
        }

        /// <summary>
        /// Sets a color property when the renderer shader exposes it.
        /// </summary>
        /// <param name="renderer">Renderer to update.</param>
        /// <param name="propertyName">Shader property name.</param>
        /// <param name="color">Color value to assign.</param>
        private void SetColorPropertyOnCurrentBlock(Material material, string propertyName, Color color)
        {
            if (string.IsNullOrWhiteSpace(propertyName) || material == null || !material.HasProperty(propertyName))
            {
                return;
            }

            propertyBlock.SetColor(propertyName, color);
        }

        /// <summary>
        /// Resolves the base tint color from the renderer shared material.
        /// </summary>
        /// <param name="renderer">Renderer to inspect.</param>
        /// <returns>Base color for later highlight blending.</returns>
        private bool TryResolveBaseColor(Renderer renderer, string propertyName, out Color color)
        {
            color = Color.white;
            if (renderer == null || renderer.sharedMaterial == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(propertyName) || !renderer.sharedMaterial.HasProperty(propertyName))
            {
                return false;
            }

            color = renderer.sharedMaterial.GetColor(propertyName);
            color = NormalizeBaseColor(renderer.sharedMaterial, color);
            return true;
        }

        /// <summary>
        /// Normalizes imported material tint colors so texture-driven tiles keep their authored appearance.
        /// Some imported Mahjong materials store black tint values while relying on the texture; in that case white is the neutral multiplier.
        /// </summary>
        private static Color NormalizeBaseColor(Material material, Color color)
        {
            if (material == null)
            {
                return color;
            }

            if (color.maxColorComponent > 0.0001f)
            {
                return color;
            }

            bool hasBaseMap = material.HasProperty("_BaseMap") && material.GetTexture("_BaseMap") != null;
            bool hasMainTex = material.HasProperty("_MainTex") && material.GetTexture("_MainTex") != null;
            if (!hasBaseMap && !hasMainTex)
            {
                return color;
            }

            return new Color(1f, 1f, 1f, Mathf.Approximately(color.a, 0f) ? 1f : color.a);
        }

        /// <summary>
        /// Resolves which cached base color should drive highlight blending when multiple shader color properties exist.
        /// </summary>
        private static Color ResolveFallbackBaseColor(Color primaryBaseColor, bool hasPrimaryBaseColor, Color secondaryBaseColor, bool hasSecondaryBaseColor)
        {
            if (hasPrimaryBaseColor && primaryBaseColor.maxColorComponent > 0.0001f)
            {
                return primaryBaseColor;
            }

            if (hasSecondaryBaseColor)
            {
                return secondaryBaseColor;
            }

            if (hasPrimaryBaseColor)
            {
                return primaryBaseColor;
            }

            return Color.white;
        }

        /// <summary>
        /// Builds the final tint color for the supplied tile state without losing the renderer's original base color.
        /// </summary>
        private Color ResolveTintColor(Color baseColor, TileState state)
        {
            if (isHintHighlighted)
            {
                return Color.Lerp(baseColor, GetHintTintColor(), GetHintTintStrength());
            }

            if (state == TileState.Visible && currentBlockedWeight > 0.001f)
            {
                float strength = GetBlockedTintStrength() * currentBlockedWeight;
                return Color.Lerp(baseColor, GetBlockedTintColor(), strength);
            }

            return baseColor;
        }

        /// <summary>
        /// Shows or hides the outline visual.
        /// </summary>
        /// <param name="isVisible">True to show outline; otherwise false.</param>
        private void SetOutlineVisible(bool isVisible)
        {
            if (outlinePresenter != null)
            {
                outlinePresenter.SetOutlineVisible(isVisible);
                outlinePresenter.SetHintHighlighted(isVisible && isHintHighlighted);
            }
        }

        /// <summary>
        /// Shows or hides every configured renderer.
        /// </summary>
        /// <param name="isVisible">True to render; otherwise false.</param>
        private void SetRenderersVisible(bool isVisible)
        {
            for (int index = 0; index < targetRenderers.Length; index++)
            {
                MeshRenderer renderer = targetRenderers[index];
                if (renderer != null)
                {
                    renderer.enabled = isVisible;
                }
            }
        }

        /// <summary>
        /// Applies the computed local scale to the configured scale root.
        /// </summary>
        /// <param name="scaleValue">Scale value to assign.</param>
        private void ApplyScale(Vector3 scaleValue)
        {
            if (scaleRoot != null)
            {
                scaleRoot.localScale = scaleValue;
            }
        }

        /// <summary>
        /// Gets the scale smoothing duration.
        /// </summary>
        /// <returns>Scale smoothing duration.</returns>
        private float GetScaleSmoothing()
        {
            return settings != null ? settings.ScaleSmoothing : 0.06f;
        }

        /// <summary>
        /// Gets the visible scale multiplier.
        /// </summary>
        /// <returns>Visible scale multiplier.</returns>
        private float GetVisibleScaleMultiplier()
        {
            return settings != null ? settings.VisibleScaleMultiplier : 1f;
        }

        /// <summary>
        /// Gets the selected scale multiplier.
        /// </summary>
        /// <returns>Selected scale multiplier.</returns>
        private float GetSelectedScaleMultiplier()
        {
            return settings != null ? settings.SelectedScaleMultiplier : 1.05f;
        }

        /// <summary>
        /// Gets the matched scale multiplier.
        /// </summary>
        /// <returns>Matched scale multiplier.</returns>
        private float GetMatchedScaleMultiplier()
        {
            return settings != null ? settings.MatchedScaleMultiplier : 1.02f;
        }

        /// <summary>
        /// Gets the temporary pulse multiplier used by hint feedback.
        /// </summary>
        /// <returns>Animated hint pulse scale multiplier.</returns>
        private float GetHintPulseScaleMultiplier()
        {
            float pulseTime = settings != null && settings.UseUnscaledTime ? Time.unscaledTime : Time.time;
            float wave = 0.5f + (0.5f * Mathf.Sin(pulseTime * 8f));
            return Mathf.Lerp(1.04f, 1.12f, wave);
        }

        /// <summary>
        /// Gets the selected tint color.
        /// </summary>
        /// <returns>Selected tint color.</returns>
        private Color GetSelectedTintColor()
        {
            return settings != null ? settings.SelectedTintColor : new Color(1f, 0.95f, 0.8f, 1f);
        }

        /// <summary>
        /// Gets the selected emission color.
        /// </summary>
        /// <returns>Selected emission color.</returns>
        private Color GetSelectedEmissionColor()
        {
            return settings != null ? settings.SelectedEmissionColor : new Color(0.95f, 0.8f, 0.35f, 1f);
        }

        /// <summary>
        /// Gets the matched emission color.
        /// </summary>
        /// <returns>Matched emission color.</returns>
        private Color GetMatchedEmissionColor()
        {
            return settings != null ? settings.MatchedEmissionColor : new Color(0.45f, 1f, 0.8f, 1f);
        }

        /// <summary>
        /// Gets the selected tint blend strength.
        /// </summary>
        /// <returns>Selected tint strength.</returns>
        private float GetSelectedTintStrength()
        {
            return settings != null ? settings.SelectedTintStrength : 0.18f;
        }

        /// <summary>
        /// Gets the selected emission intensity.
        /// </summary>
        /// <returns>Selected emission intensity.</returns>
        private float GetSelectedEmissionIntensity()
        {
            return settings != null ? settings.SelectedEmissionIntensity : 1.35f;
        }

        /// <summary>
        /// Gets the matched emission intensity.
        /// </summary>
        /// <returns>Matched emission intensity.</returns>
        private float GetMatchedEmissionIntensity()
        {
            return settings != null ? settings.MatchedEmissionIntensity : 1.75f;
        }

        /// <summary>
        /// Gets the hint tint color.
        /// </summary>
        /// <returns>Hint tint color.</returns>
        private Color GetHintTintColor()
        {
            return settings != null ? settings.HintTintColor : new Color(0.35f, 1f, 0.75f, 1f);
        }

        /// <summary>
        /// Gets the hint emission color.
        /// </summary>
        /// <returns>Hint emission color.</returns>
        private Color GetHintEmissionColor()
        {
            return settings != null ? settings.HintEmissionColor : new Color(0.15f, 1f, 0.85f, 1f);
        }

        /// <summary>
        /// Gets the hint tint blend strength.
        /// </summary>
        /// <returns>Hint tint strength.</returns>
        private float GetHintTintStrength()
        {
            return settings != null ? settings.HintTintStrength : 0.7f;
        }

        /// <summary>
        /// Gets the hint emission intensity.
        /// </summary>
        /// <returns>Hint emission intensity.</returns>
        private float GetHintEmissionIntensity()
        {
            return settings != null ? settings.HintEmissionIntensity : 2.2f;
        }

        /// <summary>
        /// Gets the blocked tint color.
        /// </summary>
        /// <returns>Blocked tint color.</returns>
        private Color GetBlockedTintColor()
        {
            return settings != null ? settings.BlockedTintColor : new Color(0f, 0f, 0f, 1f);
        }

        /// <summary>
        /// Gets the blocked tint blend strength.
        /// </summary>
        /// <returns>Blocked tint strength.</returns>
        private float GetBlockedTintStrength()
        {
            return settings != null ? settings.BlockedTintStrength : 0.6f;
        }

        /// <summary>
        /// Gets the base color property name.
        /// </summary>
        /// <returns>Base color property name.</returns>
        private string GetBaseColorProperty()
        {
            return settings != null ? settings.BaseColorProperty : "_BaseColor";
        }

        /// <summary>
        /// Gets the secondary base color property name.
        /// </summary>
        /// <returns>Secondary base color property name.</returns>
        private string GetSecondaryBaseColorProperty()
        {
            return settings != null ? settings.SecondaryBaseColorProperty : "_Color";
        }

        /// <summary>
        /// Gets the emission color property name.
        /// </summary>
        /// <returns>Emission color property name.</returns>
        private string GetEmissionColorProperty()
        {
            return settings != null ? settings.EmissionColorProperty : "_EmissionColor";
        }

        /// <summary>
        /// Gets the duration of the unblock color fade transition.
        /// </summary>
        /// <returns>Fade duration in seconds.</returns>
        private float GetUnblockFadeDuration()
        {
            return settings != null ? settings.UnblockFadeDuration : 0.35f;
        }

        /// <summary>
        /// Gets the active time step used by the visual controller.
        /// </summary>
        /// <returns>Current delta time.</returns>
        private float GetDeltaTime()
        {
            if (settings != null && settings.UseUnscaledTime)
            {
                return Time.unscaledDeltaTime;
            }

            return Time.deltaTime;
        }
    }
}
