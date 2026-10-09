const fs = require('node:fs');
const path = require('node:path');
const { GoogleAuth } = require('google-auth-library');

const PACKAGE_NAME = 'com.rascallab.liargame';
const PROJECT_NUMBER = '560556107495';
const MANAGEMENT_ORIGIN = 'https://firebase.googleapis.com/v1beta1';
const DISTRIBUTION_ORIGIN = 'https://firebaseappdistribution.googleapis.com/v1';

function certificateHash(value, type, required = true) {
    if (!value && !required) return '';
    const hash = String(value || '').replace(/[:\s]/g, '').toUpperCase();
    if (!new RegExp(`^[A-F0-9]{${type === 'SHA_1' ? 40 : 64}}$`).test(hash))
        throw new Error(`Firebase ${type === 'SHA_1' ? 'SHA-1' : 'SHA-256'} 인증서 지문이 잘못되었습니다.`);
    return hash;
}

function permissionError(status, permissions) {
    const value = Number.isInteger(status) && status >= 100 && status <= 599 ? status : '연결 오류';
    return new Error(`Firebase 요청 실패 (HTTP ${value}). ${value === 403 ? `Jenkins 서비스 계정에 ${permissions} 권한과 API 사용 권한이 필요합니다.` : 'API·서비스 계정·Firebase 앱 설정을 확인하세요.'}`);
}

async function main(env = process.env, argv = process.argv, dependencies = {}) {
    const checkOnly = argv.includes('--check-only');
    const projectNumber = env.FIREBASE_PROJECT_NUMBER || PROJECT_NUMBER;
    const packageName = env.FIREBASE_PACKAGE_NAME || PACKAGE_NAME;
    if (!/^\d+$/.test(projectNumber)) throw new Error('Firebase Project Number가 잘못되었습니다.');
    if (packageName !== PACKAGE_NAME) throw new Error('Firebase 자동 등록은 DrawLiar 패키지에만 허용됩니다.');
    if (!env.GOOGLE_APPLICATION_CREDENTIALS) throw new Error('Jenkins Firebase Secret file이 필요합니다.');
    const sha1 = certificateHash(env.FIREBASE_CERTIFICATE_SHA1, 'SHA_1', !checkOnly);
    const sha256 = certificateHash(env.FIREBASE_CERTIFICATE_SHA256, 'SHA_256', !checkOnly);
    const fileSystem = dependencies.fs || fs;
    const log = dependencies.log || console.log;
    const now = dependencies.now || Date.now;
    const wait = dependencies.wait || (milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)));
    const resultFile = path.resolve(env.FIREBASE_APP_RESULT || 'Build/Logs/FirebaseApp.json');
    function writeResult(result) {
        try {
            fileSystem.mkdirSync(path.dirname(resultFile), { recursive: true });
            fileSystem.writeFileSync(resultFile, JSON.stringify(result, null, 2) + '\n');
        } catch { throw new Error('Firebase 앱 확인 결과를 저장하지 못했습니다.'); }
    }
    if (!checkOnly) writeResult({ Success: false, ProjectNumber: projectNumber, PackageName: packageName });
    let auth;
    try {
        auth = dependencies.auth || await new GoogleAuth({
            keyFile: env.GOOGLE_APPLICATION_CREDENTIALS,
            scopes: ['https://www.googleapis.com/auth/cloud-platform'],
        }).getClient();
    } catch { throw new Error('Firebase 서비스 계정 인증을 시작하지 못했습니다.'); }
    async function request(origin, resource, permissions, options = {}) {
        try {
            return (await auth.request({ url: `${origin}/${resource}`, timeout: 60000, retry: false, ...options })).data;
        } catch (error) {
            // HTTP 원문에는 인증 헤더와 토큰이 들어갈 수 있다.
            const safeError = permissionError(error.response?.status, permissions);
            safeError.status = Number.isInteger(error.response?.status) ? error.response.status : 0;
            throw safeError;
        }
    }
    async function listPages(origin, resource, key, permissions) {
        const values = [], tokens = new Set();
        let token = '';
        for (let page = 0; page < 100; page++) {
            const query = `?pageSize=100${token ? `&pageToken=${encodeURIComponent(token)}` : ''}`;
            const response = await request(origin, `${resource}${query}`, permissions);
            if (!response || (response[key] !== undefined && !Array.isArray(response[key])))
                throw new Error('Firebase 목록 응답이 잘못되었습니다.');
            values.push(...(response[key] || []));
            token = response.nextPageToken || '';
            if (!token) return values;
            if (typeof token !== 'string' || token.length > 16384 || tokens.has(token))
                throw new Error('Firebase 목록 페이지 응답이 잘못되었습니다.');
            tokens.add(token);
        }
        throw new Error('Firebase 목록 조회 한도를 초과했습니다.');
    }
    function validateApp(app, expectedPackage = '') {
        if (!app || !new RegExp(`^1:${projectNumber}:android:[a-f0-9]+$`).test(app.appId || '') ||
            !/^[a-z][a-z0-9-]{4,62}$/.test(app.projectId || '') ||
            ![`projects/${app.projectId}/androidApps/${app.appId}`, `projects/${projectNumber}/androidApps/${app.appId}`].includes(app.name) ||
            !/^[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+$/.test(app.packageName || '') ||
            app.state === 'DELETED' || (expectedPackage && app.packageName !== expectedPackage))
            throw new Error('Firebase Android 앱 응답의 프로젝트·패키지명이 일치하지 않습니다.');
        return app;
    }
    function certificateNameMatches(name, app) {
        if (name && typeof name !== 'string') return false;
        return !name || [`projects/${app.projectId}/androidApps/${app.appId}/sha/`,
            `projects/${projectNumber}/androidApps/${app.appId}/sha/`].some(prefix => name.startsWith(prefix));
    }
    async function listApps() {
        const apps = await listPages(MANAGEMENT_ORIGIN, `projects/${projectNumber}/androidApps`, 'apps', 'firebase.clients.list');
        return apps.map(app => validateApp(app));
    }
    function matchingApp(apps) {
        const matches = apps.filter(app => app.packageName === packageName);
        if (matches.length > 1) throw new Error('동일 패키지의 Firebase Android 앱이 여러 개입니다. App ID를 확인하세요.');
        return matches[0];
    }
    async function certificates(app) {
        const response = await request(MANAGEMENT_ORIGIN, `projects/${projectNumber}/androidApps/${app.appId}/sha`, 'firebase.clients.get');
        if (!response || (response.certificates !== undefined && !Array.isArray(response.certificates)))
            throw new Error('Firebase 인증서 목록 응답이 잘못되었습니다.');
        return (response.certificates || []).map(value => {
            if (!['SHA_1', 'SHA_256'].includes(value.certType)) throw new Error('Firebase 인증서 종류가 잘못되었습니다.');
            const hash = certificateHash(value.shaHash, value.certType);
            if (!certificateNameMatches(value.name, app))
                throw new Error('Firebase 인증서 응답의 앱이 일치하지 않습니다.');
            return { Type: value.certType, Hash: hash };
        });
    }
    function publicGroup(group) {
        const prefix = `projects/${projectNumber}/groups/`;
        const alias = typeof group?.name === 'string' && group.name.startsWith(prefix) ? group.name.slice(prefix.length) : '';
        if (!/^[a-z0-9][a-z0-9-]{0,62}$/.test(alias) ||
            ['testerCount', 'releaseCount', 'inviteLinkCount'].some(key => !Number.isInteger(group[key] || 0) || (group[key] || 0) < 0))
            throw new Error('Firebase 테스터 그룹 응답이 잘못되었습니다.');
        return { Alias: alias, TesterCount: group.testerCount || 0, ReleaseCount: group.releaseCount || 0, InviteLinkCount: group.inviteLinkCount || 0 };
    }
    const apps = await listApps();
    let app = matchingApp(apps);
    if (checkOnly) {
        const groups = await listPages(DISTRIBUTION_ORIGIN, `projects/${projectNumber}/groups`, 'groups', 'firebaseappdistro.testers.list');
        const publicGroups = groups.map(publicGroup);
        const publicApps = [];
        for (const value of apps) publicApps.push({ AppId: value.appId, ProjectId: value.projectId, PackageName: value.packageName, Certificates: await certificates(value) });
        const result = { Success: true, CheckOnly: true, ProjectNumber: projectNumber, PackageName: packageName,
            AppId: app?.appId || '', ProjectId: app?.projectId || '', Apps: publicApps, Groups: publicGroups };
        log(JSON.stringify(result));
        return result;
    }
    const aliases = [...new Set((env.FIREBASE_GROUPS || 'drawliar-testers').split(',').map(value => value.trim()).filter(Boolean))];
    if (!aliases.length || aliases.length > 999 || aliases.some(value => !/^drawliar-[a-z0-9][a-z0-9-]{0,53}$/.test(value)))
        throw new Error('Firebase 자동 등록에는 drawliar-로 시작하는 전용 테스터 그룹을 지정하세요.');
    const groups = [];
    for (const alias of aliases) {
        let group;
        try { group = await request(DISTRIBUTION_ORIGIN, `projects/${projectNumber}/groups/${alias}`, 'firebaseappdistro.testers.list'); }
        catch (error) {
            if (error.status !== 404) throw error;
            throw new Error('Firebase DrawLiar 전용 테스터 그룹이 없습니다. 기존 테스터 그룹 설정을 확인하세요.');
        }
        const value = publicGroup(group);
        if (value.Alias !== alias) throw new Error('Firebase 테스터 그룹 응답이 요청한 그룹과 다릅니다.');
        groups.push(value);
    }
    let created = false;
    if (!app) {
        let operation;
        try {
            operation = await request(MANAGEMENT_ORIGIN, `projects/${projectNumber}/androidApps`, 'firebase.clients.create', {
                method: 'POST', data: { packageName, displayName: 'DrawLiar' },
            });
        } catch (error) {
            if (error.status !== 409) throw error;
            app = matchingApp(await listApps());
            if (!app) throw new Error('Firebase Android 앱 생성이 충돌했습니다. 동일 패키지의 앱 등록 상태를 확인하세요.');
        }
        if (operation) {
            const operationName = operation.name;
            if (!/^operations\/[A-Za-z0-9_/-]+$/.test(operationName || '') || operationName.includes('//'))
                throw new Error('Firebase Android 앱 생성 작업 응답이 잘못되었습니다.');
            const deadline = now() + 5 * 60000;
            for (let attempt = 0; !operation.done && attempt < 60 && now() < deadline; attempt++) {
                await wait(5000);
                operation = await request(MANAGEMENT_ORIGIN, operationName, 'firebase.clients.get');
                if (operation?.name !== operationName) throw new Error('Firebase Android 앱 생성 작업이 일치하지 않습니다.');
            }
            if (!operation.done) throw new Error('Firebase Android 앱 생성 시간이 초과되었습니다. 등록 상태를 확인한 뒤 다시 실행하세요.');
            if (operation.error) {
                if (operation.error.code === 7) throw permissionError(403, 'firebase.clients.create');
                throw new Error('Firebase Android 앱 생성에 실패했습니다. 앱 등록 상태와 API 설정을 확인하세요.');
            }
            app = validateApp(operation.response, packageName);
            created = true;
        }
    }
    const registered = await certificates(app);
    const added = { SHA_1: false, SHA_256: false };
    for (const certificate of [{ Type: 'SHA_1', Hash: sha1 }, { Type: 'SHA_256', Hash: sha256 }]) {
        if (registered.some(value => value.Type === certificate.Type && value.Hash === certificate.Hash)) continue;
        try {
            const response = await request(MANAGEMENT_ORIGIN, `projects/${projectNumber}/androidApps/${app.appId}/sha`, 'firebase.clients.update·firebase.projects.update·clientauthconfig.clients.create', {
                method: 'POST', data: { shaHash: certificate.Hash, certType: certificate.Type },
            });
            if (response?.certType !== certificate.Type || certificateHash(response.shaHash, certificate.Type) !== certificate.Hash ||
                !certificateNameMatches(response.name, app))
                throw new Error('Firebase 인증서 등록 응답이 일치하지 않습니다.');
            added[certificate.Type] = true;
        } catch (error) {
            if (error.status !== 409) throw error;
            const refreshed = await certificates(app);
            if (!refreshed.some(value => value.Type === certificate.Type && value.Hash === certificate.Hash))
                throw new Error('Firebase 인증서 등록이 충돌했습니다. 등록된 지문을 확인하세요.');
        }
    }
    const result = { Success: true, ProjectNumber: projectNumber, ProjectId: app.projectId, AppId: app.appId,
        PackageName: packageName, Created: created, Sha1: sha1, Sha256: sha256,
        Sha1Registered: true, Sha256Registered: true, Sha1Added: added.SHA_1, Sha256Added: added.SHA_256, Groups: groups };
    writeResult(result);
    log(`Firebase DrawLiar Android 앱·업로드키 SHA-1/SHA-256 등록 확인 완료 (${app.appId})`);
    return result;
}

if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1; });
module.exports = { main, certificateHash, PACKAGE_NAME, PROJECT_NUMBER };
