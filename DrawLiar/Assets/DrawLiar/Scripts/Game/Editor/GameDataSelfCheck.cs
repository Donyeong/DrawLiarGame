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

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("DrawLiar topic self-check failed: " + message);
        }
    }
}
