'use strict';
let adminKey = '';
const status = document.getElementById('status');
async function request(path, method = 'GET', body) {
  if (!adminKey) throw new Error('관리 키를 입력하세요.');
  const response = await fetch(path, { method, cache: 'no-store', headers: { Authorization: `Bearer ${adminKey}`, 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
  if (!response.ok) { const error = await response.json().catch(() => ({})); throw new Error(error.Code || `HTTP ${response.status}`); }
  return response.status === 204 ? null : response.json();
}
function row(values) {
  const element = document.createElement('tr');
  for (const value of values) { const cell = document.createElement('td'); cell.textContent = String(value); element.append(cell); }
  return element;
}
async function load() {
  try {
    const query = encodeURIComponent(document.getElementById('query').value);
    const accounts = await request(`/api/accounts?search=${query}`);
    const rooms = await request(`/api/rooms?search=${query}`);
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
        button.addEventListener('click', async () => { try { await request(`/api/accounts/${encodeURIComponent(account.AccountId)}/ban`, 'PATCH', { IsBanned: banned }); await load(); status.textContent = `${account.DisplayName}: ${label} 완료`; } catch (error) { status.textContent = error.message; } });
        actions.append(button);
      }
      element.append(actions); accountsBody.append(element);
    }
    const roomsBody = document.getElementById('rooms'); roomsBody.replaceChildren();
    for (const room of rooms.Rooms) roomsBody.append(row([room.Name, room.RoomId, room.PlayerCount, room.SpectatorCount, room.IsInProgress ? '진행 중' : '대기 중']));
    status.textContent = `계정 ${accounts.length}개, 방 ${rooms.Rooms.length}개`;
  } catch (error) { status.textContent = error.message; }
}
document.getElementById('auth').addEventListener('submit', event => { event.preventDefault(); adminKey = document.getElementById('key').value; document.getElementById('key').value = ''; load(); });
document.getElementById('search').addEventListener('submit', event => { event.preventDefault(); load(); });
document.getElementById('logout').addEventListener('click', () => { adminKey = ''; document.getElementById('accounts').replaceChildren(); document.getElementById('rooms').replaceChildren(); status.textContent = '연결을 해제했습니다.'; });
