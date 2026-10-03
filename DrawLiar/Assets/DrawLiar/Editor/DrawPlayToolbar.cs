#if UNITY_EDITOR && UNITY_6000_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Toolbars;
using UnityEngine;

namespace DrawLiar.Editor
{
    public static class DrawPlayToolbar
    {
        private const string PC_ELEMENT_ID = "DrawLiar/PC Test";
        private const string MOBILE_ELEMENT_ID = "DrawLiar/Mobile Test";
        private const string GAME_SCENE_PATH = "Assets/DrawLiar/Scenes/DrawLiar.unity";
        private const string ACTIVE_KEY = "DrawLiar.PlayToolbar.Active";
        private const string START_SCENE_KEY = "DrawLiar.PlayToolbar.PreviousStartScene";
        private const BindingFlags REFLECTION_FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly string _layoutKey = "DrawLiar.PlayToolbar.Unity6000_3.LayoutV1." + Hash128.Compute(Application.dataPath);
        private static bool _lastCanPlay;
        private static bool CanPlay => !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating;

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            _lastCanPlay = CanPlay;
            EditorApplication.update -= RefreshAvailability;
            EditorApplication.update += RefreshAvailability;
            EditorApplication.delayCall -= ShowToolbarButtons;
            EditorApplication.delayCall += ShowToolbarButtons;
            AssemblyReloadEvents.beforeAssemblyReload -= Unsubscribe;
            AssemblyReloadEvents.beforeAssemblyReload += Unsubscribe;
        }

        private static void Unsubscribe()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.update -= RefreshAvailability;
            EditorApplication.delayCall -= ShowToolbarButtons;
            AssemblyReloadEvents.beforeAssemblyReload -= Unsubscribe;
        }

        [MainToolbarElement(PC_ELEMENT_ID, defaultDockPosition = MainToolbarDockPosition.Middle, defaultDockIndex = 0)]
        private static IEnumerable<MainToolbarElement> CreatePCButton()
        {
            yield return new MainToolbarButton(new MainToolbarContent("PC테스트", "1600×900 PC 화면으로 게임 씬을 바로 실행합니다."), PlayPC)
            { displayed = true, enabled = CanPlay };
        }

        [MainToolbarElement(MOBILE_ELEMENT_ID, defaultDockPosition = MainToolbarDockPosition.Middle, defaultDockIndex = 1)]
        private static IEnumerable<MainToolbarElement> CreateMobileButton()
        {
            yield return new MainToolbarButton(new MainToolbarContent("모바일테스트", "1440×3120 S24 Ultra 화면으로 모바일 UI를 바로 실행합니다."), PlayMobile)
            { displayed = true, enabled = CanPlay };
        }

        [MenuItem("DrawLiar/테스트/PC테스트", false, 100)]
        public static void PlayPC() => Play(MobileUILayout.EditorTestMode.PC, 1600, 900);

        [MenuItem("DrawLiar/테스트/모바일테스트", false, 101)]
        public static void PlayMobile() => Play(MobileUILayout.EditorTestMode.Mobile, 1440, 3120);

        [MenuItem("DrawLiar/테스트/PC테스트", true)]
        [MenuItem("DrawLiar/테스트/모바일테스트", true)]
        private static bool ValidatePlay() => CanPlay;

        private static void Play(MobileUILayout.EditorTestMode mode, int width, int height)
        {
            if (!CanPlay) return;
            var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(GAME_SCENE_PATH);
            if (scene == null)
            {
                Debug.LogError("게임 씬을 찾을 수 없습니다: " + GAME_SCENE_PATH);
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            try { SetGameViewResolution(width, height); }
            catch (Exception exception)
            {
                Debug.LogError("테스트 화면 크기를 설정하지 못했습니다: " + exception.GetBaseException().Message);
                return;
            }
            SessionState.SetString(START_SCENE_KEY, AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene));
            SessionState.SetInt(MobileUILayout.EDITOR_TEST_MODE_KEY, (int)mode);
            SessionState.SetBool(ACTIVE_KEY, true);
            EditorSceneManager.playModeStartScene = scene;
            EditorApplication.EnterPlaymode();
        }

        private static void SetGameViewResolution(int width, int height)
        {
            var assembly = typeof(UnityEditor.Editor).Assembly;
            var sizesType = assembly.GetType("UnityEditor.GameViewSizes", true);
            var singletonType = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
            var sizes = singletonType.GetProperty("instance", REFLECTION_FLAGS).GetValue(null);
            var groupType = assembly.GetType("UnityEditor.GameViewSizeGroupType", true);
            var groupKind = sizesType.GetProperty("currentGroupType", REFLECTION_FLAGS)?.GetValue(sizes)
                ?? Enum.Parse(groupType, EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android ? "Android" : "Standalone");
            var group = sizesType.GetMethod("GetGroup", REFLECTION_FLAGS).Invoke(sizes, new[] { groupKind });
            var groupClass = group.GetType();
            int count = (int)groupClass.GetMethod("GetBuiltinCount", REFLECTION_FLAGS).Invoke(group, null)
                + (int)groupClass.GetMethod("GetCustomCount", REFLECTION_FLAGS).Invoke(group, null);
            var sizeType = assembly.GetType("UnityEditor.GameViewSize", true);
            var kindType = assembly.GetType("UnityEditor.GameViewSizeType", true);
            var fixedResolution = Enum.Parse(kindType, "FixedResolution");
            int index = -1;
            for (int i = 0; i < count; i++)
            {
                var size = groupClass.GetMethod("GetGameViewSize", REFLECTION_FLAGS).Invoke(group, new object[] { i });
                if ((int)sizeType.GetProperty("width", REFLECTION_FLAGS).GetValue(size) == width
                    && (int)sizeType.GetProperty("height", REFLECTION_FLAGS).GetValue(size) == height
                    && fixedResolution.Equals(sizeType.GetProperty("sizeType", REFLECTION_FLAGS).GetValue(size)))
                { index = i; break; }
            }
            if (index < 0)
            {
                var size = Activator.CreateInstance(sizeType, REFLECTION_FLAGS, null,
                    new[] { fixedResolution, (object)width, height, "DrawLiar " + (width > height ? "PC" : "S24 Ultra") }, null);
                groupClass.GetMethod("AddCustomSize", REFLECTION_FLAGS).Invoke(group, new[] { size });
                index = count;
            }
            var viewType = assembly.GetType("UnityEditor.GameView", true);
            var view = EditorWindow.GetWindow(viewType);
            viewType.GetProperty("selectedSizeIndex", REFLECTION_FLAGS).SetValue(view, index);
            view.Focus(); view.Repaint();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(ACTIVE_KEY, false))
            {
                var previousPath = SessionState.GetString(START_SCENE_KEY, "");
                EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(previousPath) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(previousPath);
                SessionState.EraseString(START_SCENE_KEY);
                SessionState.EraseInt(MobileUILayout.EDITOR_TEST_MODE_KEY);
                SessionState.EraseBool(ACTIVE_KEY);
            }
            RefreshButtons();
        }

        private static void RefreshAvailability()
        {
            if (CanPlay != _lastCanPlay) RefreshButtons();
        }

        private static void RefreshButtons()
        {
            _lastCanPlay = CanPlay;
            MainToolbar.Refresh(PC_ELEMENT_ID);
            MainToolbar.Refresh(MOBILE_ELEMENT_ID);
        }

        private static void ShowToolbarButtons()
        {
            if (EditorPrefs.GetBool(_layoutKey, false)) return;
            // Unity가 저장한 기존 툴바 레이아웃에도 새 버튼을 한 번 표시한다.
            var show = typeof(MainToolbar).GetMethod("ShowAll", BindingFlags.NonPublic | BindingFlags.Static);
            if (show == null) return;
            show.Invoke(null, new object[] { PC_ELEMENT_ID });
            show.Invoke(null, new object[] { MOBILE_ELEMENT_ID });
            EditorPrefs.SetBool(_layoutKey, true);
            RefreshButtons();
        }
    }
}
#endif
