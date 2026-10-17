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
        internal string LogoCode { get; }

        internal DrawLanguage(string code, string displayName, string i2Name, string logoCode = null)
        {
            Code = code;
            DisplayName = displayName;
            I2Name = i2Name;
            LogoCode = logoCode ?? code;
        }
    }

    [Serializable]
    public sealed class DrawLocalizationCatalogue
    {
        public string[] LanguageCodes;
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
            new DrawLanguage("ko", "한국어", "Korean", "ko-KR"),
            new DrawLanguage("en", "English", "English"),
            new DrawLanguage("ja", "日本語", "Japanese", "ja-JP"),
            new DrawLanguage("zh-CN", "简体中文", "Chinese (Simplified)"),
            new DrawLanguage("de", "Deutsch", "German", "de-DE"),
            new DrawLanguage("fr", "Français", "French", "fr-FR"),
            new DrawLanguage("es", "Español", "Spanish"),
            new DrawLanguage("es-419", "Español (Latinoamérica)", "Spanish (Latin America)", "es"),
            new DrawLanguage("pt", "Português", "Portuguese", "pt-BR"),
            new DrawLanguage("it", "Italiano", "Italian"),
            new DrawLanguage("nl", "Nederlands", "Dutch"),
            new DrawLanguage("da", "Dansk", "Danish"),
            new DrawLanguage("no", "Norsk", "Norwegian"),
            new DrawLanguage("sv", "Svenska", "Swedish"),
            new DrawLanguage("fi", "Suomi", "Finnish"),
            new DrawLanguage("pl", "Polski", "Polish"),
            new DrawLanguage("cs", "Čeština", "Czech"),
            new DrawLanguage("sk", "Slovenčina", "Slovak"),
            new DrawLanguage("hu", "Magyar", "Hungarian"),
            new DrawLanguage("ro", "Română", "Romanian"),
            new DrawLanguage("ru", "Русский", "Russian"),
            new DrawLanguage("uk", "Українська", "Ukrainian"),
            new DrawLanguage("tr", "Türkçe", "Turkish"),
            new DrawLanguage("ar", "العربية", "Arabic"),
            new DrawLanguage("iw", "עברית", "Hebrew"),
            new DrawLanguage("hi", "हिन्दी", "Hindi", "hi-IN"),
            new DrawLanguage("id", "Bahasa Indonesia", "Indonesian"),
            new DrawLanguage("th", "ไทย", "Thai"),
            new DrawLanguage("vi", "Tiếng Việt", "Vietnamese")
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
        public static bool IsRightToLeft => CurrentLanguageCode == "ar" || CurrentLanguageCode == "iw";
        public static string LogoResourcePath { get { EnsureInitialized(); return LOGO_RESOURCE_ROOT + _language.LogoCode; } }

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
            if (catalogue?.LanguageCodes != null)
            {
                if (catalogue.LanguageCodes.Length != _languages.Count)
                    throw new InvalidOperationException("번역 카탈로그의 언어 수가 지원 언어 수와 다릅니다.");
                for (int i = 0; i < _languages.Count; i++)
                    if (catalogue.LanguageCodes[i] != _languages[i].Code)
                        throw new InvalidOperationException("번역 카탈로그의 언어 순서가 지원 언어 순서와 다릅니다.");
            }
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
                    throw new InvalidOperationException($"각 번역 항목에는 언어 순서에 맞는 {_languages.Count}개 값이 필요합니다.");
                foreach (string value in row.Values)
                    if (string.IsNullOrWhiteSpace(value))
                        throw new InvalidOperationException("비어 있는 UI 번역이 있습니다: " + row.Key);
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
            string savedCode = PlayerPrefs.GetString(LANGUAGE_PREF_KEY, string.Empty);
            var savedLanguage = FindLanguage(savedCode);
            ApplyLanguage(savedLanguage ?? DeviceLanguage());
            if (savedLanguage != null && savedCode != savedLanguage.Code)
            {
                PlayerPrefs.SetString(LANGUAGE_PREF_KEY, savedLanguage.Code);
                PlayerPrefs.Save();
            }
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
            try { return CultureInfo.GetCultureInfo(_language.Code == "iw" ? "he" : _language.Code); }
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
            code = code.Trim().Replace('_', '-');
            foreach (var language in _languages)
                if (string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase)) return language;
            string primary = code.Split('-')[0].ToLowerInvariant();
            if (primary == "zh") primary = "zh-CN";
            else if (primary == "he") primary = "iw";
            else if (primary == "nb" || primary == "nn") primary = "no";
            else if (primary == "in") primary = "id";
            if (primary == "es" && IsLatinAmericanSpanish(code)) primary = "es-419";
            foreach (var language in _languages)
                if (language.Code == primary) return language;
            return null;
        }

        private static bool IsLatinAmericanSpanish(string code)
        {
            switch (code.ToUpperInvariant())
            {
                case "ES-419":
                case "ES-AR": case "ES-BO": case "ES-CL": case "ES-CO": case "ES-CR":
                case "ES-CU": case "ES-DO": case "ES-EC": case "ES-GT": case "ES-HN":
                case "ES-MX": case "ES-NI": case "ES-PA": case "ES-PE": case "ES-PR":
                case "ES-PY": case "ES-SV": case "ES-US": case "ES-UY": case "ES-VE":
                    return true;
                default: return false;
            }
        }

        private static DrawLanguage DeviceLanguage()
        {
            string code;
            switch (Application.systemLanguage)
            {
                case SystemLanguage.Korean: code = "ko"; break;
                case SystemLanguage.ChineseSimplified:
                case SystemLanguage.ChineseTraditional:
                case SystemLanguage.Chinese: code = "zh-CN"; break;
                case SystemLanguage.Japanese: code = "ja"; break;
                case SystemLanguage.German: code = "de"; break;
                case SystemLanguage.French: code = "fr"; break;
                case SystemLanguage.Spanish:
                    code = IsLatinAmericanSpanish(CultureInfo.CurrentUICulture.Name) ? "es-419" : "es"; break;
                case SystemLanguage.Portuguese: code = "pt"; break;
                case SystemLanguage.Italian: code = "it"; break;
                case SystemLanguage.Dutch: code = "nl"; break;
                case SystemLanguage.Danish: code = "da"; break;
                case SystemLanguage.Norwegian: code = "no"; break;
                case SystemLanguage.Swedish: code = "sv"; break;
                case SystemLanguage.Finnish: code = "fi"; break;
                case SystemLanguage.Polish: code = "pl"; break;
                case SystemLanguage.Czech: code = "cs"; break;
                case SystemLanguage.Slovak: code = "sk"; break;
                case SystemLanguage.Hungarian: code = "hu"; break;
                case SystemLanguage.Romanian: code = "ro"; break;
                case SystemLanguage.Russian: code = "ru"; break;
                case SystemLanguage.Ukrainian: code = "uk"; break;
                case SystemLanguage.Turkish: code = "tr"; break;
                case SystemLanguage.Arabic: code = "ar"; break;
                case SystemLanguage.Hebrew: code = "iw"; break;
                case SystemLanguage.Hindi: code = "hi"; break;
                case SystemLanguage.Indonesian: code = "id"; break;
                case SystemLanguage.Thai: code = "th"; break;
                case SystemLanguage.Vietnamese: code = "vi"; break;
                default: code = FALLBACK_LANGUAGE_CODE; break;
            }
            return FindLanguage(code);
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
