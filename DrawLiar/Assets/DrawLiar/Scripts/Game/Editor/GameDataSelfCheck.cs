using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace DrawLiar.Editor
{
    public static class GameDataSelfCheck
    {
        [UnityEditor.MenuItem("DrawLiar/Verify custom topic files")]
        public static void Run()
        {
            string directory = Path.Combine(Path.GetTempPath(), "DrawLiar-topics-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "topics.json");
            try
            {
                GameDataStore.UpsertCustomTopic("우리 주제", "사과,수박\n사과", path);
                Check(GameDataStore.LoadCustomTopics(path).Single().Words.SequenceEqual(new[] { "사과", "수박" }), "Create and reload deduplicated custom words.");
                GameDataStore.UpsertCustomTopic("우리 주제", "딸기", path);
                Check(GameDataStore.LoadCustomTopics(path).Single().Words.Single() == "딸기", "Update persists without duplicating topic.");
                GameDataStore.DeleteCustomTopic("우리 주제", path);
                Check(GameDataStore.LoadCustomTopics(path).Length == 0, "Delete persists.");
                VerifyBuiltInNameCollision(path);

                var topics = Enumerable.Range(0, 100).Select(index => new TopicData
                {
                    Name = "검증용 주제 " + index,
                    Words = Enumerable.Range(0, 200).Select(word => new string('가', 36) + word.ToString("D4")).ToArray()
                }).ToArray();
                GameDataStore.SaveCustomTopics(topics, path);
                Check(new FileInfo(path).Length > 1024 * 1024, "Fixture crosses old one-MiB read ceiling.");
                var loaded = GameDataStore.LoadCustomTopics(path);
                Check(loaded.Length == 100 && loaded.All(topic => topic.Words.Length == 200), "Every maximum-size saved topic reloads.");
                var combined = GameDataStore.Load(path);
                Check(topics.All(topic => combined.Topics.Any(entry => entry.Name == topic.Name)), "Combining built-ins retains all 100 custom topics.");
                string existing = File.ReadAllText(path);
                bool rejected = false;
                try { GameDataStore.UpsertCustomTopic("101번째", "단어", path); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected && File.ReadAllText(path) == existing, "Over-limit save reports failure and preserves existing file.");

                File.WriteAllText(path, "{\"unexpected\":true}");
                rejected = false;
                try { GameDataStore.UpsertCustomTopic("새 주제", "단어", path); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected && File.ReadAllText(path) == "{\"unexpected\":true}", "Unreadable topic data is never overwritten by an edit.");
                Debug.Log("DrawLiar custom topic file self-check PASS: isolated create/read/update/delete, large-file roundtrip, merge, limits, and preservation.");
            }
            finally
            {
                foreach (string file in new[] { path, path + ".bak", path + ".tmp" })
                    if (File.Exists(file)) File.Delete(file);
                if (Directory.Exists(directory)) Directory.Delete(directory);
            }
        }

        private static void VerifyBuiltInNameCollision(string path)
        {
            const string WORKSHOP_ID = "0823942d-3d51-49e0-b337-d54d13f332ed";
            string saved = "{\"Topics\":[{\"Name\":\"악기\",\"Words\":[\"우리 단어\"],\"WorkshopId\":\"" + WORKSHOP_ID
                + "\",\"LanguageCode\":\"ko\"},{\"Name\":\"악기2\",\"Words\":[\"다른 단어\"]}]}";
            File.WriteAllText(path, saved);
            var custom = GameDataStore.LoadCustomTopics(path);
            Check(File.ReadAllText(path) == saved, "기본 주제와 이름이 겹쳐도 읽기만으로 저장 파일을 변경하지 않습니다.");
            Check(custom.Select(topic => topic.Name).SequenceEqual(new[] { "악기3", "악기2" })
                && GameDataStore.LoadCustomTopics(path).Select(topic => topic.Name).SequenceEqual(custom.Select(topic => topic.Name)),
                "기존 커스텀 이름을 피한 표시 이름은 반복해서 불러와도 같습니다.");
            Check(custom[0].Words.Single() == "우리 단어" && custom[0].WorkshopId == WORKSHOP_ID && custom[0].LanguageCode == "ko",
                "이름이 겹친 창작마당 주제의 제시어와 게시물 정보는 보존합니다.");
            var combined = GameDataStore.Load(path);
            Check(combined.Topics.Single(topic => topic.Name == "악기").Words.Contains("피아노")
                && combined.Topics.Single(topic => topic.Name == "악기3").Words.Single() == "우리 단어",
                "기본 주제와 동명인 커스텀 주제를 각각 선택할 수 있습니다.");
            GameDataStore.UpsertCustomTopic("악기3", "갱신 단어", path);
            Check(GameDataStore.LoadCustomTopics(path).Single(topic => topic.Name == "악기3").Words.Single() == "갱신 단어",
                "표시된 이름으로 기존 커스텀 주제를 수정할 수 있습니다.");
            GameDataStore.DeleteCustomTopic("악기3", path);
            Check(GameDataStore.LoadCustomTopics(path).Single().Name == "악기2", "동명 주제를 삭제해도 다른 커스텀 주제는 유지합니다.");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("DrawLiar topic self-check failed: " + message);
        }
    }
}
