const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const source = fs.readFileSync(path.join(__dirname,
  '../DrawLiar/Assets/DrawLiar/Plugins/WebGL/DrawLiarBrowser.jslib'), 'utf8');

function fixture() {
  const library = {};
  const listeners = [];
  let clockMs = 0;
  const rectangle = {
    left: 0, top: 0, width: 1200, height: 800,
    get right() { return this.left + this.width; },
    get bottom() { return this.top + this.height; }
  };
  const viewport = {
    height: 800, width: 1200, offsetTop: 0, offsetLeft: 0, scale: 1,
    addEventListener(type, callback) { listeners.push({ type, callback, surface: 'viewport' }); },
    removeEventListener(type, callback) {
      const index = listeners.findIndex(entry => entry.surface === 'viewport' && entry.type === type && entry.callback === callback);
      if (index >= 0) listeners.splice(index, 1);
    }
  };
  function appendChild(element) {
    const moving = element.parentNode && element.parentNode !== this;
    element.parentNode = this;
    if (moving && context.document.activeElement === element) {
      context.document.activeElement = null;
      element.events.blur?.();
    }
  }
  const context = {
    LibraryManager: { library }, autoAddDeps() {}, mergeInto: Object.assign,
    UTF8ToString: value => value,
    setTimeout, clearTimeout, HEAPF32: new Float32Array(16), performance: { now: () => clockMs },
    window: {
      innerWidth: 1200, innerHeight: 800, visualViewport: viewport, matchMedia: () => ({ matches: false }),
      addEventListener(type, callback, capture) { listeners.push({ type, callback, capture }); },
      removeEventListener(type, callback, capture) {
        const index = listeners.findIndex(entry => entry.type === type && entry.callback === callback && entry.capture === capture);
        if (index >= 0) listeners.splice(index, 1);
      }
    },
    document: {
      body: { appendChild }, documentElement: {},
      addEventListener() {}, removeEventListener() {},
      createElement() {
        return {
          value: '', style: {}, events: {},
          setAttribute() {}, select() {},
          remove() {
            this.parentNode = null;
            if (context.document.activeElement === this) context.document.activeElement = null;
          },
          focus() {
            const changed = context.document.activeElement !== this;
            this.focusCount = (this.focusCount || 0) + 1;
            context.document.activeElement = this;
            if (changed) this.events.focus?.();
          },
          addEventListener(type, callback) { this.events[type] = callback; }
        };
      }
    },
    Module: { deinitializers: [], canvas: {
      focus() { context.document.activeElement = this; },
      getBoundingClientRect: () => rectangle
    } }
  };
  vm.createContext(context);
  vm.runInContext(source, context);
  const browser = context.DrawBrowser = library.$DrawBrowser;
  browser.string = value => value;
  vm.runInContext(library.$DrawBrowser__postset, context);
  let nativeSubmits = 0;
  context.window.addEventListener('keydown', () => nativeSubmits++, true);
  const field = {
    Id: 1, Limit: 160, Value: '보존할 초안', Name: 'room-chat-input', ChatShortcut: true,
    SubmitOnCompositionEnd: true, X: 0, Y: 0, Width: .5, Height: .1,
    ClipX: 0, ClipY: 0, ClipWidth: .5, ClipHeight: .1, FontSize: .02, Color: '#34304b', Background: '#ffffff'
  };
  function configure(overrides = {}) {
    Object.assign(field, overrides);
    configureFields([field]);
  }
  function configureFields(fields, token = 0) {
    library.DrawBrowserInputConfigure(JSON.stringify({ Fields: fields, ChatShortcutToken: token }));
  }
  function key(overrides = {}) {
    const event = {
      key: 'Enter', code: 'Enter', keyCode: 13, target: context.Module.canvas,
      preventDefault() { this.prevented = true; },
      stopImmediatePropagation() { this.stopped = true; }, ...overrides
    };
    for (const listener of [...listeners]) {
      if (listener.type !== 'keydown' || !listener.capture) continue;
      listener.callback(event);
      if (event.stopped) break;
    }
    return event;
  }
  configure();
  return { browser, library, context, field, configure, configureFields, key, viewport, rectangle,
    advance(ms) { clockMs += ms; },
    createParent: () => ({ appendChild }),
    resizeViewport() {
      for (const listener of [...listeners]) if (listener.surface === 'viewport' && listener.type === 'resize') listener.callback();
    },
    metrics() {
      const open = library.DrawBrowserKeyboardMetrics(16);
      return { open, ratio: context.HEAPF32[4], top: context.HEAPF32[5], bottom: context.HEAPF32[6] };
    },
    nativeSubmits: () => nativeSubmits,
    state: () => library.DrawBrowserInputState(1) };
}

test('첫 Enter는 초기 캡처에서 입력을 focus하고 초안을 전송하지 않는다', () => {
  const f = fixture();
  const event = f.key();
  assert.equal(event.prevented, true);
  assert.equal(event.stopped, true);
  assert.equal(f.nativeSubmits(), 0);
  assert.equal(f.context.document.activeElement.name, 'room-chat-input');
  assert.equal(f.library.DrawBrowserInputFocused(1), 1);
  assert.equal(f.library.DrawBrowserInputValue(1), '보존할 초안');
  assert.equal(f.state(), 128);
  assert.equal(f.state(), 0);
});

test('여는 Enter의 반복은 전송하지 않고 keyup 뒤 새 Enter만 한 번 전송한다', () => {
  const f = fixture();
  f.key();
  f.state();
  const input = f.browser.input.element;
  const enter = extra => ({ key: 'Enter', code: 'Enter', keyCode: 13,
    stopPropagation() {}, preventDefault() {}, ...extra });
  input.events.keydown(enter({ repeat: true }));
  input.events.keydown(enter());
  assert.equal(f.state() & 1, 0);
  input.events.keyup(enter());
  input.events.keydown(enter());
  assert.equal(f.state() & 1, 1);
  assert.equal(f.state() & 1, 0);
});

test('NumpadEnter도 같은 입력 열기 경로를 사용한다', () => {
  const f = fixture();
  f.key({ key: 'Enter', code: 'NumpadEnter' });
  assert.equal(f.nativeSubmits(), 0);
  assert.equal(f.state(), 128);
});

test('수정키·반복·IME·다른 키는 채팅 단축키가 소비하지 않는다', () => {
  for (const hidden of [false, true]) {
    for (const extra of [{ altKey: true }, { ctrlKey: true }, { metaKey: true },
      { repeat: true }, { isComposing: true }, { keyCode: 229 }, { key: ' ', code: 'Space', keyCode: 32 }]) {
      const f = fixture();
      if (hidden) f.configureFields([], 7);
      const event = f.key(extra);
      assert.equal(event.stopped, undefined);
      assert.equal(f.browser.input, null);
      assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 0);
      assert.equal(f.nativeSubmits(), 1);
    }
  }
});

test('HTML 입력·버튼·링크·편집 영역의 Enter는 기존 동작을 유지한다', () => {
  for (const hidden of [false, true]) {
    for (const selector of ['input', 'textarea', 'select', 'button', 'a', '[contenteditable="true"]']) {
      const f = fixture();
      if (hidden) f.configureFields([], 7);
      const target = { closest: () => selector, isContentEditable: selector.startsWith('[contenteditable') };
      assert.equal(f.key({ target }).stopped, undefined);
      assert.equal(f.browser.input, null);
      assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 0);
    }
  }
});

test('모달·다른 입력·페이지 전환으로 단축키 대상이 없어지면 Enter를 넘긴다', () => {
  const f = fixture();
  f.configure({ ChatShortcut: false });
  assert.equal(f.key().stopped, undefined);
  assert.equal(f.browser.input, null);
  assert.equal(f.nativeSubmits(), 1);
  f.library.DrawBrowserInputConfigure(JSON.stringify({ Fields: [] }));
  assert.equal(f.key().stopped, undefined);
  assert.equal(f.nativeSubmits(), 2);
});

test('설정 갱신은 중복 캡처를 추가하거나 입력 중인 초안을 덮어쓰지 않는다', () => {
  const f = fixture();
  f.configure();
  f.key();
  f.state();
  f.browser.input.element.value = '바로 입력한 내용';
  f.configure();
  assert.equal(f.library.DrawBrowserInputValue(1), '바로 입력한 내용');
  assert.equal(f.nativeSubmits(), 0);
});

test('종료 후 재활성화해도 최초 캡처 우선순위를 유지한다', () => {
  const f = fixture();
  f.library.DrawBrowserInputShutdown();
  assert.equal(f.key().stopped, undefined);
  assert.equal(f.nativeSubmits(), 1);
  f.configure();
  f.key();
  assert.equal(f.nativeSubmits(), 1);
  assert.equal(f.state(), 128);
});

test('Unity 인스턴스 종료는 캡처를 한 번 정리한다', () => {
  const f = fixture();
  f.configureFields([], 7);
  f.key();
  f.browser.installChatShortcut();
  assert.equal(f.context.Module.deinitializers.length, 1);
  f.context.Module.deinitializers[0]();
  assert.equal(f.browser.inputShortcut, null);
  assert.equal(f.browser.chatShortcutToken, 0);
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 0);
  assert.equal(f.key().stopped, undefined);
  assert.equal(f.nativeSubmits(), 1);
  assert.equal(f.browser.input, null);
});

test('숨겨진 모바일 입력의 Enter와 NumpadEnter는 열기 요청만 한 번 소비한다', () => {
  for (const code of ['Enter', 'NumpadEnter']) {
    const f = fixture();
    f.context.window.innerWidth = 640;
    f.configureFields([], 7);
    const event = f.key({ code });
    assert.equal(event.prevented, true);
    assert.equal(event.stopped, true);
    assert.equal(f.nativeSubmits(), 0);
    assert.equal(f.library.DrawBrowserInputActive(), 0);
    assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 7);
    assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 0);
    assert.equal(f.browser.input, null);
    assert.equal(f.field.Value, '보존할 초안');
  }
});

test('같은 화면의 배치 갱신은 열기 요청을 보존하고 리사이즈 대상 교체는 폐기한다', () => {
  const f = fixture();
  f.configureFields([], 7);
  f.key();
  f.configureFields([], 7);
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 7);
  f.key();
  f.configureFields([], 8);
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 0);
  f.key();
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 8);
});

test('모달이 단축키 토큰을 끄면 이전 요청을 폐기하고 새 Enter로 채팅을 열지 않는다', () => {
  const f = fixture();
  f.configureFields([], 7);
  f.key();
  f.configureFields([], 0);
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 0);
  assert.equal(f.key().stopped, undefined);
  assert.equal(f.nativeSubmits(), 1);
  f.configureFields([], 9);
  f.key();
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 9);
});

test('열기 대기 중 Escape는 요청만 취소하고 Unity에 전달하며 HTML 입력과 IME를 유지한다', () => {
  const f = fixture();
  f.configureFields([], 7);
  f.key();
  const escape = { key: 'Escape', code: 'Escape', keyCode: 27 };
  for (const extra of [{ target: { closest: () => 'input' } }, { target: {} },
    { isComposing: true }, { keyCode: 229 }]) {
    assert.equal(f.key({ ...escape, ...extra }).stopped, undefined);
    assert.equal(f.browser.chatShortcutRequest, 7);
  }
  const deliveredBefore = f.nativeSubmits();
  const event = f.key(escape);
  assert.equal(event.prevented, undefined);
  assert.equal(event.stopped, undefined);
  assert.equal(f.nativeSubmits(), deliveredBefore + 1);
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 0);
  assert.equal(f.key(escape).stopped, undefined);
  assert.equal(f.browser.input, null);
});

test('모바일 시트 가시화 후 DOM focus와 닫은 뒤 재열기는 초안을 전송하지 않는다', () => {
  const f = fixture();
  f.configureFields([], 7);
  f.key();
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 7);
  f.configureFields([f.field], 7);
  assert.equal(f.library.DrawBrowserInputOpen(1), 1);
  assert.equal(f.library.DrawBrowserInputFocused(1), 1);
  assert.equal(f.state(), 0);
  f.browser.input.element.value = '작성 중인 초안';
  f.field.Value = f.library.DrawBrowserInputValue(1);
  f.library.DrawBrowserInputClose(1, 1);
  f.configureFields([], 7);
  f.key({ code: 'NumpadEnter' });
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 7);
  f.configureFields([f.field], 7);
  assert.equal(f.library.DrawBrowserInputOpen(1), 1);
  assert.equal(f.library.DrawBrowserInputValue(1), '작성 중인 초안');
  assert.equal(f.state(), 0);
  f.library.DrawBrowserInputClose(1, 1);
  f.key();
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 0);
  assert.equal(f.state(), 128);
  assert.equal(f.nativeSubmits(), 0);
});

test('입력 서비스 종료는 모바일 열기 요청을 지우고 재등록은 최초 캡처 순서를 유지한다', () => {
  const f = fixture();
  f.configureFields([], 7);
  f.key();
  f.library.DrawBrowserInputShutdown();
  assert.equal(f.browser.chatShortcutToken, 0);
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 0);
  assert.equal(f.key().stopped, undefined);
  assert.equal(f.nativeSubmits(), 1);
  f.configureFields([], 11);
  f.key();
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 11);
  assert.equal(f.nativeSubmits(), 1);
});

test('로비 열기 요청은 다른 입력을 건드리지 않고 새 로비 입력의 다음 Enter만 전송한다', () => {
  const f = fixture();
  const search = { ...f.field, Id: 2, Name: 'room-search-input', Value: '찾는 방', ChatShortcut: false };
  const lobby = { ...f.field, Id: 3, Name: 'lobby-chat-input', Value: '로비 초안' };
  f.configureFields([search], 17);
  assert.equal(f.key({ code: 'NumpadEnter' }).stopped, true);
  assert.equal(f.library.DrawBrowserInputActive(), 0);
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 17);
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 0);
  f.configureFields([lobby, search], 18);
  assert.equal(f.library.DrawBrowserInputOpen(3), 1);
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 0);
  assert.equal(f.library.DrawBrowserInputActive(), 3);
  assert.equal(f.library.DrawBrowserInputFocused(3), 1);
  assert.equal(f.library.DrawBrowserInputValue(3), '로비 초안');
  assert.equal(f.library.DrawBrowserInputState(3), 0);
  const input = f.browser.input.element;
  const enter = { key: 'Enter', code: 'Enter', keyCode: 13,
    stopPropagation() {}, preventDefault() {} };
  input.events.keydown(enter);
  assert.equal(f.library.DrawBrowserInputState(3), 1);
  input.events.keydown(enter);
  assert.equal(f.library.DrawBrowserInputState(3), 0);
  f.library.DrawBrowserInputClose(3, 1);
  assert.equal(f.key().stopped, true);
  assert.equal(f.library.DrawBrowserInputActive(), 3);
  assert.equal(f.library.DrawBrowserInputState(3), 128);
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 0);
  assert.equal(search.Value, '찾는 방');
  assert.equal(f.nativeSubmits(), 0);
});

test('전송 후 관리 측 값 비우기를 DOM에 반영하면 닫고 재열어도 보낸 초안이 복원되지 않는다', () => {
  const f = fixture();
  f.key();
  f.state();
  f.browser.input.element.value = '전송한 메시지';
  f.field.Value = '';
  f.library.DrawBrowserInputSetValue(1, f.field.Value);
  assert.equal(f.library.DrawBrowserInputValue(1), '');
  assert.equal(f.browser.inputFields[1].Value, '');
  f.library.DrawBrowserInputClose(1, 1);
  f.configureFields([], 7);
  f.key();
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 7);
  f.configureFields([f.field], 7);
  assert.equal(f.library.DrawBrowserInputOpen(1), 1);
  assert.equal(f.library.DrawBrowserInputFocused(1), 1);
  assert.equal(f.library.DrawBrowserInputValue(1), '');
  assert.equal(f.state(), 0);
});

test('제거된 입력 ID의 DOM과 이전 화면의 열기 요청은 설정 갱신 때 즉시 정리한다', () => {
  const f = fixture();
  f.configureFields([], 7);
  f.key();
  f.configureFields([f.field], 7);
  f.library.DrawBrowserInputOpen(1);
  const previousInput = f.browser.input.element;
  f.configureFields([], 0);
  assert.equal(f.library.DrawBrowserInputActive(), 0);
  assert.equal(f.library.DrawBrowserInputFocused(1), 0);
  assert.equal(previousInput.parentNode, null);
  assert.equal(f.context.document.activeElement, f.context.Module.canvas);
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 0);
  assert.equal(f.library.DrawBrowserInputOpen(1), 0);
  assert.equal(f.key().stopped, undefined);
  assert.equal(f.nativeSubmits(), 1);
});

test('제거된 DOM 입력 정리는 다른 HTML 포커스를 유지하고 새 채팅 입력만 재열 수 있다', () => {
  const f = fixture();
  f.key();
  const previousInput = f.browser.input.element;
  const htmlButton = { closest: () => 'button' };
  f.context.document.activeElement = htmlButton;
  const next = { ...f.field, Id: 2, Name: 'lobby-chat-input', Value: '' };
  f.configureFields([next], 8);
  assert.equal(f.browser.input, null);
  assert.equal(previousInput.parentNode, null);
  assert.equal(f.context.document.activeElement, htmlButton);
  assert.equal(f.key({ target: htmlButton }).stopped, undefined);
  assert.equal(f.browser.input, null);
  assert.equal(f.key({ code: 'NumpadEnter' }).stopped, true);
  assert.equal(f.library.DrawBrowserInputActive(), 2);
  assert.equal(f.library.DrawBrowserInputFocused(2), 1);
  assert.equal(f.library.DrawBrowserInputValue(2), '');
  assert.equal(f.library.DrawBrowserInputState(2), 128);
  assert.equal(f.library.DrawBrowserInputTakeChatShortcut(), 0);
  assert.equal(f.nativeSubmits(), 1);
});

function mobileFixture(height = 900) {
  const f = fixture();
  f.context.window.innerWidth = 640;
  f.context.window.innerHeight = height;
  Object.assign(f.rectangle, { width: 640, height });
  Object.assign(f.viewport, { width: 640, height });
  f.configure({ Y: .8, ClipY: .8 });
  f.key();
  f.state();
  return f;
}

test('모바일 키보드 viewport는 가시영역을 보고하고 DOM 입력은 Unity 위치를 유지한다', () => {
  const f = mobileFixture();
  f.viewport.height = 500;
  f.viewport.offsetTop = 20;
  f.resizeViewport();
  const metrics = f.metrics();
  assert.equal(metrics.open, 1);
  assert.equal(metrics.ratio, 1);
  assert.ok(Math.abs(metrics.top - 20 / 900) < 1e-6);
  assert.ok(Math.abs(metrics.bottom - 380 / 900) < 1e-6);
  assert.equal(f.browser.input.element.style.top, '720px');
  f.configure({ Y: .4, ClipY: .4 });
  assert.equal(f.browser.input.element.style.top, '360px');
});

test('키보드가 canvas 높이까지 줄이면 입력 전 높이비율과 현재 가시영역을 보고한다', () => {
  const f = mobileFixture();
  f.context.window.innerHeight = 500;
  f.rectangle.height = 500;
  f.viewport.height = 500;
  f.resizeViewport();
  const metrics = f.metrics();
  assert.equal(metrics.open, 1);
  assert.ok(Math.abs(metrics.ratio - 1.8) < 1e-6);
  assert.equal(metrics.top, 0);
  assert.equal(metrics.bottom, 0);
});

test('입력 중 순간적인 viewport 복원은 키보드 배치를 유지하고 초안·DOM·포커스를 보존한다', () => {
  const f = mobileFixture();
  const input = f.browser.input.element;
  const focusCount = input.focusCount;
  input.value = '작성 중인 채팅';
  f.viewport.height = 500;
  const keyboard = f.metrics();
  for (const delay of [0, 80, 60]) {
    f.advance(delay);
    f.viewport.height = 900;
    f.resizeViewport();
    assert.deepEqual(f.metrics(), keyboard);
    assert.equal(f.browser.input.element, input);
    assert.equal(f.library.DrawBrowserInputValue(1), '작성 중인 채팅');
    assert.equal(f.library.DrawBrowserInputFocused(1), 1);
    assert.equal(f.state(), 0);
  }
  f.viewport.height = 500;
  assert.deepEqual(f.metrics(), keyboard);
  f.advance(200);
  f.viewport.height = 900;
  assert.deepEqual(f.metrics(), keyboard);
  assert.equal(input.focusCount, focusCount);
});

test('canvas까지 순간적으로 복원되면 현재 크기로 가림을 환산해 기준 높이와 입력 위치를 유지한다', () => {
  const f = mobileFixture();
  const input = f.browser.input.element;
  const focusCount = input.focusCount;
  f.context.window.innerHeight = f.rectangle.height = f.viewport.height = 500;
  f.resizeViewport();
  assert.equal(input.style.top, '400px');
  const reduced = f.metrics();
  assert.ok(Math.abs(reduced.ratio * 500 - 900) < 1e-4);
  f.context.window.innerHeight = f.rectangle.height = f.viewport.height = 900;
  f.resizeViewport();
  assert.equal(input.style.top, '400px');
  const restored = f.metrics();
  assert.equal(restored.open, 1);
  assert.equal(restored.ratio, 1);
  assert.ok(Math.abs(900 * (1 - restored.bottom) - 500) < 1e-4);
  f.configure({ Y: 400 / 900, ClipY: 400 / 900 });
  assert.ok(Math.abs(parseFloat(input.style.top) - 400) < 1e-4);
  f.context.window.innerHeight = f.rectangle.height = f.viewport.height = 500;
  f.resizeViewport();
  assert.ok(Math.abs(parseFloat(input.style.top) - 400) < 1e-4);
  assert.deepEqual(f.metrics(), reduced);
  f.configure({ Y: .8, ClipY: .8 });
  assert.equal(input.style.top, '400px');
  assert.equal(input.focusCount, focusCount);
  assert.equal(f.state(), 0);
});

test('키보드 닫힘이 150ms 유지되면 focus가 남아도 배치를 정상 복원한다', () => {
  const f = mobileFixture();
  f.viewport.height = 500;
  assert.equal(f.metrics().open, 1);
  f.viewport.height = 900;
  assert.equal(f.metrics().open, 1);
  f.advance(149);
  assert.equal(f.metrics().open, 1);
  f.advance(1);
  assert.equal(f.metrics().open, 0);
  assert.equal(f.browser.keyboardViewport, null);
  assert.equal(f.library.DrawBrowserInputFocused(1), 1);
  f.viewport.height = 500;
  assert.equal(f.metrics().open, 1);
});

test('입력 닫기·다른 HTML focus·pinch·회전은 이전 키보드 배치를 붙잡지 않는다', () => {
  for (const change of [
    f => f.library.DrawBrowserInputClose(1, 1),
    f => { f.context.document.activeElement = { closest: () => 'input' }; },
    f => { f.viewport.scale = 1.2; },
    f => { f.context.window.innerWidth = f.viewport.width = f.rectangle.width = 800; }
  ]) {
    const f = mobileFixture();
    f.context.window.matchMedia = () => ({ matches: true });
    f.viewport.height = 500;
    assert.equal(f.metrics().open, 1);
    f.viewport.height = 900;
    change(f);
    assert.equal(f.metrics().open, 0);
  }
  const f = mobileFixture();
  f.viewport.height = 500;
  assert.equal(f.metrics().open, 1);
  f.viewport.height = 900;
  f.viewport.scale = 1.2;
  assert.equal(f.metrics().open, 0);
  f.viewport.scale = 1;
  assert.equal(f.metrics().open, 0);
});

test('주소표시줄 변화·pinch·큰 폭 변경·PC 입력은 모바일 키보드로 취급하지 않는다', () => {
  for (const change of [
    f => { f.viewport.height = 830; },
    f => { f.viewport.height = 500; f.viewport.scale = 1.2; },
    f => { f.viewport.height = 500; f.viewport.width = 800; f.context.window.innerWidth = 800; f.rectangle.width = 800; },
    f => { f.viewport.height = 500; f.library.DrawBrowserInputClose(1, 1); }
  ]) {
    const f = mobileFixture();
    change(f);
    assert.equal(f.metrics().open, 0);
  }
  const pc = fixture();
  pc.key();
  pc.viewport.height = 400;
  assert.equal(pc.metrics().open, 0);
});

test('키보드 resize와 같은 입력의 재배치는 DOM·초안·포커스를 유지하며 focus를 반복하지 않는다', () => {
  const f = mobileFixture();
  const input = f.browser.input.element;
  const focusCount = input.focusCount;
  input.value = '모바일에서 작성 중';
  for (const height of [600, 500, 520]) {
    f.viewport.height = height;
    f.resizeViewport();
    f.configure({ Y: .4, ClipY: .4 });
    assert.equal(f.browser.input.element, input);
    assert.equal(f.library.DrawBrowserInputFocused(1), 1);
    assert.equal(f.library.DrawBrowserInputValue(1), '모바일에서 작성 중');
    assert.equal(f.state(), 0);
  }
  assert.equal(input.focusCount, focusCount);
});

test('fullscreen 재부모화의 blur는 입력을 닫지 않고 실제 변경 때만 focus를 복원한다', () => {
  const f = mobileFixture();
  f.viewport.height = 500;
  assert.equal(f.metrics().open, 1);
  const input = f.browser.input.element;
  const focusCount = input.focusCount;
  const parent = f.createParent();
  f.context.Module.canvas.parentElement = parent;
  f.resizeViewport();
  assert.equal(input.parentNode, parent);
  assert.equal(f.library.DrawBrowserInputFocused(1), 1);
  assert.equal(f.state(), 0);
  assert.equal(input.focusCount, focusCount + 1);
  f.resizeViewport();
  assert.equal(input.focusCount, focusCount + 1);
  const other = { closest: () => 'input' };
  f.context.document.activeElement = other;
  f.context.Module.canvas.parentElement = f.createParent();
  f.resizeViewport();
  assert.equal(f.context.document.activeElement, other);
  assert.equal(f.state(), 0);
});

test('포커스가 복원된 뒤 늦게 온 blur만 무시하고 실제 다른 입력으로 이동한 blur는 전달한다', () => {
  const f = mobileFixture();
  const input = f.browser.input.element;
  input.events.blur();
  assert.equal(f.state(), 0);
  f.context.document.activeElement = { closest: () => 'input' };
  input.events.blur();
  assert.equal(f.state(), 4);
});

test('키보드가 이미 열린 새 입력과 회전 뒤에도 현재 viewport 차이로 가림을 인식한다', () => {
  const f = mobileFixture();
  f.context.window.matchMedia = () => ({ matches: true });
  f.viewport.height = 500;
  assert.equal(f.metrics().open, 1);
  f.library.DrawBrowserInputClose(1, 1);
  f.key();
  f.state();
  assert.equal(f.metrics().open, 1);
  f.context.window.innerWidth = 900;
  f.context.window.innerHeight = 640;
  Object.assign(f.rectangle, { width: 900, height: 640 });
  Object.assign(f.viewport, { width: 900, height: 300 });
  assert.equal(f.metrics().open, 0);
  assert.equal(f.metrics().open, 1);
});

test('canvas도 줄인 키보드를 닫고 재열면 기준 높이를 유지하고 전체 복원 뒤에는 새 기준을 사용한다', () => {
  const f = mobileFixture(844);
  f.context.window.innerHeight = 500;
  f.rectangle.height = 500;
  f.viewport.height = 500;
  assert.equal(f.metrics().open, 1);
  assert.ok(Math.abs(f.metrics().ratio - 844 / 500) < 1e-6);
  const oldInput = f.browser.input.element;
  f.library.DrawBrowserInputClose(1, 1);
  f.key();
  f.state();
  assert.notEqual(f.browser.input.element, oldInput);
  assert.equal(oldInput.parentNode, null);
  assert.equal(f.metrics().open, 1);
  assert.ok(Math.abs(f.metrics().ratio - 844 / 500) < 1e-6);
  f.library.DrawBrowserInputClose(1, 1);
  f.context.window.innerHeight = 844;
  f.rectangle.height = 844;
  f.viewport.height = 844;
  f.resizeViewport();
  assert.equal(f.browser.keyboardViewport, null);
  f.context.window.innerHeight = 900;
  f.rectangle.height = 900;
  f.viewport.height = 900;
  f.key();
  f.state();
  assert.equal(f.metrics().open, 0);
  f.context.window.innerHeight = 500;
  f.rectangle.height = 500;
  f.viewport.height = 500;
  assert.equal(f.metrics().open, 1);
  assert.ok(Math.abs(f.metrics().ratio - 900 / 500) < 1e-6);
  f.library.DrawBrowserInputShutdown();
  assert.equal(f.browser.keyboardViewport, null);
});

test('빈 입력의 열기 Enter와 다음 Enter·Escape는 구분되어 한 번씩 전달된다', () => {
  const f = fixture();
  f.configure({ Value: '' });
  f.key();
  assert.equal(f.state(), 128);
  assert.equal(f.library.DrawBrowserInputValue(1), '');
  const input = f.browser.input.element;
  const key = { key: 'Enter', code: 'Enter', keyCode: 13, stopPropagation() {}, preventDefault() {} };
  input.events.keyup(key);
  input.events.keydown(key);
  input.events.keydown({ ...key, key: 'Escape', code: 'Escape', keyCode: 27 });
  assert.equal(f.state(), 3);
  assert.equal(f.state(), 0);
  assert.equal(f.browser.input.element, input);
  assert.equal(f.nativeSubmits(), 0);
});
