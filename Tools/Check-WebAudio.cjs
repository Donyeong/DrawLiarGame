const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const unityRoot = process.env.DRAWLIAR_UNITY_WEBGL_TOOLS ||
  'C:/Program Files/Unity/Hub/Editor/6000.3.20f1/Editor/Data/PlaybackEngines/WebGLSupport/BuildTools';
const audioSource = fs.readFileSync(path.join(unityRoot, 'lib/Audio.js'), 'utf8');
const loaderSource = fs.readFileSync(path.join(unityRoot, 'UnityLoader/UnityLoader.js'), 'utf8');
const browserSource = fs.readFileSync(path.join(__dirname,
  '../DrawLiar/Assets/DrawLiar/Plugins/WebGL/DrawLiarBrowser.jslib'), 'utf8');

function extractFunction(source, name, declaration = false) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const pattern = declaration ? new RegExp(`function ${escaped}\\s*\\(`) :
    new RegExp(`${escaped}:\\s*(function\\s*\\()`);
  const match = pattern.exec(source);
  assert.ok(match, `Unity 함수가 존재해야 합니다: ${name}`);
  const start = declaration ? match.index : source.indexOf('function', match.index);
  const open = source.indexOf('{', start);
  let depth = 1;
  for (let i = open + 1; i < source.length; i++) {
    if (source[i] === '"' || source[i] === "'" || source[i] === '`') {
      const quote = source[i++];
      while (i < source.length && source[i] !== quote) { if (source[i] === '\\') i++; i++; }
    } else if (source.slice(i, i + 2) === '//') {
      i = source.indexOf('\n', i + 2);
    } else if (source.slice(i, i + 2) === '/*') {
      i = source.indexOf('*/', i + 2) + 1;
    } else if (source[i] === '{') depth++;
    else if (source[i] === '}' && --depth === 0) return source.slice(start, i + 1);
  }
  throw new Error(`Unity 함수의 끝을 찾을 수 없습니다: ${name}`);
}

function preprocess(source) {
  return source.replace(/#if SYMBOLS_FILENAME\s*\n[\s\S]*?#else[^\n]*\n([\s\S]*?)#endif[^\n]*/g, '$1')
    .replace(/\{\{\{\s*makeDynCall\('vi', 'self.callback'\)\s*\}\}\}/g, 'resolveCallback(self.callback)');
}

function fixture({ initialize = true } = {}) {
  const promises = [];
  const alerts = [], warnings = [], errors = [], media = [], timers = new Map();
  let nativePlays = 0, ended = 0, decoded = 0, timerId = 0;
  let behavior = () => BrowserPromise.resolve();

  // 브라우저의 rejection 전달을 기록해 Node 테스트 러너와 분리한다.
  class BrowserPromise {
    constructor(executor) { this.attach(new Promise(executor)); }
    attach(native) {
      this.native = native; this.handled = false; this.state = 'pending'; promises.push(this);
      native.then(value => { this.state = 'fulfilled'; this.value = value; },
        reason => { this.state = 'rejected'; this.reason = reason; });
    }
    static from(native) { const result = Object.create(this.prototype); result.attach(native); return result; }
    static resolve(value) { return this.from(Promise.resolve(value)); }
    static reject(reason) { return this.from(Promise.reject(reason)); }
    then(resolve, reject) { this.handled = true; return BrowserPromise.from(this.native.then(resolve, reject)); }
    catch(reject) { return this.then(undefined, reject); }
  }

  class Audio {
    constructor() { this.src = ''; this.currentTime = 0; this.playbackRate = 1; this.paused = true; this.pauseCount = 0; this.loadCount = 0; media.push(this); }
    play() { nativePlays++; this.paused = false; return behavior(this); }
    pause() { this.paused = true; this.pauseCount++; }
    removeAttribute(name) { if (name === 'src') this.src = ''; }
    load() { this.loadCount++; }
  }
  const originalPrototypePlay = Audio.prototype.play;
  const node = () => ({ gain: { value: 1 }, connect() {}, disconnect() { this.disconnected = true; } });
  const library = {};
  const context = {
    Audio, Blob, Promise: BrowserPromise, URL: { createObjectURL: () => 'blob:unity-audio', revokeObjectURL() {} },
    LibraryManager: { library }, mergeInto: Object.assign, autoAddDeps() {},
    Module: {}, window: {}, alert: message => alerts.push(message),
    console: { log() {}, warn: (...args) => warnings.push(args), error: (...args) => errors.push(args) },
    setTimeout(callback) { timers.set(++timerId, callback); return timerId; }, clearTimeout(id) { timers.delete(id); },
    resolveCallback: callback => callback,
    WEBAudio: { audioCache: [], audioContext: {
      currentTime: 0, destination: {}, createGain: node, createPanner: node,
      createMediaElementSource: element => ({ ...node(), mediaElement: element }),
      decodeAudioData() { decoded++; throw new Error('추가 디코딩은 허용하지 않습니다.'); }
    } }
  };
  vm.createContext(context);
  for (const name of ['jsAudioMixinSetPitch', 'jsAudioGetMimeTypeFromType', 'jsAudioCreateCompressedSoundClip', 'jsAudioCreateChannel'])
    vm.runInContext(`var ${name} = ${preprocess(extractFunction(audioSource, '$' + name))};`, context, { filename: 'Unity/Audio.js' });
  for (const name of ['errorHandler', 'errorListener'])
    vm.runInContext(`var ${name} = ${preprocess(extractFunction(loaderSource, name, true))};`, context, { filename: 'Unity/UnityLoader.js' });
  vm.runInContext(browserSource, context, { filename: 'DrawLiarBrowser.jslib' });
  context.DrawBrowser = library.$DrawBrowser;
  if (initialize) library.DrawBrowserAudioInit();

  const flush = async () => {
    for (let i = 0; i < 3; i++) await new Promise(resolve => setImmediate(resolve));
    for (const promise of promises)
      if (promise.state === 'rejected' && !promise.handled && !promise.dispatched) {
        promise.dispatched = true; context.errorListener({ reason: promise.reason });
      }
  };
  return {
    context, library, Audio, BrowserPromise, alerts, warnings, errors, media, originalPrototypePlay,
    behavior: value => { behavior = value; },
    clip: () => context.jsAudioCreateCompressedSoundClip(new Uint8Array(200000), 0),
    channel: () => context.jsAudioCreateChannel(() => { ended++; }, 1),
    flush, stats: () => ({ nativePlays, ended, decoded }),
    runTimers() { const pending = [...timers.values()]; timers.clear(); pending.forEach(callback => callback()); }
  };
}

function failure(name) { const error = new Error('오디오 테스트 오류'); error.name = name; return error; }
function play(f, clip = f.clip()) {
  const channel = f.channel(); channel.playSoundClip(clip, 0, 0);
  return { clip, channel, source: channel.source };
}

test('Unity 원래 압축 오디오 실패는 실제 loader의 alert로 연결된다', async () => {
  const f = fixture({ initialize: false }); f.behavior(() => f.BrowserPromise.reject(failure('NotSupportedError')));
  play(f); await f.flush(); assert.equal(f.alerts.length, 1); assert.match(f.alerts[0], /NotSupportedError/);
});

test('정상 압축 오디오 재생과 중복 초기화는 기존 동작을 유지한다', async () => {
  const f = fixture(); const factory = f.context.jsAudioCreateCompressedSoundClip;
  f.library.DrawBrowserAudioInit(); assert.equal(f.context.jsAudioCreateCompressedSoundClip, factory);
  assert.equal(f.Audio.prototype.play, f.originalPrototypePlay);
  const p = play(f); await f.flush(); assert.equal(p.clip.error, false); assert.equal(p.source.playPromise, null);
  assert.equal(p.channel.isStopped(), false); assert.equal(p.source.mediaElement.loadCount, 0);
  assert.equal(f.alerts.length, 0); assert.equal(f.errors.length, 0); assert.deepEqual(f.stats(), { nativePlays: 1, ended: 0, decoded: 0 });
});

for (const name of ['NotAllowedError', 'AbortError']) test(`${name}의 Unity 기존 처리를 보존한다`, async () => {
  const f = fixture(); f.behavior(() => f.BrowserPromise.reject(failure(name)));
  const p = play(f); await f.flush(); assert.equal(p.clip.error, false); assert.equal(p.source.playPromise, null);
  assert.equal(p.source.mediaElement.loadCount, 0); assert.equal(f.warnings.length, 0); assert.equal(f.alerts.length, 0);
});

test('다른 비동기 오류는 실제 Unity 오류 경로로 전달한다', async () => {
  const f = fixture(); f.behavior(() => f.BrowserPromise.reject(failure('UnexpectedAudioError')));
  const p = play(f); await f.flush(); assert.equal(p.clip.error, false); assert.equal(f.alerts.length, 1);
  assert.match(f.alerts[0], /UnexpectedAudioError/); assert.equal(p.source.mediaElement.loadCount, 0);
});

test('지원하지 않는 clip과 재시도는 미디어만 정리하고 alert나 추가 디코딩이 없다', async () => {
  const f = fixture(); f.behavior(() => f.BrowserPromise.reject(failure('NotSupportedError')));
  const first = play(f); await f.flush(); assert.equal(first.clip.error, true); assert.equal(first.channel.source, undefined);
  assert.equal(first.source.isStopped, true); assert.equal(first.source.playPromise, null); assert.equal(first.source.pauseRequested, false);
  assert.equal(first.source.mediaElement.src, ''); assert.equal(first.source.mediaElement.loadCount, 1); assert.equal(first.source.disconnected, true);
  const retry = play(f, first.clip); assert.ok(retry.channel.source); assert.equal(retry.source.playbackStartTime, 0);
  await f.flush(); assert.equal(retry.channel.source, undefined); assert.equal(retry.source.mediaElement.loadCount, 1);
  assert.equal(f.warnings.length, 1); assert.equal(f.alerts.length, 0); assert.equal(f.errors.length, 0);
  assert.deepEqual(f.stats(), { nativePlays: 1, ended: 2, decoded: 0 });
});

test('동기 NotSupportedError도 Unity 채널 설정 뒤에 정리한다', async () => {
  const f = fixture(); f.behavior(() => { throw failure('NotSupportedError'); });
  const p = play(f); assert.ok(p.channel.source); assert.equal(p.source.playbackStartTime, 0);
  await f.flush(); assert.equal(p.channel.source, undefined); assert.equal(p.clip.error, true);
  assert.equal(f.errors.length, 0); assert.equal(f.alerts.length, 0); assert.equal(p.source.mediaElement.loadCount, 1);
});

test('재생 완료 전 stop 후 정상 완료하면 원래 pause 요청을 처리한다', async () => {
  const f = fixture(); let complete; f.behavior(() => new f.BrowserPromise(resolve => { complete = resolve; }));
  const p = play(f); p.channel.stop(0); assert.equal(p.source.pauseRequested, true); complete(); await f.flush();
  assert.equal(p.channel.source, undefined); assert.equal(p.source.playPromise, null); assert.equal(p.source.pauseRequested, false);
  assert.ok(p.source.mediaElement.pauseCount > 0); assert.equal(p.clip.error, false); assert.equal(f.alerts.length, 0);
});

test('재생 완료 전 stop 후 NotSupportedError는 남은 소스를 정리한다', async () => {
  const f = fixture(); let reject; f.behavior(() => new f.BrowserPromise((_, failure) => { reject = failure; }));
  const p = play(f); p.channel.stop(0); reject(failure('NotSupportedError')); await f.flush();
  assert.equal(p.channel.source, undefined); assert.equal(p.source.playPromise, null); assert.equal(p.source.pauseRequested, false);
  assert.equal(p.source.mediaElement.loadCount, 1); assert.equal(f.stats().ended, 0); assert.equal(f.alerts.length, 0);
});

test('Unity 소스 외부의 미디어 오류와 동기 일반 오류를 숨기지 않는다', async () => {
  const f = fixture(); f.behavior(() => f.BrowserPromise.reject(failure('NotSupportedError')));
  new f.Audio().play(); await f.flush(); assert.equal(f.alerts.length, 1);
  const g = fixture(); g.behavior(() => { throw failure('UnexpectedAudioError'); });
  play(g); await g.flush(); assert.equal(g.errors.length, 1); assert.match(g.errors[0][0], /Channel.playSoundClip error/);
  assert.equal(g.stats().decoded, 0);
});
