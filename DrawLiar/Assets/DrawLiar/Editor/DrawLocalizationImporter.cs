using System;
using System.IO;
using I2.Loc;
using UnityEditor;
using UnityEngine;

namespace DrawLiar.Editor
{
    [InitializeOnLoad]
    public sealed class DrawLocalizationImporter : AssetPostprocessor
    {
        public const string CATALOGUE_PATH = "Assets/DrawLiar/Resources/DrawLiar/Localization/UiTranslations.json";
        public const string SOURCE_PATH = "Assets/DrawLiar/Resources/DrawLiar/Localization/DrawLiarLanguages.asset";
        private static bool _refreshQueued;

        static DrawLocalizationImporter()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Unsubscribe;
            QueueRefresh();
        }

        private static void Unsubscribe()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.delayCall -= RefreshWhenReady;
            AssemblyReloadEvents.beforeAssemblyReload -= Unsubscribe;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode) QueueRefresh();
        }

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (Array.IndexOf(imported, CATALOGUE_PATH) >= 0 || Array.IndexOf(moved, CATALOGUE_PATH) >= 0)
                QueueRefresh();
        }

        private static void QueueRefresh()
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
            EditorApplication.delayCall += RefreshWhenReady;
        }

        private static void RefreshWhenReady()
        {
            _refreshQueued = false;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                QueueRefresh();
                return;
            }
            if (!File.Exists(CATALOGUE_PATH)) return;
            RefreshCatalogue();
        }

        [MenuItem("Tools/DrawLiar/Localization/Refresh Catalogue")]
        public static void RefreshCatalogue()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            try
            {
                var json = File.ReadAllText(CATALOGUE_PATH);
                var catalogue = JsonUtility.FromJson<DrawLocalizationCatalogue>(json);
                if (catalogue?.Terms == null) throw new InvalidOperationException("UI 번역 카탈로그에 Terms 배열이 필요합니다.");
                var source = DrawLocalization.CreateLanguageSource(catalogue);
                var asset = AssetDatabase.LoadAssetAtPath<LanguageSourceAsset>(SOURCE_PATH);
                if (asset == null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SOURCE_PATH));
                    asset = ScriptableObject.CreateInstance<LanguageSourceAsset>();
                    asset.SourceData = source;
                    AssetDatabase.CreateAsset(asset, SOURCE_PATH);
                }
                else
                {
                    var previous = EditorJsonUtility.ToJson(asset);
                    asset.SourceData = source;
                    if (string.Equals(previous, EditorJsonUtility.ToJson(asset), StringComparison.Ordinal)) return;
                    EditorUtility.SetDirty(asset);
                }
                source.owner = asset;
                AssetDatabase.SaveAssetIfDirty(asset);
                Debug.Log($"UI 번역 카탈로그: {source.mTerms.Count}개 문구, {source.mLanguages.Count}개 언어.");
            }
            catch (Exception error)
            {
                Debug.LogError("UI 번역 카탈로그를 가져올 수 없습니다: " + error.Message);
            }
        }
    }
}
