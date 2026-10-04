using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace DrawLiar
{
    public static class GameDataStore
    {
        private const int MaximumFileBytes = 8 * 1024 * 1024;
        private const int MaximumCustomTopics = 100;
        [Serializable]
        private sealed class CustomTopics { public TopicData[] Topics; }

        public static string CustomTopicsPath => Path.Combine(Application.persistentDataPath, "custom-topics.json");

        public static GameData Load(string customTopicsPath = null)
        {
            var asset = Resources.Load<TextAsset>("DrawLiar/GameData");
            var data = asset != null ? JsonUtility.FromJson<GameData>(asset.text) : new GameData();
            data.Scoring ??= new ScoreRules();
            data.Scoring.LiarUncaught = Mathf.Clamp(data.Scoring.LiarUncaught, 0, 1000);
            data.Scoring.LiarCorrectGuess = Mathf.Clamp(data.Scoring.LiarCorrectGuess, 0, 1000);
            data.Scoring.CitizenCorrectVote = Mathf.Clamp(data.Scoring.CitizenCorrectVote, 0, 1000);
            data.Topics = Sanitize((data.Topics ?? Array.Empty<TopicData>()).Concat(LoadCustomTopics(customTopicsPath)).ToArray());
            if (data.Topics.Length == 0) data.Topics = new[] { new TopicData { Name = "과일", Words = new[] { "사과", "바나나", "수박" } } };
            return data;
        }

        public static TopicData[] LoadCustomTopics(string path = null)
        {
            try
            {
                return ReadCustomTopics(path ?? CustomTopicsPath);
            }
            catch (Exception exception) when (exception is IOException || exception is ArgumentException || exception is UnauthorizedAccessException)
            {
                Debug.LogWarning("커스텀 주제를 불러올 수 없어요: " + exception.Message);
                return Array.Empty<TopicData>();
            }
        }

        private static TopicData[] ReadCustomTopics(string path)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (path == CustomTopicsPath)
            {
                string content = DrawBrowserInterop.LoadCustomTopics();
                if (string.IsNullOrEmpty(content)) return Array.Empty<TopicData>();
                if (Encoding.UTF8.GetByteCount(content) > MaximumFileBytes) throw new IOException("커스텀 주제 파일은 8MiB까지 사용할 수 있습니다.");
                return ParseCustomTopics(content);
            }
#endif
            if (!File.Exists(path)) return Array.Empty<TopicData>();
            if (new FileInfo(path).Length > MaximumFileBytes) throw new IOException("커스텀 주제 파일은 8MiB까지 사용할 수 있습니다.");
            return ParseCustomTopics(File.ReadAllText(path));
        }

        private static TopicData[] ParseCustomTopics(string content)
        {
            var parsed = JsonUtility.FromJson<CustomTopics>(content);
            if (parsed?.Topics == null) throw new ArgumentException("커스텀 주제 파일 형식이 올바르지 않습니다. 기존 파일은 보존됩니다.");
            return Sanitize(parsed.Topics);
        }

        public static void SaveCustomTopics(TopicData[] topics, string path = null)
        {
            var valid = Sanitize(topics);
            if (valid.Length > MaximumCustomTopics) throw new ArgumentException("커스텀 주제는 최대 100개까지 저장할 수 있습니다.");
            var content = JsonUtility.ToJson(new CustomTopics { Topics = valid }, true);
            if (Encoding.UTF8.GetByteCount(content) > MaximumFileBytes) throw new ArgumentException("커스텀 주제 파일은 8MiB까지 저장할 수 있습니다.");
            path = path ?? CustomTopicsPath;
#if UNITY_WEBGL && !UNITY_EDITOR
            if (path == CustomTopicsPath) { DrawBrowserInterop.SaveCustomTopics(content); return; }
#endif
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, content);
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }

        public static void UpsertCustomTopic(string name, string words, string path = null)
        {
            name = GameRules.CleanText(name, 40);
            var parsed = Sanitize(new[] { new TopicData { Name = name, Words = (words ?? "").Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries) } });
            if (parsed.Length == 0) throw new ArgumentException("주제 이름과 하나 이상의 단어를 입력해 주세요.");
            SaveCustomTopics(ReadCustomTopics(path ?? CustomTopicsPath).Where(topic => topic.Name != name).Concat(parsed).ToArray(), path);
        }

        public static void DeleteCustomTopic(string name, string path = null) => SaveCustomTopics(ReadCustomTopics(path ?? CustomTopicsPath).Where(topic => topic.Name != name).ToArray(), path);

        private static TopicData[] Sanitize(TopicData[] topics)
        {
            return (topics ?? Array.Empty<TopicData>()).Where(topic => topic != null)
                .Select(topic => new TopicData
                {
                    Name = GameRules.CleanText(topic.Name, 40),
                    Words = (topic.Words ?? Array.Empty<string>()).Select(word => GameRules.CleanText(word, 40))
                        .Where(word => word.Length > 0).Distinct().Take(200).ToArray()
                }).Where(topic => topic.Name.Length > 0 && topic.Words.Length > 0)
                .GroupBy(topic => topic.Name).Select(group => group.Last()).ToArray();
        }
    }
}
