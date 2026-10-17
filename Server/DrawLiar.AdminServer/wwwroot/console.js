'use strict';
let adminKey = '';
let loadVersion = 0;
const REPORT_REASONS = { Abuse: '욕설·비하', Drawing: '부적절한 그림', Nickname: '부적절한 닉네임', Spam: '도배·광고', Topic: '부적절한 주제', Other: '기타' };
const status = document.getElementById('status');
async function request(path, method = 'GET', body) {
  const loopback = ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname);
  if (location.protocol !== 'https:' && !(location.protocol === 'http:' && loopback)) throw new Error('HTTPS 주소로 접속하세요.');
  if (!adminKey) throw new Error('관리 키를 입력하세요.');
  const response = await fetch(path, { method, cache: 'no-store', headers: { Authorization: `Bearer ${adminKey}`, 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
  if (!response.ok) { const error = await response.json().catch(() => ({})); throw new Error(error.Code === 'Unauthorized' ? '관리 키를 확인하세요.' : error.Code === 'HttpsRequired' ? 'HTTPS 주소로 접속하세요.' : error.Code === 'InvalidClientVersion' ? '버전(0.1.0 형식)과 HTTPS 주소를 확인하세요. 강제 버전은 일반 버전보다 높을 수 없습니다.' : error.Code || `HTTP ${response.status}`); }
  return response.status === 204 ? null : response.json();
}
function row(values) {
  const element = document.createElement('tr');
  for (const value of values) { const cell = document.createElement('td'); cell.textContent = String(value); element.append(cell); }
  return element;
}
function renderClientVersions(policies) {
  const body = document.getElementById('client-versions'); body.replaceChildren();
  for (const policy of policies) {
    const element = row([policy.Platform]);
    const inputs = {};
    for (const [field, label, maxLength] of [['MinimumVersion', '강제 업데이트 버전', 32], ['LatestVersion', '일반 업데이트 버전', 32], ['StoreUrl', '스토어 주소', 512]]) {
      const cell = document.createElement('td');
      const input = document.createElement('input'); input.type = 'text'; input.value = policy[field]; input.maxLength = maxLength; input.setAttribute('aria-label', `${policy.Platform} ${label}`);
      input.placeholder = field === 'StoreUrl' ? policy.Platform === 'Android' ? 'https://play.google.com/store/apps/details?id=com.rascallab.liargame' : 'https://' : '0.1.0';
      inputs[field] = input; cell.append(input); element.append(cell);
    }
    const actions = document.createElement('td');
    const button = document.createElement('button'); button.type = 'button'; button.textContent = '저장';
    button.addEventListener('click', async () => {
      const actionVersion = loadVersion; button.disabled = true;
      try {
        await request(`api/client-versions/${encodeURIComponent(policy.Platform)}`, 'PUT', { Platform: policy.Platform, MinimumVersion: inputs.MinimumVersion.value, LatestVersion: inputs.LatestVersion.value, StoreUrl: inputs.StoreUrl.value });
        if (actionVersion !== loadVersion) return;
        const refreshVersion = loadVersion + 1;
        if (await load() && refreshVersion === loadVersion) status.textContent = `${policy.Platform}: 클라이언트 버전 저장 완료`;
      } catch (error) { if (actionVersion === loadVersion) status.textContent = error.message; }
      finally { button.disabled = false; }
    });
    actions.append(button); element.append(actions); body.append(element);
  }
}
async function load() {
  const version = ++loadVersion;
  try {
    const query = encodeURIComponent(document.getElementById('query').value);
    const accounts = await request(`api/accounts?search=${query}`);
    if (version !== loadVersion) return false;
    const rooms = await request(`api/rooms?search=${query}`);
    if (version !== loadVersion) return false;
    const policies = await request('api/client-versions');
    if (version !== loadVersion) return false;
    const accountsBody = document.getElementById('accounts');
    accountsBody.replaceChildren();
    for (const account of accounts) {
      const element = row([account.DisplayName, account.AccountId, account.Coins, account.IsBanned ? '차단' : '정상']);
      const actions = document.createElement('td');
      const confirmation = document.createElement('label');
      const checkbox = document.createElement('input'); checkbox.type = 'checkbox';
      confirmation.append(checkbox, document.createTextNode(' 차단 확인 ')); actions.append(confirmation);
      for (const [label, banned] of [['차단', true], ['차단 해제', false]]) {
        const button = document.createElement('button'); button.type = 'button'; button.textContent = label;
        if (banned) { button.disabled = true; checkbox.addEventListener('change', () => { button.disabled = !checkbox.checked; }); }
        button.addEventListener('click', async () => {
          const actionVersion = loadVersion;
          try {
            await request(`api/accounts/${encodeURIComponent(account.AccountId)}/ban`, 'PATCH', { IsBanned: banned });
            if (actionVersion !== loadVersion) return;
            const refreshVersion = loadVersion + 1;
            if (await load() && refreshVersion === loadVersion) status.textContent = `${account.DisplayName}: ${label} 완료`;
          } catch (error) { if (actionVersion === loadVersion) status.textContent = error.message; }
        });
        actions.append(button);
      }
      element.append(actions); accountsBody.append(element);
    }
    const roomsBody = document.getElementById('rooms'); roomsBody.replaceChildren();
    for (const room of rooms.Rooms) roomsBody.append(row([room.Name, room.RoomId, room.PlayerCount, room.SpectatorCount, room.IsInProgress ? '진행 중' : '대기 중']));
    const reports = await request('api/reports?status=Open');
    if (version !== loadVersion) return false;
    renderClientVersions(policies);
    const reportsBody = document.getElementById('reports'); reportsBody.replaceChildren();
    for (const report of reports) {
      const element = row([new Date(report.CreatedAt).toLocaleString(), REPORT_REASONS[report.Reason] || report.Reason,
        `${report.TargetName} (${report.TargetAccountId})`, `${report.ReporterName} (${report.ReporterAccountId})`, report.Context,
        report.WorkshopTopicId ? `주제 ${report.WorkshopTopicId}` : report.RoomId ? `방 ${report.RoomId}` : '']);
      const actions = document.createElement('td');
      for (const [label, next] of [['처리 완료', 'Resolved'], ['기각', 'Dismissed']]) {
        const button = document.createElement('button'); button.type = 'button'; button.textContent = label;
        button.addEventListener('click', async () => {
          const actionVersion = loadVersion;
          try {
            await request(`api/reports/${encodeURIComponent(report.ReportId)}`, 'PATCH', { Status: next });
            if (actionVersion === loadVersion && await load()) status.textContent = `신고 ${label}`;
          } catch (error) { if (actionVersion === loadVersion) status.textContent = error.message; }
        });
        actions.append(button);
      }
      element.append(actions); reportsBody.append(element);
    }
    status.textContent = `계정 ${accounts.length}개, 방 ${rooms.Rooms.length}개, 처리 대기 신고 ${reports.length}개`;
    return true;
  } catch (error) { if (version === loadVersion) status.textContent = error.message; return false; }
}
document.getElementById('auth').addEventListener('submit', event => { event.preventDefault(); adminKey = document.getElementById('key').value; document.getElementById('key').value = ''; load(); });
document.getElementById('search').addEventListener('submit', event => { event.preventDefault(); load(); });
document.getElementById('logout').addEventListener('click', () => { ++loadVersion; adminKey = ''; document.getElementById('client-versions').replaceChildren(); document.getElementById('accounts').replaceChildren(); document.getElementById('rooms').replaceChildren(); document.getElementById('reports').replaceChildren(); status.textContent = '연결을 해제했습니다.'; });
