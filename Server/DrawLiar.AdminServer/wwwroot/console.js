'use strict';
let adminKey = '';
let loadVersion = 0;
const status = document.getElementById('status');
async function request(path, method = 'GET', body) {
  const loopback = ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname);
  if (location.protocol !== 'https:' && !(location.protocol === 'http:' && loopback)) throw new Error('HTTPS 주소로 접속하세요.');
  if (!adminKey) throw new Error('관리 키를 입력하세요.');
  const response = await fetch(path, { method, cache: 'no-store', headers: { Authorization: `Bearer ${adminKey}`, 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
  if (!response.ok) { const error = await response.json().catch(() => ({})); throw new Error(error.Code === 'Unauthorized' ? '관리 키를 확인하세요.' : error.Code === 'HttpsRequired' ? 'HTTPS 주소로 접속하세요.' : error.Code || `HTTP ${response.status}`); }
  return response.status === 204 ? null : response.json();
}
function row(values) {
  const element = document.createElement('tr');
  for (const value of values) { const cell = document.createElement('td'); cell.textContent = String(value); element.append(cell); }
  return element;
}
async function load() {
  const version = ++loadVersion;
  try {
    const query = encodeURIComponent(document.getElementById('query').value);
    const accounts = await request(`api/accounts?search=${query}`);
    if (version !== loadVersion) return false;
    const rooms = await request(`api/rooms?search=${query}`);
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
    status.textContent = `계정 ${accounts.length}개, 방 ${rooms.Rooms.length}개`;
    return true;
  } catch (error) { if (version === loadVersion) status.textContent = error.message; return false; }
}
document.getElementById('auth').addEventListener('submit', event => { event.preventDefault(); adminKey = document.getElementById('key').value; document.getElementById('key').value = ''; load(); });
document.getElementById('search').addEventListener('submit', event => { event.preventDefault(); load(); });
document.getElementById('logout').addEventListener('click', () => { ++loadVersion; adminKey = ''; document.getElementById('accounts').replaceChildren(); document.getElementById('rooms').replaceChildren(); status.textContent = '연결을 해제했습니다.'; });
