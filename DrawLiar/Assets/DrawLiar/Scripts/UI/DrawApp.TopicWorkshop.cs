using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using L = DrawLiar.DrawLocalization;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private VisualElement _workshopView, _workshopEditor, _workshopBrowser, _workshopLocalList, _workshopList;
        private TextField _workshopName, _workshopWords, _workshopSearchField;
        private DropdownField _workshopPublishLanguageField, _workshopBrowseLanguageField, _workshopSortField;
        private Label _workshopLimits, _workshopError, _workshopOwnCount, _workshopTotal, _workshopPage;
        private Button _workshopSave, _workshopPublish, _workshopPrevious, _workshopNext, _workshopRefresh, _workshopSearchButton;
        private Toggle _workshopMineField;
        private bool _workshopActive, _workshopEventsAttached, _workshopBrowse, _workshopMine, _workshopBusy, _workshopLoading, _workshopRefreshPending, _workshopReady;
        private bool _workshopPublishLanguageChosen, _workshopBrowseLanguageChosen;
        private int _workshopVersion, _workshopOffset, _workshopPendingCalls, _workshopListRevision;
        private string _workshopAccountId, _workshopPublishLanguage, _workshopBrowseLanguage, _workshopNameDraft = "", _workshopWordsDraft = "";
        private TopicData _workshopSelectedLocal;
        private string _workshopLoadError = "";
        private string _workshopSearch = "", _workshopSearchDraft = "", _workshopSort = "latest";
        private static readonly string[] WORKSHOP_SORT_CODES = { "latest", "downloads", "popular" };
        private static readonly string[] WORKSHOP_SORT_LABELS = { "최신순", "다운로드순", "인기순" };

        private void AttachTopicWorkshopEvents()
        {
            if (lobby == null || _workshopEventsAttached) return;
            lobby.TopicWorkshopChanged += OnTopicWorkshopChanged;
            _workshopEventsAttached = true;
            RefreshTopicWorkshopControls();
        }

        private void DetachTopicWorkshopEvents()
        {
            if (lobby != null && _workshopEventsAttached) lobby.TopicWorkshopChanged -= OnTopicWorkshopChanged;
            _workshopEventsAttached = false;
        }

        private void BeginTopicWorkshopPage()
        {
            CloseWorkshopPreview(false);
            _workshopVersion++;
            _workshopActive = true;
            _workshopAccountId = lobby.Profile?.AccountId;
            _workshopPublishLanguage = _workshopBrowseLanguage = L.CurrentLanguageCode;
            _workshopPublishLanguageChosen = _workshopBrowseLanguageChosen = false;
            _workshopNameDraft = _workshopWordsDraft = "";
            _workshopSelectedLocal = null;
            _workshopBrowse = _workshopMine = _workshopBusy = _workshopLoading = _workshopReady = false;
            _workshopLoadError = "";
            _workshopOffset = 0;
            _workshopSearch = _workshopSearchDraft = ""; _workshopSort = "latest";
            _workshopRefreshPending = true;
        }

        private void EndTopicWorkshopPage()
        {
            _workshopVersion++;
            _workshopActive = _workshopBusy = _workshopLoading = _workshopRefreshPending = false;
            _workshopNameDraft = _workshopWordsDraft = "";
            _workshopSelectedLocal = null;
            _workshopAccountId = _workshopPublishLanguage = _workshopBrowseLanguage = null;
            ClearTopicWorkshopView();
        }

        private void SuspendTopicWorkshopPage()
        {
            CloseWorkshopPreview(false);
            _workshopVersion++; _workshopBusy = _workshopLoading = false;
            _workshopRefreshPending = _workshopActive;
        }

        private void ClearTopicWorkshopView()
        {
            CloseWorkshopPreview(false);
            _workshopView = _workshopEditor = _workshopBrowser = _workshopLocalList = _workshopList = null;
            _workshopName = _workshopWords = _workshopSearchField = null;
            _workshopPublishLanguageField = _workshopBrowseLanguageField = _workshopSortField = null;
            _workshopLimits = _workshopError = _workshopOwnCount = _workshopTotal = _workshopPage = null;
            _workshopSave = _workshopPublish = _workshopPrevious = _workshopNext = _workshopRefresh = _workshopSearchButton = null;
            _workshopMineField = null; _workshopTitle = null;
        }

        private bool IsCurrentTopicWorkshop(int version, string accountId, VisualElement view = null) => this != null && isActiveAndEnabled
            && _workshopActive && _workshopVersion == version && (!inRoom && lobbyScreen == LobbyScreen.Topics || IsRoomTopicWorkshop) && lobby.IsAuthenticated
            && lobby.Profile?.AccountId == accountId && _workshopAccountId == accountId
            && (view == null || ReferenceEquals(_workshopView, view) && view.panel != null);

        private void BuildTopicWorkshopForm(VisualElement panel)
        {
            if (!_workshopActive || _workshopAccountId != lobby.Profile?.AccountId) BeginTopicWorkshopPage();
            var view = _workshopView = Box(panel, "topic-workshop");
            view.name = "topic-workshop";
            var tabs = Box(view, "row workshop-tabs");
            var local = Button(tabs, "내 주제", () => SelectTopicWorkshopTab(false), "secondary grow workshop-tab"); local.name = "workshop-local-tab";
            var browse = Button(tabs, "창작마당", () => SelectTopicWorkshopTab(true), "secondary grow workshop-tab"); browse.name = "workshop-browse-tab";
            _workshopEditor = Box(view, "workshop-editor");
            _workshopName = Field(_workshopEditor, "주제 이름", _workshopNameDraft); _workshopName.name = "workshop-topic-name";
            _workshopName.RegisterValueChangedCallback(evt =>
            {
                if (!ReferenceEquals(evt.target, _workshopName)) return;
                _workshopNameDraft = evt.newValue; RefreshTopicWorkshopControls();
            });
            _workshopWords = new TextField(L.Text("제시어")) { multiline = true, value = _workshopWordsDraft, name = "workshop-topic-words" };
            SetText(_workshopWords.labelElement, "제시어"); Classes(_workshopWords, "field workshop-words"); _workshopEditor.Add(_workshopWords);
            _workshopWords.textEdition.autoCorrection = false;
            _workshopWords.RegisterValueChangedCallback(evt =>
            {
                if (!ReferenceEquals(evt.target, _workshopWords)) return;
                _workshopWordsDraft = evt.newValue; RefreshTopicWorkshopControls();
            });
            Text(_workshopEditor, "쉼표 또는 줄바꿈으로 구분", "rules workshop-separator");
            _workshopPublishLanguageField = WorkshopLanguageField(_workshopEditor, "게시 언어", false, _workshopPublishLanguage, code =>
            {
                _workshopPublishLanguage = code; _workshopPublishLanguageChosen = true; RefreshTopicWorkshopControls();
            });
            _workshopPublishLanguageField.name = "workshop-publish-language";
            _workshopLimits = Text(_workshopEditor, "", "rules workshop-limits");
            _workshopError = RawText(_workshopEditor, "", "workshop-error"); _workshopError.name = "workshop-editor-error";
            var actions = Box(_workshopEditor, "row workshop-editor-actions");
            Button(actions, "새 주제", NewWorkshopTopic, "secondary grow workshop-action").name = "workshop-new";
            _workshopSave = Button(actions, "주제 저장", SaveWorkshopLocalTopic, "secondary grow"); _workshopSave.name = "workshop-save";
            _workshopPublish = Button(actions, "게시", () => PublishWorkshopTopic(null), "primary grow"); _workshopPublish.name = "workshop-publish";
            var localHeading = Box(_workshopEditor, "row workshop-list-heading");
            Text(localHeading, "내 주제", "section-title grow");
            _workshopOwnCount = Text(localHeading, "", "rules workshop-own-count");
            var localScroll = DrawSmoothScroll.Create(ScrollViewMode.Vertical); localScroll.name = "workshop-local-scroll"; localScroll.AddToClassList("workshop-local-scroll"); _workshopEditor.Add(localScroll);
            _workshopLocalList = Box(localScroll, "workshop-local-list");

            _workshopBrowser = Box(view, "workshop-browser");
            var search = Box(_workshopBrowser, "row workshop-search-row");
            _workshopSearchField = Field(search, "주제 검색", _workshopSearchDraft); _workshopSearchField.name = "workshop-search";
            _workshopSearchField.maxLength = TopicWorkshopRules.MAX_SEARCH_LENGTH; _workshopSearchField.AddToClassList("workshop-search-field");
            _workshopSearchField.RegisterValueChangedCallback(evt =>
            {
                if (ReferenceEquals(evt.target, _workshopSearchField)) _workshopSearchDraft = evt.newValue;
            });
            _workshopSearchField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
                if (!string.IsNullOrEmpty(Input.compositionString)) return;
                ApplyWorkshopSearch(); root.focusController?.IgnoreEvent(evt); evt.StopImmediatePropagation();
            });
            _workshopSearchButton = Button(search, "검색", ApplyWorkshopSearch, "secondary workshop-action"); _workshopSearchButton.name = "workshop-search-submit";
            var filters = Box(_workshopBrowser, "row workshop-filters");
            _workshopBrowseLanguageField = WorkshopLanguageField(filters, "언어", true, _workshopBrowseLanguage, code =>
            {
                _workshopBrowseLanguage = code; _workshopBrowseLanguageChosen = true; _workshopOffset = 0; ResetWorkshopBrowseScroll(); QueueTopicWorkshopRefresh();
            });
            _workshopBrowseLanguageField.name = "workshop-browse-language";
            _workshopSortField = new DropdownField(L.Text("정렬"), WORKSHOP_SORT_LABELS.Select(L.Text).ToList(), Math.Max(0, Array.IndexOf(WORKSHOP_SORT_CODES, _workshopSort))) { name = "workshop-sort" };
            SetText(_workshopSortField.labelElement, "정렬"); Classes(_workshopSortField, "field workshop-sort"); filters.Add(_workshopSortField);
            _workshopSortField.RegisterValueChangedCallback(evt =>
            {
                if (!ReferenceEquals(evt.target, _workshopSortField) || _workshopSortField.index < 0 || _workshopSortField.index >= WORKSHOP_SORT_CODES.Length) return;
                if (_workshopBusy || _workshopLoading || lobby.IsBusy) { UpdateWorkshopSortField(); return; }
                _workshopSort = WORKSHOP_SORT_CODES[_workshopSortField.index]; _workshopOffset = 0; ResetWorkshopBrowseScroll(); QueueTopicWorkshopRefresh();
            });
            _workshopMineField = new Toggle(L.Text("내 게시물")) { name = "workshop-mine", value = _workshopMine }; SetText(_workshopMineField.labelElement, "내 게시물"); _workshopMineField.AddToClassList("workshop-mine"); filters.Add(_workshopMineField);
            _workshopMineField.RegisterValueChangedCallback(evt =>
            {
                if (!ReferenceEquals(evt.target, _workshopMineField)) return;
                if (_workshopBusy || _workshopLoading || lobby.IsBusy) { _workshopMineField.SetValueWithoutNotify(_workshopMine); return; }
                _workshopMine = evt.newValue; _workshopOffset = 0; ResetWorkshopBrowseScroll(); QueueTopicWorkshopRefresh();
            });
            var browserHeading = Box(_workshopBrowser, "row workshop-list-heading");
            _workshopTotal = Text(browserHeading, "", "rules grow");
            _workshopRefresh = IconButton(browserHeading, "새로고침", DrawUIIcon.Kind.Refresh, QueueTopicWorkshopRefresh, "workshop-refresh");
            var scroll = DrawSmoothScroll.Create(ScrollViewMode.Vertical); scroll.name = "workshop-browse-scroll"; scroll.AddToClassList("workshop-browse-scroll"); _workshopBrowser.Add(scroll);
            _workshopList = Box(scroll, "workshop-list");
            var pages = Box(_workshopBrowser, "row workshop-pages");
            _workshopPrevious = Button(pages, "이전", () => ChangeTopicWorkshopPage(-1), "secondary"); _workshopPrevious.name = "workshop-previous";
            _workshopPage = RawText(pages, "", "workshop-page grow"); _workshopPage.languageDirection = LanguageDirection.LTR;
            _workshopNext = Button(pages, "다음", () => ChangeTopicWorkshopPage(1), "secondary"); _workshopNext.name = "workshop-next";
            RenderWorkshopLocalTopics(); RenderTopicWorkshopList(); SelectTopicWorkshopTab(_workshopBrowse); RefreshTopicWorkshopControls();
            view.schedule.Execute(RefreshTopicWorkshopControls).StartingIn(1);
        }

        private DropdownField WorkshopLanguageField(VisualElement parent, string label, bool all, string selected, Action<string> changed)
        {
            var languages = L.AvailableLanguages;
            var codes = (all ? new[] { "" }.Concat(languages.Select(language => language.Code)) : languages.Select(language => language.Code)).ToArray();
            var labels = (all ? new[] { L.Text("전체") }.Concat(languages.Select(language => language.DisplayName)) : languages.Select(language => language.DisplayName)).ToList();
            var field = new DropdownField(L.Text(label), labels, Mathf.Max(0, Array.IndexOf(codes, selected))); SetText(field.labelElement, label); Classes(field, "field workshop-language"); parent.Add(field);
            field.RegisterValueChangedCallback(evt =>
            {
                if (!ReferenceEquals(evt.target, field) || field.index < 0 || field.index >= codes.Length) return;
                if (!_workshopActive || !ReferenceEquals(field, all ? _workshopBrowseLanguageField : _workshopPublishLanguageField)) return;
                if (_workshopBusy || _workshopLoading || lobby.IsBusy)
                {
                    int index = Mathf.Max(0, Array.IndexOf(codes, all ? _workshopBrowseLanguage : _workshopPublishLanguage));
                    field.SetValueWithoutNotify(field.choices[index]); return;
                }
                changed(codes[field.index]);
            });
            return field;
        }

        private void SelectTopicWorkshopTab(bool browse)
        {
            if (!_workshopActive || _workshopView == null) return;
            CloseWorkshopPreview(false);
            bool entering = browse && !_workshopBrowse;
            _workshopBrowse = browse;
            if (_workshopTitle != null) SetText(_workshopTitle, browse ? "창작마당" : "나만의 주제");
            _workshopEditor.style.display = browse ? DisplayStyle.None : DisplayStyle.Flex;
            _workshopBrowser.style.display = browse ? DisplayStyle.Flex : DisplayStyle.None;
            _workshopView.Q<Button>("workshop-local-tab").EnableInClassList("workshop-tab-selected", !browse);
            _workshopView.Q<Button>("workshop-browse-tab").EnableInClassList("workshop-tab-selected", browse);
            HideMobileScrollers();
            if (entering) QueueTopicWorkshopRefresh();
        }

        private bool TryWorkshopTopic(string name, string words, out TopicWorkshopPublishRequest topic, out string error, bool forPublish = false)
        {
            var request = new TopicWorkshopPublishRequest { Name = name, Words = TopicWorkshopRules.SplitWords(words), LanguageCode = _workshopPublishLanguage };
            bool valid = TopicWorkshopRules.TryNormalize(request, lobby.TopicWorkshopPolicy, out topic, out string code, requirePublishMinimum: forPublish);
            error = valid ? "" : lobby.TopicWorkshopErrorMessage(code);
            return valid;
        }

        private bool IsLegacyWorkshopTopic(TopicData topic) => topic != null && !TryWorkshopTopic(topic.Name, string.Join("\n", topic.Words ?? Array.Empty<string>()), out _, out _);

        private void RefreshTopicWorkshopControls()
        {
            if (_workshopView?.panel == null || !_workshopActive) return;
            if (!lobby.IsAuthenticated || _workshopAccountId != lobby.Profile?.AccountId) { _workshopView.SetEnabled(false); EndTopicWorkshopPage(); return; }
            var limits = lobby.TopicWorkshopPolicy.Limits;
            bool blocked = _workshopBusy || _workshopLoading || lobby.IsBusy || !isActiveAndEnabled;
            bool legacy = IsLegacyWorkshopTopic(_workshopSelectedLocal);
            _workshopName.isReadOnly = _workshopWords.isReadOnly = blocked || legacy;
            _workshopName.maxLength = legacy || TopicWorkshopRules.TextLength(_workshopNameDraft) > limits.NameMaxLength ? -1 : limits.NameMaxLength;
            _workshopPublishLanguageField.SetEnabled(!blocked); _workshopBrowseLanguageField.SetEnabled(!blocked); _workshopMineField.SetEnabled(!blocked);
            _workshopSearchField.isReadOnly = blocked; _workshopSortField.SetEnabled(!blocked);
            _workshopView.Query<Button>(className: "workshop-action").ForEach(button => button.SetEnabled(!blocked));
            int ownCount = lobby.TopicWorkshop?.OwnCount ?? 0;
            _workshopView.Query<Button>(className: "workshop-row-action").ForEach(button => button.SetEnabled(!blocked && ownCount < limits.MaxUploadsPerAccount
                && button.userData is TopicData value && TryWorkshopTopic(value.Name, string.Join("\n", value.Words ?? Array.Empty<string>()), out _, out _, forPublish: true)));
            bool valid = TryWorkshopTopic(_workshopNameDraft, _workshopWordsDraft, out _, out string error);
            bool publishValid = TryWorkshopTopic(_workshopNameDraft, _workshopWordsDraft, out _, out string publishError, forPublish: true);
            SetText(_workshopLimits, "이름 {0}자 · 제시어 {1}자 · 게시 {2}~{3}개", limits.NameMaxLength, limits.WordMaxLength, limits.MinWordsPerTopic, limits.MaxWordsPerTopic);
            SetText(_workshopOwnCount, "내 게시물 {0} / {1}", ownCount, limits.MaxUploadsPerAccount);
            _workshopOwnCount.style.display = lobby.TopicWorkshop == null ? DisplayStyle.None : DisplayStyle.Flex;
            _workshopSave.SetEnabled(!blocked && !legacy && valid);
            _workshopPublish.SetEnabled(!blocked && !legacy && publishValid && ownCount < limits.MaxUploadsPerAccount);
            SetRawText(_workshopError, legacy ? L.Text("현재 제한을 초과한 기존 주제는 읽기 전용입니다.") : _workshopNameDraft.Length + _workshopWordsDraft.Length > 0 ? valid ? publishError : error : "");
            _workshopError.style.display = string.IsNullOrEmpty(_workshopError.text) ? DisplayStyle.None : DisplayStyle.Flex;
            _workshopRefresh.SetEnabled(!blocked);
            _workshopPrevious.SetEnabled(!blocked && _workshopOffset > 0);
            _workshopNext.SetEnabled(!blocked && _workshopOffset + Math.Max(1, lobby.TopicWorkshop?.Limit ?? 1) < (lobby.TopicWorkshop?.Total ?? 0));
            if (_workshopRefreshPending && !blocked) _ = RefreshTopicWorkshopPageAsync();
        }

        private void NewWorkshopTopic()
        {
            if (_workshopBusy || _workshopLoading || lobby.IsBusy || !IsCurrentTopicWorkshop(_workshopVersion, _workshopAccountId, _workshopView)) return;
            _workshopSelectedLocal = null; _workshopNameDraft = _workshopWordsDraft = "";
            _workshopName.SetValueWithoutNotify(""); _workshopWords.SetValueWithoutNotify("");
            RefreshTopicWorkshopControls(); _workshopName.Focus();
        }

        private void EditWorkshopLocalTopic(TopicData topic)
        {
            if (_workshopBusy || _workshopLoading || lobby.IsBusy || !IsCurrentTopicWorkshop(_workshopVersion, _workshopAccountId, _workshopView)) return;
            _workshopSelectedLocal = topic; _workshopNameDraft = topic.Name; _workshopWordsDraft = string.Join("\n", topic.Words ?? Array.Empty<string>());
            _workshopName.maxLength = -1; _workshopName.SetValueWithoutNotify(_workshopNameDraft); _workshopWords.SetValueWithoutNotify(_workshopWordsDraft);
            SelectTopicWorkshopTab(false); RefreshTopicWorkshopControls();
        }

        private void SaveWorkshopLocalTopic()
        {
            if (_workshopBusy || _workshopLoading || lobby.IsBusy || !IsCurrentTopicWorkshop(_workshopVersion, _workshopAccountId, _workshopView) || IsLegacyWorkshopTopic(_workshopSelectedLocal)) return;
            try
            {
                if (!TryWorkshopTopic(_workshopNameDraft, _workshopWordsDraft, out var topic, out string error)) throw new ArgumentException(error);
                var asset = Resources.Load<TextAsset>("DrawLiar/GameData"); var builtin = asset != null ? JsonUtility.FromJson<GameData>(asset.text) : null;
                if (builtin?.Topics?.Any(value => GameRules.NormalizeGuess(value.Name) == GameRules.NormalizeGuess(topic.Name)) == true) throw new ArgumentException("기본 주제와 다른 이름을 입력하세요.");
                GameDataStore.UpsertCustomTopic(topic.Name, string.Join("\n", topic.Words));
                SelectWorkshopTopic(topic.Name); RenderWorkshopLocalTopics(); Toast("새 주제를 저장했습니다.");
            }
            catch (Exception exception) { DrawAudio.Instance?.Play(DrawSound.UiError); Toast(exception.Message); }
        }

        private void SelectWorkshopTopic(string name)
        {
            var settings = TopicWorkshopSettings;
            if (settings == null) return;
            settings.Topics = (settings.Topics ?? Array.Empty<string>()).Append(name).Distinct().ToArray();
            SelectWorkshopRoomTopic(name);
        }

        private void RenderWorkshopLocalTopics()
        {
            if (_workshopLocalList == null) return;
            _workshopLocalList.Clear();
            foreach (var topic in GameDataStore.LoadCustomTopics())
            {
                var row = Box(_workshopLocalList, "workshop-local-entry");
                var edit = Button(row, "", () => EditWorkshopLocalTopic(topic), "secondary grow workshop-local-name workshop-action"); SetRawText(edit, topic.Name); edit.tooltip = topic.Name;
                var actions = Box(row, "row workshop-local-actions");
                var publish = Button(actions, "게시", () => PublishWorkshopTopic(topic), "secondary grow workshop-row-action workshop-local-publish"); publish.userData = topic; publish.name = "workshop-local-publish-" + topic.Name;
                Button(actions, "삭제", () =>
                {
                    if (_workshopBusy || _workshopLoading || lobby.IsBusy || !IsCurrentTopicWorkshop(_workshopVersion, _workshopAccountId, _workshopView)) return;
                    try
                    {
                        GameDataStore.DeleteCustomTopic(topic.Name);
                        var settings = TopicWorkshopSettings;
                        if (settings != null) settings.Topics = settings.Topics?.Where(value => value != topic.Name).ToArray();
                        if (IsRoomTopicWorkshop) _roomOptionsCustomTopics.Remove(topic.Name);
                        if (_workshopSelectedLocal?.Name == topic.Name) NewWorkshopTopic();
                        RenderWorkshopLocalTopics(); RefreshTopicWorkshopControls();
                    }
                    catch (Exception exception) { Toast(exception.Message); }
                }, "danger grow workshop-action").name = "workshop-local-delete-" + topic.Name;
            }
        }

        private void PublishWorkshopTopic(TopicData local)
        {
            string name = local?.Name ?? _workshopNameDraft;
            string words = local == null ? _workshopWordsDraft : string.Join("\n", local.Words ?? Array.Empty<string>());
            if (local != null && IsLegacyWorkshopTopic(local)) return;
            RunTopicWorkshopAction(async (version, account, view) =>
            {
                if (!TryWorkshopTopic(name, words, out var topic, out string error, forPublish: true)) throw new ArgumentException(error);
                await lobby.PublishTopicWorkshopAsync(topic.Name, string.Join("\n", topic.Words), topic.LanguageCode);
                if (IsCurrentTopicWorkshop(version, account, view)) Toast("주제를 게시했습니다.");
            });
        }

        private void RenderTopicWorkshopList()
        {
            if (_workshopList == null) return;
            var list = _workshopList;
            var scroll = list.GetFirstAncestorOfType<ScrollView>();
            var offset = scroll?.scrollOffset ?? Vector2.zero;
            int revision = ++_workshopListRevision;
            DrawSmoothScroll.Bind(scroll);
            var response = lobby.TopicWorkshop;
            _workshopList.Clear();
            if (!_workshopReady || response == null)
            {
                SetRawText(_workshopTotal, ""); SetRawText(_workshopPage, "");
                if (string.IsNullOrEmpty(_workshopLoadError)) Text(_workshopList, "처리 중…", "workshop-empty muted");
                else RawText(_workshopList, _workshopLoadError, "workshop-empty workshop-error");
                return;
            }
            SetText(_workshopTotal, "게시물 {0}개", response.Total);
            int limit = Math.Max(1, response.Limit); SetRawText(_workshopPage, response.Total == 0 ? "0 / 0" : (response.Offset / limit + 1) + " / " + ((response.Total + limit - 1) / limit));
            if (response.Items.Length == 0) Text(_workshopList, string.IsNullOrEmpty(_workshopSearch) ? "게시된 주제가 없습니다." : "검색 결과가 없습니다.", "workshop-empty muted");
            foreach (var entry in response.Items)
            {
                var row = Box(_workshopList, "workshop-entry"); row.name = "workshop-topic-" + entry.Id;
                RawText(row, entry.Name, "workshop-topic-name");
                var metadata = Box(row, "row workshop-metadata"); RawText(metadata, entry.CreatorName, "workshop-creator grow");
                RawText(metadata, L.AvailableLanguages.FirstOrDefault(language => language.Code == entry.LanguageCode)?.DisplayName ?? entry.LanguageCode, "workshop-entry-language");
                var rating = Box(row, "row workshop-rating");
                Text(rating, "제시어 {0}개 · 다운로드 {1}회 · 추천 {2}개", "rules workshop-topic-count grow", entry.WordCount, entry.DownloadCount, entry.RecommendationCount);
                var recommend = Button(rating, entry.IsRecommended ? "추천 취소" : "추천", () => RunTopicWorkshopAction((version, account, view) =>
                    lobby.RecommendTopicWorkshopAsync(entry.Id, !entry.IsRecommended)), "secondary workshop-action workshop-recommend");
                recommend.name = "workshop-recommend-" + entry.Id;
                SetTooltip(recommend, entry.IsRecommended ? "추천 취소" : "추천");
                recommend.EnableInClassList("workshop-recommended", entry.IsRecommended);
                var actions = Box(row, "row workshop-entry-actions");
                Button preview = null;
                preview = Button(actions, "미리보기", () => OpenWorkshopPreview(entry, preview), "secondary grow workshop-action workshop-preview");
                preview.name = "workshop-preview-" + entry.Id;
                Button(actions, "다운로드", () => RunTopicWorkshopAction(async (version, account, view) =>
                {
                    var downloaded = await lobby.DownloadTopicWorkshopAsync(entry.Id);
                    if (!IsCurrentTopicWorkshop(version, account, view)) return;
                    SelectWorkshopTopic(downloaded.Name); RenderWorkshopLocalTopics(); Toast("주제를 다운로드했습니다.");
                }), "primary grow workshop-action workshop-download").name = "workshop-download-" + entry.Id;
                if (entry.IsMine) Button(actions, "게시물 삭제", () => RunTopicWorkshopAction(async (version, account, view) =>
                {
                    await lobby.DeleteTopicWorkshopAsync(entry.Id);
                    if (IsCurrentTopicWorkshop(version, account, view)) Toast("게시물을 삭제했습니다.");
                }), "secondary grow workshop-action workshop-delete").name = "workshop-delete-" + entry.Id;
            }
            scroll?.schedule.Execute(() =>
            {
                if (_workshopList != list || revision != _workshopListRevision || scroll.panel == null) return;
                scroll.scrollOffset = new Vector2(0, Mathf.Clamp(offset.y, 0, Mathf.Max(0, scroll.verticalScroller.highValue)));
            }).StartingIn(20);
        }

        private void RunTopicWorkshopAction(Func<int, string, VisualElement, Task> action)
        {
            if (_workshopBusy || _workshopLoading || lobby.IsBusy || !IsCurrentTopicWorkshop(_workshopVersion, _workshopAccountId, _workshopView)) return;
            _ = TopicWorkshopActionAsync(action);
        }

        private async Task TopicWorkshopActionAsync(Func<int, string, VisualElement, Task> action)
        {
            int version = _workshopVersion; string account = _workshopAccountId; var view = _workshopView;
            _workshopBusy = true; _workshopPendingCalls++; RefreshTopicWorkshopControls();
            try { await action(version, account, view); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { if (IsCurrentTopicWorkshop(version, account, view)) { DrawAudio.Instance?.Play(DrawSound.UiError); Toast(exception.Message); } }
            finally
            {
                _workshopPendingCalls--;
                if (IsCurrentTopicWorkshop(version, account))
                {
                    _workshopBusy = false; RenderTopicWorkshopList(); RefreshTopicWorkshopControls();
                }
            }
        }

        private void QueueTopicWorkshopRefresh()
        {
            if (!_workshopActive || !isActiveAndEnabled) return;
            CloseWorkshopPreview(false);
            _workshopRefreshPending = true; _workshopReady = false; _workshopLoadError = ""; RenderTopicWorkshopList();
            RefreshTopicWorkshopControls();
        }

        private async Task RefreshTopicWorkshopPageAsync()
        {
            int version = _workshopVersion; string account = _workshopAccountId, language = _workshopBrowseLanguage, search = _workshopSearch, sort = _workshopSort; bool mine = _workshopMine; int offset = _workshopOffset; var view = _workshopView;
            bool CurrentQuery() => language == _workshopBrowseLanguage && mine == _workshopMine && offset == _workshopOffset && search == _workshopSearch && sort == _workshopSort;
            _workshopRefreshPending = false; _workshopLoading = true; _workshopPendingCalls++; RefreshTopicWorkshopControls();
            try
            {
                await lobby.RefreshTopicWorkshopAsync(language, mine, offset, search, sort);
                if (IsCurrentTopicWorkshop(version, account) && CurrentQuery())
                {
                    var response = lobby.TopicWorkshop;
                    if (response != null && _workshopOffset > 0 && _workshopOffset >= response.Total)
                    {
                        int limit = Math.Max(1, response.Limit);
                        _workshopOffset = response.Total == 0 ? 0 : (response.Total - 1) / limit * limit;
                        ResetWorkshopBrowseScroll();
                        QueueTopicWorkshopRefresh();
                    }
                    else { _workshopReady = true; RenderTopicWorkshopList(); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (IsCurrentTopicWorkshop(version, account) && CurrentQuery())
                {
                    _workshopLoadError = exception.Message; RenderTopicWorkshopList();
                    if (IsCurrentTopicWorkshop(version, account, view)) { DrawAudio.Instance?.Play(DrawSound.UiError); Toast(exception.Message); }
                }
            }
            finally
            {
                _workshopPendingCalls--;
                if (IsCurrentTopicWorkshop(version, account)) { _workshopLoading = false; RefreshTopicWorkshopControls(); }
            }
        }

        private void ChangeTopicWorkshopPage(int direction)
        {
            if (_workshopBusy || _workshopLoading || lobby.IsBusy) return;
            _workshopOffset = Math.Max(0, _workshopOffset + direction * Math.Max(1, lobby.TopicWorkshop?.Limit ?? 1)); ResetWorkshopBrowseScroll(); QueueTopicWorkshopRefresh();
        }

        private void ApplyWorkshopSearch()
        {
            if (_workshopBusy || _workshopLoading || lobby.IsBusy || !IsCurrentTopicWorkshop(_workshopVersion, _workshopAccountId, _workshopView)) return;
            _workshopSearch = _workshopSearchDraft.Trim(); _workshopOffset = 0; ResetWorkshopBrowseScroll(); QueueTopicWorkshopRefresh();
        }

        private void ResetWorkshopBrowseScroll()
        {
            _workshopListRevision++;
            var scroll = _workshopList?.GetFirstAncestorOfType<ScrollView>();
            if (scroll == null) return;
            DrawSmoothScroll.Bind(scroll); scroll.scrollOffset = Vector2.zero;
        }

        private void UpdateWorkshopSortField()
        {
            if (_workshopSortField == null) return;
            _workshopSortField.choices = WORKSHOP_SORT_LABELS.Select(L.Text).ToList();
            _workshopSortField.SetValueWithoutNotify(_workshopSortField.choices[Math.Max(0, Array.IndexOf(WORKSHOP_SORT_CODES, _workshopSort))]);
        }

        private void OnTopicWorkshopChanged()
        {
            if (_workshopView?.panel == null) return;
            if (_workshopAccountId != lobby.Profile?.AccountId) { EndTopicWorkshopPage(); return; }
            var response = lobby.TopicWorkshop;
            if (response == null) { _workshopReady = false; _workshopRefreshPending = true; RenderTopicWorkshopList(); RefreshTopicWorkshopControls(); return; }
            if (!_workshopLoading && response != null && _workshopOffset > 0 && _workshopOffset >= response.Total)
            {
                int limit = Math.Max(1, response.Limit); _workshopOffset = response.Total == 0 ? 0 : (response.Total - 1) / limit * limit;
                ResetWorkshopBrowseScroll();
                QueueTopicWorkshopRefresh(); return;
            }
            if (!_workshopLoading) RenderTopicWorkshopList();
            RefreshTopicWorkshopControls();
        }

        private void OnTopicWorkshopLanguageChanged()
        {
            if (!_workshopActive || _workshopView == null) return;
            CloseWorkshopPreview(false);
            if (!_workshopPublishLanguageChosen) _workshopPublishLanguage = L.CurrentLanguageCode;
            bool refresh = !_workshopBrowseLanguageChosen && _workshopBrowseLanguage != L.CurrentLanguageCode;
            if (refresh) { _workshopBrowseLanguage = L.CurrentLanguageCode; _workshopOffset = 0; ResetWorkshopBrowseScroll(); }
            void Update(DropdownField field, bool all, string code)
            {
                var choices = (all ? new[] { L.Text("전체") }.Concat(L.AvailableLanguages.Select(language => language.DisplayName)) : L.AvailableLanguages.Select(language => language.DisplayName)).ToList();
                int index = all && code == "" ? 0 : L.AvailableLanguages.ToList().FindIndex(language => language.Code == code) + (all ? 1 : 0);
                field.choices = choices; field.SetValueWithoutNotify(choices[Mathf.Clamp(index, 0, choices.Count - 1)]);
            }
            Update(_workshopPublishLanguageField, false, _workshopPublishLanguage); Update(_workshopBrowseLanguageField, true, _workshopBrowseLanguage);
            UpdateWorkshopSortField();
            if (refresh) QueueTopicWorkshopRefresh(); else RefreshTopicWorkshopControls();
        }
    }
}
