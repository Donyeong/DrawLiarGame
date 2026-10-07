const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { GoogleAuth } = require('google-auth-library');

const PACKAGE_NAME = 'com.rascallab.liargame';
const TRACK = 'internal';
const STATUS = 'draft';
const MAX_VERSION_CODE = 2100000000;
const API_ORIGIN = 'https://androidpublisher.googleapis.com';
const API_ROOT = `/androidpublisher/v3/applications/${PACKAGE_NAME}/edits`;

class SafeError extends Error {}

function ParseVersionCode(value, label) {
    if ((typeof value !== 'number' && typeof value !== 'string') || !/^[1-9]\d*$/.test(String(value)))
        throw new SafeError(`${label}는 양의 정수여야 합니다.`);
    const code = Number(value);
    if (!Number.isSafeInteger(code) || code > MAX_VERSION_CODE)
        throw new SafeError(`${label}가 Google Play 최대 버전 코드를 초과했습니다.`);
    return code;
}

function ReadJson(file, label) {
    try {
        if (!fs.statSync(file).isFile() || fs.statSync(file).size > 1024 * 1024) throw new Error();
        const value = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
        if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error();
        return value;
    } catch { throw new SafeError(`${label} JSON을 확인할 수 없습니다.`); }
}

function WriteJson(file, value) {
    let temporary;
    let descriptor;
    try {
        fs.mkdirSync(path.dirname(file), { recursive: true });
        temporary = `${file}.${crypto.randomUUID()}.tmp`;
        descriptor = fs.openSync(temporary, 'wx');
        fs.writeFileSync(descriptor, `${JSON.stringify(value, null, 2)}\n`);
        fs.fsyncSync(descriptor);
        fs.closeSync(descriptor);
        descriptor = undefined;
        for (let attempt = 0; ; attempt++) {
            try { fs.renameSync(temporary, file); break; }
            catch (error) {
                if (process.platform !== 'win32' || !['EACCES', 'EPERM', 'EBUSY'].includes(error.code) || attempt >= 4) throw error;
                // Windows 파일 검사기의 짧은 공유 잠금 동안 기존 상태 파일을 그대로 보존한다.
                Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 25);
            }
        }
    } catch { throw new SafeError('Google Play 결과 또는 버전 코드 상태를 저장하지 못했습니다.'); }
    finally {
        if (descriptor !== undefined) { try { fs.closeSync(descriptor); } catch {} }
        if (temporary) { try { fs.unlinkSync(temporary); } catch {} }
    }
}

function SamePath(first, second) {
    return path.resolve(first).toLowerCase() === path.resolve(second).toLowerCase();
}

function ReadVersionState(file) {
    if (!fs.existsSync(file)) return { HighWaterVersionCode: 0 };
    const state = ReadJson(file, 'Google Play 버전 코드 상태');
    if (state.PackageName !== PACKAGE_NAME) throw new SafeError('Google Play 버전 코드 상태의 패키지명이 다릅니다.');
    ParseVersionCode(state.HighWaterVersionCode, '저장된 버전 코드');
    ParseVersionCode(state.BuildNumber, '저장된 Jenkins 빌드 번호');
    return state;
}

function ReserveVersionCode(file, buildNumber, observedMax) {
    const lockFile = `${file}.lock`;
    let lock;
    try {
        fs.mkdirSync(path.dirname(file), { recursive: true });
        try { lock = fs.openSync(lockFile, 'wx'); }
        catch { throw new SafeError('Google Play 버전 코드 상태를 다른 실행이 사용 중이거나 저장 경로에 접근할 수 없습니다.'); }
        const state = ReadVersionState(file);
        const versionCode = ParseVersionCode(Math.max(buildNumber, observedMax + 1, state.HighWaterVersionCode + 1), '예약할 버전 코드');
        WriteJson(file, {
            PackageName: PACKAGE_NAME, HighWaterVersionCode: versionCode,
            BuildNumber: buildNumber, UpdatedUtc: new Date().toISOString(),
        });
        return versionCode;
    } catch (error) {
        if (error instanceof SafeError) throw error;
        throw new SafeError('Google Play 버전 코드 예약에 실패했습니다.');
    } finally {
        if (lock !== undefined) {
            try { fs.closeSync(lock); } catch {}
            try { fs.unlinkSync(lockFile); } catch {}
        }
    }
}

function RequireObject(value, label) {
    if (!value || typeof value !== 'object' || Array.isArray(value))
        throw new SafeError(`Google Play ${label} 응답을 확인할 수 없습니다.`);
    return value;
}

function ListField(value, field, label) {
    RequireObject(value, label);
    if (value[field] === undefined) return [];
    if (!Array.isArray(value[field])) throw new SafeError(`Google Play ${label} 목록을 확인할 수 없습니다.`);
    return value[field];
}

function InspectInventory(bundlesResponse, apksResponse, tracksResponse) {
    const codes = [];
    for (const bundle of ListField(bundlesResponse, 'bundles', 'AAB'))
        codes.push(ParseVersionCode(RequireObject(bundle, 'AAB').versionCode, 'Google Play AAB 버전 코드'));
    for (const apk of ListField(apksResponse, 'apks', 'APK'))
        codes.push(ParseVersionCode(RequireObject(apk, 'APK').versionCode, 'Google Play APK 버전 코드'));
    const tracks = ListField(tracksResponse, 'tracks', '트랙');
    let internal;
    for (const track of tracks) {
        RequireObject(track, '트랙');
        if (typeof track.track !== 'string' || !track.track) throw new SafeError('Google Play 트랙 이름을 확인할 수 없습니다.');
        if (track.track === TRACK) {
            if (internal) throw new SafeError('Google Play 내부 테스트 트랙이 중복되었습니다.');
            internal = track;
        }
        for (const release of ListField(track, 'releases', '릴리스')) {
            RequireObject(release, '릴리스');
            if (!['draft', 'completed', 'inProgress', 'halted'].includes(release.status))
                throw new SafeError('Google Play 기존 릴리스의 상태를 확인할 수 없습니다.');
            const versions = ListField(release, 'versionCodes', '릴리스 버전 코드');
            if (!versions.length && release.status !== 'draft') throw new SafeError('Google Play 기존 릴리스의 버전 코드가 없습니다.');
            for (const version of versions) codes.push(ParseVersionCode(version, 'Google Play 트랙 버전 코드'));
        }
    }
    const releases = internal ? ListField(internal, 'releases', '내부 테스트 릴리스') : [];
    if (releases.some(release => release.status === STATUS))
        throw new SafeError('Google Play 내부 테스트에 기존 초안이 있습니다. Play Console에서 완료하거나 폐기한 뒤 다시 실행하세요. 기존 초안은 자동으로 덮어쓰지 않습니다.');
    return { MaxVersionCode: codes.reduce((maximum, code) => Math.max(maximum, code), 0), Releases: releases };
}

function StableJson(value) {
    if (Array.isArray(value)) return `[${value.map(StableJson).join(',')}]`;
    if (value && typeof value === 'object')
        return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${StableJson(value[key])}`).join(',')}}`;
    return JSON.stringify(value);
}

function ReleaseIdentity(release) {
    return StableJson({
        name: release.name || '', status: release.status,
        versionCodes: ListField(release, 'versionCodes', '릴리스 버전 코드').map(code => String(ParseVersionCode(code, 'Google Play 릴리스 버전 코드'))).sort(),
        releaseNotes: release.releaseNotes || [], userFraction: release.userFraction ?? null,
        countryTargeting: release.countryTargeting ?? null, inAppUpdatePriority: release.inAppUpdatePriority ?? 0,
    });
}

function VerifyTrack(value, previousReleases, versionCode) {
    RequireObject(value, '내부 테스트 갱신');
    const releases = ListField(value, 'releases', '내부 테스트 갱신 릴리스');
    const drafts = releases.filter(release => release?.status === STATUS);
    if (value.track !== TRACK || releases.length !== previousReleases.length + 1 || drafts.length !== 1 ||
        drafts[0].versionCodes?.length !== 1 || String(drafts[0].versionCodes[0]) !== String(versionCode))
        throw new SafeError('Google Play 내부 테스트 응답이 요청한 단일 초안과 다릅니다.');
    const remaining = releases.filter(release => release.status !== STATUS).map(ReleaseIdentity).sort();
    const previous = previousReleases.map(ReleaseIdentity).sort();
    if (StableJson(remaining) !== StableJson(previous))
        throw new SafeError('Google Play 내부 테스트 응답에서 기존 릴리스 보존을 확인하지 못했습니다.');
}

async function HashArtifact(file) {
    try {
        const hash = crypto.createHash('sha256');
        for await (const chunk of fs.createReadStream(file)) hash.update(chunk);
        return hash.digest('hex');
    } catch { throw new SafeError('제출할 AAB의 SHA-256을 확인하지 못했습니다.'); }
}

function VerifyBuild(env, plan, buildNumber, file, buildResultFile, stateFile) {
    const versionCode = ParseVersionCode(env.DRAWLIAR_VERSION_CODE, 'Android 버전 코드');
    const state = ReadVersionState(stateFile);
    if (plan.Success !== true || plan.PackageName !== PACKAGE_NAME || plan.Track !== TRACK || plan.Status !== STATUS ||
        plan.BuildNumber !== buildNumber || plan.VersionCode !== versionCode ||
        state.HighWaterVersionCode !== versionCode || state.BuildNumber !== buildNumber)
        throw new SafeError('Google Play 예약 계획·저장된 버전 코드가 이번 Jenkins 빌드와 다릅니다.');
    if (!env.BUNDLE_VERSION || env.BUNDLE_VERSION.length > 128 || /[\u0000-\u001f\u007f]/.test(env.BUNDLE_VERSION))
        throw new SafeError('제출할 앱 버전 이름이 잘못되었습니다.');
    const build = ReadJson(buildResultFile, 'Android 빌드 결과');
    let stat;
    try { stat = fs.statSync(file); }
    catch { throw new SafeError('제출할 AAB 파일을 찾을 수 없습니다.'); }
    if (!stat.isFile() || stat.size <= 0 || !Number.isSafeInteger(stat.size)) throw new SafeError('제출할 AAB 파일 크기가 잘못되었습니다.');
    if (build.Result !== 'Succeeded' || build.Format !== 'AAB' || build.Errors !== 0 || build.Development !== false ||
        build.VersionCode !== versionCode || build.BundleVersion !== env.BUNDLE_VERSION ||
        typeof build.OutputPath !== 'string' || !SamePath(build.OutputPath, file) || build.TotalBytes !== stat.size)
        throw new SafeError('Android 빌드 결과·버전·경로·파일 크기가 제출할 AAB와 다릅니다.');
    return { VersionCode: versionCode, BundleVersion: build.BundleVersion, TotalBytes: stat.size };
}

function ApiError(error, stage) {
    const rawStatus = error?.response?.status;
    const status = Number.isInteger(rawStatus) && rawStatus >= 100 && rawStatus <= 599 ? rawStatus : '연결 오류';
    const details = error?.response?.data?.error?.details;
    if (stage === 'commit' && Array.isArray(details) && details.some(detail => detail?.reason === 'CHANGES_ALREADY_IN_REVIEW'))
        return new SafeError('Google Play에 심사 중인 변경이 있어 초안 저장을 중단했습니다. 심사가 완료된 뒤 다시 실행하세요.');
    if (stage === 'insert' && [400, 403, 404].includes(status))
        return new SafeError(`Google Play 편집 시작 실패 (HTTP ${status}). Play Console의 앱·첫 AAB 수동 등록과 서비스 계정 앱 권한·API 활성화를 확인하세요.`);
    if (stage === 'track')
        return new SafeError(`Google Play 내부 테스트 초안 저장 실패 (HTTP ${status}). 기존 릴리스를 유지한 채 초안을 저장할 수 없어 중단했습니다. Play Console의 트랙 상태를 확인하세요.`);
    if (stage === 'commit')
        return new SafeError(`Google Play 초안 저장 결과를 확인하지 못했습니다 (HTTP ${status}). 자동 재시도하지 않습니다. Play Console에서 저장 여부·심사 상태를 확인하세요.`);
    return new SafeError(`Google Play 요청 실패 (HTTP ${status}). 앱 등록·권한·버전 코드·업로드 키와 트랙 설정을 확인하세요.`);
}

async function Main(env = process.env, argv = process.argv, dependencies = {}) {
    const planOnly = argv.includes('--plan');
    const planFile = path.resolve(env.GOOGLE_PLAY_PLAN || 'Build/Logs/GooglePlayPlan.json');
    const resultFile = path.resolve(env.GOOGLE_PLAY_RESULT || 'Build/Logs/GooglePlayUploadResult.json');
    const stateFile = path.resolve(env.GOOGLE_PLAY_VERSION_STATE || 'C:\\DrawLiarJenkinsCache\\GooglePlayVersionCode.json');
    const buildResultFile = path.resolve(env.ANDROID_BUILD_RESULT || 'Build/Logs/AndroidBuildResult.json');
    const file = env.BUILD_FILE ? path.resolve(env.BUILD_FILE) : '';
    let editId;
    let request;
    let committed = false;
    let commitAttempted = false;
    let pending;
    try {
        const buildNumber = ParseVersionCode(env.BUILD_NUMBER, 'Jenkins 빌드 번호');
        if (!env.GOOGLE_APPLICATION_CREDENTIALS) throw new SafeError('Jenkins Google Play Secret file이 필요합니다.');
        if (!planOnly && (!file || path.extname(file).toUpperCase() !== '.AAB')) throw new SafeError('Google Play 제출에는 AAB 파일이 필요합니다.');
        const distinctPaths = [planFile, resultFile, stateFile, buildResultFile, path.resolve(env.GOOGLE_APPLICATION_CREDENTIALS)];
        if (file) distinctPaths.push(file);
        if (new Set(distinctPaths.map(value => value.toLowerCase())).size !== distinctPaths.length)
            throw new SafeError('Google Play 계획·결과·상태·빌드·인증 파일은 서로 다른 경로여야 합니다.');
        pending = { Success: false, Committed: false, PackageName: PACKAGE_NAME, Track: TRACK, Status: STATUS, BuildNumber: buildNumber };
        WriteJson(planOnly ? planFile : resultFile, pending);
        let build;
        let hash;
        if (!planOnly) {
            build = VerifyBuild(env, ReadJson(planFile, 'Google Play 예약 계획'), buildNumber, file, buildResultFile, stateFile);
            pending = { ...pending, ...build };
            WriteJson(resultFile, pending);
            hash = await HashArtifact(file);
        }
        let auth;
        try {
            auth = dependencies.CreateAuth ? await dependencies.CreateAuth() :
                await new GoogleAuth({ keyFile: env.GOOGLE_APPLICATION_CREDENTIALS, scopes: ['https://www.googleapis.com/auth/androidpublisher'] }).getClient();
        } catch { throw new SafeError('Google Play 서비스 계정 인증을 시작하지 못했습니다.'); }
        request = async (resource, stage, options = {}) => {
            try {
                return (await auth.request({ url: `${API_ORIGIN}${resource}`, timeout: 60000, retry: false, ...options })).data;
            } catch (error) { throw ApiError(error, stage); }
        };
        const edit = RequireObject(await request(API_ROOT, 'insert', { method: 'POST', data: {} }), '편집 시작');
        if (typeof edit.id !== 'string' || !/^[A-Za-z0-9_-]{1,128}$/.test(edit.id)) throw new SafeError('Google Play 편집 ID가 잘못되었습니다.');
        editId = edit.id;
        const editRoot = `${API_ROOT}/${editId}`;
        const inventory = InspectInventory(
            await request(`${editRoot}/bundles`, 'read'),
            await request(`${editRoot}/apks`, 'read'),
            await request(`${editRoot}/tracks`, 'read'),
        );
        if (planOnly) {
            const versionCode = ReserveVersionCode(stateFile, buildNumber, inventory.MaxVersionCode);
            const plan = {
                Success: true, PackageName: PACKAGE_NAME, BuildNumber: buildNumber,
                VersionCode: versionCode, Track: TRACK, Status: STATUS, ReservedUtc: new Date().toISOString(),
            };
            WriteJson(planFile, plan);
            console.log(`Google Play 내부 테스트 초안용 버전 코드 ${versionCode} 예약 완료`);
            return plan;
        }
        if (inventory.MaxVersionCode >= build.VersionCode) throw new SafeError('예약한 버전 코드 이상의 빌드가 이미 Google Play에 있습니다. 새 Jenkins 빌드로 다시 예약하세요.');
        console.log('Google Play DrawLiar AAB 업로드 시작');
        const stream = fs.createReadStream(file);
        let bundle;
        try {
            bundle = RequireObject(await request(`/upload${editRoot}/bundles?uploadType=media`, 'upload', {
                method: 'POST', timeout: 20 * 60000,
                headers: { 'Content-Type': 'application/octet-stream', 'Content-Length': String(build.TotalBytes) }, data: stream,
            }), 'AAB 업로드');
        } finally { stream.destroy(); }
        if (ParseVersionCode(bundle.versionCode, '업로드된 AAB 버전 코드') !== build.VersionCode ||
            typeof bundle.sha256 !== 'string' || !/^[a-fA-F0-9]{64}$/.test(bundle.sha256) || bundle.sha256.toLowerCase() !== hash ||
            await HashArtifact(file) !== hash)
            throw new SafeError('Google Play AAB 응답의 버전 코드·SHA-256이 실제 빌드 파일과 다릅니다.');
        const nextTrack = {
            track: TRACK,
            releases: [...inventory.Releases, { name: `DrawLiar ${build.BundleVersion} (${build.VersionCode})`, versionCodes: [String(build.VersionCode)], status: STATUS }],
        };
        const savedTrack = await request(`${editRoot}/tracks/${TRACK}`, 'track', { method: 'PUT', data: nextTrack });
        VerifyTrack(savedTrack, inventory.Releases, build.VersionCode);
        const validation = RequireObject(await request(`${editRoot}:validate`, 'validate', { method: 'POST' }), '편집 검증');
        if (validation.id !== editId) throw new SafeError('Google Play 편집 검증 응답이 요청과 다릅니다.');
        commitAttempted = true;
        WriteJson(resultFile, { ...pending, Committed: null, CommitAttempted: true });
        const confirmation = RequireObject(await request(`${editRoot}:commit?changesInReviewBehavior=ERROR_IF_IN_REVIEW`, 'commit', { method: 'POST' }), '초안 저장');
        if (confirmation.id !== editId) throw new SafeError('Google Play 초안 저장 응답을 확인하지 못했습니다. Play Console에서 저장 여부를 확인하세요.');
        committed = true;
        const result = { ...pending, Success: true, Committed: true, CommitAttempted: true, Sha256: hash, CompletedUtc: new Date().toISOString() };
        WriteJson(resultFile, result);
        console.log('Google Play 내부 테스트 초안 저장 완료');
        return result;
    } catch (error) {
        if (pending && !planOnly) {
            try { WriteJson(resultFile, { ...pending, Committed: committed ? true : commitAttempted ? null : false, CommitAttempted: commitAttempted }); } catch {}
        }
        if (error instanceof SafeError) throw error;
        throw new SafeError('Google Play 제출 처리에 실패했습니다. 파일·서비스 계정·앱 설정을 확인하세요.');
    } finally {
        if (editId && request && !committed) {
            try { await request(`${API_ROOT}/${editId}`, 'delete', { method: 'DELETE' }); }
            catch { console.warn('Google Play 임시 편집 삭제를 확인하지 못했습니다. 임시 편집은 만료 후 제거됩니다.'); }
        }
    }
}

if (require.main === module) Main().catch(error => { console.error(error instanceof SafeError ? error.message : 'Google Play 제출 처리에 실패했습니다.'); process.exitCode = 1; });
module.exports = { Main, ParseVersionCode, PACKAGE_NAME, TRACK, STATUS, MAX_VERSION_CODE };
