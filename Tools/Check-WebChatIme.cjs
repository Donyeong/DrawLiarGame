const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const source = fs.readFileSync(path.join(__dirname,
  '../DrawLiar/Assets/DrawLiar/Plugins/WebGL/DrawLiarBrowser.jslib'), 'utf8');

function fixture(options = {}) {
  const timers = new Map();
  const library = {};
  let timerId = 0;
  const rectangle = { left: 0, top: 0, width: 1200, height: 800 };
  function appendChild(element) {
    const moving = element.parentNode && element.parentNode !== this;
    element.parentNode = this;
    if (moving && context.document.activeElement === element) {
      context.document.activeElement = null;
      element.events.blur?.();
    }
  }
  const context = {
    LibraryManager: { library },
    autoAddDeps() {},
    mergeInto: Object.assign,
    UTF8ToString: value => value,
    setTimeout(callback) { timers.set(++timerId, callback); return timerId; },
    clearTimeout(id) { timers.delete(id); },
    window: { innerWidth: 1200, innerHeight: 800, matchMedia: () => ({ matches: false }),
      addEventListener() {}, removeEventListener() {},
      visualViewport: { width: 1200, height: 800, offsetTop: 0, scale: 1, addEventListener() {}, removeEventListener() {} } },
    document: {
      body: { appendChild },
      addEventListener() {}, removeEventListener() {},
      createElement() {
        return {
          value: '', style: {}, events: {},
          setAttribute() {}, select() {},
          remove() { if (context.document.activeElement === this) context.document.activeElement = null; },
          focus() {
            const changed = context.document.activeElement !== this;
            context.document.activeElement = this;
            if (changed) this.events.focus?.();
          },
          addEventListener(name, callback) { this.events[name] = callback; }
        };
      }
    },
    Module: { canvas: { focus() {}, getBoundingClientRect: () => rectangle } }
  };
  vm.createContext(context);
  vm.runInContext(source, context);
  const browser = context.DrawBrowser = library.$DrawBrowser;
  browser.string = value => value;
  browser.inputFields[1] = {
    Id: 1, Limit: 160, Value: '', Name: 'chat-input', SubmitOnCompositionEnd: true,
    X: 0, Y: 0, Width: .5, Height: .1, ClipX: 0, ClipY: 0, ClipWidth: .5, ClipHeight: .1,
    FontSize: .02, Color: '#34304b', ...options
  };
  library.DrawBrowserInputOpen(1);
  const input = browser.input.element;
  const key = (type, extra = {}) => {
    const event = { key: 'Enter', code: 'Enter', keyCode: 13, stopPropagation() {},
      preventDefault() { this.prevented = true; }, ...extra };
    input.events[type](event);
    return event;
  };
  return {
    input, browser, library, context, key,
    reparent() {
      context.Module.canvas.parentElement = { appendChild };
      browser.layoutInput(browser.input);
    },
    configure(overrides = {}) {
      library.DrawBrowserInputConfigure(JSON.stringify({ Fields: [{ ...browser.inputFields[1], ...overrides }], ChatShortcutToken: 0 }));
    },
    poll: () => library.DrawBrowserInputState(1) & 1,
    flush() {
      const pending = [...timers.values()];
      timers.clear();
      for (const callback of pending) callback();
    },
    end(value) { input.value = value; input.events.compositionend(); input.events.input(); }
  };
}

test('조합 완료 타이머 이후 도착한 229 Enter도 한 번 전송한다', () => {
  const f = fixture();
  f.input.events.compositionstart();
  f.end('안녕하세요');
  f.flush();
  assert.equal(f.poll(), 0);
  f.key('keydown', { key: 'Process', code: 'Enter', keyCode: 229 });
  f.flush();
  assert.equal(f.poll(), 1);
  assert.equal(f.library.DrawBrowserInputValue(1), '안녕하세요');
  f.key('keyup');
  f.flush();
  assert.equal(f.poll(), 0);
});

test('조합 중 Enter는 최종 글자를 확정한 뒤 한 번 전송한다', () => {
  const f = fixture();
  f.input.events.compositionstart();
  f.input.value = '안녕하세';
  const enter = f.key('keydown', { isComposing: true, keyCode: 229 });
  f.flush();
  assert.equal(f.poll(), 0);
  assert.equal(enter.prevented, undefined);
  f.end('안녕하세요');
  f.key('keyup');
  f.flush();
  assert.equal(f.poll(), 1);
  assert.equal(f.library.DrawBrowserInputValue(1), '안녕하세요');
  assert.equal(f.poll(), 0);
});

test('Process keydown에서 숨겨진 Enter는 실제 Enter keyup으로 복원한다', () => {
  const f = fixture();
  f.input.events.compositionstart();
  f.key('keydown', { key: 'Process', code: '', keyCode: 229, isComposing: true });
  f.end('한글 메시지');
  f.flush();
  assert.equal(f.poll(), 0);
  f.key('keyup');
  f.flush();
  assert.equal(f.poll(), 1);
  assert.equal(f.poll(), 0);
});

test('일반 한글 타자·후보 확정만으로는 전송하지 않는다', () => {
  const f = fixture();
  f.key('keydown', { key: 'Process', code: '', keyCode: 229, isComposing: true });
  f.input.events.compositionstart();
  f.end('초안');
  f.key('keyup', { key: 'r', code: 'KeyR', keyCode: 82 });
  f.flush();
  assert.equal(f.poll(), 0);
});

test('Enter 반복 및 IME 중복 keydown은 중복 전송하지 않는다', () => {
  const f = fixture();
  f.input.events.compositionstart();
  f.key('keydown', { isComposing: true });
  f.end('한 번만');
  f.flush();
  assert.equal(f.poll(), 1);
  f.key('keydown', { repeat: true });
  f.key('keydown');
  f.flush();
  assert.equal(f.poll(), 0);
  f.key('keyup');
  f.library.DrawBrowserInputSetValue(1, '다음 메시지');
  f.key('keydown');
  assert.equal(f.poll(), 1);
});

for (const cancel of ['Escape', 'blur', 'close', 'newComposition']) {
  test(`전송 예약 뒤 ${cancel}은 이전 메시지 전송을 취소한다`, () => {
    const f = fixture();
    f.input.events.compositionstart();
    f.key('keydown', { isComposing: true });
    f.end('취소할 초안');
    if (cancel === 'Escape') f.key('keydown', { key: 'Escape', code: 'Escape', keyCode: 27 });
    else if (cancel === 'blur') { f.input.events.blur(); f.context.document.activeElement = null; }
    else if (cancel === 'close') f.library.DrawBrowserInputClose(1, 0);
    else f.input.events.compositionstart();
    f.flush();
    assert.equal(f.poll(), 0);
  });
}

test('다른 텍스트 필드와 여러 줄 입력은 조합 확정 Enter로 전송하지 않는다', () => {
  for (const options of [{ SubmitOnCompositionEnd: false }, { Multiline: true }]) {
    const f = fixture(options);
    f.input.events.compositionstart();
    f.key('keydown', { key: 'Process', code: '', keyCode: 229, isComposing: true });
    f.end('입력할 이름');
    f.key('keyup');
    f.flush();
    assert.equal(f.poll(), 0);
  }
});

test('모바일 viewport 변화·재부모화·동일 입력 설정 중에도 조합 초안과 Enter 한 번 전송을 유지한다', () => {
  const f = fixture();
  f.context.window.innerWidth = 640;
  f.context.window.visualViewport.height = 400;
  f.input.events.compositionstart();
  f.input.value = '완성할 초';
  f.reparent();
  f.configure({ Value: '이전 관리 값' });
  assert.equal(f.browser.input.element, f.input);
  assert.equal(f.library.DrawBrowserInputFocused(1), 1);
  assert.equal(f.library.DrawBrowserInputValue(1), '완성할 초');
  assert.equal(f.poll(), 0);
  f.key('keydown', { isComposing: true, keyCode: 229 });
  f.end('완성할 초안');
  f.key('keyup');
  f.flush();
  assert.equal(f.poll(), 1);
  assert.equal(f.library.DrawBrowserInputValue(1), '완성할 초안');
  assert.equal(f.poll(), 0);
});
