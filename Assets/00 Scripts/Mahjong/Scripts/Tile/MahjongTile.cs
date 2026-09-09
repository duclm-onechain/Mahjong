using System;
using System.Collections;
using MahjongOut3D.Data;
using UnityEngine;

namespace MahjongOut3D.TileSystem
{
    /// <summary>
    /// Represents a single Mahjong tile instance inside the 3D puzzle block.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MahjongTile : MonoBehaviour
    {
        [Header("Identity")]
        [SerializeField] private int tileId;
        [SerializeField] private int matchId;
        [SerializeField] private Vector3Int gridCoordinate;
        [SerializeField] private int surfaceShellIndex;
        [SerializeField] private int runtimeBlockIndex;
        [SerializeField] private Vector3 boardLocalPosition;
        [SerializeField] private Vector3 boardLocalEulerAngles;
        [SerializeField] private bool isBufferedSelection;
        [SerializeField] private Transform boardParent;

        [Header("Components")]
        [SerializeField] private MeshRenderer meshRenderer;
        [SerializeField] private MeshRenderer pieceRenderer;
        [SerializeField] private MeshRenderer fillRenderer;
        [SerializeField] private MeshRenderer matchIndicatorRenderer;
        [SerializeField] private GameObject comboIndicatorObject;
        [SerializeField] private Collider tileCollider;
        [SerializeField] private Animator animator;
        [SerializeField] private TileOutlinePresenter outlinePresenter;
        [SerializeField] private TileVisualController visualController;
        [SerializeField] private TileDirectionFrame directionFrame;

        [Header("Runtime")]
        [SerializeField] private TileState state = TileState.Hidden;
        [SerializeField] private bool isFaceDown;
        [SerializeField] private bool isComboTile;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private const float FaceFlipDurationSeconds = 0.18f;
        private static readonly Quaternion FaceFlipDeltaRotation = Quaternion.Euler(180f, 0f, 0f);

        private MaterialPropertyBlock baseColorPropertyBlock;
        private MaterialPropertyBlock piecePropertyBlock;
        private MaterialPropertyBlock fillPropertyBlock;
        private Coroutine blockedTapFeedbackRoutine;
        private Coroutine faceFlipRoutine;
        private Vector3 blockedTapBaseLocalPosition;
        private Vector3 pieceFaceBaseLocalPosition;
        private Vector3 pieceFaceBaseLocalScale;
        private Vector3 fillFaceBaseLocalPosition;
        private Vector3 fillFaceBaseLocalScale;
        private Quaternion pieceFaceBaseLocalRotation;
        private Quaternion fillFaceBaseLocalRotation;
        private bool hasCachedFaceBaseLocalRotations;
        private Vector3 faceDownVisualLocalOffset;
        private bool hasDebugBaseColor;
        private Color debugBaseColor = Color.white;
        private Texture2D pieceTexture;
        private Texture2D fillTexture;

        /// <summary>
        /// Occurs when the tile changes runtime state.
        /// </summary>
        public event Action<TileStateChangedEvent> StateChanged;

        /// <summary>
        /// Occurs when the tile enters or exits the selected state.
        /// </summary>
        public event Action<TileSelectionChangedEvent> SelectionChanged;

        /// <summary>
        /// Gets the unique tile identifier.
        /// </summary>
        public int TileId => tileId;

        /// <summary>
        /// Gets the shared match identifier used to pair this tile.
        /// </summary>
        public int MatchId => matchId;

        /// <summary>
        /// Gets the logical grid coordinate inside the voxel block.
        /// </summary>
        public Vector3Int GridCoordinate => gridCoordinate;

        /// <summary>
        /// Gets the nested shell depth for surface-generated levels, where zero is outermost.
        /// </summary>
        public int SurfaceShellIndex => Mathf.Max(0, surfaceShellIndex);

        /// <summary>
        /// Gets the duplicated runtime block index that owns this tile.
        /// </summary>
        public int RuntimeBlockIndex => Mathf.Max(0, runtimeBlockIndex);

        /// <summary>
        /// Gets the authored board-local position used when restoring the tile from the selection tray.
        /// </summary>
        public Vector3 BoardLocalPosition => boardLocalPosition;

        /// <summary>
        /// Gets the authored board-local Euler rotation used when restoring the tile from the selection tray.
        /// </summary>
        public Vector3 BoardLocalEulerAngles => boardLocalEulerAngles;

        /// <summary>
        /// Gets a value indicating whether the tile is currently parked in the temporary selection tray.
        /// </summary>
        public bool IsBufferedSelection => isBufferedSelection;

        /// <summary>
        /// Gets the primary tile renderer.
        /// </summary>
        public MeshRenderer MeshRenderer => meshRenderer;

        /// <summary>
        /// Gets the optional renderer used to display a match-indicator material on the tile.
        /// </summary>
        public MeshRenderer MatchIndicatorRenderer => matchIndicatorRenderer;

        /// <summary>
        /// Gets the current fill material used to visually identify the tile pair.
        /// </summary>
        public Material FillMaterial
        {
            get
            {
                if (fillRenderer == null)
                {
                    CacheReferences();
                }

                return fillRenderer != null ? fillRenderer.sharedMaterial : null;
            }
        }

        /// <summary>
        /// Gets the current piece texture applied through the renderer property block.
        /// </summary>
        public Texture2D PieceTexture => pieceTexture;

        /// <summary>
        /// Gets the current fill texture applied through the renderer property block.
        /// </summary>
        public Texture2D FillTexture => fillTexture;

        /// <summary>
        /// Gets the visual match key used by gameplay matching.
        /// Tiles sharing the same displayed image may match each other even when their authored match ids differ.
        /// </summary>
        public string VisualMatchKey
        {
            get
            {
                if (fillTexture != null)
                {
                    return $"fill:{fillTexture.name}:{fillTexture.GetEntityId()}";
                }

                if (pieceTexture != null)
                {
                    return $"piece:{pieceTexture.name}:{pieceTexture.GetEntityId()}";
                }

                return $"match:{matchId}";
            }
        }

        /// <summary>
        /// Gets the tile collider used for hit testing.
        /// </summary>
        public Collider TileCollider => tileCollider;

        /// <summary>
        /// Gets the prefab-local direction frame used by the layout authoring tool.
        /// </summary>
        public TileDirectionFrame DirectionFrame => directionFrame;

        /// <summary>
        /// Gets the tile animator reference.
        /// </summary>
        public Animator Animator => animator;

        /// <summary>
        /// Gets the tile outline presenter.
        /// </summary>
        public TileOutlinePresenter Outline => outlinePresenter;

        /// <summary>
        /// Gets the tile visual controller.
        /// </summary>
        public TileVisualController VisualController => visualController;

        /// <summary>
        /// Gets the current tile state.
        /// </summary>
        public TileState State => state;

        /// <summary>
        /// Gets a value indicating whether the tile can currently receive pointer hits.
        /// </summary>
        public bool IsInteractable => state == TileState.Visible || state == TileState.Selected;

        /// <summary>
        /// Gets a value indicating whether the tile has already been removed from the puzzle.
        /// </summary>
        public bool IsRemoved => state == TileState.Removed;

        /// <summary>
        /// Gets a value indicating whether the tile is currently in the matched transition state.
        /// </summary>
        public bool IsMatched => state == TileState.Matched;

        /// <summary>
        /// Gets a value indicating whether the tile is currently shown face-down.
        /// </summary>
        public bool IsFaceDown => isFaceDown;

        /// <summary>
        /// Gets a value indicating whether this tile is marked as a combo tile.
        /// </summary>
        public bool IsComboTile => isComboTile;

        /// <summary>
        /// Applies serialized defaults once the component awakens.
        /// </summary>
        private void Awake()
        {
            CacheReferences();
            Setup();
            ApplyStateToPresentation(state, true);
        }

        /// <summary>
        /// Auto-caches tile references in the editor when values change.
        /// </summary>
        private void OnValidate()
        {
            CacheReferences();
            Setup();
        }

        /// <summary>
        /// Applies the configured material set to the tile's piece and fill renderers.
        /// </summary>
        public void Setup()
        {
           
        }
        public void SetupPieceMaterial(Material pieceMaterial)
        {
            ApplyPieceAppearance(pieceMaterial, ResolveMaterialTexture(pieceMaterial));
        }

        /// <summary>
        /// Applies a new piece texture while keeping the shared piece base material unchanged.
        /// </summary>
        public void SetupPieceTexture(Texture2D texture)
        {
            ApplyPieceAppearance(null, texture);
        }

        public void SetupFillMaterial(Material fillMaterial)
        {
            ApplyFillAppearance(fillMaterial, ResolveMaterialTexture(fillMaterial));
        }

        /// <summary>
        /// Applies a new fill texture while keeping the shared base material unchanged.
        /// </summary>
        public void SetupFillTexture(Texture2D texture)
        {
            ApplyFillAppearance(null, texture);
        }
        /// <summary>
        /// Applies explicit piece and fill materials to the tile renderers.
        /// </summary>
        public void Setup(Material pieceMaterial, Material fillMaterial)
        {
            if (pieceRenderer == null || fillRenderer == null)
            {
                CacheReferences();
            }

            if (pieceRenderer != null)
            {
                ApplyPieceAppearance(pieceMaterial, ResolveMaterialTexture(pieceMaterial));
            }

            if (fillRenderer != null)
            {
                ApplyFillAppearance(fillMaterial, ResolveMaterialTexture(fillMaterial));
            }
        }

        /// <summary>
        /// Applies runtime data generated by a level loader or voxel generator.
        /// </summary>
        /// <param name="runtimeData">Tile runtime data to apply.</param>
        public void ApplyRuntimeData(TileRuntimeData runtimeData)
        {
            if (runtimeData == null)
            {
                return;
            }

            tileId = runtimeData.TileId;
            matchId = runtimeData.MatchId;
            gridCoordinate = runtimeData.GridCoordinate;
            surfaceShellIndex = runtimeData.SurfaceShellIndex;
            runtimeBlockIndex = runtimeData.RuntimeBlockIndex;
            boardLocalPosition = runtimeData.LocalPosition;
            boardLocalEulerAngles = runtimeData.LocalEulerAngles;
            isBufferedSelection = false;
            isFaceDown = false;
            isComboTile = false;
            boardParent = transform.parent;
            transform.localPosition = runtimeData.LocalPosition;
            transform.localRotation = Quaternion.Euler(runtimeData.LocalEulerAngles);
            gameObject.name = $"MahjongTile_{tileId}_{matchId}";
            ApplyComboIndicatorVisibility();
        }

        /// <summary>
        /// Updates the local-space transform driven by the voxel grid.
        /// </summary>
        /// <param name="localPosition">Local-space position to apply.</param>
        /// <param name="localEulerAngles">Local-space rotation to apply.</param>
        public void SetLocalPose(Vector3 localPosition, Vector3 localEulerAngles)
        {
            boardLocalPosition = localPosition;
            boardLocalEulerAngles = localEulerAngles;
            transform.localPosition = localPosition;
            transform.localRotation = Quaternion.Euler(localEulerAngles);
        }

        /// <summary>
        /// Restores the current transform back to the cached board-local pose without changing board metadata.
        /// </summary>
        public void RestoreBoardPose()
        {
            transform.localPosition = boardLocalPosition;
            transform.localRotation = Quaternion.Euler(boardLocalEulerAngles);
        }

        /// <summary>
        /// Detaches the tile from the rotating board hierarchy while preserving world pose.
        /// </summary>
        public void DetachFromBoardParent()
        {
            if (boardParent == null)
            {
                boardParent = transform.parent;
            }

            transform.SetParent(null, true);
        }

        /// <summary>
        /// Reattaches the tile to its authored board parent while preserving world pose.
        /// </summary>
        public void RestoreBoardParent()
        {
            if (boardParent != null)
            {
                transform.SetParent(boardParent, true);
            }
        }

        /// <summary>
        /// Changes the tile visibility state.
        /// </summary>
        /// <param name="isVisible">True to show the tile; otherwise false.</param>
        public void SetVisible(bool isVisible)
        {
            SetState(isVisible ? TileState.Visible : TileState.Hidden);
        }

        /// <summary>
        /// Selects the tile when it is currently visible.
        /// </summary>
        /// <returns>True when the tile becomes selected; otherwise false.</returns>
        public bool TrySelect()
        {
            if (state == TileState.Selected)
            {
                return true;
            }

            if (state != TileState.Visible)
            {
                return false;
            }

            SetState(TileState.Selected);
            return true;
        }

        /// <summary>
        /// Returns a selected tile back to the visible state.
        /// </summary>
        public void Deselect()
        {
            if (state == TileState.Selected)
            {
                SetState(TileState.Visible);
            }
        }

        /// <summary>
        /// Marks the tile as matched and disables further interaction.
        /// </summary>
        public void MarkMatched()
        {
            SetState(TileState.Matched);
        }

        /// <summary>
        /// Marks the tile as removed and hides its presentation.
        /// </summary>
        public void MarkRemoved()
        {
            SetState(TileState.Removed);
        }

        /// <summary>
        /// Resets the tile to a hidden default state.
        /// </summary>
        public void ResetTile()
        {
            isFaceDown = false;
            ApplyFaceVisualState(true);
            SetState(TileState.Hidden, true);
        }

        /// <summary>
        /// Restores a removed or matched tile back into the active puzzle.
        /// </summary>
        /// <param name="isVisible">True to restore the tile as visible; otherwise hidden.</param>
        public void Restore(bool isVisible)
        {
            isBufferedSelection = false;
            SetState(isVisible ? TileState.Visible : TileState.Hidden, true);
        }

        /// <summary>
        /// Sets whether this tile should behave as a combo tile.
        /// </summary>
        /// <param name="comboTile">True to enable combo behavior; otherwise false.</param>
        public void SetComboTile(bool comboTile)
        {
            isComboTile = comboTile;
            ApplyComboIndicatorVisibility();
        }

        /// <summary>
        /// Sets whether the tile should currently display its back face.
        /// </summary>
        public void SetFaceDown(bool faceDown, bool instant = true)
        {
            isFaceDown = faceDown;
            ApplyFaceVisualState(instant);
        }

        /// <summary>
        /// Animates the tile into a face-up presentation.
        /// </summary>
        public Coroutine FlipFaceUp(Action onCompleted = null, bool instant = false)
        {
            return FlipFaceState(false, onCompleted, instant);
        }

        /// <summary>
        /// Animates the tile into a face-down presentation.
        /// </summary>
        public Coroutine FlipFaceDown(Action onCompleted = null, bool instant = false)
        {
            return FlipFaceState(true, onCompleted, instant);
        }

        /// <summary>
        /// Marks whether the tile is currently parked inside the temporary selection tray.
        /// </summary>
        public void SetBufferedSelection(bool isBuffered)
        {
            isBufferedSelection = isBuffered;
            RefreshPresentation(true);
            if (!isBuffered)
            {
                CacheReferences();
            }
        }

        /// <summary>
        /// Updates the logical grid coordinate used by the level systems.
        /// </summary>
        /// <param name="coordinate">New grid coordinate.</param>
        public void SetGridCoordinate(Vector3Int coordinate)
        {
            gridCoordinate = coordinate;
        }

        /// <summary>
        /// Updates the match identifier while keeping the current transform and state untouched.
        /// </summary>
        /// <param name="newMatchId">New shared match identifier.</param>
        public void SetMatchId(int newMatchId)
        {
            matchId = newMatchId;
            gameObject.name = $"MahjongTile_{tileId}_{matchId}";
        }

        /// <summary>
        /// Returns whether this tile may match another tile based on the currently displayed visuals.
        /// </summary>
        public bool HasSameVisualIdentity(MahjongTile other)
        {
            if (other == null || other == this)
            {
                return false;
            }

            return string.Equals(VisualMatchKey, other.VisualMatchKey, StringComparison.Ordinal);
        }

        /// <summary>
        /// Stops any running face flip and snaps to the current face state.
        /// </summary>
        public void StopFaceFlipAnimation(bool snapToCurrentState = true)
        {
            if (faceFlipRoutine != null)
            {
                StopCoroutine(faceFlipRoutine);
                faceFlipRoutine = null;
            }

            if (snapToCurrentState)
            {
                ApplyFaceVisualState(true);
            }
        }

        /// <summary>
        /// Gets the current local scale of the Mahjong piece renderer.
        /// </summary>
        public Vector3 PieceLocalScale
        {
            get
            {
                if (pieceRenderer == null)
                {
                    CacheReferences();
                }

                return pieceRenderer != null ? pieceRenderer.transform.localScale : Vector3.one;
            }
        }

        /// <summary>
        /// Restores the Mahjong piece renderer to its original prefab scale.
        /// </summary>
        public void RestorePieceLocalScale()
        {
            if (pieceRenderer == null)
            {
                CacheReferences();
            }

            if (pieceRenderer == null)
            {
                return;
            }

            CacheFaceBaseLocalRotations();
            Vector3 baseScale = pieceFaceBaseLocalScale == Vector3.zero ? pieceRenderer.transform.localScale : pieceFaceBaseLocalScale;
            pieceRenderer.transform.localScale = baseScale;
        }

        /// <summary>
        /// Applies an exact local scale to the Mahjong piece renderer.
        /// </summary>
        /// <param name="localScale">Local scale to apply.</param>
        public void SetPieceLocalScale(Vector3 localScale)
        {
            if (pieceRenderer == null)
            {
                CacheReferences();
            }

            if (pieceRenderer == null)
            {
                return;
            }

            pieceRenderer.transform.localScale = localScale;
        }

        /// <summary>
        /// Applies a runtime local scale override to the Mahjong piece renderer only.
        /// </summary>
        /// <param name="scaleMultiplier">Per-axis scale multiplier applied on top of the cached piece scale.</param>
        public void SetPieceLocalScaleMultiplier(Vector3 scaleMultiplier)
        {
            if (pieceRenderer == null)
            {
                CacheReferences();
            }

            if (pieceRenderer == null)
            {
                return;
            }

            CacheFaceBaseLocalRotations();
            Vector3 baseScale = pieceFaceBaseLocalScale == Vector3.zero ? pieceRenderer.transform.localScale : pieceFaceBaseLocalScale;
            pieceRenderer.transform.localScale = Vector3.Scale(baseScale, scaleMultiplier);
        }

        /// <summary>
        /// Resolves the fill texture from a source material asset.
        /// </summary> умassistant to=functions.Edit code (commentary) 代񟿿 天天中彩票大奖 《json彩票官网{
        private static Texture2D ResolveMaterialTexture(Material material)
        {
            if (material == null)
            {
                return null;
            }

            if (material.HasProperty(BaseMapId))
            {
                return material.GetTexture(BaseMapId) as Texture2D;
            }

            if (material.HasProperty(MainTexId))
            {
                return material.GetTexture(MainTexId) as Texture2D;
            }

            return null;
        }

        /// <summary>
        /// Applies runtime piece visuals using a shared base material plus a texture property override.
        /// </summary>
        private void ApplyPieceAppearance(Material sourceMaterial, Texture2D texture)
        {
            if (pieceRenderer == null)
            {
                CacheReferences();
            }

            if (pieceRenderer == null)
            {
                return;
            }

            if (pieceRenderer.sharedMaterial == null && sourceMaterial != null)
            {
                pieceRenderer.sharedMaterial = sourceMaterial;
            }

            Material sharedMaterial = pieceRenderer.sharedMaterial;
            if (sharedMaterial == null)
            {
                return;
            }

            pieceTexture = texture;

            if (piecePropertyBlock == null)
            {
                piecePropertyBlock = new MaterialPropertyBlock();
            }

            pieceRenderer.GetPropertyBlock(piecePropertyBlock);
            piecePropertyBlock.Clear();

            if (texture == null)
            {
                pieceRenderer.SetPropertyBlock(piecePropertyBlock);
                return;
            }

            bool applied = false;
            if (sharedMaterial.HasProperty(BaseMapId))
            {
                piecePropertyBlock.SetTexture(BaseMapId, texture);
                applied = true;
            }

            if (sharedMaterial.HasProperty(MainTexId))
            {
                piecePropertyBlock.SetTexture(MainTexId, texture);
                applied = true;
            }

            if (applied)
            {
                pieceRenderer.SetPropertyBlock(piecePropertyBlock);
            }
        }

        /// <summary>
        /// Applies runtime fill visuals using a shared base material plus a texture property override.
        /// </summary>
        private void ApplyFillAppearance(Material sourceMaterial, Texture2D texture)
        {
            if (fillRenderer == null)
            {
                CacheReferences();
            }

            if (fillRenderer == null)
            {
                return;
            }

            if (fillRenderer.sharedMaterial == null && sourceMaterial != null)
            {
                fillRenderer.sharedMaterial = sourceMaterial;
            }

            Material sharedMaterial = fillRenderer.sharedMaterial;
            if (sharedMaterial == null)
            {
                return;
            }

            fillTexture = texture;

            if (fillPropertyBlock == null)
            {
                fillPropertyBlock = new MaterialPropertyBlock();
            }

            fillRenderer.GetPropertyBlock(fillPropertyBlock);
            fillPropertyBlock.Clear();

            if (texture == null)
            {
                ResetFillRendererScale();
                fillRenderer.SetPropertyBlock(fillPropertyBlock);
                return;
            }

            bool applied = false;
            if (sharedMaterial.HasProperty(BaseMapId))
            {
                fillPropertyBlock.SetTexture(BaseMapId, texture);
                applied = true;
            }

            if (sharedMaterial.HasProperty(MainTexId))
            {
                fillPropertyBlock.SetTexture(MainTexId, texture);
                applied = true;
            }

            if (applied)
            {
                fillRenderer.SetPropertyBlock(fillPropertyBlock);
            }

            ApplyFillRendererAspect(texture);
        }

        private void ResetFillRendererScale()
        {
            CacheFaceBaseLocalRotations();
            if (fillRenderer == null)
            {
                return;
            }

            fillRenderer.transform.localScale = fillFaceBaseLocalScale == Vector3.zero
                ? fillRenderer.transform.localScale
                : fillFaceBaseLocalScale;
        }

        private void ApplyFillRendererAspect(Texture2D texture)
        {
            CacheFaceBaseLocalRotations();
            if (fillRenderer == null || texture == null)
            {
                return;
            }

            MeshFilter meshFilter = fillRenderer.GetComponent<MeshFilter>();
            Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (mesh == null)
            {
                ResetFillRendererScale();
                return;
            }

            Vector3 baseScale = fillFaceBaseLocalScale == Vector3.zero ? fillRenderer.transform.localScale : fillFaceBaseLocalScale;
            float meshWidth = Mathf.Abs(mesh.bounds.size.x * baseScale.x);
            float meshHeight = Mathf.Abs(mesh.bounds.size.y * baseScale.y);
            if (meshWidth <= Mathf.Epsilon || meshHeight <= Mathf.Epsilon || texture.height <= 0)
            {
                ResetFillRendererScale();
                return;
            }

            float surfaceAspect = meshWidth / meshHeight;
            float textureAspect = texture.width / (float)texture.height;
            Vector3 adjustedScale = baseScale;

            if (Mathf.Abs(surfaceAspect - textureAspect) <= 0.01f)
            {
                fillRenderer.transform.localScale = adjustedScale;
                return;
            }

            if (textureAspect > surfaceAspect)
            {
                float fittedHeight = meshWidth / textureAspect;
                adjustedScale.y = baseScale.y * (fittedHeight / meshHeight);
            }
            else
            {
                float fittedWidth = meshHeight * textureAspect;
                adjustedScale.x = baseScale.x * (fittedWidth / meshWidth);
            }

            fillRenderer.transform.localScale = adjustedScale;
        }

        /// <summary>
        /// Resolves the offset between the tile root and the visual or collider center.
        /// </summary>
        /// <returns>Root-space offset that should be subtracted from spawn placement.</returns>
        public Vector3 GetPlacementOffset()
        {
            CacheReferences();

            if (TryGetPlacementLocalBounds(out Bounds placementBounds))
            {
                return placementBounds.center;
            }

            return Vector3.zero;
        }

        /// <summary>
        /// Resolves the physical placement size of the tile in root local space.
        /// </summary>
        /// <returns>Root-local bounds size when available; otherwise zero.</returns>
        public Vector3 GetPlacementSize()
        {
            CacheReferences();

            return TryGetPlacementLocalBounds(out Bounds placementBounds)
                ? placementBounds.size
                : Vector3.zero;
        }

        /// <summary>
        /// Applies a runtime base color override used to visually distinguish matched pairs.
        /// </summary>
        /// <param name="color">Base color for this tile instance.</param>
        public void SetDebugMatchColor(Color color)
        {
            if (visualController == null)
            {
                CacheReferences();
            }

            hasDebugBaseColor = true;
            debugBaseColor = color;
            visualController?.SetRuntimeBaseColor(color);
            visualController?.ApplyState(state, true);
            ApplyDirectBaseColorOverride();
        }

        /// <summary>
        /// Clears the runtime base color override and restores the tile's default material colors.
        /// </summary>
        public void ClearDebugMatchColor()
        {
            if (visualController == null)
            {
                CacheReferences();
            }

            hasDebugBaseColor = false;
            debugBaseColor = Color.white;
            visualController?.ClearRuntimeBaseColor();
            visualController?.ApplyState(state, true);

            if (meshRenderer != null)
            {
                meshRenderer.SetPropertyBlock(null);
            }
        }

        /// <summary>
        /// Assigns a material to the optional match-indicator renderer used for gameplay testing.
        /// </summary>
        /// <param name="material">Material to display on the indicator quad, or null to leave the current material unchanged.</param>
        public void SetMatchIndicatorMaterial(Material material)
        {
            if (matchIndicatorRenderer == null)
            {
                CacheReferences();
            }

            if (matchIndicatorRenderer != null && material != null)
            {
                matchIndicatorRenderer.sharedMaterial = material;
            }
        }

        /// <summary>
        /// Enables or disables the temporary hint highlight for this tile.
        /// </summary>
        /// <param name="isHighlighted">True to show hint feedback; otherwise false.</param>
        public void SetHintHighlighted(bool isHighlighted)
        {
            if (visualController == null)
            {
                CacheReferences();
            }

            if (visualController == null)
            {
                return;
            }

            visualController.SetHintHighlighted(isHighlighted);
            visualController.ApplyState(state, true);
        }

        /// <summary>
        /// Enables or disables the dimmed feedback used when the tile cannot currently be selected.
        /// </summary>
        /// <param name="isBlocked">True to darken the tile; otherwise false.</param>
        public void SetSelectionBlockedVisual(bool isBlocked)
        {
            if (visualController == null)
            {
                CacheReferences();
            }

            if (visualController == null)
            {
                return;
            }

            visualController.SetSelectionBlocked(isBlocked);
            visualController.ApplyState(state, true);
        }

        /// <summary>
        /// Plays a short shake to indicate that the tile cannot currently be selected.
        /// </summary>
        public void PlayBlockedTapFeedback()
        {
            if (!isActiveAndEnabled)
            {
                return;
            }

            if (blockedTapFeedbackRoutine != null)
            {
                StopCoroutine(blockedTapFeedbackRoutine);
                SetBlockedOutlineHighlighted(false);
                transform.localPosition = blockedTapBaseLocalPosition;
            }

            blockedTapFeedbackRoutine = StartCoroutine(PlayBlockedTapFeedbackRoutine());
        }

        private void SetBlockedOutlineHighlighted(bool isHighlighted)
        {
            if (outlinePresenter == null)
            {
                CacheReferences();
            }

            if (outlinePresenter != null)
            {
                outlinePresenter.SetBlockedHighlighted(isHighlighted);
            }
        }

        /// <summary>
        /// Changes the runtime tile state and updates visuals and colliders.
        /// </summary>
        /// <param name="newState">New tile state.</param>
        /// <param name="force">True to force the transition even if the state matches.</param>
        public void SetState(TileState newState, bool force = false)
        {
            if (!force && state == newState)
            {
                return;
            }

            if (newState == TileState.Matched || newState == TileState.Removed || newState == TileState.Hidden)
            {
                SetHintHighlighted(false);
            }

            TileState previousState = state;
            state = newState;
            ApplyStateToPresentation(state, false);

            TileStateChangedEvent stateEvent = new TileStateChangedEvent(this, previousState, state);
            StateChanged?.Invoke(stateEvent);

            bool wasSelected = previousState == TileState.Selected;
            bool isSelected = state == TileState.Selected;
            if (wasSelected != isSelected)
            {
                SelectionChanged?.Invoke(new TileSelectionChangedEvent(this, isSelected));
            }
        }

        /// <summary>
        /// Reapplies the current runtime state to colliders and visuals without changing gameplay state.
        /// </summary>
        /// <param name="instant">True to snap the presentation immediately.</param>
        public void RefreshPresentation(bool instant = true)
        {
            ApplyStateToPresentation(state, instant);
        }

        private IEnumerator PlayBlockedTapFeedbackRoutine()
        {
            blockedTapBaseLocalPosition = transform.localPosition;
            const float durationSeconds = 0.24f;
            const float amplitude = 0.03f;
            const float oscillationCount = 4.5f;
            Vector3 localShakeAxis = ResolveBlockedTapLocalShakeAxis();

            SetBlockedOutlineHighlighted(true);

            float elapsed = 0f;
            while (elapsed < durationSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                float normalizedTime = Mathf.Clamp01(elapsed / durationSeconds);
                float damping = 1f - normalizedTime;
                float offset = Mathf.Sin(normalizedTime * oscillationCount * Mathf.PI * 2f) * amplitude * damping;
                transform.localPosition = blockedTapBaseLocalPosition + (localShakeAxis * offset);
                yield return null;
            }

            transform.localPosition = blockedTapBaseLocalPosition;
            SetBlockedOutlineHighlighted(false);
            blockedTapFeedbackRoutine = null;
        }

        private Vector3 ResolveBlockedTapLocalShakeAxis()
        {
            Vector3 worldShakeAxis = ResolveBlockedTapWorldShakeAxis();

            if (transform.parent == null)
            {
                return worldShakeAxis.normalized;
            }

            Vector3 localShakeAxis = transform.parent.InverseTransformDirection(worldShakeAxis);
            if (localShakeAxis.sqrMagnitude <= Mathf.Epsilon)
            {
                return Vector3.right;
            }

            return localShakeAxis.normalized;
        }

        private Vector3 ResolveBlockedTapWorldShakeAxis()
        {
            if (tileCollider is BoxCollider boxCollider)
            {
                Transform colliderTransform = boxCollider.transform;
                Vector3 absoluteScale = new Vector3(
                    Mathf.Abs(colliderTransform.lossyScale.x),
                    Mathf.Abs(colliderTransform.lossyScale.y),
                    Mathf.Abs(colliderTransform.lossyScale.z));
                Vector3 scaledSize = Vector3.Scale(boxCollider.size, absoluteScale);

                Vector3 primaryAxis = colliderTransform.right;
                float primaryLength = Mathf.Abs(scaledSize.x);
                float secondaryLength = Mathf.Abs(scaledSize.z);
                if (secondaryLength > primaryLength)
                {
                    primaryAxis = colliderTransform.forward;
                }

                if (primaryAxis.sqrMagnitude > Mathf.Epsilon)
                {
                    return primaryAxis.normalized;
                }
            }

            if (meshRenderer != null)
            {
                Vector3 rendererSize = meshRenderer.bounds.size;
                Vector3 primaryAxis = transform.right;
                float primaryLength = rendererSize.x;

                if (rendererSize.z > primaryLength)
                {
                    primaryAxis = transform.forward;
                }

                if (primaryAxis.sqrMagnitude > Mathf.Epsilon)
                {
                    return primaryAxis.normalized;
                }
            }

            return transform.right;
        }

        /// <summary>
        /// Caches missing component references from the tile hierarchy.
        /// </summary>
        private void CacheReferences()
        {
            if (visualController == null)
            {
                visualController = GetComponentInChildren<TileVisualController>(true);
            }

            if (meshRenderer == null)
            {
                meshRenderer = visualController != null ? visualController.GetPrimaryRenderer() : GetComponentInChildren<MeshRenderer>(true);
            }

            if (pieceRenderer == null || fillRenderer == null || matchIndicatorRenderer == null || comboIndicatorObject == null || directionFrame == null)
            {
                MeshRenderer[] renderers = GetComponentsInChildren<MeshRenderer>(true);
                for (int index = 0; index < renderers.Length; index++)
                {
                    MeshRenderer renderer = renderers[index];
                    if (renderer == null)
                    {
                        continue;
                    }

                    string rendererName = renderer.gameObject.name;
                    if (pieceRenderer == null && rendererName.Equals("Mahjong Piece", StringComparison.OrdinalIgnoreCase))
                    {
                        pieceRenderer = renderer;
                    }

                    if (fillRenderer == null && rendererName.Equals("Object Fill", StringComparison.OrdinalIgnoreCase))
                    {
                        fillRenderer = renderer;
                    }

                    if (matchIndicatorRenderer == null && renderer != meshRenderer && rendererName.Equals("Quad", StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndicatorRenderer = renderer;
                    }
                }

                if (directionFrame == null)
                {
                    directionFrame = GetComponentInChildren<TileDirectionFrame>(true);
                }

                if (comboIndicatorObject == null)
                {
                    Transform[] childTransforms = GetComponentsInChildren<Transform>(true);
                    for (int index = 0; index < childTransforms.Length; index++)
                    {
                        Transform childTransform = childTransforms[index];
                        if (childTransform == null || childTransform == transform)
                        {
                            continue;
                        }

                        if (childTransform.name.Equals("combo", StringComparison.OrdinalIgnoreCase))
                        {
                            comboIndicatorObject = childTransform.gameObject;
                            break;
                        }
                    }
                }
            }

            if (tileCollider == null)
            {
                tileCollider = GetComponentInChildren<Collider>(true);
            }

            if (animator == null)
            {
                animator = GetComponentInChildren<Animator>(true);
            }

            if (outlinePresenter == null)
            {
                outlinePresenter = visualController != null ? visualController.GetOutlinePresenter() : GetComponentInChildren<TileOutlinePresenter>(true);
            }

            CacheFaceBaseLocalRotations();
        }

        private void ApplyComboIndicatorVisibility()
        {
            if (comboIndicatorObject == null)
            {
                return;
            }

            bool shouldShow = isComboTile && state != TileState.Hidden && state != TileState.Removed;
            if (comboIndicatorObject.activeSelf != shouldShow)
            {
                comboIndicatorObject.SetActive(shouldShow);
            }
        }

        /// <summary>
        /// Tries to resolve a stable bounds source for placement alignment.
        /// </summary>
        /// <param name="placementBounds">Resolved bounds when available.</param>
        /// <returns>True when a non-empty bounds source was found; otherwise false.</returns>
        private bool TryGetPlacementLocalBounds(out Bounds placementBounds)
        {
            placementBounds = default;

            if (meshRenderer != null)
            {
                MeshFilter meshFilter = meshRenderer.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    return TryTransformLocalBounds(meshFilter.transform, meshFilter.sharedMesh.bounds, out placementBounds);
                }
            }

            if (tileCollider is MeshCollider meshCollider && meshCollider.sharedMesh != null)
            {
                return TryTransformLocalBounds(meshCollider.transform, meshCollider.sharedMesh.bounds, out placementBounds);
            }

            if (tileCollider is BoxCollider boxCollider)
            {
                return TryTransformLocalBounds(boxCollider.transform, new Bounds(boxCollider.center, boxCollider.size), out placementBounds);
            }

            return false;
        }

        /// <summary>
        /// Converts a component-local bounds into the tile root local space.
        /// </summary>
        private bool TryTransformLocalBounds(Transform sourceTransform, Bounds sourceBounds, out Bounds transformedBounds)
        {
            transformedBounds = default;
            if (sourceTransform == null || sourceBounds.size.sqrMagnitude <= Mathf.Epsilon)
            {
                return false;
            }

            Matrix4x4 toRootMatrix = transform.worldToLocalMatrix * sourceTransform.localToWorldMatrix;
            Vector3 sourceCenter = sourceBounds.center;
            Vector3 sourceExtents = sourceBounds.extents;

            Vector3[] corners = new Vector3[8];
            int cornerIndex = 0;
            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 corner = sourceCenter + Vector3.Scale(sourceExtents, new Vector3(x, y, z));
                        corners[cornerIndex++] = toRootMatrix.MultiplyPoint3x4(corner);
                    }
                }
            }

            transformedBounds = new Bounds(corners[0], Vector3.zero);
            for (int index = 1; index < corners.Length; index++)
            {
                transformedBounds.Encapsulate(corners[index]);
            }

            return transformedBounds.size.sqrMagnitude > Mathf.Epsilon;
        }

        /// <summary>
        /// Applies runtime state to colliders and visual presentation.
        /// </summary>
        /// <param name="currentState">Tile state to present.</param>
        /// <param name="instant">True to snap visual transitions immediately.</param>
        private void ApplyStateToPresentation(TileState currentState, bool instant)
        {
            if (tileCollider != null)
            {
                tileCollider.enabled = (currentState == TileState.Visible || currentState == TileState.Selected) && !isBufferedSelection && !IsRemoved && !IsMatched;
            }

            if (matchIndicatorRenderer != null)
            {
                matchIndicatorRenderer.enabled = currentState != TileState.Hidden && currentState != TileState.Removed && currentState != TileState.Matched && !isBufferedSelection;
            }

            ApplyComboIndicatorVisibility();

            if (visualController != null)
            {
                visualController.ApplyState(currentState, instant);
            }

            ApplyFaceVisualState(instant);
        }

        private Coroutine FlipFaceState(bool faceDown, Action onCompleted, bool instant)
        {
            isFaceDown = faceDown;

            if (!faceDown && fillRenderer != null)
            {
                fillRenderer.enabled = state != TileState.Hidden && state != TileState.Removed;
            }

            if (instant || !isActiveAndEnabled)
            {
                StopFaceFlipAnimation(false);
                ApplyFaceVisualState(true);
                onCompleted?.Invoke();
                return null;
            }

            StopFaceFlipAnimation(false);
            faceFlipRoutine = StartCoroutine(FlipFaceStateRoutine(faceDown, onCompleted));
            return faceFlipRoutine;
        }

        private IEnumerator FlipFaceStateRoutine(bool faceDown, Action onCompleted)
        {
            CacheFaceBaseLocalRotations();

            if (!faceDown && fillRenderer != null)
            {
                fillRenderer.enabled = state != TileState.Hidden && state != TileState.Removed;
            }

            Vector3 startPiecePosition = pieceRenderer != null ? pieceRenderer.transform.localPosition : Vector3.zero;
            Quaternion startPieceRotation = pieceRenderer != null ? pieceRenderer.transform.localRotation : Quaternion.identity;
            Vector3 startFillPosition = fillRenderer != null ? fillRenderer.transform.localPosition : Vector3.zero;
            Quaternion startFillRotation = fillRenderer != null ? fillRenderer.transform.localRotation : Quaternion.identity;
            GetFaceTargetPose(
                faceDown,
                out Vector3 targetPiecePosition,
                out Quaternion targetPieceRotation,
                out Vector3 targetFillPosition,
                out Quaternion targetFillRotation);
            float elapsed = 0f;

            while (elapsed < FaceFlipDurationSeconds)
            {
                elapsed += Time.unscaledDeltaTime > 0f ? Time.unscaledDeltaTime : Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / FaceFlipDurationSeconds);
                float easedT = 1f - Mathf.Pow(1f - t, 3f);
                if (pieceRenderer != null)
                {
                    pieceRenderer.transform.localPosition = Vector3.LerpUnclamped(startPiecePosition, targetPiecePosition, easedT);
                    pieceRenderer.transform.localRotation = Quaternion.SlerpUnclamped(startPieceRotation, targetPieceRotation, easedT);
                }

                if (fillRenderer != null)
                {
                    fillRenderer.transform.localPosition = Vector3.LerpUnclamped(startFillPosition, targetFillPosition, easedT);
                    fillRenderer.transform.localRotation = Quaternion.SlerpUnclamped(startFillRotation, targetFillRotation, easedT);
                }

                yield return null;
            }

            faceFlipRoutine = null;
            ApplyFaceRotations(faceDown);
            ApplyFaceRendererVisibility(faceDown);
            onCompleted?.Invoke();
        }

        private void ApplyFaceVisualState(bool instant)
        {
            if (instant && faceFlipRoutine != null)
            {
                StopCoroutine(faceFlipRoutine);
                faceFlipRoutine = null;
            }

            if (faceFlipRoutine == null)
            {
                CacheFaceBaseLocalRotations();
                ApplyFaceRotations(isFaceDown);
                ApplyFaceRendererVisibility(isFaceDown);
            }
        }

        private void CacheFaceBaseLocalRotations()
        {
            if (hasCachedFaceBaseLocalRotations)
            {
                return;
            }

            if (pieceRenderer != null)
            {
                pieceFaceBaseLocalPosition = pieceRenderer.transform.localPosition;
                pieceFaceBaseLocalScale = pieceRenderer.transform.localScale;
                pieceFaceBaseLocalRotation = pieceRenderer.transform.localRotation;
            }

            if (fillRenderer != null)
            {
                fillFaceBaseLocalPosition = fillRenderer.transform.localPosition;
                fillFaceBaseLocalScale = fillRenderer.transform.localScale;
                fillFaceBaseLocalRotation = fillRenderer.transform.localRotation;
            }

            faceDownVisualLocalOffset = ResolveFaceDownVisualLocalOffset();

            hasCachedFaceBaseLocalRotations = pieceRenderer != null || fillRenderer != null;
        }

        private void ApplyFaceRotations(bool faceDown)
        {
            GetFaceTargetPose(
                faceDown,
                out Vector3 piecePosition,
                out Quaternion pieceRotation,
                out Vector3 fillPosition,
                out Quaternion fillRotation);

            if (pieceRenderer != null)
            {
                pieceRenderer.transform.localPosition = piecePosition;
                pieceRenderer.transform.localRotation = pieceRotation;
            }

            if (fillRenderer != null)
            {
                fillRenderer.transform.localPosition = fillPosition;
                fillRenderer.transform.localRotation = fillRotation;
            }
        }

        private void GetFaceTargetRotations(bool faceDown, out Quaternion pieceRotation, out Quaternion fillRotation)
        {
            CacheFaceBaseLocalRotations();
            Quaternion deltaRotation = faceDown ? FaceFlipDeltaRotation : Quaternion.identity;
            pieceRotation = pieceFaceBaseLocalRotation * deltaRotation;
            fillRotation = fillFaceBaseLocalRotation * deltaRotation;
        }

        private void GetFaceTargetPose(
            bool faceDown,
            out Vector3 piecePosition,
            out Quaternion pieceRotation,
            out Vector3 fillPosition,
            out Quaternion fillRotation)
        {
            GetFaceTargetRotations(faceDown, out pieceRotation, out fillRotation);

            Vector3 localOffset = faceDown ? faceDownVisualLocalOffset : Vector3.zero;
            piecePosition = pieceFaceBaseLocalPosition + localOffset;
            fillPosition = fillFaceBaseLocalPosition + localOffset;
        }

        private Vector3 ResolveFaceDownVisualLocalOffset()
        {
            if (pieceRenderer == null)
            {
                return Vector3.zero;
            }

            Quaternion faceDownRotation = pieceFaceBaseLocalRotation * FaceFlipDeltaRotation;
            if (!TryGetRendererLocalBounds(pieceRenderer, pieceFaceBaseLocalPosition, pieceFaceBaseLocalRotation, out Bounds faceUpBounds) ||
                !TryGetRendererLocalBounds(pieceRenderer, pieceFaceBaseLocalPosition, faceDownRotation, out Bounds faceDownBounds))
            {
                return Vector3.zero;
            }

            Vector3 faceUpAnchor = GetFaceAlignmentAnchor(faceUpBounds);
            Vector3 faceDownAnchor = GetFaceAlignmentAnchor(faceDownBounds);
            return faceUpAnchor - faceDownAnchor;
        }

        private static Vector3 GetFaceAlignmentAnchor(Bounds bounds)
        {
            return new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        }

        private bool TryGetRendererLocalBounds(MeshRenderer renderer, Vector3 localPosition, Quaternion localRotation, out Bounds transformedBounds)
        {
            transformedBounds = default;
            if (renderer == null)
            {
                return false;
            }

            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                return false;
            }

            Transform rendererTransform = renderer.transform;
            Transform parentTransform = rendererTransform.parent;
            Matrix4x4 localToWorldMatrix = parentTransform != null
                ? parentTransform.localToWorldMatrix * Matrix4x4.TRS(localPosition, localRotation, rendererTransform.localScale)
                : Matrix4x4.TRS(localPosition, localRotation, rendererTransform.localScale);

            Matrix4x4 toRootMatrix = transform.worldToLocalMatrix * localToWorldMatrix;
            return TryTransformLocalBounds(toRootMatrix, meshFilter.sharedMesh.bounds, out transformedBounds);
        }

        private bool TryTransformLocalBounds(Matrix4x4 toRootMatrix, Bounds sourceBounds, out Bounds transformedBounds)
        {
            transformedBounds = default;
            if (sourceBounds.size.sqrMagnitude <= Mathf.Epsilon)
            {
                return false;
            }

            Vector3 sourceCenter = sourceBounds.center;
            Vector3 sourceExtents = sourceBounds.extents;

            Vector3[] corners = new Vector3[8];
            int cornerIndex = 0;
            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 corner = sourceCenter + Vector3.Scale(sourceExtents, new Vector3(x, y, z));
                        corners[cornerIndex++] = toRootMatrix.MultiplyPoint3x4(corner);
                    }
                }
            }

            transformedBounds = new Bounds(corners[0], Vector3.zero);
            for (int index = 1; index < corners.Length; index++)
            {
                transformedBounds.Encapsulate(corners[index]);
            }

            return transformedBounds.size.sqrMagnitude > Mathf.Epsilon;
        }

        private void ApplyFaceRendererVisibility(bool faceDown)
        {
           if (fillRenderer == null)
            {
                return;
            }

            bool tileVisible = state != TileState.Hidden && state != TileState.Removed && state != TileState.Matched && !isBufferedSelection;
            bool shouldShowFill = tileVisible && !faceDown;
            if (fillRenderer.enabled != shouldShowFill)
            {
                fillRenderer.enabled = shouldShowFill;
            }
        }

        /// <summary>
        /// Reapplies the runtime base color directly onto the primary renderer so debug tinting stays visible.
        /// </summary>
        private void ApplyDirectBaseColorOverride()
        {
            if (!hasDebugBaseColor)
            {
                return;
            }

            if (meshRenderer == null)
            {
                CacheReferences();
            }

            if (meshRenderer == null)
            {
                return;
            }

            Material sharedMaterial = meshRenderer.sharedMaterial;
            if (sharedMaterial == null)
            {
                return;
            }

            if (baseColorPropertyBlock == null)
            {
                baseColorPropertyBlock = new MaterialPropertyBlock();
            }

            meshRenderer.GetPropertyBlock(baseColorPropertyBlock);

            bool applied = false;
            if (sharedMaterial.HasProperty(BaseColorId))
            {
                baseColorPropertyBlock.SetColor(BaseColorId, debugBaseColor);
                applied = true;
            }

            if (sharedMaterial.HasProperty(ColorId))
            {
                baseColorPropertyBlock.SetColor(ColorId, debugBaseColor);
                applied = true;
            }

            if (applied)
            {
                meshRenderer.SetPropertyBlock(baseColorPropertyBlock);
            }
        }
    }
}
