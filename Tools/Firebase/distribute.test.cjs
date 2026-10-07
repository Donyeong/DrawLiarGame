const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const test = require('node:test');

async function run(options = {}) {
    const calls = [], writes = [], logs = [];
    const appId = '1:123:android:abc';
    const app = `projects/123/apps/${appId}`;
    const release = `${app}/releases/release1`;
    const testingUri = `https://appdistribution.firebase.google.com/testerapps/${appId}/releases/release1`;
    let groupReads = 0;
    const client = { request: async request => {
        calls.push(request);
        if (options.failure && request.url.includes(options.failure)) throw { response: { status: 403, data: 'SECRET_HTTP_BODY' }, message: 'SECRET_AUTH_SIGNATURE' };
        if (request.url.includes('/androidApps/')) return { data: { packageName: options.wrongPackage ? 'com.rascallab.konasurvival' : 'com.rascallab.liargame' } };
        if (request.url.endsWith('/aabInfo')) return { data: {
            name: options.wrongAabApp ? 'projects/999/apps/other/aabInfo' : `${app}/aabInfo`,
            integrationState: options.aabState || 'INTEGRATED',
        } };
        if (request.url.includes('/groups/')) {
            groupReads++;
            return { data: { name: 'projects/123/groups/drawliar-testers', testerCount: groupReads > 1 && options.joinedDuringUpload ? 1 : options.testers || 0 } };
        }
        if (request.url.includes('/upload/')) return { data: {
            name: options.wrongOperation ? 'https://attacker.invalid/SECRET' : `${release}/operations/upload1`, done: true,
            response: { release: { name: release, testingUri: options.wrongLink ? testingUri.replace('abc', 'def') : testingUri,
                firebaseConsoleUri: 'https://console.firebase.google.com/project/drawliar-ci/appdistribution/app/android:1:123:android:abc/releases/release1',
                binaryDownloadUri: 'https://firebaseappdistribution.googleapis.com/download?token=SECRET_SIGNED_URL', displayVersion: '1.0.0', buildVersion: '5' } },
        } };
        if (request.url.endsWith('/releases/release1')) return { data: {
            name: release, binaryDownloadUri: options.badBinary ? 'https://attacker.invalid/SECRET_SIGNED_URL' : 'https://firebaseappdistribution.googleapis.com/download?token=SECRET_SIGNED_URL',
        } };
        return { data: {} };
    } };
    const fakeFs = {
        mkdirSync() {}, writeFileSync(file, content) { writes.push({ file, content }); },
        statSync() { return { size: options.size ?? 68 * 1024 * 1024 }; }, createReadStream() { return 'APK_STREAM'; },
    };
    const context = vm.createContext({
        require(name) {
            if (name === 'google-auth-library') return { GoogleAuth: class { async getClient() { return client; } } };
            return name === 'node:fs' ? fakeFs : require(name);
        }, URL, Date, module: { exports: {} }, console: { log(message) { logs.push(message); } }, setTimeout,
        process: { argv: options.check ? ['--check'] : [], env: {
            FIREBASE_APP_ID: appId, GOOGLE_APPLICATION_CREDENTIALS: 'secret-file', BUILD_FILE: 'DrawLiar.apk', FIREBASE_UPLOAD_ONLY: 'false', ...options.env,
        } },
    });
    vm.runInContext(fs.readFileSync(`${__dirname}/distribute.cjs`, 'utf8'), context);
    let result, error;
    try { result = await vm.runInContext('main()', context); } catch (caught) { error = caught; }
    return { calls, writes, logs, result, error, testingUri };
}

test('빈 전용 그룹 배포와 APK 스트리밍, 버전·안전한 링크만 성공 기록', async () => {
    const value = await run();
    assert.equal(value.error, undefined);
    assert.equal(value.result.Success, true);
    assert.equal(value.result.Format, 'APK');
    assert.equal(value.result.Distribution, 'EMPTY_GROUP');
    assert.equal(value.result.DisplayVersion, '1.0.0');
    assert.equal(value.result.BuildVersion, '5');
    assert.equal(value.result.TestingUri, value.testingUri);
    const upload = value.calls.find(call => call.url.includes('/upload/'));
    assert.equal(upload.data, 'APK_STREAM');
    assert.equal(upload.retry, false);
    assert.equal(value.calls.at(-1).data.groupAliases[0], 'drawliar-testers');
    assert.doesNotMatch(JSON.stringify(value.writes) + value.logs.join(), /SECRET_SIGNED_URL|binaryDownloadUri/);
    assert.equal(value.calls.some(call => call.url.endsWith('/aabInfo')), false);
});
test('연결된 AAB는 그룹 배포와 다운로드 링크까지 처리', async () => {
    for (const aabState of ['INTEGRATED', 'AAB_STATE_UNAVAILABLE']) {
        const value = await run({ aabState, testers: 2, env: {
            BUILD_FILE: 'DrawLiar.AAB', FIREBASE_ALLOW_TESTER_EMAIL: 'true',
            DOWNLOAD_RESULT: '.jenkins-private/FirebaseDownload.json',
        } });
        assert.equal(value.error, undefined);
        assert.equal(value.result.Format, 'AAB');
        assert.equal(value.result.Distribution, 'DISTRIBUTED');
        assert.equal(value.result.TestingUri, value.testingUri);
        assert.ok(value.calls.findIndex(call => call.url.endsWith('/aabInfo')) < value.calls.findIndex(call => call.url.includes('/upload/')));
        assert.equal(value.calls.find(call => call.url.includes('/upload/')).headers['X-Goog-Upload-File-Name'], 'DrawLiar.AAB');
        assert.doesNotMatch(JSON.stringify(value.writes.filter(write => !write.file.includes('.jenkins-private'))) + value.logs.join(), /SECRET_SIGNED_URL|BinaryDownloadUri/);
    }
});
test('AAB 연결 미완료는 상태를 기록하고 업로드·배포 전 중단', async () => {
    for (const aabState of ['PLAY_ACCOUNT_NOT_LINKED', 'NO_APP_WITH_GIVEN_BUNDLE_ID_IN_PLAY_ACCOUNT', 'APP_NOT_PUBLISHED', 'PLAY_IAS_TERMS_NOT_ACCEPTED', 'AAB_INTEGRATION_STATE_UNSPECIFIED']) {
        const value = await run({ aabState, env: { BUILD_FILE: 'DrawLiar.aab' } });
        assert.match(value.error.message, /Firebase AAB 배포 준비/);
        assert.equal(value.calls.some(call => call.method), false);
        const result = JSON.parse(value.writes.at(-1).content);
        assert.equal(result.Success, false);
        assert.equal(result.AabIntegrationState, aabState);
    }
    const wrong = await run({ wrongAabApp: true, env: { BUILD_FILE: 'DrawLiar.aab' } });
    assert.ok(wrong.error);
    assert.equal(wrong.calls.some(call => call.method), false);
});
test('AAB 사전 검사는 Play 연결도 조회하며 클라우드·파일 수정 없음', async () => {
    const value = await run({ check: true, env: { BUILD_FORMAT: 'AAB' } });
    assert.equal(value.error, undefined);
    assert.equal(value.calls.some(call => call.url.endsWith('/aabInfo')), true);
    assert.equal(value.calls.every(call => !call.method), true);
    assert.equal(value.writes.length, 0);
});
test('지원하지 않는 파일 형식은 인증·업로드 전 중단', async () => {
    const value = await run({ env: { BUILD_FILE: 'DrawLiar.zip' } });
    assert.match(value.error.message, /APK 또는 AAB/);
    assert.equal(value.calls.length, 0);
});
test('테스터가 있으면 기본값으로 이메일 없이 업로드만 유지', async () => {
    const value = await run({ testers: 2 });
    assert.equal(value.error, undefined);
    assert.equal(value.result.Success, true);
    assert.equal(value.result.Distribution, 'SKIPPED_TESTER_EMAIL');
    assert.equal(value.calls.some(call => call.url.includes(':distribute')), false);
});
test('업로드 도중 가입한 테스터도 배포 직전 재검사하여 이메일을 보내지 않음', async () => {
    const value = await run({ joinedDuringUpload: true });
    assert.equal(value.result.Distribution, 'SKIPPED_TESTER_EMAIL');
    assert.equal(value.calls.some(call => call.url.includes(':distribute')), false);
});
test('업로드 전용 옵션은 빈 그룹에도 배포하지 않음', async () => {
    const value = await run({ env: { FIREBASE_UPLOAD_ONLY: 'true' } });
    assert.equal(value.result.Distribution, 'UPLOAD_ONLY');
    assert.equal(value.calls.some(call => call.url.includes(':distribute')), false);
});
test('옵션을 생략해도 기본적으로 업로드만 수행', async () => {
    const value = await run({ env: { FIREBASE_UPLOAD_ONLY: undefined } });
    assert.equal(value.result.Distribution, 'UPLOAD_ONLY');
    assert.equal(value.calls.some(call => call.url.includes(':distribute')), false);
});
test('명시 이메일 허용은 DrawLiar 전용 그룹만 배포', async () => {
    const value = await run({ testers: 2, env: { FIREBASE_ALLOW_TESTER_EMAIL: 'true' } });
    assert.equal(value.result.Distribution, 'DISTRIBUTED');
    assert.equal(value.calls.at(-1).data.groupAliases.join(','), 'drawliar-testers');
});
test('다른 프로젝트 패키지 또는 Kona 그룹이면 업로드·메일 이전에 중단', async () => {
    for (const options of [{ wrongPackage: true }, { env: { FIREBASE_GROUPS: 'kona-testers' } }]) {
        const value = await run(options);
        assert.ok(value.error);
        assert.equal(value.calls.some(call => call.url.includes('/upload/')), false);
        assert.equal(JSON.parse(value.writes[0].content).Success, false);
    }
});
test('권한 거부 원문·인증 값은 오류와 결과에 노출하지 않음', async () => {
    const value = await run({ failure: '/groups/' });
    assert.match(value.error.message, /403/);
    assert.doesNotMatch(value.error.message + JSON.stringify(value.writes), /SECRET/);
    assert.equal(value.calls.some(call => call.url.includes('/upload/')), false);
});
test('릴리스 링크가 다른 앱을 가리키거나 업로드 응답이 잘못되면 성공을 기록하지 않음', async () => {
    for (const options of [{ wrongLink: true }, { wrongOperation: true }, { failure: ':distribute' }]) {
        const value = await run(options);
        assert.ok(value.error);
        assert.equal(value.writes.some(write => JSON.parse(write.content).Success), false);
    }
});
test('사전 검사는 클라우드 수정·업로드·결과 파일 기록을 하지 않음', async () => {
    const value = await run({ check: true });
    assert.equal(value.error, undefined);
    assert.equal(value.writes.length, 0);
    assert.equal(value.calls.every(call => !call.method), true);
});
test('직접 다운로드 서명은 별도 private 파일에만 기록하고 공개 결과·로그에 포함하지 않음', async () => {
    const value = await run({ env: { DOWNLOAD_RESULT: '.jenkins-private/FirebaseDownload.json', FIREBASE_UPLOAD_ONLY: 'true' } });
    assert.equal(value.error, undefined);
    const privateWrites = value.writes.filter(write => write.file.includes('.jenkins-private'));
    const privateResult = JSON.parse(privateWrites.at(-1).content);
    assert.match(privateResult.BinaryDownloadUri, /^https:\/\/firebaseappdistribution.googleapis.com\//);
    assert.ok(Date.parse(privateResult.ExpiresUtc) > Date.now());
    assert.doesNotMatch(JSON.stringify(value.writes.filter(write => !write.file.includes('.jenkins-private'))) + value.logs.join(), /SECRET_SIGNED_URL|BinaryDownloadUri/);
    assert.equal(value.calls.some(call => call.url.includes(':distribute')), false);
});
test('외부 다운로드 호스트·공개 경로는 서명 파일 기록과 성공 처리 전에 거부', async () => {
    for (const options of [{ badBinary: true, env: { DOWNLOAD_RESULT: '.jenkins-private/FirebaseDownload.json' } }, { env: { DOWNLOAD_RESULT: 'Build/Logs/FirebaseDownload.json' } }]) {
        const value = await run(options);
        assert.ok(value.error);
        assert.equal(value.writes.some(write => JSON.parse(write.content).Success), false);
        assert.equal(value.writes.some(write => /SECRET_SIGNED_URL/.test(write.content)), false);
    }
});
