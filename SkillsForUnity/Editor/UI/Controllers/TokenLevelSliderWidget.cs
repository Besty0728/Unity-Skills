using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnitySkills
{
    /// <summary>
    /// Encapsulates the custom capsule Token consumption level slider widget, supporting
    /// live drag, preset clicks, galaxy gradient, stardust animation, and advanced controls foldout.
    /// </summary>
    public sealed class TokenLevelSliderWidget : IDisposable
    {
        private static readonly SurfaceProfileKind[] SurfaceProfileOrder =
        {
            SurfaceProfileKind.Full,
            SurfaceProfileKind.Guide,
            SurfaceProfileKind.NoSceneAuthoring,
        };

        private readonly VisualElement _root;
        private readonly Label _tokenLevelLabel;
        private readonly Label _tokenLevelValue;
        private readonly SliderInt _tokenLevelSlider;
        private readonly VisualElement _tokenLevelTrack;
        private readonly Label _tokenLevelScaleMinimal;
        private readonly Label _tokenLevelScaleStandard;
        private readonly Label _tokenLevelScaleFull;
        private readonly Label _tokenLevelScaleMaximum;
        private readonly Label _tokenLevelHint;
        private readonly Foldout _tokenLevelAdvancedFoldout;
        private readonly Label _tokenLevelSurfaceProfileLabel;
        private readonly DropdownField _tokenLevelSurfaceProfile;
        private readonly Label _tokenLevelSurfaceProfileHint;
        private readonly Label _tokenLevelSummaryTruncateLabel;
        private readonly Toggle _tokenLevelSummaryTruncate;
        private readonly Label _tokenLevelSummaryTruncateHint;
        private readonly Label _tokenLevelSummaryPageSizeLabel;
        private readonly IntegerField _tokenLevelSummaryPageSize;
        private readonly Label _tokenLevelSummaryPageSizeHint;
        private readonly Label _tokenLevelSummaryPageSizeDisabledHint;
        private readonly VisualElement _tokenLevelMaximumEffect;

        private bool _isDragging;
        private bool _tokenSettingsWriteInProgress;
        private IVisualElementScheduledItem _maximumEffectAnimation;
        private float _maximumEffectTime;
        private const float ActiveEffectDuration = 3.5f; // Play 60fps dynamic animation for 3.5s then sleep
        private float _effectActiveTimer;
        private TokenLevel _previousLevel = (TokenLevel)(-1);
        private bool _disposed;

        private readonly List<Particle> _galaxyParticles = new List<Particle>
        {
            new Particle { baseX = 0.07f, yOffset = -0.38f, speed = 0.07f, twinkleSpeed = 3.2f, size = 1.2f, baseAlpha = 0.75f, colorType = 0, shapeType = 1 }, // Diamond
            new Particle { baseX = 0.15f, yOffset =  0.35f, speed = 0.11f, twinkleSpeed = 2.4f, size = 1.4f, baseAlpha = 0.85f, colorType = 1, shapeType = 0 }, // Dot
            new Particle { baseX = 0.24f, yOffset = -0.22f, speed = 0.05f, twinkleSpeed = 4.1f, size = 1.5f, baseAlpha = 0.90f, colorType = 2, shapeType = 2 }, // 4-Point Star
            new Particle { baseX = 0.32f, yOffset =  0.45f, speed = 0.13f, twinkleSpeed = 2.8f, size = 1.3f, baseAlpha = 0.80f, colorType = 0, shapeType = 1 }, // Diamond
            new Particle { baseX = 0.40f, yOffset = -0.42f, speed = 0.08f, twinkleSpeed = 3.6f, size = 1.0f, baseAlpha = 0.70f, colorType = 1, shapeType = 0 }, // Dot
            new Particle { baseX = 0.49f, yOffset =  0.18f, speed = 0.10f, twinkleSpeed = 2.1f, size = 1.6f, baseAlpha = 0.95f, colorType = 2, shapeType = 1 }, // Diamond
            new Particle { baseX = 0.58f, yOffset = -0.30f, speed = 0.14f, twinkleSpeed = 3.9f, size = 1.4f, baseAlpha = 0.85f, colorType = 0, shapeType = 2 }, // 4-Point Star
            new Particle { baseX = 0.66f, yOffset =  0.40f, speed = 0.06f, twinkleSpeed = 2.5f, size = 1.1f, baseAlpha = 0.65f, colorType = 1, shapeType = 0 }, // Dot
            new Particle { baseX = 0.74f, yOffset = -0.18f, speed = 0.12f, twinkleSpeed = 4.5f, size = 1.5f, baseAlpha = 0.90f, colorType = 2, shapeType = 1 }, // Diamond
            new Particle { baseX = 0.82f, yOffset =  0.28f, speed = 0.09f, twinkleSpeed = 3.0f, size = 1.3f, baseAlpha = 0.75f, colorType = 0, shapeType = 2 }, // 4-Point Star
            new Particle { baseX = 0.90f, yOffset = -0.32f, speed = 0.15f, twinkleSpeed = 2.7f, size = 1.4f, baseAlpha = 0.85f, colorType = 1, shapeType = 1 }, // Diamond
            new Particle { baseX = 0.96f, yOffset =  0.20f, speed = 0.08f, twinkleSpeed = 3.8f, size = 1.1f, baseAlpha = 0.70f, colorType = 2, shapeType = 0 }  // Dot
        };

        private struct Particle
        {
            public float baseX;
            public float yOffset;
            public float speed;
            public float twinkleSpeed;
            public float size;
            public float baseAlpha;
            public int   colorType; // 0: Diamond White, 1: Electric Sky Cyan, 2: Sakura Pink
            public int   shapeType; // 0: Dot, 1: Rhombus/Diamond, 2: 4-Pointed Sparkle Star
        }

        public TokenLevelSliderWidget(VisualElement root)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));

            _tokenLevelLabel = _root.Q<Label>("token-level-label");
            _tokenLevelValue = _root.Q<Label>("token-level-value");
            _tokenLevelSlider = _root.Q<SliderInt>("token-level-slider");
            _tokenLevelTrack = _root.Q<VisualElement>("token-level-track");
            _tokenLevelScaleMinimal = _root.Q<Label>("token-level-scale-minimal");
            _tokenLevelScaleStandard = _root.Q<Label>("token-level-scale-standard");
            _tokenLevelScaleFull = _root.Q<Label>("token-level-scale-full");
            _tokenLevelScaleMaximum = _root.Q<Label>("token-level-scale-maximum");
            _tokenLevelHint = _root.Q<Label>("token-level-hint");
            _tokenLevelAdvancedFoldout = _root.Q<Foldout>("token-level-advanced-foldout");
            _tokenLevelSurfaceProfileLabel = _root.Q<Label>("token-level-surface-profile-label");
            _tokenLevelSurfaceProfile = _root.Q<DropdownField>("token-level-surface-profile");
            _tokenLevelSurfaceProfileHint = _root.Q<Label>("token-level-surface-profile-hint");
            _tokenLevelSummaryTruncateLabel = _root.Q<Label>("token-level-summary-truncate-label");
            _tokenLevelSummaryTruncate = _root.Q<Toggle>("token-level-summary-truncate");
            _tokenLevelSummaryTruncateHint = _root.Q<Label>("token-level-summary-truncate-hint");
            _tokenLevelSummaryPageSizeLabel = _root.Q<Label>("token-level-summary-page-size-label");
            _tokenLevelSummaryPageSize = _root.Q<IntegerField>("token-level-summary-page-size");
            _tokenLevelSummaryPageSizeHint = _root.Q<Label>("token-level-summary-page-size-hint");
            _tokenLevelSummaryPageSizeDisabledHint = _root.Q<Label>("token-level-summary-page-size-disabled-hint");
            _tokenLevelMaximumEffect = _root.Q<VisualElement>("token-level-maximum-effect");

            BindEvents();
            RefreshTokenLevelLocalization();
            RefreshTokenLevelUi();

            SkillsTokenLevel.OnChanged += OnTokenLevelChanged;
            SkillsLocalization.LanguageChanged += RefreshTokenLevelLocalization;
            _root.RegisterCallback<AttachToPanelEvent>(OnRootAttached);
            _root.RegisterCallback<DetachFromPanelEvent>(OnRootDetached);
        }

        public VisualElement Track => _tokenLevelTrack;

        private void BindEvents()
        {
            if (_tokenLevelTrack != null)
            {
                _tokenLevelTrack.RegisterCallback<PointerDownEvent>(OnTrackPointerDown);
                _tokenLevelTrack.RegisterCallback<PointerMoveEvent>(OnTrackPointerMove);
                _tokenLevelTrack.RegisterCallback<PointerUpEvent>(OnTrackPointerUp);
                _tokenLevelTrack.RegisterCallback<PointerCancelEvent>(OnTrackPointerCancel);
                _tokenLevelTrack.RegisterCallback<PointerCaptureOutEvent>(OnTrackPointerCaptureOut);
                _tokenLevelTrack.RegisterCallback<PointerEnterEvent>(_ => { if (SkillsTokenLevel.Current == TokenLevel.Maximum) TriggerMaximumEffect(); });
                _tokenLevelTrack.RegisterCallback<ClickEvent>(OnTrackClicked);
                _tokenLevelTrack.generateVisualContent += DrawTokenSlider;
                _tokenLevelTrack.RegisterCallback<GeometryChangedEvent>(_ => _tokenLevelTrack.MarkDirtyRepaint());
            }

            if (_tokenLevelScaleMinimal != null)
                _tokenLevelScaleMinimal.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) { evt.StopPropagation(); ApplyTokenPreset(TokenLevel.Minimal); } });
            if (_tokenLevelScaleStandard != null)
                _tokenLevelScaleStandard.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) { evt.StopPropagation(); ApplyTokenPreset(TokenLevel.Standard); } });
            if (_tokenLevelScaleFull != null)
                _tokenLevelScaleFull.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) { evt.StopPropagation(); ApplyTokenPreset(TokenLevel.Full); } });
            if (_tokenLevelScaleMaximum != null)
                _tokenLevelScaleMaximum.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) { evt.StopPropagation(); ApplyTokenPreset(TokenLevel.Maximum); } });

            if (_tokenLevelSlider != null)
            {
                _tokenLevelSlider.lowValue = 0;
                _tokenLevelSlider.highValue = 3;
                _tokenLevelSlider.RegisterValueChangedCallback(evt =>
                {
                    var level = (TokenLevel)Mathf.Clamp(evt.newValue, 0, 3);
                    if (!SkillsTokenLevel.TryGetPreset(level, out _)) return;
                    ApplyTokenPreset(level);
                });
            }

            if (_tokenLevelSurfaceProfile != null)
            {
                _tokenLevelSurfaceProfile.RegisterValueChangedCallback(evt =>
                {
                    int index = _tokenLevelSurfaceProfile.choices.IndexOf(evt.newValue);
                    if (index < 0 || index >= SurfaceProfileOrder.Length) return;
                    var newProfile = SurfaceProfileOrder[index];
                    if (SkillsTokenLevel.SurfaceProfile != newProfile)
                        SkillsTokenLevel.SurfaceProfile = newProfile;
                    RefreshTokenLevelUi();
                });
            }

            if (_tokenLevelSummaryTruncate != null)
            {
                _tokenLevelSummaryTruncate.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue != SkillsTokenLevel.SummaryAutoTruncate)
                        SkillsTokenLevel.SummaryAutoTruncate = evt.newValue;
                    RefreshTokenLevelUi();
                });
            }

            if (_tokenLevelSummaryPageSize != null)
            {
                _tokenLevelSummaryPageSize.RegisterValueChangedCallback(evt =>
                {
                    int normalized = Math.Max(1, evt.newValue);
                    if (normalized != evt.newValue)
                        _tokenLevelSummaryPageSize.SetValueWithoutNotify(normalized);
                    if (normalized != SkillsTokenLevel.SummaryPageSize)
                        SkillsTokenLevel.SummaryPageSize = normalized;
                    RefreshTokenLevelUi();
                });
            }
        }

        private void OnTrackPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || _tokenLevelTrack == null) return;
            _tokenLevelTrack.CapturePointer(evt.pointerId);
            _isDragging = true;
            evt.StopPropagation();
            UpdateFromWorldPointer(evt.position);
        }

        private void OnTrackPointerMove(PointerMoveEvent evt)
        {
            if (!_isDragging || _tokenLevelTrack == null || !_tokenLevelTrack.HasPointerCapture(evt.pointerId)) return;
            evt.StopPropagation();
            UpdateFromWorldPointer(evt.position);
        }

        private void OnTrackPointerUp(PointerUpEvent evt)
        {
            if (evt.button != 0 || _tokenLevelTrack == null) return;
            if (_tokenLevelTrack.HasPointerCapture(evt.pointerId))
                _tokenLevelTrack.ReleasePointer(evt.pointerId);
            _isDragging = false;
            evt.StopPropagation();
            UpdateFromWorldPointer(evt.position);
        }

        private void OnTrackPointerCancel(PointerCancelEvent evt)
        {
            if (_tokenLevelTrack != null && _tokenLevelTrack.HasPointerCapture(evt.pointerId))
                _tokenLevelTrack.ReleasePointer(evt.pointerId);
            _isDragging = false;
        }

        private void OnTrackPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            _isDragging = false;
            _tokenLevelTrack?.MarkDirtyRepaint();
        }

        private void OnTrackClicked(ClickEvent evt)
        {
            if (evt.button != 0 || _tokenLevelTrack == null) return;
            evt.StopPropagation();
            UpdateFromWorldPointer(evt.position);
        }

        private void UpdateFromWorldPointer(Vector2 worldPos)
        {
            if (_tokenLevelTrack == null) return;
            float localX = _tokenLevelTrack.WorldToLocal(worldPos).x;
            float r = _tokenLevelTrack.contentRect.height * 0.5f;
            float travel = Mathf.Max(1f, _tokenLevelTrack.contentRect.width - 2f * r);
            float t = Mathf.Clamp01((localX - r) / travel);
            int tier = Mathf.Clamp(Mathf.RoundToInt(t * 3f), 0, 3);
            var targetPreset = (TokenLevel)tier;
            ApplyTokenPreset(targetPreset);
        }

        public void ApplyTokenPreset(TokenLevel level)
        {
            if (_tokenSettingsWriteInProgress) return;
            _tokenSettingsWriteInProgress = true;
            try
            {
                SkillsTokenLevel.TryApplyPreset(level);
            }
            finally
            {
                _tokenSettingsWriteInProgress = false;
                RefreshTokenLevelUi();
            }
        }

        private void OnTokenLevelChanged()
        {
            if (_disposed) return;
            RefreshTokenLevelUi();
        }

        private void OnRootAttached(AttachToPanelEvent _)
        {
            if (_disposed) return;
            RefreshTokenLevelUi();
        }

        private void OnRootDetached(DetachFromPanelEvent _)
        {
            _maximumEffectAnimation?.Pause();
        }

        public void RefreshTokenLevelLocalization()
        {
            if (_disposed) return;
            if (_tokenLevelLabel != null) _tokenLevelLabel.text = SkillsLocalization.Get("token_level");
            if (_tokenLevelScaleMinimal != null) _tokenLevelScaleMinimal.text = SkillsLocalization.Get("token_level_minimal");
            if (_tokenLevelScaleStandard != null) _tokenLevelScaleStandard.text = SkillsLocalization.Get("token_level_standard");
            if (_tokenLevelScaleFull != null) _tokenLevelScaleFull.text = SkillsLocalization.Get("token_level_full");
            if (_tokenLevelScaleMaximum != null) _tokenLevelScaleMaximum.text = SkillsLocalization.Get("token_level_maximum");
            if (_tokenLevelAdvancedFoldout != null) _tokenLevelAdvancedFoldout.text = SkillsLocalization.Get("token_level_advanced");
            if (_tokenLevelSurfaceProfileLabel != null) _tokenLevelSurfaceProfileLabel.text = SkillsLocalization.Get("token_level_surface_profile");
            ApplySurfaceProfileHint();
            if (_tokenLevelSummaryTruncateLabel != null) _tokenLevelSummaryTruncateLabel.text = SkillsLocalization.Get("token_level_summary_truncate");
            if (_tokenLevelSummaryTruncateHint != null) _tokenLevelSummaryTruncateHint.text = SkillsLocalization.Get("token_level_summary_truncate_hint");
            if (_tokenLevelSummaryPageSizeLabel != null) _tokenLevelSummaryPageSizeLabel.text = SkillsLocalization.Get("token_level_summary_page_size");
            if (_tokenLevelSummaryPageSizeHint != null) _tokenLevelSummaryPageSizeHint.text = SkillsLocalization.Get("token_level_summary_page_size_hint");
            if (_tokenLevelSummaryPageSizeDisabledHint != null) _tokenLevelSummaryPageSizeDisabledHint.text = SkillsLocalization.Get("token_level_maximum_page_size_hint");

            if (_tokenLevelSurfaceProfile != null)
            {
                var choices = new List<string>
                {
                    SkillsLocalization.Get("surface_profile_full"),
                    SkillsLocalization.Get("surface_profile_guide"),
                    SkillsLocalization.Get("surface_profile_no_scene_authoring")
                };
                _tokenLevelSurfaceProfile.choices = choices;
            }

            RefreshTokenLevelUi();
        }

        private void ApplySurfaceProfileHint()
        {
            if (_tokenLevelSurfaceProfileHint == null) return;
            switch (SkillsTokenLevel.SurfaceProfile)
            {
                case SurfaceProfileKind.Full:
                    _tokenLevelSurfaceProfileHint.text = SkillsLocalization.Get("surface_profile_full_hint");
                    break;
                case SurfaceProfileKind.Guide:
                    _tokenLevelSurfaceProfileHint.text = SkillsLocalization.Get("surface_profile_guide_hint");
                    break;
                case SurfaceProfileKind.NoSceneAuthoring:
                    _tokenLevelSurfaceProfileHint.text = SkillsLocalization.Get("surface_profile_no_scene_authoring_hint");
                    break;
                default:
                    _tokenLevelSurfaceProfileHint.text = SkillsLocalization.Get("surface_profile_tooltip");
                    break;
            }
        }

        public void RefreshTokenLevelUi()
        {
            if (_disposed) return;
            var level = SkillsTokenLevel.Current;
            int sliderValue = level == TokenLevel.Custom ? -1 : (int)level;

            if (_tokenLevelSlider != null && sliderValue >= 0)
                _tokenLevelSlider.SetValueWithoutNotify(sliderValue);

            if (_tokenLevelValue != null)
            {
                string valueText;
                string badgeModifier;
                switch (level)
                {
                    case TokenLevel.Minimal:
                        valueText = SkillsLocalization.Get("token_level_minimal");
                        badgeModifier = "token-level-value--minimal";
                        break;
                    case TokenLevel.Standard:
                        valueText = SkillsLocalization.Get("token_level_standard");
                        badgeModifier = "token-level-value--standard";
                        break;
                    case TokenLevel.Full:
                        valueText = SkillsLocalization.Get("token_level_full");
                        badgeModifier = "token-level-value--full";
                        break;
                    case TokenLevel.Maximum:
                        valueText = SkillsLocalization.Get("token_level_maximum");
                        badgeModifier = "token-level-value--maximum";
                        break;
                    default:
                        valueText = SkillsLocalization.Get("token_level_custom");
                        badgeModifier = "token-level-value--custom";
                        break;
                }

                _tokenLevelValue.text = valueText;
                _tokenLevelValue.EnableInClassList("token-level-value--minimal", badgeModifier == "token-level-value--minimal");
                _tokenLevelValue.EnableInClassList("token-level-value--standard", badgeModifier == "token-level-value--standard");
                _tokenLevelValue.EnableInClassList("token-level-value--full", badgeModifier == "token-level-value--full");
                _tokenLevelValue.EnableInClassList("token-level-value--maximum", badgeModifier == "token-level-value--maximum");
                _tokenLevelValue.EnableInClassList("token-level-value--custom", badgeModifier == "token-level-value--custom");
            }

            if (_tokenLevelHint != null)
            {
                _tokenLevelHint.text = level == TokenLevel.Custom
                    ? SkillsLocalization.Get("token_level_custom_hint")
                    : SkillsLocalization.Get("token_level_hint");
            }

            _tokenLevelScaleMinimal?.EnableInClassList("token-level-scale__label--active", level == TokenLevel.Minimal);
            _tokenLevelScaleStandard?.EnableInClassList("token-level-scale__label--active", level == TokenLevel.Standard);
            _tokenLevelScaleFull?.EnableInClassList("token-level-scale__label--active", level == TokenLevel.Full);
            _tokenLevelScaleMaximum?.EnableInClassList("token-level-scale__label--active", level == TokenLevel.Maximum);

            _tokenLevelScaleMinimal?.EnableInClassList("token-level-scale__label--minimal", true);
            _tokenLevelScaleStandard?.EnableInClassList("token-level-scale__label--standard", true);
            _tokenLevelScaleFull?.EnableInClassList("token-level-scale__label--full", true);
            _tokenLevelScaleMaximum?.EnableInClassList("token-level-scale__label--maximum", true);

            ApplySurfaceProfileHint();

            if (_tokenLevelSurfaceProfile != null)
            {
                int profileIndex = Array.IndexOf(SurfaceProfileOrder, SkillsTokenLevel.SurfaceProfile);
                if (profileIndex >= 0 && profileIndex < _tokenLevelSurfaceProfile.choices.Count)
                    _tokenLevelSurfaceProfile.SetValueWithoutNotify(_tokenLevelSurfaceProfile.choices[profileIndex]);
            }

            _tokenLevelSummaryTruncate?.SetValueWithoutNotify(SkillsTokenLevel.SummaryAutoTruncate);
            _tokenLevelSummaryPageSize?.SetValueWithoutNotify(SkillsTokenLevel.SummaryPageSize);

            bool maximum = level == TokenLevel.Maximum;
            _tokenLevelSummaryPageSize?.SetEnabled(!maximum);
            if (_tokenLevelSummaryPageSizeDisabledHint != null)
                _tokenLevelSummaryPageSizeDisabledHint.style.display = maximum ? DisplayStyle.Flex : DisplayStyle.None;

            _tokenLevelTrack?.MarkDirtyRepaint();

            bool levelSwitchedToMaximum = (_previousLevel != TokenLevel.Maximum && level == TokenLevel.Maximum);
            _previousLevel = level;

            if (level == TokenLevel.Maximum && _root.panel != null)
            {
                if (levelSwitchedToMaximum)
                {
                    TriggerMaximumEffect();
                }
            }
            else
            {
                _effectActiveTimer = 0f;
                _maximumEffectAnimation?.Pause();
            }
        }

        private void TriggerMaximumEffect()
        {
            if (_disposed || _root.panel == null) return;
            _effectActiveTimer = ActiveEffectDuration;

            if (_maximumEffectAnimation == null)
            {
                _maximumEffectAnimation = _root.schedule.Execute(() =>
                {
                    if (_disposed || SkillsTokenLevel.Current != TokenLevel.Maximum)
                    {
                        _maximumEffectAnimation?.Pause();
                        return;
                    }

                    _maximumEffectTime += 0.0166f;
                    _effectActiveTimer -= 0.0166f;
                    _tokenLevelTrack?.MarkDirtyRepaint();

                    // Settle into pristine static state after duration to avoid unnecessary CPU/GPU usage
                    if (_effectActiveTimer <= 0f)
                    {
                        _effectActiveTimer = 0f;
                        _maximumEffectAnimation?.Pause();
                    }
                }).Every(16); // True 60 FPS
            }
            else
            {
                _maximumEffectAnimation.Resume();
            }
        }

        private void DrawTokenSlider(MeshGenerationContext mgc)
        {
            if (_tokenLevelTrack == null) return;
            Rect rect = _tokenLevelTrack.contentRect;
            if (rect.width <= 0 || rect.height <= 0) return;

            var painter = mgc.painter2D;
            var level = SkillsTokenLevel.Current;
            float r = rect.height * 0.5f;

            float xMin = rect.xMin;
            float xMax = rect.xMax;
            float xCenterLeft = xMin + r;
            float xCenterRight = xMax - r;
            float travel = Mathf.Max(1f, xCenterRight - xCenterLeft);

            int currentTier = level == TokenLevel.Custom ? 2 : (int)level;
            float thumbX = xCenterLeft + (currentTier / 3f) * travel;
            Vector2 thumbCenter = new Vector2(thumbX, rect.yMin + r);

            // 1. Dark Background Track
            painter.fillColor = new Color(0.13f, 0.13f, 0.15f, 1f); // #222226
            painter.BeginPath();
            painter.Arc(new Vector2(xCenterLeft, rect.yMin + r), r, 90f, 270f);
            painter.Arc(new Vector2(xCenterRight, rect.yMin + r), r, 270f, 450f);
            painter.ClosePath();
            painter.Fill();

            // 2. Track border outline & Maximum ambient glow
            if (level == TokenLevel.Maximum)
            {
                // Breathing ambient glow halo around the entire track
                float breathe = 0.5f + 0.5f * Mathf.Sin(_maximumEffectTime * 2.8f);
                float glowAlpha = 0.12f + breathe * 0.08f;
                painter.strokeColor = new Color(0.76f, 0.42f, 0.98f, glowAlpha);
                painter.lineWidth = 3.5f;
                painter.BeginPath();
                painter.Arc(new Vector2(xCenterLeft, rect.yMin + r), r + 1f, 90f, 270f);
                painter.Arc(new Vector2(xCenterRight, rect.yMin + r), r + 1f, 270f, 450f);
                painter.ClosePath();
                painter.Stroke();
            }

            painter.strokeColor = new Color(0.24f, 0.24f, 0.28f, 0.75f);
            painter.lineWidth = 1f;
            painter.BeginPath();
            painter.Arc(new Vector2(xCenterLeft, rect.yMin + r), r, 90f, 270f);
            painter.Arc(new Vector2(xCenterRight, rect.yMin + r), r, 270f, 450f);
            painter.ClosePath();
            painter.Stroke();

            // 3. Filled Track
            if (level == TokenLevel.Maximum)
            {
                float fillW = thumbX - xMin;
                if (fillW > 0f)
                {
                    float dynamicBlend = Mathf.Clamp01(_effectActiveTimer / 1.0f);

                    // Left cap
                    painter.fillColor = EvaluateMaximumTrackColor(0f, 0.5f, _maximumEffectTime, dynamicBlend);
                    painter.BeginPath();
                    painter.Arc(new Vector2(xCenterLeft, rect.yMin + r), r, 90f, 270f);
                    painter.ClosePath();
                    painter.Fill();

                    // Middle slices with curved wave surge when active -> settled static galaxy gradient
                    float straightW = thumbX - xCenterLeft;
                    if (straightW > 0f)
                    {
                        int xSlices = 48;
                        int ySlices = 6;
                        float sliceW = straightW / xSlices;
                        float sliceH = rect.height / ySlices;

                        for (int i = 0; i < xSlices; i++)
                        {
                            float u = (float)i / xSlices;
                            float x0 = xCenterLeft + i * sliceW;
                            float x1 = x0 + sliceW + 0.5f;

                            for (int j = 0; j < ySlices; j++)
                            {
                                float v = (j + 0.5f) / ySlices;
                                float y0 = rect.yMin + j * sliceH;
                                float y1 = y0 + sliceH + 0.5f;

                                painter.fillColor = EvaluateMaximumTrackColor(u, v, _maximumEffectTime, dynamicBlend);
                                painter.BeginPath();
                                painter.MoveTo(new Vector2(x0, y0));
                                painter.LineTo(new Vector2(x1, y0));
                                painter.LineTo(new Vector2(x1, y1));
                                painter.LineTo(new Vector2(x0, y1));
                                painter.ClosePath();
                                painter.Fill();
                            }
                        }
                    }

                    // Right half cap at thumb
                    painter.fillColor = EvaluateMaximumTrackColor(1f, 0.5f, _maximumEffectTime, dynamicBlend);
                    painter.BeginPath();
                    painter.Arc(new Vector2(thumbX, rect.yMin + r), r, 270f, 450f);
                    painter.ClosePath();
                    painter.Fill();

                    // Dynamic floating / resting diamonds and stardust
                    for (int pIdx = 0; pIdx < _galaxyParticles.Count; pIdx++)
                    {
                        var p = _galaxyParticles[pIdx];
                        float relX = dynamicBlend > 0.001f
                            ? Mathf.Repeat(p.baseX + _maximumEffectTime * p.speed, 1f)
                            : p.baseX;
                        float pX = xCenterLeft + relX * straightW;
                        if (pX > xCenterLeft + 2f && pX < thumbX - 3f)
                        {
                            float yWave = dynamicBlend > 0.001f
                                ? Mathf.Sin(_maximumEffectTime * 2.2f + pIdx * 1.8f) * 1.2f
                                : 0f;
                            float pY = rect.yMin + r + p.yOffset * (r * 0.65f) + yWave;

                            float twinkle = dynamicBlend > 0.001f
                                ? (0.5f + 0.5f * Mathf.Sin(_maximumEffectTime * p.twinkleSpeed + pIdx * 2.1f))
                                : 0.6f;
                            float alpha = p.baseAlpha * (0.40f + twinkle * 0.60f);

                            Color pCol;
                            if (p.colorType == 1) pCol = new Color(0.55f, 0.90f, 1f, alpha);       // Electric Sky Cyan
                            else if (p.colorType == 2) pCol = new Color(1f, 0.65f, 0.92f, alpha);  // Neon Sakura Pink
                            else pCol = new Color(1f, 1f, 1f, alpha);                              // Diamond White

                            painter.fillColor = pCol;
                            float size = p.size * (0.85f + twinkle * 0.3f);

                            if (p.shapeType == 1)
                            {
                                // Crisp Rhombus / Diamond (菱形)
                                float dw = size * 0.95f;
                                float dh = size * 1.45f;
                                painter.BeginPath();
                                painter.MoveTo(new Vector2(pX, pY - dh));
                                painter.LineTo(new Vector2(pX + dw, pY));
                                painter.LineTo(new Vector2(pX, pY + dh));
                                painter.LineTo(new Vector2(pX - dw, pY));
                                painter.ClosePath();
                                painter.Fill();
                            }
                            else if (p.shapeType == 2)
                            {
                                // 4-Pointed Sparkle Star (四角棱星)
                                float arm = size * 1.45f;
                                float inner = size * 0.38f;
                                painter.BeginPath();
                                painter.MoveTo(new Vector2(pX, pY - arm));
                                painter.LineTo(new Vector2(pX + inner, pY - inner));
                                painter.LineTo(new Vector2(pX + arm, pY));
                                painter.LineTo(new Vector2(pX + inner, pY + inner));
                                painter.LineTo(new Vector2(pX, pY + arm));
                                painter.LineTo(new Vector2(pX - inner, pY + inner));
                                painter.LineTo(new Vector2(pX - arm, pY));
                                painter.LineTo(new Vector2(pX - inner, pY - inner));
                                painter.ClosePath();
                                painter.Fill();
                            }
                            else
                            {
                                // Circular Stardust (圆点星尘)
                                painter.BeginPath();
                                painter.Arc(new Vector2(pX, pY), size, 0f, 360f);
                                painter.Fill();
                            }
                        }
                    }
                }
            }
            else
            {
                Color fillColor;
                switch (level)
                {
                    case TokenLevel.Minimal:
                        fillColor = new Color(0.06f, 0.73f, 0.51f, 1f); // #10B981
                        break;
                    case TokenLevel.Standard:
                        fillColor = new Color(0.22f, 0.62f, 0.98f, 1f); // #38BDF8
                        break;
                    case TokenLevel.Full:
                        fillColor = new Color(0.98f, 0.57f, 0.20f, 1f); // #FB923C
                        break;
                    default:
                        fillColor = new Color(0.48f, 0.54f, 0.64f, 1f); // #7C8BA1 (Custom)
                        break;
                }

                painter.fillColor = fillColor;
                painter.BeginPath();
                painter.Arc(new Vector2(xCenterLeft, rect.yMin + r), r, 90f, 270f);
                painter.Arc(new Vector2(thumbX, rect.yMin + r), r, 270f, 450f);
                painter.ClosePath();
                painter.Fill();
            }

            // 4. Tick dots at preset positions
            for (int i = 0; i < 4; i++)
            {
                float tickX = xCenterLeft + (i / 3f) * travel;
                if (Mathf.Abs(tickX - thumbX) > r * 0.65f)
                {
                    Vector2 tickPos = new Vector2(tickX, rect.yMin + r);
                    bool inFilled = tickX < thumbX;
                    painter.fillColor = inFilled
                        ? new Color(1f, 1f, 1f, 0.75f)
                        : new Color(1f, 1f, 1f, 0.20f);
                    painter.BeginPath();
                    painter.Arc(tickPos, 2.5f, 0f, 360f);
                    painter.Fill();
                }
            }

            // 5. Thumb Knob
            float thumbR = Mathf.Max(8f, r - 1.5f);

            // Maximum energized outer ring
            if (level == TokenLevel.Maximum)
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin(_maximumEffectTime * 3.5f);
                painter.strokeColor = new Color(0.85f, 0.45f, 0.98f, 0.40f + pulse * 0.35f);
                painter.lineWidth = 2.2f;
                painter.BeginPath();
                painter.Arc(thumbCenter, thumbR + 2.5f, 0f, 360f);
                painter.Stroke();
            }

            // Drop shadow
            painter.fillColor = new Color(0f, 0f, 0f, 0.35f);
            painter.BeginPath();
            painter.Arc(thumbCenter + new Vector2(0f, 1.2f), thumbR + 0.8f, 0f, 360f);
            painter.Fill();

            // Solid White Circle Disc
            painter.fillColor = Color.white;
            painter.BeginPath();
            painter.Arc(thumbCenter, thumbR, 0f, 360f);
            painter.Fill();

            // Crisp outer rim
            painter.strokeColor = new Color(0.85f, 0.88f, 0.92f, 0.85f);
            painter.lineWidth = 1f;
            painter.BeginPath();
            painter.Arc(thumbCenter, thumbR, 0f, 360f);
            painter.Stroke();
        }

        private static Color EvaluateMaximumTrackColor(float u, float v, float time, float dynamicBlend)
        {
            Color staticCol = EvaluateGalaxyColor(u);
            if (dynamicBlend <= 0.001f) return staticCol;

            Color dynamicCol = EvaluateCosmicFlowColor(u, v, time);
            return Color.Lerp(staticCol, dynamicCol, dynamicBlend);
        }

        private static Color EvaluateGalaxyColor(float u)
        {
            if (u <= 0.35f)
            {
                float factor = u / 0.35f;
                return Color.Lerp(new Color(0.16f, 0.42f, 0.98f, 1f), new Color(0.38f, 0.32f, 0.96f, 1f), factor);
            }
            if (u <= 0.70f)
            {
                float factor = (u - 0.35f) / 0.35f;
                return Color.Lerp(new Color(0.38f, 0.32f, 0.96f, 1f), new Color(0.68f, 0.30f, 0.96f, 1f), factor);
            }
            else
            {
                float factor = (u - 0.70f) / 0.30f;
                return Color.Lerp(new Color(0.68f, 0.30f, 0.96f, 1f), new Color(0.96f, 0.35f, 0.82f, 1f), factor);
            }
        }

        private static Color EvaluateCosmicFlowColor(float u, float v, float time)
        {
            // Organic ocean wave curvature: wave crest surges forward in the upper middle with dynamic undulating foam
            float waveCurvature = Mathf.Sin(v * Mathf.PI) * 0.09f + Mathf.Sin(v * Mathf.PI * 2f - time * 2.5f) * 0.03f;
            float uCurved = u - waveCurvature;

            // Smooth continuous wave flow
            float flow = Mathf.Repeat(uCurved - time * 0.20f, 1f);

            // Cosmic 4-stop spectral palette: Deep Cyan-Blue -> Indigo Purple -> Electric Violet -> Magenta Peach
            Color baseCol;
            if (flow <= 0.30f)
            {
                float f = flow / 0.30f;
                baseCol = Color.Lerp(new Color(0.12f, 0.48f, 0.98f, 1f), new Color(0.36f, 0.28f, 0.96f, 1f), f);
            }
            else if (flow <= 0.65f)
            {
                float f = (flow - 0.30f) / 0.35f;
                baseCol = Color.Lerp(new Color(0.36f, 0.28f, 0.96f, 1f), new Color(0.72f, 0.26f, 0.98f, 1f), f);
            }
            else
            {
                float f = (flow - 0.65f) / 0.35f;
                baseCol = Color.Lerp(new Color(0.72f, 0.26f, 0.98f, 1f), new Color(0.98f, 0.38f, 0.76f, 1f), f);
            }

            // Curved steep-slope ocean wave surge (海浪陡坡型渐进: steep front, luminous smooth body)
            float shimmerCycle = Mathf.Repeat(time * 0.40f, 1.8f) - 0.4f;
            float shimmerDist = uCurved - shimmerCycle; // Directional distance from wave front

            if (shimmerDist > -0.18f && shimmerDist < 0.05f)
            {
                float waveFactor;
                if (shimmerDist >= 0f)
                {
                    // Steep wave front (陡峭前沿波浪)
                    waveFactor = 1f - (shimmerDist / 0.05f);
                }
                else
                {
                    // Smooth trailing wave slope (平缓后掠坡度)
                    waveFactor = 1f - (-shimmerDist / 0.18f);
                }

                waveFactor = Mathf.Clamp01(waveFactor);
                float shimmerIntensity = waveFactor * waveFactor * 0.45f;

                // Dynamic seafoam crest highlight at the very peak
                if (shimmerDist > -0.025f && shimmerDist < 0.025f)
                {
                    float crest = 1f - Mathf.Abs(shimmerDist) / 0.025f;
                    shimmerIntensity += crest * 0.22f;
                }

                baseCol = Color.Lerp(baseCol, Color.white, shimmerIntensity);
            }

            return baseCol;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SkillsTokenLevel.OnChanged -= OnTokenLevelChanged;
            SkillsLocalization.LanguageChanged -= RefreshTokenLevelLocalization;
            _root.UnregisterCallback<AttachToPanelEvent>(OnRootAttached);
            _root.UnregisterCallback<DetachFromPanelEvent>(OnRootDetached);
            if (_tokenLevelTrack != null)
                _tokenLevelTrack.generateVisualContent -= DrawTokenSlider;
            _maximumEffectAnimation?.Pause();
            _maximumEffectAnimation = null;
        }
    }
}

// Producer:Betsy
