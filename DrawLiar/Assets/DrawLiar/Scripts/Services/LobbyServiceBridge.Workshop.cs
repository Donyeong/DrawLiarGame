using System;
using System.Threading.Tasks;
using UnityEngine;

namespace DrawLiar
{
    public sealed partial class LobbyServiceBridge
    {
        private TopicWorkshopPolicy _topicWorkshopPolicy;
        private string _topicWorkshopLanguage = "";
        private bool _topicWorkshopMine;
        private int _topicWorkshopOffset, _topicWorkshopRevision;
        public TopicWorkshopListResponse TopicWorkshop { get; private set; }
        public event Action TopicWorkshopChanged;

        public TopicWorkshopPolicy TopicWorkshopPolicy
        {
            get
            {
                if (_topicWorkshopPolicy != null) return _topicWorkshopPolicy;
                var asset = Resources.Load<TextAsset>("DrawLiar/TopicWorkshopPolicy");
                var policy = asset == null ? null : JsonUtility.FromJson<TopicWorkshopPolicy>(asset.text);
                if (!TopicWorkshopRules.ValidatePolicy(policy))
                    throw new InvalidOperationException(DrawLocalization.Text("창작마당 설정을 불러오지 못했습니다."));
                return _topicWorkshopPolicy = policy;
            }
        }

        public Task RefreshTopicWorkshopAsync(string language, bool mine = false, int offset = 0) => RunAsync(async () =>
        {
            RequireLogin();
            _topicWorkshopLanguage = language ?? DrawLocalization.CurrentLanguageCode;
            _topicWorkshopMine = mine;
            _topicWorkshopOffset = Math.Max(0, offset);
            int revision = ++_topicWorkshopRevision;
            await FetchTopicWorkshopAsync(_gameSession, Profile.AccountId, revision);
        });

        private async Task FetchTopicWorkshopAsync(string session, string accountId, int revision)
        {
            string path = "/api/topic-workshop?language=" + Uri.EscapeDataString(_topicWorkshopLanguage)
                + "&mine=" + (_topicWorkshopMine ? "true" : "false")
                + "&offset=" + _topicWorkshopOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var result = await SendAsync<TopicWorkshopListResponse>(_gameServerUrl, path, "GET", bearer: session);
            EnsureTopicWorkshopIdentity(session, accountId, revision);
            if (!TopicWorkshopRules.ValidatePolicy(result.Policy) || result.Offset < 0 || result.Limit <= 0
                || result.Total < 0 || result.OwnCount < 0)
                throw new InvalidOperationException(DrawLocalization.Text("창작마당 설정을 불러오지 못했습니다."));
            result.Items ??= Array.Empty<TopicWorkshopEntry>();
            _topicWorkshopPolicy = result.Policy;
            _topicWorkshopOffset = result.Offset;
            TopicWorkshop = result;
            TopicWorkshopChanged?.Invoke();
        }

        public Task PublishTopicWorkshopAsync(string name, string words, string language) => RunAsync(async () =>
        {
            RequireLogin();
            var request = new TopicWorkshopPublishRequest
            {
                Name = name, Words = TopicWorkshopRules.SplitWords(words), LanguageCode = language
            };
            if (!TopicWorkshopRules.TryNormalize(request, TopicWorkshopPolicy, out var normalized, out string error))
                throw new InvalidOperationException(TopicWorkshopErrorMessage(error));
            string session = _gameSession, accountId = Profile.AccountId;
            int revision = ++_topicWorkshopRevision;
            await SendGameAsync<TopicWorkshopDetailResponse>("/api/topic-workshop", "POST", normalized);
            EnsureTopicWorkshopIdentity(session, accountId, revision);
            await RefreshAfterWorkshopMutationAsync(session, accountId, revision);
            SetStatus("주제를 게시했습니다.");
        });

        public async Task<TopicData> DownloadTopicWorkshopAsync(string id)
        {
            TopicData imported = null;
            await RunAsync(async () =>
            {
                RequireLogin();
                string session = _gameSession, accountId = Profile.AccountId;
                int revision = ++_topicWorkshopRevision;
                var result = await SendGameAsync<TopicWorkshopDetailResponse>("/api/topic-workshop/" + Uri.EscapeDataString(id), "GET");
                EnsureTopicWorkshopIdentity(session, accountId, revision);
                if (!string.Equals(result.Topic?.Id, id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(DrawLocalization.Text("주제 이름과 제시어를 확인하세요."));
                imported = GameDataStore.ImportWorkshopTopic(result, TopicWorkshopPolicy);
                await RefreshAfterWorkshopMutationAsync(session, accountId, revision);
                SetStatus("주제를 다운로드했습니다.");
            });
            return imported;
        }

        public Task DeleteTopicWorkshopAsync(string id) => RunAsync(async () =>
        {
            RequireLogin();
            string session = _gameSession, accountId = Profile.AccountId;
            int revision = ++_topicWorkshopRevision;
            await SendGameAsync<ApiError>("/api/topic-workshop/" + Uri.EscapeDataString(id), "DELETE");
            EnsureTopicWorkshopIdentity(session, accountId, revision);
            await RefreshAfterWorkshopMutationAsync(session, accountId, revision);
            SetStatus("게시물을 삭제했습니다.");
        });

        private async Task RefreshAfterWorkshopMutationAsync(string session, string accountId, int revision)
        {
            try { await FetchTopicWorkshopAsync(session, accountId, revision); }
            catch (OperationCanceledException) { throw; }
            catch
            {
                EnsureTopicWorkshopIdentity(session, accountId, revision);
                TopicWorkshop = null;
                TopicWorkshopChanged?.Invoke();
            }
        }

        private void EnsureTopicWorkshopIdentity(string session, string accountId, int revision)
        {
            if (revision != _topicWorkshopRevision || session != _gameSession || accountId != Profile?.AccountId || _loggingOut)
                throw new OperationCanceledException();
        }

        private void ResetTopicWorkshop()
        {
            ++_topicWorkshopRevision;
            TopicWorkshop = null;
            _topicWorkshopPolicy = null;
            _topicWorkshopLanguage = "";
            _topicWorkshopMine = false;
            _topicWorkshopOffset = 0;
            TopicWorkshopChanged?.Invoke();
        }

        public string TopicWorkshopErrorMessage(string code)
        {
            var limits = TopicWorkshopPolicy.Limits;
            switch (code)
            {
                case "TopicWorkshopInvalidName": return DrawLocalization.Format("주제 이름은 1~{0}자로 입력하세요.", limits.NameMaxLength);
                case "TopicWorkshopInvalidWords": return DrawLocalization.Format("제시어는 1~{0}자로 입력하세요.", limits.WordMaxLength);
                case "TopicWorkshopTooManyWords": return DrawLocalization.Format("제시어는 최대 {0}개까지 입력할 수 있습니다.", limits.MaxWordsPerTopic);
                case "TopicWorkshopUploadLimit": return DrawLocalization.Format("게시할 수 있는 주제는 최대 {0}개입니다.", limits.MaxUploadsPerAccount);
                case "TopicWorkshopInvalidLanguage": return DrawLocalization.Text("게시 언어를 선택하세요.");
                case "TopicWorkshopNameConflict": return DrawLocalization.Text("주제 이름이 이미 사용 중입니다. 다른 이름을 입력하세요.");
                case "TopicWorkshopUnavailable": return DrawLocalization.Text("게시물을 찾지 못했습니다. 목록을 새로고침하세요.");
                default: return DrawLocalization.Text("창작마당 설정을 불러오지 못했습니다.");
            }
        }
    }
}
