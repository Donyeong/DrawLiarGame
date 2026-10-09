const assert = require('node:assert/strict');
const test = require('node:test');
const { main, PACKAGE_NAME, PROJECT_NUMBER } = require('./ensure-android-app.cjs');

const SHA1 = 'A1'.repeat(20);
const SHA256 = 'B2'.repeat(32);
const PROJECT_ID = 'drawliar-ci';
const APP_ID = `1:${PROJECT_NUMBER}:android:abc`;
const OLD_APP_ID = `1:${PROJECT_NUMBER}:android:def`;

function androidApp(appId = APP_ID, packageName = PACKAGE_NAME) {
    return { appId, projectId: PROJECT_ID, packageName, state: 'ACTIVE',
        name: `projects/${PROJECT_ID}/androidApps/${appId}`, apiKeyId: 'SECRET_RESPONSE_FIELD' };
}

async function run(options = {}) {
    const calls = [], writes = [], logs = [], waits = [];
    const app = androidApp();
    const oldApp = androidApp(OLD_APP_ID, 'com.rascallab.drawliar');
    let pollCount = 0, time = 0;
    const registered = options.certificates === undefined ? [
        { certType: 'SHA_1', shaHash: SHA1 }, { certType: 'SHA_256', shaHash: SHA256 },
    ] : options.certificates;
    const auth = { request: async request => {
        calls.push(request);
        if (options.request) {
            const response = await options.request(request, { calls, app, oldApp, registered });
            if (response !== undefined) return { data: response };
        }
        if (options.failure && request.url.includes(options.failure))
            throw { response: { status: options.status || 403, data: 'SECRET_HTTP_BODY' }, message: 'SECRET_AUTH_HEADER' };
        const url = new URL(request.url);
        if (url.pathname.endsWith('/androidApps')) {
            if (!request.method) return { data: { apps: options.apps || (options.create ? [oldApp] : [oldApp, app]) } };
            return { data: options.operation || { name: 'operations/create1', done: false } };
        }
        if (url.pathname.includes('/operations/')) {
            pollCount++;
            return { data: options.pollOperation || { name: 'operations/create1', done: pollCount > 1,
                response: app, metadata: { unprinted: 'SECRET_OPERATION_METADATA' } } };
        }
        if (url.pathname.endsWith('/sha')) {
            if (!request.method) return { data: { certificates: registered } };
            registered.push(request.data);
            return { data: { ...request.data, name: `${app.name}/sha/${request.data.shaHash}` } };
        }
        if (url.pathname.endsWith('/groups')) return { data: { groups: [
            { name: `projects/${PROJECT_NUMBER}/groups/drawliar-testers`, testerCount: 2, releaseCount: 1, inviteLinkCount: 1, emails: ['SECRET_TESTER_EMAIL'] },
            { name: `projects/${PROJECT_NUMBER}/groups/kona-testers`, testerCount: 7 },
        ] } };
        if (url.pathname.includes('/groups/')) return { data: { name: `projects/${PROJECT_NUMBER}/groups/drawliar-testers`, testerCount: 2 } };
        throw new Error('예상하지 못한 테스트 요청');
    } };
    let result, error;
    try {
        result = await main({ GOOGLE_APPLICATION_CREDENTIALS: 'mock-secret-file', FIREBASE_CERTIFICATE_SHA1: SHA1,
            FIREBASE_CERTIFICATE_SHA256: SHA256, ...options.env }, options.checkOnly ? ['--check-only'] : [], {
            auth, fs: { mkdirSync() {}, writeFileSync(file, content) { writes.push({ file, content }); } },
            log(message) { logs.push(message); }, now() { return time; }, async wait(milliseconds) { waits.push(milliseconds); time += milliseconds; },
        });
    } catch (caught) { error = caught; }
    return { calls, writes, logs, waits, result, error };
}

test('정확한 신규 패키지 앱을 선택하고 이전 앱·그룹·테스터를 수정하지 않음', async () => {
    const value = await run();
    assert.equal(value.error, undefined);
    assert.equal(value.result.Success, true);
    assert.equal(value.result.AppId, APP_ID);
    assert.equal(value.result.ProjectId, PROJECT_ID);
    assert.equal(value.result.Created, false);
    assert.equal(value.result.Groups[0].TesterCount, 2);
    assert.equal(value.calls.some(call => call.method), false);
    assert.equal(value.calls.some(call => call.url.includes(OLD_APP_ID)), false);
    assert.equal(JSON.parse(value.writes.at(-1).content).AppId, APP_ID);
    assert.doesNotMatch(JSON.stringify(value.writes) + value.logs.join(), /SECRET|apiKeyId|emails/);
});

test('없는 앱만 생성하고 완료 작업을 기다린 뒤 해당 앱의 인증서를 등록', async () => {
    const value = await run({ create: true, certificates: [] });
    assert.equal(value.error, undefined);
    assert.equal(value.result.Created, true);
    assert.equal(value.result.Sha1Added, true);
    assert.equal(value.result.Sha256Added, true);
    assert.equal(value.waits.length, 2);
    const mutations = value.calls.filter(call => call.method);
    assert.equal(mutations.length, 3);
    assert.deepEqual(mutations[0].data, { packageName: PACKAGE_NAME, displayName: 'DrawLiar' });
    assert.equal(mutations.slice(1).every(call => call.url.includes(APP_ID) && call.url.endsWith('/sha')), true);
    assert.equal(value.calls.every(call => call.retry === false && call.timeout === 60000), true);
    assert.doesNotMatch(JSON.stringify(value.writes) + value.logs.join(), /SECRET/);
});

test('콜론·소문자 지문도 정규화하고 기존 SHA를 중복 등록하지 않음', async () => {
    const value = await run({ certificates: [{ certType: 'SHA_1', shaHash: SHA1.toLowerCase().match(/../g).join(':') }],
        env: { FIREBASE_CERTIFICATE_SHA1: SHA1.toLowerCase().match(/../g).join(':') } });
    assert.equal(value.error, undefined);
    assert.equal(value.result.Sha1Added, false);
    assert.equal(value.result.Sha256Added, true);
    const mutations = value.calls.filter(call => call.method);
    assert.equal(mutations.length, 1);
    assert.equal(mutations[0].data.certType, 'SHA_256');
});

test('앱·SHA 응답에 프로젝트 ID 또는 번호 리소스명이 모두 유효', async () => {
    const app = androidApp();
    app.name = `projects/${PROJECT_NUMBER}/androidApps/${APP_ID}`;
    const value = await run({ apps: [app], certificates: [
        { certType: 'SHA_1', shaHash: SHA1, name: `projects/${PROJECT_ID}/androidApps/${APP_ID}/sha/${SHA1}` },
        { certType: 'SHA_256', shaHash: SHA256, name: `projects/${PROJECT_NUMBER}/androidApps/${APP_ID}/sha/${SHA256}` },
    ] });
    assert.equal(value.error, undefined);
    assert.equal(value.result.AppId, APP_ID);
    assert.equal(value.calls.some(call => call.method), false);
});

test('check-only는 기존 모든 Android 앱·SHA와 그룹 카운트만 조회하며 파일과 클라우드 수정 없음', async () => {
    const value = await run({ checkOnly: true, env: { FIREBASE_CERTIFICATE_SHA1: '', FIREBASE_CERTIFICATE_SHA256: '' } });
    assert.equal(value.error, undefined);
    assert.equal(value.result.CheckOnly, true);
    assert.equal(value.result.Apps.length, 2);
    assert.equal(value.result.Apps[0].AppId, OLD_APP_ID);
    assert.equal(value.result.Apps[0].Certificates.length, 2);
    assert.equal(value.result.Groups[1].Alias, 'kona-testers');
    assert.equal(value.result.Groups[1].TesterCount, 7);
    assert.equal(value.writes.length, 0);
    assert.equal(value.calls.every(call => !call.method), true);
    assert.equal(value.calls.some(call => call.url.includes('/testers')), false);
    assert.doesNotMatch(value.logs.join(), /SECRET|apiKeyId|emails/);
});

test('check-only에서 앱이 없으면 생성하지 않고 빈 AppId를 반환', async () => {
    const value = await run({ create: true, checkOnly: true });
    assert.equal(value.error, undefined);
    assert.equal(value.result.AppId, '');
    assert.equal(value.calls.some(call => call.method), false);
});

test('동일 패키지가 중복 등록되면 임의 앱을 선택하지 않음', async () => {
    const value = await run({ apps: [androidApp(), androidApp(OLD_APP_ID)] });
    assert.match(value.error.message, /여러 개/);
    assert.equal(value.calls.some(call => call.method), false);
});

test('권한 거부 오류는 필요한 권한만 안내하고 HTTP·인증 원문을 노출하지 않음', async () => {
    for (const failure of ['/androidApps?', '/groups/', '/sha']) {
        const value = await run({ failure });
        assert.match(value.error.message, /HTTP 403/);
        assert.match(value.error.message, /권한/);
        assert.doesNotMatch(value.error.message + JSON.stringify(value.writes) + value.logs.join(), /SECRET/);
        assert.equal(value.writes.some(write => JSON.parse(write.content).Success), false);
    }
});

test('지문·자격 증명·타 프로젝트 패키지 입력이 잘못되면 API 호출 전 중단', async () => {
    for (const env of [{ FIREBASE_CERTIFICATE_SHA1: '' }, { FIREBASE_CERTIFICATE_SHA256: 'wrong' },
        { GOOGLE_APPLICATION_CREDENTIALS: '' }, { FIREBASE_PACKAGE_NAME: 'com.rascallab.konasurvival' }, { FIREBASE_PROJECT_NUMBER: 'project-id' }]) {
        const value = await run({ env });
        assert.ok(value.error);
        assert.equal(value.calls.length, 0);
    }
});

test('기존 전용 그룹이 없거나 응답 그룹이 다르면 앱 생성·SHA 등록 이전 중단', async () => {
    const missing = await run({ create: true, failure: '/groups/', status: 404 });
    assert.match(missing.error.message, /테스터 그룹이 없습니다/);
    assert.equal(missing.calls.some(call => call.method), false);
    for (const options of [{ env: { FIREBASE_GROUPS: 'kona-testers' } }, { request(request) {
        if (request.url.includes('/groups/')) return { name: `projects/${PROJECT_NUMBER}/groups/kona-testers`, testerCount: 2 };
    } }]) {
        const value = await run({ create: true, ...options });
        assert.ok(value.error);
        assert.equal(value.calls.some(call => call.method), false);
    }
});

test('신규 앱 생성의 패키지·프로젝트·작업 이름 응답을 검증', async () => {
    for (const options of [{ operation: { name: 'https://attacker.invalid/SECRET' } },
        { operation: { name: 'operations/create1', done: true, response: androidApp(APP_ID, 'com.rascallab.kona') } },
        { operation: { name: 'operations/create1', done: true, response: androidApp('1:999:android:abc') } },
        { pollOperation: { name: 'operations/other', done: true, response: androidApp() } }]) {
        const value = await run({ create: true, ...options });
        assert.ok(value.error);
        assert.equal(value.calls.some(call => call.method && call.url.endsWith('/sha')), false);
        assert.equal(value.writes.some(write => JSON.parse(write.content).Success), false);
        assert.doesNotMatch(value.error.message + JSON.stringify(value.writes), /SECRET/);
    }
});

test('신규 앱 생성의 비동기 실패·5분 시간 초과를 안전하게 처리', async () => {
    const failed = await run({ create: true, operation: { name: 'operations/create1', done: true,
        error: { code: 7, message: 'SECRET_ERROR_DETAILS' } } });
    assert.match(failed.error.message, /403.*firebase.clients.create/);
    assert.doesNotMatch(failed.error.message, /SECRET/);
    const timed = await run({ create: true, pollOperation: { name: 'operations/create1', done: false } });
    assert.match(timed.error.message, /시간이 초과/);
    assert.equal(timed.waits.length, 60);
    assert.equal(timed.calls.some(call => call.method && call.url.endsWith('/sha')), false);
});

test('앱 생성 충돌은 재조회에서 정확한 패키지가 확인된 경우만 재사용', async () => {
    let reads = 0;
    const value = await run({ create: true, request(request) {
        if (new URL(request.url).pathname.endsWith('/androidApps')) {
            if (request.method) throw { response: { status: 409, data: 'SECRET_RESPONSE' } };
            reads++;
            return { apps: reads === 1 ? [] : [androidApp()] };
        }
    } });
    assert.equal(value.error, undefined);
    assert.equal(value.result.Created, false);
    assert.equal(value.result.AppId, APP_ID);
    assert.equal(reads, 2);
    assert.doesNotMatch(JSON.stringify(value.writes) + value.logs.join(), /SECRET/);
});

test('SHA 동시 등록 충돌도 재조회에서 같은 지문이 확인되면 성공', async () => {
    const value = await run({ certificates: [], request(request, context) {
        if (request.method && request.url.endsWith('/sha')) {
            context.registered.push(request.data);
            throw { response: { status: 409, data: 'SECRET_RESPONSE' } };
        }
    } });
    assert.equal(value.error, undefined);
    assert.equal(value.result.Sha1Registered, true);
    assert.equal(value.result.Sha256Registered, true);
    assert.equal(value.result.Sha1Added, false);
});

test('SHA 등록 응답이 다른 앱 또는 지문이면 성공 기록하지 않음', async () => {
    for (const response of [{ certType: 'SHA_1', shaHash: 'C3'.repeat(20) },
        { certType: 'SHA_1', shaHash: SHA1, name: `projects/${PROJECT_ID}/androidApps/${OLD_APP_ID}/sha/${SHA1}` }]) {
        const value = await run({ certificates: [], request(request) {
            if (request.method && request.url.endsWith('/sha')) return response;
        } });
        assert.ok(value.error);
        assert.equal(value.writes.some(write => JSON.parse(write.content).Success), false);
    }
});

test('목록 페이지를 끝까지 조회하며 반복 토큰은 안전하게 중단', async () => {
    let reads = 0;
    const value = await run({ request(request) {
        if (new URL(request.url).pathname.endsWith('/androidApps') && !request.method) {
            reads++;
            return reads === 1 ? { apps: [androidApp(OLD_APP_ID, 'com.rascallab.drawliar')], nextPageToken: 'secret-token/+==' } : { apps: [androidApp()] };
        }
    } });
    assert.equal(value.error, undefined);
    assert.equal(value.result.AppId, APP_ID);
    assert.match(value.calls[1].url, /pageToken=secret-token%2F%2B%3D%3D/);
    assert.doesNotMatch(JSON.stringify(value.writes) + value.logs.join(), /secret-token/);
    const repeated = await run({ request(request) {
        if (new URL(request.url).pathname.endsWith('/androidApps')) return { apps: [], nextPageToken: 'SECRET_PAGE_TOKEN' };
    } });
    assert.match(repeated.error.message, /페이지 응답/);
    assert.doesNotMatch(repeated.error.message, /SECRET/);
    assert.equal(repeated.calls.length, 2);
});
