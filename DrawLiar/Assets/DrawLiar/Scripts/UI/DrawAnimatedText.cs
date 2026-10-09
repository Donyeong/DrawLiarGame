using System;
using System.Globalization;
using Febucci.TextAnimatorCore;
using Febucci.TextAnimatorCore.Settings;
using Febucci.TextAnimatorCore.Text;
using Febucci.TextAnimatorCore.Time;
using Febucci.TextAnimatorCore.Typing;
using Febucci.TextAnimatorForUnity;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    internal sealed class DrawAnimatedText : IDisposable, ITextGenerator, ITypingTimingsProvider,
        ISettingsProvider<AnimatorSettings>, ISettingsProvider<TypewriterSettings>, ISettingsProvider<GlobalSettingsBase>
    {
        private const float MIN_CHARACTER_SECONDS = 0.001f;
        private readonly TextAnimator _animator;
        private readonly TypewriterCore _typewriter;
        private readonly string _rawText, _engineText;
        private readonly int[] _textElementStarts;
        private readonly AnimatorSettings _animationSettings = new AnimatorSettings
        {
            timeScale = TimeScale.Unscaled,
            defaultBehaviorTags = Array.Empty<string>(),
            defaultAppearanceTags = Array.Empty<string>(),
            defaultDisappearanceTags = Array.Empty<string>(),
            isAnimatingBehaviors = false,
            isAnimatingAppearances = false,
            isAnimatingDisappearances = false,
            useDynamicScaling = false
        };
        private readonly TypewriterSettings _typingSettings = new UnityTypewriterSettings
        {
            useTypeWriter = true,
            startTypewriterMode = StartTypewriterMode.FromScriptOnly,
            triggerEventsOnSkip = false,
            hideAppearancesOnSkip = true,
            hideDisappearancesOnSkip = true,
            resetTypingSpeedAtStartup = true
        };
        private readonly GlobalSettingsBase _globalSettings = new UnityGlobalSettings
        {
            isAnimatingBehaviors = false,
            isAnimatingAppearances = false,
            isAnimatingDisappearances = false
        };
        private float _secondsPerCharacter, _elapsedSeconds;
        private float _reservedWidth = -1, _reservedFontSize = -1;
        private int _visibleLength;
        private bool _started, _cancelled, _disposed, _completed;

        internal Label Element { get; }
        internal bool IsComplete => _completed;
        AnimatorSettings ISettingsProvider<AnimatorSettings>.Settings => _animationSettings;
        TypewriterSettings ISettingsProvider<TypewriterSettings>.Settings => _typingSettings;
        GlobalSettingsBase ISettingsProvider<GlobalSettingsBase>.Settings => _globalSettings;

        private DrawAnimatedText(string rawFullText)
        {
            _rawText = rawFullText ?? string.Empty;
            _textElementStarts = StringInfo.ParseCombiningCharacters(_rawText);
            // 엔진의 태그 시작 기호만 치환하고 화면에는 원문을 그대로 표시한다.
            _engineText = _rawText.Replace('<', '\uFF1C');
            Element = new Label { enableRichText = false, parseEscapeSequences = false };
            _animator = new TextAnimator(false, '<', '/', '>', UnityEngineProvider.Instance,
                this, this, this, false, null, this);
            _typewriter = new TypewriterCore(_animator, this, this, this, this, null);
            _typewriter.OnTextShowed += OnTextShowed;
            _animator.SetText(_engineText, ShowTextMode.Hidden);
            _animator.PauseAnimation();
            Element.RegisterCallback<AttachToPanelEvent>(OnAttached);
            Element.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            Element.RegisterCallback<DetachFromPanelEvent>(OnDetached);
        }

        internal static DrawAnimatedText Create(VisualElement parent, string rawFullText, string classes = "", string name = "")
        {
            var text = new DrawAnimatedText(rawFullText);
            text.Element.name = name ?? string.Empty;
            foreach (var className in (classes ?? string.Empty).Split(' '))
                if (className.Length > 0) text.Element.AddToClassList(className);
            parent?.Add(text.Element);
            return text;
        }

        internal static float MeasureDuration(string rawFullText, float secondsPerCharacter)
            => StringInfo.ParseCombiningCharacters(rawFullText ?? string.Empty).Length * ClampCharacterSeconds(secondsPerCharacter);

        internal float Duration(float secondsPerCharacter) => MeasureDuration(_rawText, secondsPerCharacter);

        internal void Seek(float elapsedSeconds, float secondsPerCharacter)
        {
            if (_disposed || _cancelled) return;
            if (elapsedSeconds < 0)
            {
                if (_started)
                {
                    _typewriter.StopShowingText();
                    _animator.SetVisibilityEntireText(false, false);
                    _started = _completed = false;
                    _elapsedSeconds = 0;
                    RenderVisibleText();
                }
                return;
            }
            if (_textElementStarts.Length == 0) { Complete(); return; }
            var delay = ClampCharacterSeconds(secondsPerCharacter);
            var elapsed = float.IsNaN(elapsedSeconds) ? 0 : Mathf.Max(0, elapsedSeconds);
            elapsed = Mathf.Min(elapsed, Duration(delay) + delay);
            if (_started && Mathf.Approximately(delay, _secondsPerCharacter)
                && Mathf.Approximately(elapsed, _elapsedSeconds)) return;
            if (!_started || !Mathf.Approximately(delay, _secondsPerCharacter) || elapsed < _elapsedSeconds)
            {
                _secondsPerCharacter = delay;
                _elapsedSeconds = 0;
                _completed = false;
                _started = true;
                _typewriter.StopShowingText();
                _typewriter.StartShowingText(true);
            }
            _animator.ResumeAnimation();
            _animator.Animate(elapsed - _elapsedSeconds);
            _animator.PauseAnimation();
            RenderVisibleText();
            _elapsedSeconds = elapsed;
        }

        internal void Complete()
        {
            if (_disposed || _cancelled) return;
            _typewriter.StopShowingText();
            _typewriter.StopDisappearingText();
            _animator.SetVisibilityEntireText(true, false);
            RenderVisibleText();
            _completed = true;
        }

        internal void Cancel()
        {
            if (_cancelled || _disposed) return;
            _cancelled = true;
            _typewriter.StopShowingText();
            _typewriter.StopDisappearingText();
            _animator.PauseAnimation();
        }

        public void Dispose()
        {
            if (_disposed) return;
            Cancel();
            _disposed = true;
            Element.UnregisterCallback<AttachToPanelEvent>(OnAttached);
            Element.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            Element.UnregisterCallback<DetachFromPanelEvent>(OnDetached);
            _typewriter.OnTextShowed -= OnTextShowed;
            _animator.Dispose();
        }

        float ITypingTimingsProvider.GetWaitAppearanceTimeOf(CharacterData character, TextAnimator animator)
        {
            var nextIndex = character.index + 1;
            return nextIndex == _rawText.Length || Array.BinarySearch(_textElementStarts, nextIndex) >= 0 ? _secondsPerCharacter : 0;
        }

        float ITypingTimingsProvider.GetWaitDisappearanceTimeOf(CharacterData character, TextAnimator animator) => 0;
        void ITextGenerator.SetTextToSource(string text) { }
        string ITextGenerator.GetFullText() => _engineText;
        string ITextGenerator.GetStrippedTextWithoutAnyTags(string text) => text;
        int ITextGenerator.GetCharactersCount() => _engineText.Length;
        int ITextGenerator.GetFirstCharacterIndexInsidePage() => 0;
        int ITextGenerator.GetRenderedCharactersCountInsidePage(int charactersCount) => charactersCount;
        bool ITextGenerator.HasChangedMeshRenderingSettings() => false;
        void ITextGenerator.ForceMeshUpdate() { }
        void ITextGenerator.PasteMeshToSource(CharacterData[] characters, int charactersCount) { }

        void ITextGenerator.CopyMeshFromSource(ref CharacterData[] characters, int charactersCount)
        {
            for (var index = 0; index < charactersCount && index < _rawText.Length; index++)
            {
                characters[index].info.character = _rawText[index];
                characters[index].info.isRendered = !char.IsWhiteSpace(_rawText[index]);
                characters[index].info.pointSize = 1;
            }
        }

        private void RenderVisibleText()
        {
            var visible = 0;
            while (visible < _animator.CharactersCount && _animator.Characters[visible].isVisible) visible++;
            var position = Array.BinarySearch(_textElementStarts, visible);
            if (visible < _rawText.Length && position < 0)
            {
                var preceding = ~position - 1;
                visible = preceding >= 0 ? _textElementStarts[preceding] : 0;
            }
            if (visible == _visibleLength) return;
            _visibleLength = visible;
            Element.text = visible >= _rawText.Length ? _rawText : _rawText.Substring(0, visible);
        }

        private void ReserveTextHeight()
        {
            var width = Element.contentRect.width;
            var fontSize = Element.resolvedStyle.fontSize;
            if (width <= 0 || float.IsNaN(width) || Mathf.Approximately(width, _reservedWidth)
                && Mathf.Approximately(fontSize, _reservedFontSize)) return;
            _reservedWidth = width;
            _reservedFontSize = fontSize;
            var size = Element.MeasureTextSize(_rawText, width, VisualElement.MeasureMode.Exactly,
                0, VisualElement.MeasureMode.Undefined);
            if (size.y > 0 && !float.IsNaN(size.y)) Element.style.minHeight = size.y;
        }

        private void OnAttached(AttachToPanelEvent evt) => ReserveTextHeight();
        private void OnGeometryChanged(GeometryChangedEvent evt) => ReserveTextHeight();
        private void OnTextShowed() => _completed = true;
        private void OnDetached(DetachFromPanelEvent evt) => Dispose();
        private static float ClampCharacterSeconds(float value) => float.IsNaN(value) || float.IsInfinity(value) ? MIN_CHARACTER_SECONDS : Mathf.Max(MIN_CHARACTER_SECONDS, value);
    }
}
