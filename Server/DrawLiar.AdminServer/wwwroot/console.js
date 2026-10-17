'use strict';

let csrfToken = '';
let loadVersion = 0;
let sessionExpired = false;
let mailSending = false;
let logVersion = 0;
let drawingVersion = 0;
let logView = null;
let logLoading = false;
let drawingLoading = false;
let activeDrawing = null;
const logHistory = [];
const REPORT_REASONS = { Abuse: '욕설·비하', Drawing: '부적절한 그림', Nickname: '부적절한 닉네임', Spam: '도배·광고', Topic: '부적절한 주제', Other: '기타' };
const LOG_KINDS = {
  Login: '로그인', Logout: '로그아웃', GoogleLink: 'Google 계정 연동', ProfileUpdate: '프로필 변경', CosmeticEquip: '꾸미기 착용', SubscriberBadge: '구독 뱃지 설정', LobbyChat: '로비 채팅',
  RoomCreate: '방 생성', RoomEnter: '방 입장', RoomLeave: '방 퇴장', RoomChat: '방 채팅', RoomGuess: '정답 입력', RoomConfigure: '방 설정 변경', RoomKick: '강퇴',
  RoomInvite: '방 초대', RoomInviteRespond: '초대 응답', MatchStart: '경기 시작', MatchReward: '경기 보상',
  MailClaim: '우편 수령', AdReward: '광고 보상', Purchase: '상품 구매', Payment: '결제 완료',
  FriendRequest: '친구 요청', FriendCancel: '친구 요청 취소', FriendRespond: '친구 요청 응답', FriendRemove: '친구 삭제', Block: '차단', Unblock: '차단 해제', Report: '신고',
  WorkshopPublish: '창작마당 게시', WorkshopDelete: '창작마당 삭제', WorkshopRecommend: '창작마당 추천', AdminBan: '운영자 차단 변경'
};
const LOG_DETAIL_LABELS = {
  Method: '로그인 방식', Platform: '플랫폼', Version: '버전', DisplayName: '닉네임', Category: '분류', ProductId: '상품', ProductIds: '상품', ShowBadge: '뱃지 표시',
  RoomCode: '방 코드', RoomName: '방 이름', Spectator: '관전', Voluntary: '직접 나감', Coins: '코인', PaidGems: '보석', Experience: '경험치', Score: '점수', Rank: '순위',
  Won: '우승', Count: '개수', Provider: '제공자', Accept: '수락', Reason: '사유', TopicId: '주제 ID', Name: '이름', Recommended: '추천', IsBanned: '차단', Admin: '운영자', Text: '내용'
};
const LOG_CHAT_KINDS = new Set(['LobbyChat', 'RoomChat', 'RoomGuess']);
const LOG_REWARD_KINDS = new Set(['MatchReward', 'MailClaim', 'AdReward', 'Payment']);
const LOG_MODERATION_KINDS = new Set(['RoomKick', 'Report', 'Block', 'AdminBan']);
const API_ERRORS = {
  InvalidMail: '받는 계정, 제목, 보상과 유효 기간을 확인하세요.',
  AccountNotFound: '받는 계정 ID를 확인하세요. 존재하지 않는 계정이 있습니다.',
  MailUnavailable: '우편을 찾을 수 없습니다.',
  InvalidClientVersion: '버전(0.1.0 형식)과 HTTPS 주소를 확인하세요. 강제 버전은 일반 버전보다 높을 수 없습니다.',
  InvalidCsrfToken: '페이지를 새로 고친 후 다시 시도하세요.',
  HttpsRequired: 'HTTPS 주소로 접속하세요.',
  DrawingReplayUnavailable: '보관된 그림이 없습니다.',
  DrawingReplayExpired: '그림 보관기간이 만료되었습니다.',
  InvalidAccount: 'ID 형식을 확인하세요.',
  InvalidRoom: 'ID 형식을 확인하세요.',
  InvalidMatch: 'ID 형식을 확인하세요.'
};
const SECTIONS = {
  overview: ['운영 현황', 'Operations'], versions: ['클라이언트 버전', 'Operations'], rooms: ['방 모니터링', 'Operations'],
  accounts: ['계정 관리', 'Users'], mail: ['우편 발송', 'Users'], reports: ['신고 관리', 'Users']
};
const statusElement = document.getElementById('status');
const refreshButton = document.getElementById('refresh');
const searchForm = document.getElementById('search');
const queryInput = document.getElementById('query');
const mailForm = document.getElementById('mail-form');
const mailRecipients = document.getElementById('mail-recipients');
const mailAll = document.getElementById('mail-all');
const mailConfirmation = document.getElementById('mail-confirm');
const logDialog = document.getElementById('log-dialog');
const logEntries = document.getElementById('log-entries');
const logStatus = document.getElementById('log-status');
const logMore = document.getElementById('log-more');
const logBack = document.getElementById('log-back');
const logClose = document.getElementById('log-close');
const drawingStatus = document.getElementById('drawing-status');
const drawingAuthor = document.getElementById('drawing-author');
const drawingCanvas = document.getElementById('drawing-canvas');

function setStatus(message, tone = 'info') {
  statusElement.className = `alert alert-${tone}`;
  statusElement.textContent = message;
}

function setConnection(connected) {
  document.getElementById('connection-dot').className = `status-dot ${connected ? 'bg-green' : 'bg-secondary'}`;
  document.getElementById('connection-label').textContent = connected ? '운영 도구 연결됨' : '연결 확인 필요';
}

function expireSession() {
  if (sessionExpired) return;
  sessionExpired = true;
  csrfToken = '';
  ++loadVersion;
  if (logDialog.open) logDialog.close();
  clearLogDialog();
  for (const id of ['client-versions', 'accounts', 'rooms', 'reports', 'mails']) document.getElementById(id).replaceChildren();
  for (const element of document.querySelectorAll('[id^="metric-"], [id$="-count"], #admin-name, #overview-updated')) element.textContent = '';
  mailForm.reset();
  queryInput.value = '';
  document.querySelector('.page').hidden = true;
  location.replace(new URL('login', document.baseURI).href);
}

async function request(path, method = 'GET', body) {
  if (sessionExpired) throw new Error('로그인이 필요합니다.');
  const loopback = ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname);
  if (location.protocol !== 'https:' && !(location.protocol === 'http:' && loopback)) throw new Error('HTTPS 주소로 접속하세요.');
  const headers = { 'Content-Type': 'application/json' };
  if (method !== 'GET') headers['X-CSRF-TOKEN'] = csrfToken;
  const response = await fetch(path, { method, credentials: 'same-origin', cache: 'no-store', headers, body: body === undefined ? undefined : JSON.stringify(body) });
  if (response.status === 401) {
    expireSession();
    throw new Error('로그인이 만료되었습니다.');
  }
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    throw new Error(API_ERRORS[error.Code] || error.Code || `요청을 처리하지 못했습니다. (HTTP ${response.status})`);
  }
  return response.status === 204 ? null : response.json();
}

function selectSection() {
  const section = Object.hasOwn(SECTIONS, location.hash.slice(1)) ? location.hash.slice(1) : 'overview';
  for (const panel of document.querySelectorAll('[data-panel]')) panel.hidden = panel.dataset.panel !== section;
  for (const link of document.querySelectorAll('[data-section]')) {
    const selected = link.dataset.section === section;
    link.classList.toggle('active', selected);
    if (selected) link.setAttribute('aria-current', 'page');
    else link.removeAttribute('aria-current');
  }
  document.getElementById('page-title').textContent = SECTIONS[section][0];
  document.getElementById('page-pretitle').textContent = SECTIONS[section][1];
  document.title = `${SECTIONS[section][0]} - DrawLiar Admin`;
  searchForm.hidden = section !== 'accounts' && section !== 'rooms';
  queryInput.placeholder = section === 'rooms' ? '방 이름 검색' : '계정 이름 검색';
  const menu = document.getElementById('sidebar-menu');
  if (menu.classList.contains('show') && window.tabler?.Collapse) window.tabler.Collapse.getOrCreateInstance(menu).hide();
}

function row(values) {
  const element = document.createElement('tr');
  for (const value of values) {
    const cell = document.createElement('td');
    cell.textContent = String(value ?? '');
    element.append(cell);
  }
  return element;
}

function emptyRow(body, columns, message) {
  if (body.children.length) return;
  const element = row([message]);
  element.firstChild.colSpan = columns;
  element.firstChild.className = 'empty-row';
  body.append(element);
}

function button(label, tone = 'outline-secondary') {
  const element = document.createElement('button');
  element.type = 'button';
  element.className = `btn btn-${tone} btn-sm`;
  element.textContent = label;
  return element;
}

function badge(cell, label, tone) {
  const element = document.createElement('span');
  element.className = `badge bg-${tone}-lt`;
  element.textContent = label;
  cell.replaceChildren(element);
}

function setDialogStatus(element, message = '', tone = 'info') {
  element.className = `alert alert-${tone}`;
  element.textContent = message;
  element.hidden = !message;
}

function updateLogControls() {
  const busy = logLoading || drawingLoading;
  for (const control of logDialog.querySelectorAll('button')) {
    if (control !== logClose) control.disabled = busy;
  }
  drawingAuthor.disabled = busy;
  logBack.hidden = logHistory.length === 0;
  logMore.hidden = !logView || (logView.loaded ? !logView.nextBefore : logLoading);
  logMore.textContent = logView?.loaded ? '이전 기록 더 보기' : '다시 시도';
}

function clearLogDrawing() {
  ++drawingVersion;
  drawingLoading = false;
  activeDrawing = null;
  document.getElementById('log-drawing').hidden = true;
  document.getElementById('log-drawing-title').textContent = '라운드 그림';
  document.getElementById('drawing-content').hidden = true;
  document.getElementById('drawing-summary').textContent = '';
  drawingAuthor.replaceChildren();
  const context = drawingCanvas.getContext('2d');
  if (context) context.clearRect(0, 0, drawingCanvas.width, drawingCanvas.height);
  setDialogStatus(drawingStatus);
}

function clearLogDialog() {
  ++logVersion;
  logView = null;
  logLoading = false;
  logHistory.length = 0;
  logEntries.replaceChildren();
  document.getElementById('log-title').textContent = '';
  document.getElementById('log-match-list').replaceChildren();
  document.getElementById('log-matches').hidden = true;
  setDialogStatus(logStatus);
  clearLogDrawing();
  updateLogControls();
}

function logButton(label, kind, id, name = '') {
  const control = button(label, 'outline-primary');
  control.disabled = !id;
  control.addEventListener('click', () => openLogs(kind, id, name));
  return control;
}

function openLogs(kind, id, name = '', remember = true) {
  if (!id || sessionExpired) return;
  if (remember && logDialog.open && logView) {
    if (logView.kind === kind && logView.id === id) return;
    logHistory.push({ kind: logView.kind, id: logView.id, name: logView.name });
  } else if (!logDialog.open) {
    logHistory.length = 0;
  }
  ++logVersion;
  logView = { kind, id, name, loaded: false, nextBefore: 0, count: 0 };
  logLoading = false;
  logEntries.replaceChildren();
  logEntries.closest('.table-responsive').scrollLeft = 0;
  document.getElementById('log-title').textContent = kind === 'room' ? `방 로그 · ${id.slice(0, 8)}` : `유저 로그 · ${name || id}`;
  document.getElementById('log-match-list').replaceChildren();
  document.getElementById('log-matches').hidden = kind !== 'room';
  clearLogDrawing();
  if (!logDialog.open) logDialog.showModal();
  loadLogPage();
}

async function loadLogPage() {
  if (!logView || logLoading || drawingLoading || sessionExpired || logView.loaded && !logView.nextBefore) return;
  const view = logView;
  const version = logVersion;
  const append = view.loaded;
  const resource = view.kind === 'room' ? 'rooms' : 'accounts';
  const query = append ? `?before=${encodeURIComponent(view.nextBefore)}` : '';
  logLoading = true;
  setDialogStatus(logStatus, append ? '이전 기록을 불러오는 중입니다.' : '로그를 불러오는 중입니다.');
  updateLogControls();
  try {
    const result = await request(`api/${resource}/${encodeURIComponent(view.id)}/logs${query}`);
    if (version !== logVersion || sessionExpired) return;
    const entries = result.Entries || [];
    if (!append) logEntries.replaceChildren();
    renderLogEntries(entries, view);
    view.count += entries.length;
    view.loaded = true;
    view.nextBefore = result.NextBefore || 0;
    if (view.kind === 'room' && !append) renderLogMatches(result.Matches || []);
    emptyRow(logEntries, 5, '기록된 로그가 없습니다.');
    setDialogStatus(logStatus, view.count ? `${view.count.toLocaleString()}개 로그를 표시합니다.` : '');
  } catch (error) {
    if (version === logVersion && !sessionExpired) setDialogStatus(logStatus, error.message, 'danger');
  } finally {
    if (version === logVersion) {
      logLoading = false;
      updateLogControls();
    }
  }
}

function logDetailLabel(key) {
  return Object.hasOwn(LOG_DETAIL_LABELS, key) ? LOG_DETAIL_LABELS[key] : key;
}

function formatLogValue(value, key = '') {
  if (Array.isArray(value)) return value.map(item => formatLogValue(item, key)).join(', ');
  if (typeof value === 'boolean') return value ? '예' : '아니오';
  if (value && typeof value === 'object') return Object.entries(value).map(([name, item]) => `${logDetailLabel(name)}: ${formatLogValue(item, name)}`).join(', ');
  if (key === 'Reason' && Object.hasOwn(REPORT_REASONS, value)) return REPORT_REASONS[value];
  return String(value ?? '');
}

function formatLogDetail(value) {
  let detail;
  try { detail = JSON.parse(value || '{}'); }
  catch { return String(value || ''); }
  if (!detail || typeof detail !== 'object' || Array.isArray(detail)) return formatLogValue(detail);
  const text = Object.hasOwn(detail, 'Text') ? formatLogValue(detail.Text) : '';
  const fields = Object.entries(detail).filter(([key]) => key !== 'Text')
    .map(([key, item]) => `${logDetailLabel(key)}: ${formatLogValue(item, key)}`).join(' · ');
  return [text, fields].filter(Boolean).join('\n');
}

function appendLogReference(cell, text, control) {
  const element = document.createElement('div');
  element.className = 'log-reference';
  const label = document.createElement('span');
  label.textContent = text;
  element.append(label);
  if (control) element.append(control);
  cell.append(element);
  return element;
}

function renderLogEntries(entries, view) {
  const fragment = document.createDocumentFragment();
  for (const entry of entries) {
    const element = row([new Date(entry.CreatedAt).toLocaleString(), '', '', formatLogDetail(entry.Detail), '']);
    const kind = Object.hasOwn(LOG_KINDS, entry.Kind) ? LOG_KINDS[entry.Kind] : entry.Kind;
    const tone = LOG_CHAT_KINDS.has(entry.Kind) ? 'blue' : LOG_REWARD_KINDS.has(entry.Kind) ? 'green' : LOG_MODERATION_KINDS.has(entry.Kind) ? 'red' : 'secondary';
    badge(element.children[1], kind, tone);
    const account = entry.AccountName || entry.AccountId || '시스템';
    const isTarget = view.kind === 'account' && entry.TargetAccountId && entry.TargetAccountId.toLowerCase() === view.id.toLowerCase();
    const actor = appendLogReference(element.children[2], isTarget ? `← 행위자: ${account}` : account,
      view.kind === 'room' && entry.AccountId ? logButton('로그', 'account', entry.AccountId, entry.AccountName) : null);
    if (isTarget) actor.classList.add('log-target-actor');
    element.children[3].className = 'log-detail';
    const related = element.children[4];
    if (entry.TargetName || entry.TargetAccountId) {
      appendLogReference(related, `대상: ${entry.TargetName || entry.TargetAccountId}`,
        view.kind === 'room' && entry.TargetAccountId ? logButton('로그', 'account', entry.TargetAccountId, entry.TargetName) : null);
    }
    if (entry.RoomId && view.kind === 'account') appendLogReference(related, `방 ${entry.RoomId}`, logButton('방 로그', 'room', entry.RoomId));
    if (entry.MatchId) appendLogReference(related, `경기 ${entry.MatchId.slice(0, 8)}`);
    fragment.append(element);
  }
  logEntries.append(fragment);
}

function renderLogMatches(matches) {
  const list = document.getElementById('log-match-list');
  list.replaceChildren();
  if (!matches.length) {
    const empty = document.createElement('p');
    empty.className = 'text-secondary';
    empty.textContent = '종료된 경기가 없습니다.';
    list.append(empty);
    return;
  }
  for (const match of matches) {
    const element = document.createElement('div');
    element.className = 'log-match';
    const summary = document.createElement('p');
    summary.className = 'log-match-summary';
    const expires = match.DrawingExpiresAt ? new Date(match.DrawingExpiresAt).toLocaleString() : '없음/만료';
    summary.textContent = `종료 ${new Date(match.PlayedAt).toLocaleString()} · ${match.PlayerCount}명 · ${match.RoundCount}라운드 · 그림 보관 만료 ${expires}`;
    element.append(summary);
    const rounds = document.createElement('div');
    rounds.className = 'log-round-buttons';
    for (const round of match.Rounds || []) {
      const control = button(`${round.Round}R ${round.Topic}/${round.Word}`, 'outline-primary');
      control.addEventListener('click', () => openDrawing(match.MatchId, round.Round));
      rounds.append(control);
    }
    if (!rounds.children.length) {
      const empty = document.createElement('span');
      empty.className = 'text-secondary';
      empty.textContent = '보관된 그림이 없습니다.';
      rounds.append(empty);
    }
    element.append(rounds);
    list.append(element);
  }
}

async function openDrawing(matchId, round) {
  if (!logView || logView.kind !== 'room' || logLoading || drawingLoading || sessionExpired) return;
  clearLogDrawing();
  const version = drawingVersion;
  const parentVersion = logVersion;
  drawingLoading = true;
  document.getElementById('log-drawing').hidden = false;
  document.getElementById('log-drawing-title').textContent = `라운드 ${round} 그림 · 경기 ${matchId.slice(0, 8)}`;
  setDialogStatus(drawingStatus, '그림을 불러오는 중입니다.');
  updateLogControls();
  document.getElementById('log-drawing').scrollIntoView({ block: 'start' });
  try {
    const result = await request(`api/matches/${encodeURIComponent(matchId)}/drawings/${encodeURIComponent(round)}`);
    if (version !== drawingVersion || parentVersion !== logVersion || sessionExpired) return;
    if (!result.Drawing) throw new Error(API_ERRORS.DrawingReplayUnavailable);
    activeDrawing = result;
    const all = document.createElement('option');
    all.value = '';
    all.textContent = '전체';
    drawingAuthor.append(all);
    for (const player of result.Drawing.Players || []) {
      const option = document.createElement('option');
      option.value = String(player.PlayerId);
      option.textContent = player.DisplayName || `플레이어 ${player.PlayerId}`;
      drawingAuthor.append(option);
    }
    renderDrawing();
    document.getElementById('drawing-content').hidden = false;
    setDialogStatus(drawingStatus);
  } catch (error) {
    if (version === drawingVersion && parentVersion === logVersion && !sessionExpired) setDialogStatus(drawingStatus, error.message, 'danger');
  } finally {
    if (version === drawingVersion && parentVersion === logVersion) {
      drawingLoading = false;
      updateLogControls();
    }
  }
}

function clamp(value, minimum, maximum) {
  return Math.min(maximum, Math.max(minimum, Number(value) || 0));
}

function renderDrawing() {
  if (!activeDrawing) return;
  const context = drawingCanvas.getContext('2d');
  if (!context) throw new Error('그림을 표시할 수 없습니다.');
  const drawing = activeDrawing.Drawing;
  const author = drawingAuthor.value;
  const allStrokes = drawing.Strokes || [];
  const strokes = author === '' ? allStrokes : allStrokes.filter(stroke => String(stroke.AuthorPlayerId) === author);
  context.fillStyle = '#fff';
  context.fillRect(0, 0, 1200, 800);
  context.lineCap = 'round';
  context.lineJoin = 'round';
  for (const stroke of strokes) {
    const startRadius = clamp((stroke.StartSize > 0 ? stroke.StartSize : stroke.Size) * 1200 * .5, 1, 48);
    const endRadius = clamp(stroke.Size * 1200 * .5, 1, 48);
    context.strokeStyle = stroke.Eraser ? '#fff' : `rgb(${clamp(stroke.R, 0, 255)}, ${clamp(stroke.G, 0, 255)}, ${clamp(stroke.B, 0, 255)})`;
    context.lineWidth = startRadius + endRadius;
    context.beginPath();
    context.moveTo(clamp(stroke.X1, 0, 1) * 1200, clamp(stroke.Y1, 0, 1) * 800);
    context.lineTo(clamp(stroke.X2, 0, 1) * 1200, clamp(stroke.Y2, 0, 1) * 800);
    context.stroke();
  }
  const count = author === '' ? `선 ${strokes.length.toLocaleString()}개` : `선 ${strokes.length.toLocaleString()}개 / 전체 ${allStrokes.length.toLocaleString()}개`;
  const expires = activeDrawing.ExpiresAt ? ` · 그림 보관 만료 ${new Date(activeDrawing.ExpiresAt).toLocaleString()}` : '';
  document.getElementById('drawing-summary').textContent = `라운드 ${drawing.Round} · 주제 ${drawing.Topic} · 제시어 ${drawing.Word} · ${count}${expires}`;
}

async function performAction(control, action, message) {
  const version = loadVersion;
  control.disabled = true;
  try {
    await action();
    if (version !== loadVersion || sessionExpired) return;
    const refreshed = loadVersion + 1;
    if (await load() && refreshed === loadVersion) setStatus(message, 'success');
  } catch (error) {
    if (version === loadVersion && !sessionExpired) setStatus(error.message, 'danger');
  } finally {
    control.disabled = false;
  }
}

function recipientIds() {
  return [...new Set(mailRecipients.value.split(/[,\r\n]+/).map(value => value.trim().toLowerCase()).filter(Boolean))];
}

function updateMailForm() {
  for (const control of mailForm.elements) control.disabled = mailSending;
  mailRecipients.disabled = mailSending || mailAll.checked;
  mailRecipients.required = !mailAll.checked;
  document.getElementById('mail-send').disabled = mailSending || !mailConfirmation.checked;
}

function addMailRecipient(account) {
  if (mailSending) return;
  const ids = recipientIds();
  if (!ids.includes(account.AccountId.toLowerCase())) {
    if (ids.length >= 1000) { setStatus('받는 계정은 최대 1,000개까지 지정할 수 있습니다.', 'warning'); return; }
    ids.push(account.AccountId);
  }
  mailRecipients.value = ids.join('\n');
  mailRecipients.setCustomValidity('');
  mailAll.checked = false;
  mailConfirmation.checked = false;
  updateMailForm();
  location.hash = 'mail';
  selectSection();
  mailRecipients.focus();
  setStatus(`${account.DisplayName}: 우편 받는 계정에 추가했습니다.`, 'success');
}

function renderClientVersions(policies) {
  const body = document.getElementById('client-versions');
  body.replaceChildren();
  for (const policy of policies) {
    const element = row([policy.Platform]);
    const inputs = {};
    for (const [field, label, maxLength] of [['MinimumVersion', '강제 업데이트 버전', 32], ['LatestVersion', '일반 업데이트 버전', 32], ['StoreUrl', '스토어 주소', 512]]) {
      const cell = document.createElement('td');
      const input = document.createElement('input');
      input.type = 'text';
      input.className = 'form-control form-control-sm';
      input.value = policy[field];
      input.maxLength = maxLength;
      input.setAttribute('aria-label', `${policy.Platform} ${label}`);
      input.placeholder = field === 'StoreUrl' ? policy.Platform === 'Android' ? 'https://play.google.com/store/apps/details?id=com.rascallab.liargame' : 'https://' : '0.1.0';
      inputs[field] = input;
      cell.append(input);
      element.append(cell);
    }
    const actions = document.createElement('td');
    const save = button('저장', 'primary');
    save.addEventListener('click', () => performAction(save,
      () => request(`api/client-versions/${encodeURIComponent(policy.Platform)}`, 'PUT', { Platform: policy.Platform, MinimumVersion: inputs.MinimumVersion.value, LatestVersion: inputs.LatestVersion.value, StoreUrl: inputs.StoreUrl.value }),
      `${policy.Platform}: 클라이언트 버전 저장 완료`));
    actions.append(save);
    element.append(actions);
    body.append(element);
  }
  emptyRow(body, 5, '등록된 클라이언트 버전이 없습니다.');
}

function renderAccounts(accounts) {
  const body = document.getElementById('accounts');
  body.replaceChildren();
  for (const account of accounts) {
    const element = row([account.DisplayName, account.AccountId, account.Coins.toLocaleString(), '']);
    element.children[1].className = 'identifier';
    badge(element.children[3], account.IsBanned ? '차단' : '정상', account.IsBanned ? 'red' : 'green');
    const actions = document.createElement('td');
    actions.className = 'actions';
    const confirmation = document.createElement('label');
    confirmation.className = 'ban-confirmation';
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.className = 'form-check-input';
    confirmation.append(checkbox, document.createTextNode('차단 확인'));
    actions.append(confirmation);
    const buttons = document.createElement('div');
    buttons.className = 'action-buttons';
    for (const [label, banned] of [['차단', true], ['차단 해제', false]]) {
      const control = button(label, banned ? 'outline-danger' : 'outline-secondary');
      if (banned) {
        control.disabled = true;
        checkbox.addEventListener('change', () => { control.disabled = !checkbox.checked; });
      }
      control.addEventListener('click', () => performAction(control,
        () => request(`api/accounts/${encodeURIComponent(account.AccountId)}/ban`, 'PATCH', { IsBanned: banned }), `${account.DisplayName}: ${label} 완료`)
        .finally(() => { if (banned) control.disabled = !checkbox.checked; }));
      buttons.append(control);
    }
    const mail = button('우편', 'outline-primary');
    mail.addEventListener('click', () => addMailRecipient(account));
    buttons.append(mail);
    actions.append(buttons);
    element.append(actions);
    body.append(element);
  }
  emptyRow(body, 5, queryInput.value.trim() ? '검색한 계정이 없습니다.' : '등록된 계정이 없습니다.');
  document.getElementById('accounts-count').textContent = `${accounts.length.toLocaleString()}개`;
}

function renderRooms(rooms) {
  const body = document.getElementById('rooms');
  body.replaceChildren();
  for (const room of rooms) {
    const element = row([room.Name, room.RoomId, room.PlayerCount, room.SpectatorCount, '']);
    element.children[1].className = 'identifier';
    badge(element.children[4], room.IsInProgress ? '진행 중' : '대기 중', room.IsInProgress ? 'green' : 'blue');
    body.append(element);
  }
  emptyRow(body, 5, queryInput.value.trim() ? '검색한 방이 없습니다.' : '운영 중인 방이 없습니다.');
  document.getElementById('rooms-count').textContent = `${rooms.length.toLocaleString()}개`;
}

function renderMails(mails) {
  const body = document.getElementById('mails');
  body.replaceChildren();
  for (const mail of mails) {
    const element = row([new Date(mail.CreatedAt).toLocaleString(), mail.AccountId ? mail.AccountName || mail.AccountId : '전체', mail.Title,
      `코인 ${mail.Coins.toLocaleString()} / 보석 ${mail.PaidGems.toLocaleString()}`, new Date(mail.ExpiresAt).toLocaleString(), mail.ClaimCount.toLocaleString()]);
    const actions = document.createElement('td');
    if (new Date(mail.ExpiresAt).getTime() > Date.now()) {
      const cancel = button('발송 취소', 'outline-danger');
      cancel.addEventListener('click', () => performAction(cancel,
        () => request(`api/mail/${encodeURIComponent(mail.MailId)}/cancel`, 'POST'), `${mail.Title}: 발송 취소 완료 (이미 수령한 보상은 유지됩니다.)`));
      actions.append(cancel);
    } else {
      actions.className = 'text-secondary';
      actions.textContent = '만료';
    }
    element.append(actions);
    body.append(element);
  }
  emptyRow(body, 7, '발송한 우편이 없습니다.');
  document.getElementById('mails-count').textContent = `${mails.length.toLocaleString()}개`;
}

function renderReports(reports) {
  const body = document.getElementById('reports');
  body.replaceChildren();
  for (const report of reports) {
    const element = row([new Date(report.CreatedAt).toLocaleString(), REPORT_REASONS[report.Reason] || report.Reason,
      `${report.TargetName} (${report.TargetAccountId})`, `${report.ReporterName} (${report.ReporterAccountId})`, report.Context,
      report.WorkshopTopicId ? `주제 ${report.WorkshopTopicId}` : report.RoomId ? `방 ${report.RoomId}` : '']);
    const actions = document.createElement('td');
    actions.className = 'actions';
    const buttons = document.createElement('div');
    buttons.className = 'action-buttons';
    buttons.append(logButton('대상 로그', 'account', report.TargetAccountId, report.TargetName),
      logButton('신고자 로그', 'account', report.ReporterAccountId, report.ReporterName));
    if (report.RoomId) buttons.append(logButton('방 로그', 'room', report.RoomId));
    for (const [label, next] of [['처리 완료', 'Resolved'], ['기각', 'Dismissed']]) {
      const control = button(label, next === 'Resolved' ? 'outline-primary' : 'outline-secondary');
      control.addEventListener('click', () => performAction(control,
        () => request(`api/reports/${encodeURIComponent(report.ReportId)}`, 'PATCH', { Status: next }), `신고 ${label}`));
      buttons.append(control);
    }
    actions.append(buttons);
    element.append(actions);
    body.append(element);
  }
  emptyRow(body, 7, '처리 대기 중인 신고가 없습니다.');
  document.getElementById('reports-count').textContent = `${reports.length.toLocaleString()}개`;
  const reportBadge = document.getElementById('report-badge');
  reportBadge.textContent = reports.length.toLocaleString();
  reportBadge.hidden = reports.length === 0;
}

function renderOverview(accounts, rooms, reports, mails) {
  for (const [key, value] of [['accounts', accounts.length], ['rooms', rooms.length], ['reports', reports.length], ['mails', mails.length],
    ['playing', rooms.filter(room => room.IsInProgress).length], ['waiting', rooms.filter(room => !room.IsInProgress).length]]) {
    document.getElementById(`metric-${key}`).textContent = value.toLocaleString();
  }
  const players = rooms.reduce((total, room) => total + room.PlayerCount, 0);
  const spectators = rooms.reduce((total, room) => total + room.SpectatorCount, 0);
  document.getElementById('metric-players').textContent = `${players.toLocaleString()} · ${spectators.toLocaleString()}`;
  document.getElementById('overview-updated').textContent = `${new Date().toLocaleTimeString()} 기준`;
}

async function load() {
  const version = ++loadVersion;
  refreshButton.disabled = true;
  setStatus('운영 정보를 불러오는 중입니다.');
  try {
    const query = encodeURIComponent(queryInput.value.trim());
    const [accounts, roomResult, policies, reports, mails] = await Promise.all([
      request(`api/accounts?search=${query}`), request(`api/rooms?search=${query}`), request('api/client-versions'), request('api/reports?status=Open'), request('api/mail')
    ]);
    if (version !== loadVersion || sessionExpired) return false;
    renderClientVersions(policies);
    renderAccounts(accounts);
    renderRooms(roomResult.Rooms);
    renderMails(mails);
    renderReports(reports);
    renderOverview(accounts, roomResult.Rooms, reports, mails);
    setConnection(true);
    setStatus(queryInput.value.trim() ? `“${queryInput.value.trim()}” 검색: 계정 ${accounts.length}개, 방 ${roomResult.Rooms.length}개` : `최근 조회 ${new Date().toLocaleTimeString()} · 처리 대기 신고 ${reports.length}개`, 'success');
    return true;
  } catch (error) {
    if (version === loadVersion && !sessionExpired) {
      setStatus(error.message, 'danger');
      setConnection(false);
    }
    return false;
  } finally {
    if (version === loadVersion) refreshButton.disabled = false;
  }
}

window.addEventListener('hashchange', selectSection);
logClose.addEventListener('click', () => logDialog.close());
logDialog.addEventListener('close', () => { if (!logDialog.open) clearLogDialog(); });
logBack.addEventListener('click', () => {
  if (logLoading || drawingLoading) return;
  const previous = logHistory.pop();
  if (previous) openLogs(previous.kind, previous.id, previous.name, false);
});
logMore.addEventListener('click', loadLogPage);
drawingAuthor.addEventListener('change', renderDrawing);
refreshButton.addEventListener('click', load);
searchForm.addEventListener('submit', event => { event.preventDefault(); load(); });
document.getElementById('search-reset').addEventListener('click', () => { queryInput.value = ''; load(); });
mailForm.addEventListener('input', event => {
  for (const id of ['mail-recipients', 'mail-title', 'mail-coins']) document.getElementById(id).setCustomValidity('');
  if (event.target !== mailConfirmation) mailConfirmation.checked = false;
  updateMailForm();
});
mailForm.addEventListener('submit', async event => {
  event.preventDefault();
  if (mailSending || sessionExpired) return;
  const title = document.getElementById('mail-title');
  const coins = document.getElementById('mail-coins');
  const gems = document.getElementById('mail-gems');
  const ids = mailAll.checked ? [] : recipientIds();
  title.setCustomValidity(title.value.trim() ? '' : '제목을 입력하세요.');
  mailRecipients.setCustomValidity(mailAll.checked || ids.length > 0 && ids.length <= 1000 ? '' : '받는 계정을 1~1,000개 입력하세요.');
  coins.setCustomValidity(coins.valueAsNumber + gems.valueAsNumber > 0 ? '' : '코인 또는 보석 보상을 입력하세요.');
  if (!mailForm.reportValidity()) return;
  const payload = { AccountIds: ids, SendToAll: mailAll.checked, Title: title.value.trim(), Message: document.getElementById('mail-message').value,
    Coins: coins.valueAsNumber, PaidGems: gems.valueAsNumber, ExpiresInDays: document.getElementById('mail-days').valueAsNumber };
  mailSending = true;
  mailConfirmation.checked = false;
  updateMailForm();
  try {
    const result = await request('api/mail', 'POST', payload);
    if (sessionExpired) return;
    mailForm.reset();
    const refreshed = loadVersion + 1;
    if (await load() && refreshed === loadVersion) setStatus(payload.SendToAll ? '전체 우편 발송 완료' : `우편 ${result.Created}개 발송 완료`, 'success');
  } catch (error) {
    if (!sessionExpired) setStatus(error.message, 'danger');
  } finally {
    mailSending = false;
    updateMailForm();
  }
});

async function initialize() {
  selectSection();
  updateMailForm();
  try {
    const session = await request('api/session');
    if (sessionExpired) return;
    csrfToken = session.CsrfToken;
    document.getElementById('admin-name').textContent = session.Username;
    await load();
  } catch (error) {
    if (!sessionExpired) {
      setStatus(error.message, 'danger');
      setConnection(false);
    }
  }
}

initialize();
