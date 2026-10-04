using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using I2.Loc;
using UnityEngine;

namespace DrawLiar
{
    public sealed class DrawLanguage
    {
        public string Code { get; }
        public string DisplayName { get; }
        internal string I2Name { get; }

        internal DrawLanguage(string code, string displayName, string i2Name)
        {
            Code = code;
            DisplayName = displayName;
            I2Name = i2Name;
        }
    }

    [Serializable]
    public sealed class DrawLocalizationCatalogue
    {
        public DrawTranslationTerm[] Terms = Array.Empty<DrawTranslationTerm>();
    }

    [Serializable]
    public sealed class DrawTranslationTerm
    {
        public string Key;
        public string[] Values;
    }

    public static class DrawLocalization
    {
        public const string CATALOGUE_RESOURCE_PATH = "DrawLiar/Localization/UiTranslations";
        public const string SOURCE_RESOURCE_PATH = "DrawLiar/Localization/DrawLiarLanguages";
        private const string LANGUAGE_PREF_KEY = "DrawLiar.LanguageCode";
        private const string LOGO_RESOURCE_ROOT = "DrawLiar/Brand/Localized/";
        private const string FALLBACK_LANGUAGE_CODE = "en";
        private static readonly IReadOnlyList<DrawLanguage> _languages = Array.AsReadOnly(new[]
        {
            new DrawLanguage("ko-KR", "한국어", "Korean"),
            new DrawLanguage("en", "English", "English"),
            new DrawLanguage("zh-CN", "简体中文", "Chinese (Simplified)"),
            new DrawLanguage("zh-TW", "繁體中文", "Chinese (Traditional)"),
            new DrawLanguage("ja-JP", "日本語", "Japanese"),
            new DrawLanguage("es", "Español", "Spanish"),
            new DrawLanguage("pt-BR", "Português (Brasil)", "Portuguese (Brazil)"),
            new DrawLanguage("fr-FR", "Français", "French"),
            new DrawLanguage("de-DE", "Deutsch", "German"),
            new DrawLanguage("id", "Bahasa Indonesia", "Indonesian"),
            new DrawLanguage("hi-IN", "हिन्दी", "Hindi"),
            new DrawLanguage("ar", "العربية", "Arabic")
        });
        private static readonly IReadOnlyList<string> _languageCodes = BuildLanguageList(false);
        private static readonly IReadOnlyList<string> _nativeLanguageNames = BuildLanguageList(true);
        private static readonly Dictionary<string, string> _translations = new Dictionary<string, string>(StringComparer.Ordinal);
        private static LanguageSourceData _source;
        private static LanguageSourceAsset _sourceAsset;
        private static DrawLanguage _language;
        private static Texture2D _logo;
        private static bool _initialized;
        private static bool _logoLoaded;

        public static event Action LanguageChanged;
        public static IReadOnlyList<DrawLanguage> AvailableLanguages => _languages;
        public static IReadOnlyList<string> LanguageCodes => _languageCodes;
        public static IReadOnlyList<string> NativeLanguageNames => _nativeLanguageNames;
        public static string CurrentLanguageCode { get { EnsureInitialized(); return _language.Code; } }
        public static bool IsRightToLeft => CurrentLanguageCode == "ar";
        public static string LogoResourcePath => LOGO_RESOURCE_ROOT + CurrentLanguageCode;

        public static string Text(string keyOrSource)
        {
            return GetTranslation(keyOrSource);
        }

        public static string Format(string keyOrSource, params object[] args)
        {
            var template = GetTranslation(keyOrSource);
            if (args == null || args.Length == 0) return template;
            try
            {
                return string.Format(GetCulture(), template, args);
            }
            catch (FormatException)
            {
                Debug.LogError("UI 번역 문구의 서식 인수가 올바르지 않습니다.");
                try { return string.Format(GetCulture(), keyOrSource ?? string.Empty, args); }
                catch (FormatException) { return template; }
            }
        }

        public static string Raw(string value) => value ?? string.Empty;

        public static bool SetLanguage(string code)
        {
            EnsureInitialized();
            var language = FindLanguage(code);
            if (language == null) return false;
            PlayerPrefs.SetString(LANGUAGE_PREF_KEY, language.Code);
            PlayerPrefs.Save();
            if (_language.Code == language.Code) return true;
            ApplyLanguage(language);
            LocalizationManager.SetLanguageAndCode(language.I2Name, language.Code, RememberLanguage: false);
            LanguageChanged?.Invoke();
            return true;
        }

        public static Texture2D LoadLogo()
        {
            EnsureInitialized();
            if (_logoLoaded) return _logo;
            _logoLoaded = true;
            _logo = Resources.Load<Texture2D>(LogoResourcePath);
            if (_logo == null && _language.Code != FALLBACK_LANGUAGE_CODE)
                _logo = Resources.Load<Texture2D>(LOGO_RESOURCE_ROOT + FALLBACK_LANGUAGE_CODE);
            return _logo;
        }

        public static LanguageSourceData CreateLanguageSource(DrawLocalizationCatalogue catalogue)
        {
            var source = new LanguageSourceData
            {
                IgnoreDeviceLanguage = true,
                OnMissingTranslation = LanguageSourceData.MissingTranslationAction.Empty,
                GoogleUpdateFrequency = LanguageSourceData.eGoogleUpdateFrequency.Never,
                GoogleInEditorCheckFrequency = LanguageSourceData.eGoogleUpdateFrequency.Never,
                _AllowUnloadingLanguages = LanguageSourceData.eAllowUnloadLanguages.Never,
                UserAgreesToHaveItOnTheScene = true,
                UserAgreesToHaveItInsideThePluginsFolder = true
            };
            foreach (var language in _languages)
                source.mLanguages.Add(new LanguageData { Name = language.I2Name, Code = language.Code });
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in catalogue?.Terms ?? Array.Empty<DrawTranslationTerm>())
            {
                if (row == null || string.IsNullOrEmpty(row.Key) || !keys.Add(row.Key))
                    throw new InvalidOperationException("번역 키는 비어 있거나 중복될 수 없습니다.");
                if (row.Values == null || row.Values.Length != _languages.Count)
                    throw new InvalidOperationException("각 번역 항목에는 언어 순서에 맞는 12개 값이 필요합니다.");
                source.mTerms.Add(new TermData
                {
                    Term = TermKey(row.Key),
                    TermType = eTermType.Text,
                    Description = row.Key,
                    Languages = (string[])row.Values.Clone(),
                    Flags = new byte[_languages.Count]
                });
            }
            source.UpdateDictionary();
            return source;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntime()
        {
            LocalizationManager.OnLocalizeEvent -= OnI2LanguageChanged;
            _source?.OnDestroy();
            _source = null;
            _sourceAsset = null;
            _language = null;
            _logo = null;
            _initialized = false;
            _logoLoaded = false;
            _translations.Clear();
            LanguageChanged = null;
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;
            _sourceAsset = Resources.Load<LanguageSourceAsset>(SOURCE_RESOURCE_PATH);
            if (_sourceAsset != null)
            {
                _source = _sourceAsset.SourceData;
                _source.owner = _sourceAsset;
            }
            else
            {
                var catalogue = Resources.Load<TextAsset>(CATALOGUE_RESOURCE_PATH);
                _source = CreateLanguageSource(catalogue != null
                    ? JsonUtility.FromJson<DrawLocalizationCatalogue>(catalogue.text)
                    : new DrawLocalizationCatalogue());
                if (catalogue == null) Debug.LogError("UI 번역 카탈로그를 찾을 수 없습니다.");
            }
            _source.GoogleUpdateFrequency = LanguageSourceData.eGoogleUpdateFrequency.Never;
            _source.GoogleInEditorCheckFrequency = LanguageSourceData.eGoogleUpdateFrequency.Never;
            _source._AllowUnloadingLanguages = LanguageSourceData.eAllowUnloadLanguages.Never;
            _source.Awake();
            LocalizationManager.InitializeIfNeeded();
            var savedLanguage = FindLanguage(PlayerPrefs.GetString(LANGUAGE_PREF_KEY, string.Empty));
            ApplyLanguage(savedLanguage ?? DeviceLanguage());
            LocalizationManager.SetLanguageAndCode(_language.I2Name, _language.Code, RememberLanguage: false);
            LocalizationManager.OnLocalizeEvent -= OnI2LanguageChanged;
            LocalizationManager.OnLocalizeEvent += OnI2LanguageChanged;
        }

        private static string GetTranslation(string keyOrSource)
        {
            if (string.IsNullOrEmpty(keyOrSource)) return string.Empty;
            EnsureInitialized();
            if (_translations.TryGetValue(keyOrSource, out var cached)) return cached;
            var term = TermKey(keyOrSource);
            var text = LocalizationManager.GetTranslation(term, FixForRTL: false, overrideLanguage: _language.I2Name);
            if (string.IsNullOrEmpty(text) && _language.Code != FALLBACK_LANGUAGE_CODE)
                text = LocalizationManager.GetTranslation(term, FixForRTL: false, overrideLanguage: "English");
            if (string.IsNullOrEmpty(text)) text = keyOrSource;
            _translations[keyOrSource] = text;
            return text;
        }

        private static string TermKey(string key)
        {
            // 원문의 문장 부호·줄바꿈이 I2 키 정규화로 충돌하지 않도록 인코딩한다.
            return "UI/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static CultureInfo GetCulture()
        {
            try { return CultureInfo.GetCultureInfo(_language.Code); }
            catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
        }

        private static void ApplyLanguage(DrawLanguage language)
        {
            _language = language;
            _translations.Clear();
            _logo = null;
            _logoLoaded = false;
        }

        private static void OnI2LanguageChanged()
        {
            if (!_initialized) return;
            var language = FindLanguage(LocalizationManager.CurrentLanguageCode);
            if (language == null || language.Code == _language.Code) return;
            ApplyLanguage(language);
            PlayerPrefs.SetString(LANGUAGE_PREF_KEY, language.Code);
            PlayerPrefs.Save();
            LanguageChanged?.Invoke();
        }

        private static DrawLanguage FindLanguage(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            foreach (var language in _languages)
                if (string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase)) return language;
            return null;
        }

        private static DrawLanguage DeviceLanguage()
        {
            switch (Application.systemLanguage)
            {
                case SystemLanguage.Korean: return _languages[0];
                case SystemLanguage.ChineseSimplified: return _languages[2];
                case SystemLanguage.ChineseTraditional: return _languages[3];
                case SystemLanguage.Chinese: return _languages[2];
                case SystemLanguage.Japanese: return _languages[4];
                case SystemLanguage.Spanish: return _languages[5];
                case SystemLanguage.Portuguese: return _languages[6];
                case SystemLanguage.French: return _languages[7];
                case SystemLanguage.German: return _languages[8];
                case SystemLanguage.Indonesian: return _languages[9];
                case SystemLanguage.Hindi: return _languages[10];
                case SystemLanguage.Arabic: return _languages[11];
                default: return _languages[1];
            }
        }

        private static IReadOnlyList<string> BuildLanguageList(bool nativeNames)
        {
            var values = new string[_languages.Count];
            for (var i = 0; i < values.Length; i++)
                values[i] = nativeNames ? _languages[i].DisplayName : _languages[i].Code;
            return Array.AsReadOnly(values);
        }
    }
}
