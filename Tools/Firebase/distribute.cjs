const fs = require('node:fs');
const path = require('node:path');
const { GoogleAuth } = require('google-auth-library');

const PACKAGE_NAME = 'com.rascallab.drawliar';
const ORIGIN = 'https://firebaseappdistribution.googleapis.com';

function booleanSetting(value, fallback = false) {
    if (value === undefined || value === '') return fallback;
    if (/^(true|1)$/i.test(value)) return true;
    if (/^(false|0)$/i.test(value)) return false;
    throw new Error('Firebase 배포 옵션은 true 또는 false로 지정하세요.');
}

function firebaseLink(value, host, pathname) {
    if (!value) return '';
    try {
        const url = new URL(value);
        if (url.protocol !== 'https:' || url.hostname !== host || url.port || url.username || url.password || url.hash || !pathname.test(url.pathname)) return '';
        return url.href;
    } catch { return ''; }
}

async function main(env = process.env, argv = process.argv) {
    const appId = env.FIREBASE_APP_ID || '';
    const checkOnly = argv.includes('--check');
    const resultFile = path.resolve(env.UPLOAD_RESULT || 'Build/Logs/UploadResult.json');
    const downloadFile = env.DOWNLOAD_RESULT ? path.resolve(env.DOWNLOAD_RESULT) : '';
    if (!checkOnly) {
        fs.mkdirSync(path.dirname(resultFile), { recursive: true });
        fs.writeFileSync(resultFile, JSON.stringify({ Success: false, AppId: appId, PackageName: PACKAGE_NAME }) + '\n');
        if (downloadFile) {
            if (downloadFile === resultFile || !downloadFile.split(path.sep).includes('.jenkins-private'))
                throw new Error('서명된 다운로드 링크는 .jenkins-private 폴더의 별도 파일에만 저장할 수 있습니다.');
            fs.mkdirSync(path.dirname(downloadFile), { recursive: true });
            fs.writeFileSync(downloadFile, '{}\n');
        }
    }
    if (!/^1:\d+:android:[a-f0-9]+$/.test(appId)) throw new Error('Firebase App ID가 잘못되었습니다.');
    const groups = [...new Set((env.FIREBASE_GROUPS || 'drawliar-testers').split(',').map(value => value.trim()).filter(Boolean))];
    if (!groups.length || groups.length > 999 || groups.some(value => !/^drawliar-[a-z0-9][a-z0-9-]{0,53}$/.test(value)))
        throw new Error('Firebase 배포에는 drawliar-로 시작하는 전용 테스터 그룹을 지정하세요.');
    if (!env.GOOGLE_APPLICATION_CREDENTIALS) throw new Error('Jenkins Firebase Secret file이 필요합니다.');
    const uploadOnly = booleanSetting(env.FIREBASE_UPLOAD_ONLY, true);
    const allowEmail = booleanSetting(env.FIREBASE_ALLOW_TESTER_EMAIL);
    const inviteUri = firebaseLink(env.FIREBASE_INVITE_URL || '', 'appdistribution.firebase.dev', /^\/i\/[A-Za-z0-9_-]{8,128}\/?$/);
    if (env.FIREBASE_INVITE_URL && !inviteUri) throw new Error('Firebase 테스터 등록 링크가 잘못되었습니다.');
    const projectNumber = appId.split(':')[1];
    const app = `projects/${projectNumber}/apps/${appId}`;
    let auth;
    try {
        auth = await new GoogleAuth({ keyFile: env.GOOGLE_APPLICATION_CREDENTIALS, scopes: ['https://www.googleapis.com/auth/cloud-platform'] }).getClient();
    } catch { throw new Error('Firebase 서비스 계정 인증을 시작하지 못했습니다.'); }
    async function request(resource, options = {}) {
        try {
            return (await auth.request({ url: `${ORIGIN}${resource}`, timeout: 60000, retry: false, ...options })).data;
        } catch (error) {
            // 인증 헤더와 서명된 다운로드 URL을 포함할 수 있는 HTTP 원문은 출력하지 않는다.
            throw new Error(`Firebase 요청 실패 (HTTP ${error.response?.status || '연결 오류'}). 서비스 계정 권한·API·앱과 그룹 설정을 확인하세요.`);
        }
    }
    const registeredApp = await request('', { url: `https://firebase.googleapis.com/v1beta1/projects/${projectNumber}/androidApps/${appId}` });
    if (registeredApp.packageName !== PACKAGE_NAME) throw new Error('Firebase 앱 패키지명이 DrawLiar와 다릅니다.');
    async function countTesters() {
        let total = 0;
        for (const group of groups) {
            const value = await request(`/v1/projects/${projectNumber}/groups/${group}`);
            if (value.name !== `projects/${projectNumber}/groups/${group}` || !Number.isInteger(value.testerCount || 0) || (value.testerCount || 0) < 0)
                throw new Error('Firebase 테스터 그룹 응답을 확인할 수 없습니다.');
            total += value.testerCount || 0;
        }
        return total;
    }
    await countTesters();
    if (checkOnly) { console.log('Firebase DrawLiar 앱·전용 테스터 그룹 접근 확인 완료'); return; }
    const file = path.resolve(env.BUILD_FILE || '');
    let size;
    try { size = fs.statSync(file).size; } catch { throw new Error('업로드할 APK 파일을 찾을 수 없습니다.'); }
    if (path.extname(file).toLowerCase() !== '.apk') throw new Error('Firebase 배포에는 APK를 선택하세요.');
    if (!size || size > 2048 * 1024 * 1024) throw new Error('APK 크기는 0 초과 2048MiB 이하여야 합니다.');
    console.log('Firebase DrawLiar APK 업로드 시작');
    let operation = await request(`/upload/v1/${app}/releases:upload`, {
        method: 'POST', timeout: 20 * 60000,
        headers: { 'X-Goog-Upload-Protocol': 'raw', 'X-Goog-Upload-File-Name': encodeURIComponent(path.basename(file)), 'Content-Type': 'application/octet-stream', 'Content-Length': String(size) },
        data: fs.createReadStream(file),
    });
    const operationPattern = new RegExp(`^${app.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/releases/[A-Za-z0-9_-]+/operations/[A-Za-z0-9_-]+$`);
    if (!operationPattern.test(operation.name || '')) throw new Error('Firebase 업로드 작업 응답이 잘못되었습니다.');
    const deadline = Date.now() + 5 * 60000;
    while (!operation.done && Date.now() < deadline) {
        await new Promise(resolve => setTimeout(resolve, 5000));
        operation = await request(`/v1/${operation.name}`);
    }
    if (!operation.done || operation.error) throw new Error('Firebase APK 처리 실패 또는 시간 초과입니다. 패키지명·서명을 확인하세요.');
    const release = operation.response?.release;
    const releasePrefix = `${app}/releases/`;
    if (!release?.name?.startsWith(releasePrefix) || !/^[A-Za-z0-9_-]+$/.test(release.name.slice(releasePrefix.length)))
        throw new Error('Firebase 릴리스 응답이 잘못되었습니다.');
    const testingUri = firebaseLink(release.testingUri, 'appdistribution.firebase.google.com', /^\/testerapps\/[^/]+\/releases\/[A-Za-z0-9_-]+\/?$/);
    if (!testingUri || decodeURIComponent(new URL(testingUri).pathname.split('/')[2]) !== appId || new URL(testingUri).pathname.split('/')[4] !== release.name.slice(releasePrefix.length))
        throw new Error('Firebase 테스터 다운로드 링크가 앱·릴리스와 다릅니다.');
    const consoleUri = firebaseLink(release.firebaseConsoleUri, 'console.firebase.google.com', /^\/project\/[A-Za-z0-9_-]+\/appdistribution(?:\/|$)/);
    if (!consoleUri) throw new Error('Firebase 관리 콘솔 링크가 잘못되었습니다.');
    const notes = `${env.JOB_NAME || 'DrawLiar'} #${env.BUILD_NUMBER || ''}\nCommit: ${env.DRAWLIAR_SOURCE_REVISION || env.GIT_COMMIT || ''}`.slice(0, 5000);
    await request(`/v1/${release.name}?updateMask=release_notes.text`, { method: 'PATCH', data: { releaseNotes: { text: notes } } });
    const testerCount = await countTesters();
    let distribution = uploadOnly ? 'UPLOAD_ONLY' : 'SKIPPED_TESTER_EMAIL';
    if (!uploadOnly && (allowEmail || testerCount === 0)) {
        await request(`/v1/${release.name}:distribute`, { method: 'POST', data: { groupAliases: groups } });
        distribution = testerCount === 0 ? 'EMPTY_GROUP' : 'DISTRIBUTED';
    }
    if (downloadFile) {
        const refreshedRelease = await request(`/v1/${release.name}`);
        if (refreshedRelease.name !== release.name) throw new Error('Firebase 직접 다운로드 릴리스가 일치하지 않습니다.');
        const binaryUri = firebaseLink(refreshedRelease.binaryDownloadUri, 'firebaseappdistribution.googleapis.com', /^\/.+/);
        if (!binaryUri) throw new Error('Firebase 직접 다운로드 링크가 잘못되었습니다.');
        // 공식 유효기간 1시간보다 짧게 안내하여 HTTP 처리 시간의 여유를 둔다.
        fs.writeFileSync(downloadFile, JSON.stringify({ BinaryDownloadUri: binaryUri, ExpiresUtc: new Date(Date.now() + 55 * 60000).toISOString() }) + '\n');
    }
    const result = {
        Success: true, AppId: appId, PackageName: PACKAGE_NAME, ReleaseName: release.name,
        DisplayVersion: release.displayVersion || '', BuildVersion: release.buildVersion || '',
        TestingUri: testingUri, FirebaseConsoleUri: consoleUri, InviteUri: inviteUri,
        Groups: groups, Distribution: distribution, TesterCount: testerCount,
        CompletedUtc: new Date().toISOString(),
    };
    fs.writeFileSync(resultFile, JSON.stringify(result, null, 2) + '\n');
    console.log(distribution === 'SKIPPED_TESTER_EMAIL' ? 'Firebase APK 업로드 완료 · 테스터 이메일 전송 권한 없이 그룹 배포 생략' : 'Firebase APK 업로드 완료');
    return result;
}

if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1; });
module.exports = { main, booleanSetting, firebaseLink, PACKAGE_NAME };
