using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using L = DrawLiar.DrawLocalization;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private VisualElement _matchDrawingOverlay, _matchDrawingFrame, _matchDrawingReturnFocus;
        private CancellationTokenSource _matchDrawingLifetime, _matchDrawingLoad;
        private string _matchDrawingAccount;
        private ProfileMatchData _matchDrawingMatch;
        private MatchDrawingListResponse _matchDrawingList;
        private RoundDrawingData _matchDrawingRound;
        private Image _matchDrawingImage;
        private Texture2D _matchDrawingTexture;
        private DropdownField _matchDrawingRounds, _matchDrawingAuthors;
        private Label _matchDrawingExpiry, _matchDrawingTopic, _matchDrawingWord, _matchDrawingStatus;
        private Button _matchDrawingSave, _matchDrawingRetry;
        private int[] _matchDrawingAuthorIds = Array.Empty<int>();
        private int? _matchDrawingAuthor;
        private string _matchDrawingPreferredAuthor;
        private bool _matchDrawingLoading, _matchDrawingSaving, _matchDrawingUpdatingChoices;
        private int _matchDrawingVersion;
        private Action _matchDrawingRetryAction;

        private void MatchDrawingHistoryAction(VisualElement card, ProfileMatchData match)
        {
            if (match.CanViewDrawings)
            {
                Button open = null;
                open = Button(card, "그림 다시보기", () => OpenMatchDrawings(match, open), "secondary match-drawing-open");
                open.name = "match-drawing-open-" + match.MatchId;
            }
            else if (_loadedPublicProfile?.AccountId == lobby.Profile?.AccountId)
                Text(card, string.IsNullOrEmpty(match.DrawingExpiresAt) ? "보관된 그림이 없습니다." : "그림 보관기간이 만료되었습니다.", "public-profile-label");
        }

        private void OpenMatchDrawings(ProfileMatchData match, VisualElement origin)
        {
            if (!lobby.IsAuthenticated || !match.CanViewDrawings || !Guid.TryParse(match.MatchId, out _)) return;
            CloseMatchDrawings(false);
            surface?.CancelDrawing();
            _matchDrawingMatch = match;
            _matchDrawingAccount = lobby.Profile.AccountId;
            _matchDrawingReturnFocus = origin;
            _matchDrawingPreferredAuthor = _matchDrawingAccount;
            _matchDrawingLifetime = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            _profileModal?.SetEnabled(false);
            var popup = _matchDrawingOverlay = Box(root, "overlay enter utility-overlay match-drawing-overlay");
            popup.name = "match-drawing-overlay";
            var modal = Box(popup, "modal utility-popup match-drawing-popup");
            modal.name = "match-drawing-popup";
            var header = Box(modal, "row utility-popup-header");
            Text(header, "그림 다시보기", "utility-popup-title grow");
            IconButton(header, "닫기", DrawUIIcon.Kind.Close, () => CloseMatchDrawings(), "utility-popup-x");
            var body = Box(modal, "match-drawing-body grow");
            var details = Box(body, "match-drawing-details");
            _matchDrawingExpiry = Text(details, "", "match-drawing-expiry");
            var controls = Box(details, "row match-drawing-controls");
            _matchDrawingRounds = new DropdownField(L.Text("라운드"));
            _matchDrawingRounds.name = "match-drawing-round";
            _matchDrawingRounds.AddToClassList("field"); controls.Add(_matchDrawingRounds);
            SetText(_matchDrawingRounds.labelElement, "라운드");
            _matchDrawingAuthors = new DropdownField(L.Text("작성자"));
            _matchDrawingAuthors.name = "match-drawing-author";
            _matchDrawingAuthors.AddToClassList("field"); controls.Add(_matchDrawingAuthors);
            SetText(_matchDrawingAuthors.labelElement, "작성자");
            _matchDrawingRounds.RegisterValueChangedCallback(evt =>
            {
                if (!ReferenceEquals(evt.target, _matchDrawingRounds) || _matchDrawingUpdatingChoices || _matchDrawingLoading || _matchDrawingSaving) return;
                int index = _matchDrawingRounds.index;
                if (_matchDrawingList != null && index >= 0 && index < _matchDrawingList.Rounds.Length)
                    _ = LoadMatchDrawingRoundAsync(_matchDrawingList.Rounds[index].Round);
            });
            _matchDrawingAuthors.RegisterValueChangedCallback(evt =>
            {
                if (!ReferenceEquals(evt.target, _matchDrawingAuthors) || _matchDrawingUpdatingChoices || _matchDrawingLoading || _matchDrawingSaving) return;
                int index = _matchDrawingAuthors.index;
                if (index < 0 || index >= _matchDrawingAuthorIds.Length) return;
                _matchDrawingAuthor = _matchDrawingAuthorIds[index] < 0 ? (int?)null : _matchDrawingAuthorIds[index];
                _matchDrawingPreferredAuthor = _matchDrawingRound?.Players.FirstOrDefault(player => player.PlayerId == _matchDrawingAuthor)?.AccountId;
                _ = RenderMatchDrawingAsync();
            });
            _matchDrawingTopic = Text(details, "", "match-drawing-caption");
            _matchDrawingWord = Text(details, "", "match-drawing-caption");
            _matchDrawingFrame = Box(body, "match-drawing-frame grow");
            _matchDrawingImage = new Image { name = "match-drawing-image", scaleMode = ScaleMode.ScaleToFit };
            _matchDrawingImage.AddToClassList("match-drawing-image"); _matchDrawingFrame.Add(_matchDrawingImage);
            _matchDrawingImage.style.display = DisplayStyle.None;
            _matchDrawingFrame.RegisterCallback<GeometryChangedEvent>(_ => FitMatchDrawingImage());
            _matchDrawingStatus = Text(modal, "그림을 불러오는 중…", "match-drawing-status");
            var footer = Box(modal, "row match-drawing-actions");
            _matchDrawingRetry = Button(footer, "다시 시도", () => _matchDrawingRetryAction?.Invoke(), "secondary");
            _matchDrawingRetry.name = "match-drawing-retry";
            _matchDrawingSave = Button(footer, "이미지로 저장", () => _ = SaveMatchDrawingAsync(), "primary grow");
            _matchDrawingSave.name = "match-drawing-save";
            Button(footer, "닫기", () => CloseMatchDrawings(), "secondary").name = "match-drawing-close";
            popup.focusable = true; popup.Focus();
            popup.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape) { CloseMatchDrawings(); evt.StopImmediatePropagation(); }
                else if (evt.keyCode == KeyCode.Tab)
                {
                    var controlsInModal = modal.Query<VisualElement>().ToList().Where(element => element.canGrabFocus
                        && element.enabledInHierarchy && element.tabIndex >= 0 && IsVisible(element)).ToList();
                    if (controlsInModal.Count == 0) return;
                    int index = controlsInModal.IndexOf(root.focusController?.focusedElement as VisualElement);
                    int next = index < 0 ? (evt.shiftKey ? controlsInModal.Count - 1 : 0)
                        : (index + (evt.shiftKey ? -1 : 1) + controlsInModal.Count) % controlsInModal.Count;
                    root.focusController?.IgnoreEvent(evt); controlsInModal[next].Focus(); evt.StopImmediatePropagation();
                }
            }, TrickleDown.TrickleDown);
            popup.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (ReferenceEquals(evt.target, popup)) { CloseMatchDrawings(); evt.StopImmediatePropagation(); }
            });
            popup.RegisterCallback<DetachFromPanelEvent>(evt =>
            {
                if (ReferenceEquals(evt.target, popup) && _matchDrawingOverlay == popup) CloseMatchDrawings(false);
            });
            DrawUIMotion.ShowModal(popup, modal);
            _ = LoadMatchDrawingListAsync();
        }

        private bool IsCurrentMatchDrawing(VisualElement popup, int version, CancellationToken token) => this != null && isActiveAndEnabled
            && !token.IsCancellationRequested && _matchDrawingOverlay == popup && _matchDrawingVersion == version
            && lobby.IsAuthenticated && lobby.Profile?.AccountId == _matchDrawingAccount;

        private async Task LoadMatchDrawingListAsync()
        {
            var popup = _matchDrawingOverlay;
            if (popup == null) return;
            int version = ++_matchDrawingVersion;
            var token = _matchDrawingLifetime.Token;
            _matchDrawingLoading = true; _matchDrawingRetryAction = null; RefreshMatchDrawingControls();
            SetText(_matchDrawingStatus, "그림을 불러오는 중…");
            try
            {
                var response = await lobby.GetMatchDrawingsAsync(_matchDrawingMatch.MatchId, token);
                if (!IsCurrentMatchDrawing(popup, version, token)) return;
                _matchDrawingList = response;
                RefreshMatchDrawingText();
                await LoadMatchDrawingRoundAsync(response.Rounds[0].Round);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!IsCurrentMatchDrawing(popup, version, token)) return;
                SetText(_matchDrawingStatus, LobbyServiceBridge.UserFacingMessage(error));
                _matchDrawingRetryAction = () => _ = LoadMatchDrawingListAsync();
            }
            finally
            {
                if (IsCurrentMatchDrawing(popup, version, token)) { _matchDrawingLoading = false; RefreshMatchDrawingControls(); }
            }
        }

        private async Task LoadMatchDrawingRoundAsync(int round)
        {
            if (_matchDrawingOverlay == null) return;
            _matchDrawingLoad?.Cancel(); _matchDrawingLoad?.Dispose();
            _matchDrawingLoad = CancellationTokenSource.CreateLinkedTokenSource(_matchDrawingLifetime.Token);
            var token = _matchDrawingLoad.Token;
            var popup = _matchDrawingOverlay;
            int version = ++_matchDrawingVersion;
            _matchDrawingLoading = true; _matchDrawingRound = null; _matchDrawingRetryAction = null;
            ReleaseMatchDrawingTexture(); RefreshMatchDrawingControls(); SetText(_matchDrawingStatus, "그림을 불러오는 중…");
            try
            {
                var response = await lobby.GetMatchDrawingAsync(_matchDrawingMatch.MatchId, round, token);
                if (!IsCurrentMatchDrawing(popup, version, token)) return;
                _matchDrawingRound = response.Drawing;
                _matchDrawingUpdatingChoices = true;
                var players = response.Drawing.Players;
                bool relay = _matchDrawingMatch.Mode == (int)DrawingMode.Relay;
                _matchDrawingAuthorIds = relay ? new[] { -1 }.Concat(players.Select(player => player.PlayerId)).ToArray() : players.Select(player => player.PlayerId).ToArray();
                _matchDrawingAuthor = relay ? null : (int?)(players.FirstOrDefault(player => player.AccountId == _matchDrawingPreferredAuthor) ?? players[0]).PlayerId;
                RefreshMatchDrawingText();
                _matchDrawingUpdatingChoices = false;
                await RenderMatchDrawingAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!IsCurrentMatchDrawing(popup, version, token)) return;
                SetText(_matchDrawingStatus, LobbyServiceBridge.UserFacingMessage(error));
                _matchDrawingRetryAction = () => _ = LoadMatchDrawingRoundAsync(round);
            }
            finally
            {
                if (IsCurrentMatchDrawing(popup, version, token)) { _matchDrawingLoading = false; RefreshMatchDrawingControls(); }
            }
        }

        private async Task RenderMatchDrawingAsync()
        {
            if (_matchDrawingRound == null || _matchDrawingOverlay == null) return;
            var popup = _matchDrawingOverlay;
            var token = _matchDrawingLoad.Token;
            int version = _matchDrawingVersion;
            _matchDrawingLoading = true; _matchDrawingRetryAction = null;
            ReleaseMatchDrawingTexture(); RefreshMatchDrawingControls(); SetText(_matchDrawingStatus, "그림을 불러오는 중…");
            Texture2D texture = null;
            try
            {
                var source = _matchDrawingRound.Strokes;
                var strokes = new DrawStroke[source.Length];
                for (int index = 0; index < source.Length; index++)
                {
                    var stroke = source[index];
                    strokes[index] = new DrawStroke { X1 = stroke.X1, Y1 = stroke.Y1, X2 = stroke.X2, Y2 = stroke.Y2,
                        Size = stroke.Size, StartSize = stroke.StartSize, R = stroke.R, G = stroke.G, B = stroke.B,
                        Eraser = stroke.Eraser, AuthorPlayerId = stroke.AuthorPlayerId };
                }
                texture = await DrawDrawingRenderer.RenderAsync(strokes, _matchDrawingAuthor, token);
                if (!IsCurrentMatchDrawing(popup, version, token)) return;
                _matchDrawingTexture = texture; texture = null;
                _matchDrawingImage.image = _matchDrawingTexture; _matchDrawingImage.style.display = DisplayStyle.Flex;
                FitMatchDrawingImage();
                SetText(_matchDrawingStatus, _matchDrawingList.IsPartial ? "일부 라운드의 그림이 보관되지 않았습니다." : "");
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!IsCurrentMatchDrawing(popup, version, token)) return;
                SetText(_matchDrawingStatus, "그림을 불러오지 못했습니다.");
                _matchDrawingRetryAction = () => _ = RenderMatchDrawingAsync();
            }
            finally
            {
                if (texture != null) Destroy(texture);
                if (IsCurrentMatchDrawing(popup, version, token)) { _matchDrawingLoading = false; RefreshMatchDrawingControls(); }
            }
        }

        private void RefreshMatchDrawingText()
        {
            if (_matchDrawingOverlay == null || _matchDrawingList == null) return;
            SetText(_matchDrawingExpiry, "보관 종료: {0}", PublicProfileDate(_matchDrawingList.ExpiresAt, true));
            _matchDrawingUpdatingChoices = true;
            int round = _matchDrawingRound?.Round ?? _matchDrawingList.Rounds[0].Round;
            _matchDrawingRounds.choices = _matchDrawingList.Rounds.Select(item => L.Format("{0}라운드", item.Round)).ToList();
            int index = Array.FindIndex(_matchDrawingList.Rounds, item => item.Round == round);
            _matchDrawingRounds.SetValueWithoutNotify(_matchDrawingRounds.choices[Math.Max(0, index)]);
            if (_matchDrawingRound != null)
            {
                bool relay = _matchDrawingMatch.Mode == (int)DrawingMode.Relay;
                var names = _matchDrawingRound.Players.Select((player, position) => L.Format("{0}. {1}", position + 1, player.DisplayName)).ToList();
                if (relay) names.Insert(0, L.Text("전체 그림"));
                _matchDrawingAuthors.choices = names;
                int author = Array.IndexOf(_matchDrawingAuthorIds, _matchDrawingAuthor ?? -1);
                _matchDrawingAuthors.SetValueWithoutNotify(names[Math.Max(0, author)]);
                SetText(_matchDrawingTopic, "주제: {0}", _matchDrawingRound.Topic);
                SetText(_matchDrawingWord, "제시어: {0}", _matchDrawingRound.Word);
                SetTooltip(_matchDrawingTopic, "주제: {0}", _matchDrawingRound.Topic);
                SetTooltip(_matchDrawingWord, "제시어: {0}", _matchDrawingRound.Word);
            }
            _matchDrawingUpdatingChoices = false;
        }

        private void RefreshMatchDrawingControls()
        {
            if (_matchDrawingOverlay == null) return;
            bool ready = !_matchDrawingLoading && !_matchDrawingSaving;
            _matchDrawingRounds.SetEnabled(ready && _matchDrawingList != null);
            _matchDrawingAuthors.SetEnabled(ready && _matchDrawingRound != null);
            _matchDrawingSave.SetEnabled(ready && _matchDrawingTexture != null);
            SetText(_matchDrawingSave, _matchDrawingSaving ? "이미지를 저장하는 중…" : "이미지로 저장");
            _matchDrawingRetry.style.display = ready && _matchDrawingRetryAction != null ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void FitMatchDrawingImage()
        {
            if (_matchDrawingFrame == null || _matchDrawingImage == null) return;
            var space = _matchDrawingFrame.contentRect.size;
            float width = Mathf.Min(space.x, space.y * DrawingSurface.AspectRatio);
            if (!(width > 0)) return;
            _matchDrawingImage.style.width = width; _matchDrawingImage.style.height = width / DrawingSurface.AspectRatio;
        }

        private async Task SaveMatchDrawingAsync()
        {
            if (_matchDrawingSaving || _matchDrawingLoading || _matchDrawingTexture == null || _matchDrawingRound == null) return;
            var popup = _matchDrawingOverlay;
            var token = _matchDrawingLifetime.Token;
            int version = _matchDrawingVersion;
            _matchDrawingSaving = true; RefreshMatchDrawingControls();
            try
            {
                string author = _matchDrawingAuthor?.ToString(CultureInfo.InvariantCulture) ?? "all";
                string fileName = "DrawLiar_" + _matchDrawingMatch.MatchId + "_round_" + _matchDrawingRound.Round + "_" + author + ".png";
                var saved = await DrawDrawingImageSaver.SaveAsync(_matchDrawingTexture, fileName, token);
                if (!IsCurrentMatchDrawing(popup, version, token)) return;
                if (saved.DownloadedInBrowser) SetText(_matchDrawingStatus, "이미지 다운로드를 시작했습니다.");
                else if (saved.SavedToGallery) SetText(_matchDrawingStatus, "사진 갤러리에 저장했습니다.");
                else SetText(_matchDrawingStatus, "이미지를 저장했습니다: {0}", saved.FilePath);
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (IsCurrentMatchDrawing(popup, version, token)) SetText(_matchDrawingStatus, "이미지를 저장하지 못했습니다.");
            }
            finally
            {
                if (IsCurrentMatchDrawing(popup, version, token)) { _matchDrawingSaving = false; RefreshMatchDrawingControls(); }
            }
        }

        private void ReleaseMatchDrawingTexture()
        {
            if (_matchDrawingImage != null) { _matchDrawingImage.image = null; _matchDrawingImage.style.display = DisplayStyle.None; }
            if (_matchDrawingTexture != null) Destroy(_matchDrawingTexture);
            _matchDrawingTexture = null;
        }

        private void CloseMatchDrawings(bool restoreFocus = true)
        {
            _matchDrawingLifetime?.Cancel(); _matchDrawingLoad?.Cancel();
            _matchDrawingLoad?.Dispose(); _matchDrawingLifetime?.Dispose();
            _matchDrawingLoad = _matchDrawingLifetime = null;
            ++_matchDrawingVersion;
            var popup = _matchDrawingOverlay;
            var origin = _matchDrawingReturnFocus;
            ReleaseMatchDrawingTexture();
            _matchDrawingOverlay = _matchDrawingFrame = _matchDrawingReturnFocus = null;
            _matchDrawingList = null; _matchDrawingRound = null; _matchDrawingMatch = null; _matchDrawingAccount = null;
            _matchDrawingAuthorIds = Array.Empty<int>(); _matchDrawingLoading = _matchDrawingSaving = false;
            _matchDrawingRetryAction = null; _matchDrawingImage = null;
            _matchDrawingRounds = _matchDrawingAuthors = null;
            _matchDrawingExpiry = _matchDrawingTopic = _matchDrawingWord = _matchDrawingStatus = null;
            _matchDrawingSave = _matchDrawingRetry = null;
            _matchDrawingAuthor = null; _matchDrawingPreferredAuthor = null; _matchDrawingUpdatingChoices = false;
            _profileModal?.SetEnabled(true);
            if (popup == null) return;
            popup.RemoveFromHierarchy();
            if (restoreFocus && origin?.panel != null) origin.Focus();
        }
    }
}
