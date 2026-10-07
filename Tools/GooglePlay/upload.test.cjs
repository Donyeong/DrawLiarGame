const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { Main, PACKAGE_NAME, MAX_VERSION_CODE } = require('./upload.cjs');

function Fixture(t) {
    const directory = fs.mkdtempSync(path.join(__dirname, '.test-'));
    t.after(() => {
        assert.ok(path.resolve(directory).startsWith(`${path.resolve(__dirname)}${path.sep}`));
        fs.rmSync(directory, { recursive: true, force: true });
    });
    const env = {
        GOOGLE_APPLICATION_CREDENTIALS: path.join(directory, 'secret-do-not-print.json'),
        GOOGLE_PLAY_PLAN: path.join(directory, 'plan.json'),
        GOOGLE_PLAY_RESULT: path.join(directory, 'result.json'),
        GOOGLE_PLAY_VERSION_STATE: path.join(directory, 'state.json'),
        ANDROID_BUILD_RESULT: path.join(directory, 'build.json'),
        BUILD_FILE: path.join(directory, 'DrawLiar.aab'),
        BUILD_NUMBER: '15', DRAWLIAR_VERSION_CODE: '15', BUNDLE_VERSION: '0.1.0',
    };
    fs.writeFileSync(env.BUILD_FILE, 'mock-signed-aab-payload');
    const Write = (file, value) => fs.writeFileSync(file, JSON.stringify(value));
    const Read = file => JSON.parse(fs.readFileSync(file, 'utf8'));
    const Build = overrides => Write(env.ANDROID_BUILD_RESULT, {
        Result: 'Succeeded', Format: 'AAB', Errors: 0, Development: false,
        OutputPath: env.BUILD_FILE, TotalBytes: fs.statSync(env.BUILD_FILE).size,
        BundleVersion: env.BUNDLE_VERSION, VersionCode: Number(env.DRAWLIAR_VERSION_CODE), ...overrides,
    });
    Build();
    const Reserve = versionCode => {
        env.DRAWLIAR_VERSION_CODE = String(versionCode);
        Write(env.GOOGLE_PLAY_PLAN, { Success: true, PackageName: PACKAGE_NAME, Track: 'internal', Status: 'draft', BuildNumber: 15, VersionCode: versionCode });
        Write(env.GOOGLE_PLAY_VERSION_STATE, { PackageName: PACKAGE_NAME, HighWaterVersionCode: versionCode, BuildNumber: 15 });
        Build();
    };
    Reserve(15);
    return { env, Write, Read, Build, Reserve, directory };
}

function Api(overrides = {}) {
    const calls = [];
    const fake = {
        Calls: calls,
        CreateAuth: async () => ({
            request: async options => {
                calls.push(options);
                const url = new URL(options.url);
                const resource = url.pathname;
                const method = options.method || 'GET';
                let operation;
                if (resource.endsWith('/edits') && method === 'POST') operation = 'insert';
                else if (method === 'DELETE') operation = 'delete';
                else if (resource.endsWith(':validate')) operation = 'validate';
                else if (resource.endsWith(':commit')) operation = 'commit';
                else if (method === 'PUT') operation = 'track';
                else if (resource.startsWith('/upload/')) operation = 'upload';
                else if (resource.endsWith('/bundles')) operation = 'bundles';
                else if (resource.endsWith('/apks')) operation = 'apks';
                else if (resource.endsWith('/tracks')) operation = 'tracks';
                else assert.fail(`Unexpected API operation: ${resource}`);
                if (overrides.Fail === operation) throw overrides.Error || new Error('secret-access-token-and-private-key');
                let value;
                if (operation === 'insert') value = { id: 'edit123' };
                if (operation === 'delete') value = {};
                if (operation === 'bundles') value = { bundles: overrides.Bundles || [] };
                if (operation === 'apks') value = { apks: overrides.Apks || [] };
                if (operation === 'tracks') value = { tracks: overrides.Tracks || [{ track: 'internal', releases: [] }] };
                if (operation === 'upload') {
                    const chunks = [];
                    for await (const chunk of options.data) chunks.push(chunk);
                    const hash = crypto.createHash('sha256').update(Buffer.concat(chunks)).digest('hex');
                    value = { versionCode: 15, sha256: hash, ...overrides.Bundle };
                    if (overrides.AfterUpload) await overrides.AfterUpload();
                }
                if (operation === 'track') value = JSON.parse(JSON.stringify(options.data));
                if (operation === 'validate' || operation === 'commit') value = { id: 'edit123' };
                if (overrides.Response) value = overrides.Response(operation, value, options);
                return { data: value };
            },
        }),
    };
    return fake;
}

function Methods(api) {
    return api.Calls.map(call => `${call.method || 'GET'} ${new URL(call.url).pathname.split('/').slice(-1)[0]}`);
}

test('첫 업로드 이후 예약은 모든 AAB·APK·트랙 및 지속 상태 중 최댓값보다 증가한다', async t => {
    const fixture = Fixture(t);
    fixture.Write(fixture.env.GOOGLE_PLAY_VERSION_STATE, { PackageName: PACKAGE_NAME, HighWaterVersionCode: 500, BuildNumber: 14 });
    const api = Api({ Bundles: [{ versionCode: 99 }], Apks: [{ versionCode: 250 }], Tracks: [{ track: 'production', releases: [{ status: 'completed', versionCodes: ['300'] }] }] });
    const plan = await Main(fixture.env, ['node', 'upload.cjs', '--plan'], api);
    assert.equal(plan.VersionCode, 501);
    assert.equal(plan.BuildNumber, 15);
    assert.equal(plan.PackageName, PACKAGE_NAME);
    assert.deepEqual(fixture.Read(fixture.env.GOOGLE_PLAY_PLAN), plan);
    assert.equal(fixture.Read(fixture.env.GOOGLE_PLAY_VERSION_STATE).HighWaterVersionCode, 501);
    assert.deepEqual(Methods(api), ['POST edits', 'GET bundles', 'GET apks', 'GET tracks', 'DELETE edit123']);
    assert.ok(api.Calls.every(call => call.retry === false));
});

test('영구 상태가 없으면 Jenkins 번호를 최소값으로 예약하고 두 번째 예약은 증가한다', async t => {
    const fixture = Fixture(t);
    fs.unlinkSync(fixture.env.GOOGLE_PLAY_VERSION_STATE);
    const first = await Main(fixture.env, ['--plan'], Api());
    const second = await Main(fixture.env, ['--plan'], Api());
    assert.equal(first.VersionCode, 15);
    assert.equal(second.VersionCode, 16);
    assert.equal(fs.existsSync(`${fixture.env.GOOGLE_PLAY_VERSION_STATE}.lock`), false);
});

test('트랙 버전 코드가 최댓값이면 API 값을 기준으로 예약한다', async t => {
    const fixture = Fixture(t);
    const plan = await Main(fixture.env, ['--plan'], Api({ Tracks: [{ track: 'beta', releases: [{ status: 'completed', versionCodes: ['700'] }] }] }));
    assert.equal(plan.VersionCode, 701);
});

test('내부 테스트 초안만 생성하고 기존 completed·inProgress·halted 릴리스를 보존한다', async t => {
    const fixture = Fixture(t);
    const releases = [
        { name: 'live', status: 'completed', versionCodes: ['11'], releaseNotes: [{ language: 'ko-KR', text: '유지' }], inAppUpdatePriority: 2 },
        { name: 'rollout', status: 'inProgress', versionCodes: ['12'], userFraction: 0.25 },
        { name: 'paused', status: 'halted', versionCodes: ['13'], userFraction: 0.1 },
    ];
    const api = Api({ Tracks: [{ track: 'internal', releases }] });
    const result = await Main(fixture.env, [], api);
    assert.equal(result.Success, true);
    assert.equal(result.Committed, true);
    assert.equal(result.Track, 'internal');
    assert.equal(result.Status, 'draft');
    assert.equal(result.VersionCode, 15);
    assert.equal(result.Sha256, crypto.createHash('sha256').update(fs.readFileSync(fixture.env.BUILD_FILE)).digest('hex'));
    const upload = api.Calls.find(call => call.url.includes('/upload/'));
    assert.equal(upload.timeout, 20 * 60000);
    assert.equal(new URL(upload.url).searchParams.get('uploadType'), 'media');
    const track = api.Calls.find(call => call.method === 'PUT');
    assert.ok(track.url.endsWith('/tracks/internal'));
    assert.deepEqual(track.data.releases.slice(0, 3), releases);
    assert.deepEqual(track.data.releases[3].versionCodes, ['15']);
    assert.equal(track.data.releases[3].status, 'draft');
    assert.deepEqual(Methods(api), ['POST edits', 'GET bundles', 'GET apks', 'GET tracks', 'POST bundles', 'PUT internal', 'POST edit123:validate', 'POST edit123:commit']);
    const commit = api.Calls.at(-1);
    assert.equal(new URL(commit.url).searchParams.get('changesInReviewBehavior'), 'ERROR_IF_IN_REVIEW');
    assert.equal(commit.data, undefined);
    assert.equal(api.Calls.at(-2).data, undefined);
    assert.ok(api.Calls.every(call => call.url.startsWith('https://androidpublisher.googleapis.com/')));
    assert.deepEqual(fixture.Read(fixture.env.GOOGLE_PLAY_RESULT), result);
});

test('기존 draft는 예약·업로드 전에 중단하고 편집을 삭제한다', async t => {
    const fixture = Fixture(t);
    const tracks = [{ track: 'internal', releases: [{ status: 'draft', versionCodes: ['14'] }] }];
    for (const argv of [[], ['--plan']]) {
        const api = Api({ Tracks: tracks });
        await assert.rejects(Main(fixture.env, argv, api), /기존 초안/);
        assert.equal(api.Calls.some(call => call.url.includes('/upload/')), false);
        assert.equal(api.Calls.at(-1).method, 'DELETE');
    }
});

test('다른 트랙의 기존 초안은 변경하지 않는다', async t => {
    const fixture = Fixture(t);
    const api = Api({ Tracks: [{ track: 'production', releases: [{ status: 'draft', versionCodes: ['14'] }] }, { track: 'internal', releases: [] }] });
    await Main(fixture.env, [], api);
    assert.equal(api.Calls.filter(call => call.method === 'PUT').length, 1);
    assert.ok(api.Calls.find(call => call.method === 'PUT').url.endsWith('/tracks/internal'));
});

test('예약 이후 AAB·APK·트랙 버전 충돌은 업로드 전에 중단한다', async t => {
    const fixture = Fixture(t);
    for (const overrides of [{ Bundles: [{ versionCode: 15 }] }, { Apks: [{ versionCode: 15 }] }, { Tracks: [{ track: 'production', releases: [{ status: 'completed', versionCodes: ['16'] }] }] }]) {
        const api = Api(overrides);
        await assert.rejects(Main(fixture.env, [], api), /이미 Google Play/);
        assert.equal(api.Calls.some(call => call.url.includes('/upload/')), false);
        assert.equal(api.Calls.at(-1).method, 'DELETE');
    }
});

test('파일 경로·빌드 결과·버전·크기·Development 불일치는 인증 전에 차단한다', async t => {
    const fixture = Fixture(t);
    const mismatches = [
        { Result: 'Failed' }, { Format: 'APK' }, { Errors: 1 }, { Development: true },
        { VersionCode: 99 }, { BundleVersion: '0.0.0' }, { OutputPath: path.join(fixture.directory, 'old.aab') }, { TotalBytes: 1 },
    ];
    for (const mismatch of mismatches) {
        fixture.Build(mismatch);
        const api = Api();
        await assert.rejects(Main(fixture.env, [], api), /빌드 결과/);
        assert.equal(api.Calls.length, 0);
    }
});

test('계획 패키지·Jenkins 번호·실제 코드 및 예약 상태 소유권을 검증한다', async t => {
    const fixture = Fixture(t);
    const original = fixture.Read(fixture.env.GOOGLE_PLAY_PLAN);
    for (const mismatch of [{ PackageName: 'other.app' }, { BuildNumber: 14 }, { VersionCode: 14 }, { Status: 'completed' }, { Track: 'production' }, { Success: false }]) {
        fixture.Write(fixture.env.GOOGLE_PLAY_PLAN, { ...original, ...mismatch });
        const api = Api();
        await assert.rejects(Main(fixture.env, [], api), /예약 계획/);
        assert.equal(api.Calls.length, 0);
    }
    fixture.Write(fixture.env.GOOGLE_PLAY_PLAN, original);
    fixture.Write(fixture.env.GOOGLE_PLAY_VERSION_STATE, { PackageName: PACKAGE_NAME, HighWaterVersionCode: 16, BuildNumber: 16 });
    await assert.rejects(Main(fixture.env, [], Api()), /예약 계획/);
});

test('손상되거나 다른 앱의 지속 상태·최대 버전 코드·동시 잠금을 차단한다', async t => {
    const fixture = Fixture(t);
    for (const state of [{ PackageName: 'other.app', HighWaterVersionCode: 15, BuildNumber: 15 }, { PackageName: PACKAGE_NAME, HighWaterVersionCode: 'broken', BuildNumber: 15 }, { PackageName: PACKAGE_NAME, HighWaterVersionCode: MAX_VERSION_CODE, BuildNumber: 15 }]) {
        fixture.Write(fixture.env.GOOGLE_PLAY_VERSION_STATE, state);
        const api = Api();
        await assert.rejects(Main(fixture.env, ['--plan'], api), /패키지명|양의 정수|최대 버전 코드/);
        assert.equal(api.Calls.at(-1).method, 'DELETE');
    }
    fixture.Write(fixture.env.GOOGLE_PLAY_VERSION_STATE, { PackageName: PACKAGE_NAME, HighWaterVersionCode: 15, BuildNumber: 15 });
    fs.writeFileSync(`${fixture.env.GOOGLE_PLAY_VERSION_STATE}.lock`, 'other-job');
    await assert.rejects(Main(fixture.env, ['--plan'], Api()), /다른 실행/);
    assert.equal(fs.readFileSync(`${fixture.env.GOOGLE_PLAY_VERSION_STATE}.lock`, 'utf8'), 'other-job');
});

test('API의 잘못된 버전 코드나 편집 ID를 URL로 사용하지 않는다', async t => {
    const fixture = Fixture(t);
    const api = Api({ Response: (operation, value) => operation === 'insert' ? { id: '../secret' } : value });
    await assert.rejects(Main(fixture.env, ['--plan'], api), /편집 ID/);
    assert.equal(api.Calls.length, 1);
    const malformed = Api({ Bundles: [{ versionCode: '123secret' }] });
    await assert.rejects(Main(fixture.env, ['--plan'], malformed), /양의 정수/);
    assert.equal(malformed.Calls.at(-1).method, 'DELETE');
});

test('업로드 versionCode·SHA-256 불일치는 track 갱신 없이 삭제한다', async t => {
    const fixture = Fixture(t);
    for (const Bundle of [{ versionCode: 16 }, { sha256: '0'.repeat(64) }, { sha256: 'secret' }]) {
        const api = Api({ Bundle });
        await assert.rejects(Main(fixture.env, [], api), /SHA-256/);
        assert.equal(api.Calls.some(call => call.method === 'PUT'), false);
        assert.equal(api.Calls.at(-1).method, 'DELETE');
    }
});

test('업로드 동안 실제 AAB 파일이 변경되면 commit하지 않는다', async t => {
    const fixture = Fixture(t);
    const api = Api({ AfterUpload: () => fs.writeFileSync(fixture.env.BUILD_FILE, 'modified-aab-payload') });
    await assert.rejects(Main(fixture.env, [], api), /SHA-256/);
    assert.equal(api.Calls.some(call => call.url.includes(':commit')), false);
});

test('track 응답이 기존 릴리스를 제거하거나 새 릴리스를 발행하면 validate·commit하지 않는다', async t => {
    const fixture = Fixture(t);
    for (const mutate of [value => ({ ...value, releases: value.releases.slice(1) }), value => ({ ...value, releases: value.releases.map((release, index) => index === 1 ? { ...release, status: 'completed' } : release) })]) {
        const api = Api({ Tracks: [{ track: 'internal', releases: [{ status: 'completed', versionCodes: ['14'] }] }], Response: (operation, value) => operation === 'track' ? mutate(value) : value });
        await assert.rejects(Main(fixture.env, [], api), /단일 초안|릴리스 보존/);
        assert.equal(api.Calls.some(call => call.url.includes(':validate') || call.url.includes(':commit')), false);
    }
});

test('tracks.update가 기존 릴리스 보존을 거부하면 안전한 오류로 중단하고 fallback 발행하지 않는다', async t => {
    const fixture = Fixture(t);
    const api = Api({ Fail: 'track', Error: { response: { status: 400, data: { error: { message: 'secret-server-body' } } }, config: { headers: { Authorization: 'secret-token' } } } });
    await assert.rejects(Main(fixture.env, [], api), error => /기존 릴리스를 유지/.test(error.message) && !/secret/.test(error.message));
    assert.equal(api.Calls.filter(call => call.method === 'PUT').length, 1);
    assert.equal(api.Calls.some(call => call.url.includes(':commit')), false);
    assert.equal(api.Calls.at(-1).method, 'DELETE');
});

test('validate 실패는 commit 없이 정리하고 실패 공개 결과만 남긴다', async t => {
    const fixture = Fixture(t);
    const api = Api({ Fail: 'validate' });
    await assert.rejects(Main(fixture.env, [], api), /Google Play 요청 실패/);
    assert.equal(api.Calls.some(call => call.url.includes(':commit')), false);
    assert.equal(api.Calls.at(-1).method, 'DELETE');
    const result = fixture.Read(fixture.env.GOOGLE_PLAY_RESULT);
    assert.equal(result.Success, false);
    assert.equal(result.Committed, false);
    assert.equal(result.CommitAttempted, false);
    assert.ok(!JSON.stringify(result).includes('secret'));
});

test('기존 릴리스 메타데이터·validate ID 변경은 commit 전에 차단한다', async t => {
    const fixture = Fixture(t);
    const preserved = { name: 'live', status: 'completed', versionCodes: ['14'], releaseNotes: [{ language: 'ko-KR', text: '기존 내용' }] };
    const metadataApi = Api({ Tracks: [{ track: 'internal', releases: [preserved] }], Response: (operation, value) => {
        if (operation === 'track') value.releases[0].releaseNotes[0].text = '변경된 내용';
        return value;
    } });
    await assert.rejects(Main(fixture.env, [], metadataApi), /릴리스 보존/);
    assert.equal(metadataApi.Calls.some(call => call.url.includes(':commit')), false);
    const validationApi = Api({ Response: (operation, value) => operation === 'validate' ? { id: 'otherEdit' } : value });
    await assert.rejects(Main(fixture.env, [], validationApi), /편집 검증 응답/);
    assert.equal(validationApi.Calls.some(call => call.url.includes(':commit')), false);
});

test('실패한 임시 편집 삭제의 원문은 출력하지 않고 원래 실패를 유지한다', async t => {
    const fixture = Fixture(t);
    const warnings = [];
    const previousWarn = console.warn;
    console.warn = value => warnings.push(value);
    t.after(() => { console.warn = previousWarn; });
    const api = Api({ Bundles: [{ versionCode: 15 }], Fail: 'delete', Error: { response: { status: 403, data: 'secret-authorization-body' }, message: 'secret-private-key' } });
    await assert.rejects(Main(fixture.env, [], api), /이미 Google Play/);
    assert.equal(warnings.length, 1);
    assert.ok(!warnings[0].includes('secret'));
    assert.equal(api.Calls.at(-1).method, 'DELETE');
});

test('commit 응답 ID가 다르면 저장 성공을 표시하거나 자동 재시도하지 않는다', async t => {
    const fixture = Fixture(t);
    const api = Api({ Response: (operation, value) => operation === 'commit' ? { id: 'otherEdit' } : value });
    await assert.rejects(Main(fixture.env, [], api), /초안 저장 응답/);
    assert.equal(api.Calls.filter(call => call.url.includes(':commit')).length, 1);
    assert.equal(fixture.Read(fixture.env.GOOGLE_PLAY_RESULT).Success, false);
    assert.equal(fixture.Read(fixture.env.GOOGLE_PLAY_RESULT).Committed, null);
});

test('심사 보호 오류는 심사 취소 옵션이나 commit 재시도 없이 중단한다', async t => {
    const fixture = Fixture(t);
    const api = Api({ Fail: 'commit', Error: { response: { status: 400, data: { error: { message: 'secret', details: [{ reason: 'CHANGES_ALREADY_IN_REVIEW' }] } } } } });
    await assert.rejects(Main(fixture.env, [], api), /심사 중/);
    assert.equal(api.Calls.filter(call => call.url.includes(':commit')).length, 1);
    assert.ok(api.Calls.every(call => !call.url.includes('CANCEL_IN_REVIEW')));
    assert.equal(api.Calls.at(-1).method, 'DELETE');
    const result = fixture.Read(fixture.env.GOOGLE_PLAY_RESULT);
    assert.equal(result.Success, false);
    assert.equal(result.Committed, null);
    assert.equal(result.CommitAttempted, true);
});

test('commit 연결 오류는 불확실 상태를 표시하고 원문·헤더를 공개하지 않는다', async t => {
    const fixture = Fixture(t);
    const api = Api({ Fail: 'commit', Error: { message: 'private-key', response: { status: 'private-key', data: 'signed-url' }, config: { headers: { Authorization: 'Bearer secret' } } } });
    await assert.rejects(Main(fixture.env, [], api), error => /자동 재시도하지 않습니다/.test(error.message) && !/private-key|signed-url|Bearer/.test(error.message));
    assert.equal(api.Calls.filter(call => call.url.includes(':commit')).length, 1);
    assert.equal(fixture.Read(fixture.env.GOOGLE_PLAY_RESULT).Committed, null);
});

test('앱 최초 미등록·권한 400·403 실패는 첫 AAB 안내와 정제된 오류만 반환한다', async t => {
    const fixture = Fixture(t);
    for (const status of [400, 403]) {
        const api = Api({ Fail: 'insert', Error: { response: { status, data: { error: { message: 'secret-sensitive-account-details' } } }, message: 'secret-json-key' } });
        await assert.rejects(Main(fixture.env, ['--plan'], api), error => error.message.includes(`HTTP ${status}`) && /첫 AAB 수동 등록/.test(error.message) && !/secret/.test(error.message));
        assert.equal(api.Calls.length, 1);
        assert.equal(fixture.Read(fixture.env.GOOGLE_PLAY_PLAN).Success, false);
    }
});

test('인증·입력 오류를 정제하고 결과 파일이 인증 키나 빌드 파일을 덮어쓰지 못하게 한다', async t => {
    const fixture = Fixture(t);
    await assert.rejects(Main(fixture.env, [], { CreateAuth: async () => { throw new Error('private_key secret'); } }), /서비스 계정 인증/);
    const before = fs.readFileSync(fixture.env.BUILD_FILE);
    await assert.rejects(Main({ ...fixture.env, GOOGLE_PLAY_RESULT: fixture.env.BUILD_FILE }, [], Api()), /서로 다른 경로/);
    assert.deepEqual(fs.readFileSync(fixture.env.BUILD_FILE), before);
    await assert.rejects(Main({ ...fixture.env, GOOGLE_PLAY_PLAN: fixture.env.GOOGLE_APPLICATION_CREDENTIALS }, ['--plan'], Api()), /서로 다른 경로/);
    await assert.rejects(Main({ ...fixture.env, BUILD_FILE: 'DrawLiar.apk' }, [], Api()), /AAB 파일/);
    await assert.rejects(Main({ ...fixture.env, BUILD_NUMBER: '15-secret' }, ['--plan'], Api()), /양의 정수/);
});
