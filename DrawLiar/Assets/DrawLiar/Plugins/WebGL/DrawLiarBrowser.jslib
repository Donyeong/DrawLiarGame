var DrawLiarBrowserLibrary = {
  $DrawBrowser__postset: 'DrawBrowser.installChatShortcut();',
  $DrawBrowser: {
    sockets: {},
    nextSocket: 1,
    guestKey: 'com.rascallab.drawliar.guest.v1',
    topicsKey: 'com.rascallab.drawliar.customTopics.v1',
    clipboard: {},
    nextClipboard: 1,
    google: null,
    nextGoogle: 1,
    inputFields: {},
    input: null,
    closedInputs: {},
    inputPointer: null,
    inputResize: null,
    keyboardViewport: null,
    inputShortcut: null,
    chatShortcutToken: 0,
    chatShortcutRequest: 0,
    audioInitialized: false,
    installChatShortcut: function() {
      if (DrawBrowser.inputShortcut || typeof window.addEventListener !== 'function') return;
      DrawBrowser.inputShortcut = function(event) {
        var target = event.target;
        if (target && (target.isContentEditable || target.closest && target.closest('input,textarea,select,button,a,[contenteditable="true"]'))) return;
        if (target !== Module.canvas && target !== document.body && target !== document.documentElement && target !== document && target !== window) return;
        if (event.key === 'Escape' && DrawBrowser.chatShortcutRequest && !event.isComposing && event.keyCode !== 229) {
          DrawBrowser.chatShortcutRequest = 0;
          return;
        }
        var enter = event.key === 'Enter' || event.code === 'Enter' || event.code === 'NumpadEnter' || event.keyCode === 13;
        if (!enter || event.repeat || event.isComposing || event.keyCode === 229 || event.altKey || event.ctrlKey || event.metaKey) return;
        var ids = Object.keys(DrawBrowser.inputFields);
        for (var index = ids.length - 1; index >= 0; index--) {
          var field = DrawBrowser.inputFields[ids[index]];
          if (!field.ChatShortcut) continue;
          if (DrawBrowser.openInput(field.Id) !== 1) break;
          DrawBrowser.input.enterDown = true;
          DrawBrowser.input.flags |= 128;
          event.preventDefault();
          event.stopImmediatePropagation();
          return;
        }
        if (!DrawBrowser.chatShortcutToken) return;
        DrawBrowser.chatShortcutRequest = DrawBrowser.chatShortcutToken;
        event.preventDefault();
        event.stopImmediatePropagation();
      };
      window.addEventListener('keydown', DrawBrowser.inputShortcut, true);
      if (Module.deinitializers) Module.deinitializers.push(function() {
        if (DrawBrowser.inputShortcut) window.removeEventListener('keydown', DrawBrowser.inputShortcut, true);
        DrawBrowser.inputShortcut = null;
        DrawBrowser.chatShortcutToken = 0;
        DrawBrowser.chatShortcutRequest = 0;
      });
    },
    string: function(value) {
      if (value === null || value === undefined) return 0;
      var length = lengthBytesUTF8(value) + 1;
      var pointer = _malloc(length);
      stringToUTF8(value, pointer, length);
      return pointer;
    },
    removeGoogle: function(request) {
      if (!request) return;
      if (request.element) request.element.remove();
      if (request.keyHandler) document.removeEventListener('keydown', request.keyHandler);
      if (request.timer) clearTimeout(request.timer);
      request.element = null;
      request.credential = '';
    },
    inputValue: function(value, limit) {
      value = String(value || '').slice(0, limit);
      var end = value.charCodeAt(value.length - 1);
      return end >= 0xd800 && end <= 0xdbff ? value.slice(0, -1) : value;
    },
    overlayParent: function(element) {
      var parent = Module.canvas && Module.canvas.parentElement || document.body;
      var fullscreen = document.fullscreenElement;
      if (fullscreen && fullscreen.tagName !== 'CANVAS' && fullscreen.contains(Module.canvas)) parent = fullscreen;
      if (element.parentNode !== parent) { parent.appendChild(element); return true; }
      return false;
    },
    layoutInput: function(entry, fromResize) {
      if (!entry || !Module.canvas || !entry.element) return;
      var rectangle = Module.canvas.getBoundingClientRect();
      var field = DrawBrowser.inputFields[entry.id];
      if (!field) return;
      var element = entry.element;
      entry.reparenting = true;
      var hadFocus = document.activeElement === element;
      var moved = DrawBrowser.overlayParent(element);
      if (moved && hadFocus) element.focus({preventScroll:true});
      entry.reparenting = false;
      var viewport = window.visualViewport;
      // 키보드 배치의 좌표는 새 Unity 설정을 받은 뒤 갱신한다.
      if (fromResize && entry.keyboardBounds && hadFocus && entry.inputCanvasWidth === rectangle.width
          && Math.abs(window.innerWidth - entry.layoutWidth) <= entry.layoutWidth * .1
          && (!viewport || Math.abs(viewport.scale - 1) <= .05)) return;
      entry.inputCanvasWidth = rectangle.width;
      var top = rectangle.top + rectangle.height * field.Y;
      element.style.left = (rectangle.left + rectangle.width * field.X) + 'px';
      element.style.top = top + 'px';
      element.style.width = (rectangle.width * field.Width) + 'px';
      element.style.height = (rectangle.height * field.Height) + 'px';
      var minimumFont = window.matchMedia('(pointer: coarse)').matches || window.innerWidth < 720 ? 16 : 1;
      element.style.fontSize = Math.max(minimumFont, rectangle.width * field.FontSize) + 'px';
      element.style.color = field.Color;
      element.style.caretColor = field.Color;
      element.style.direction = field.Rtl ? 'rtl' : 'ltr';
      element.style.backgroundColor = 'transparent';
      element.style.boxShadow = 'none';
      element.style.clipPath = 'inset(' + Math.max(0, (field.ClipY - field.Y) * rectangle.height) + 'px '
        + Math.max(0, (field.X + field.Width - field.ClipX - field.ClipWidth) * rectangle.width) + 'px '
        + Math.max(0, (field.Y + field.Height - field.ClipY - field.ClipHeight) * rectangle.height) + 'px '
        + Math.max(0, (field.ClipX - field.X) * rectangle.width) + 'px)';
    },
    closeInput: function(id, focusCanvas) {
      var entry = DrawBrowser.input;
      if (!entry || entry.id !== id) return;
      if (entry.compositionTimer) clearTimeout(entry.compositionTimer);
      DrawBrowser.input = null;
      DrawBrowser.closedInputs[id] = {value:DrawBrowser.inputValue(entry.element.value, entry.limit), limit:entry.limit};
      if (DrawBrowser.inputFields[id]) DrawBrowser.inputFields[id].Value = DrawBrowser.closedInputs[id].value;
      entry.element.remove();
      if (focusCanvas && Module.canvas) Module.canvas.focus({preventScroll:true});
    },
    settleInput: function(entry) {
      if (entry.compositionTimer) clearTimeout(entry.compositionTimer);
      entry.compositionTimer = setTimeout(function() {
        entry.compositionTimer = 0; entry.ending = false;
        if (entry.composing || !entry.submitAfterComposition) return;
        entry.submitAfterComposition = false;
        if (DrawBrowser.input === entry && document.activeElement === entry.element) entry.flags |= 1;
      }, 0);
    },
    openInput: function(id) {
      var field = DrawBrowser.inputFields[id];
      if (!field) return 0;
      if (DrawBrowser.input && DrawBrowser.input.id === id) {
        DrawBrowser.input.flags &= ~4;
        DrawBrowser.input.element.focus({preventScroll:true});
        return 1;
      }
      if (DrawBrowser.input) DrawBrowser.closeInput(DrawBrowser.input.id, false);
      var element = document.createElement(field.Multiline ? 'textarea' : 'input');
      var entry = {id:id, element:element, limit:field.Limit, flags:0, composing:false, ending:false,
        submitAfterComposition:false, enterDown:false, imeKeyDown:false, compositionTimer:0};
      DrawBrowser.captureViewport(entry);
      delete DrawBrowser.closedInputs[id];
      DrawBrowser.input = entry;
      element.id = 'drawliar-text-input';
      element.name = field.Name || 'drawliar-text-input';
      element.setAttribute('aria-label', field.Placeholder || field.Name || '');
      element.autocomplete = 'off';
      element.spellcheck = !!field.Correction;
      element.setAttribute('autocorrect', field.Correction ? 'on' : 'off');
      element.setAttribute('autocapitalize', 'off');
      element.maxLength = field.Limit;
      element.placeholder = field.Placeholder || '';
      element.value = DrawBrowser.inputValue(field.Value, field.Limit);
      if (!field.Multiline) element.type = field.Password ? 'password' : field.Keyboard === 7 ? 'email' : field.Keyboard === 3 ? 'url' : 'text';
      element.inputMode = field.Keyboard === 4 || field.Keyboard === 5 ? 'numeric' : field.Keyboard === 2 ? 'decimal' : field.Keyboard === 7 ? 'email' : field.Keyboard === 3 ? 'url' : 'text';
      element.enterKeyHint = field.Multiline ? 'enter' : 'send';
      element.style.cssText = 'position:fixed;z-index:2147483000;box-sizing:border-box;margin:0;padding:0;border:0;border-radius:0;outline:0;background:transparent;font-family:Pretendard,"Noto Sans KR",system-ui,sans-serif;line-height:1.3;resize:none;overflow:auto;';
      element.addEventListener('input', function() {
        if (!entry.composing) element.value = DrawBrowser.inputValue(element.value, field.Limit);
        entry.flags |= 64;
        if (entry.submitAfterComposition && !entry.composing) DrawBrowser.settleInput(entry);
      });
      element.addEventListener('compositionstart', function() {
        if (entry.compositionTimer) clearTimeout(entry.compositionTimer);
        entry.compositionTimer = 0; entry.composing = true; entry.ending = false; entry.submitAfterComposition = false;
      });
      element.addEventListener('compositionend', function() {
        entry.composing = false; entry.ending = true; entry.flags |= 64;
        element.value = DrawBrowser.inputValue(element.value, field.Limit);
        DrawBrowser.settleInput(entry);
      });
      element.addEventListener('keydown', function(event) {
        event.stopPropagation();
        var composing = event.isComposing || entry.composing || entry.ending || event.keyCode === 229;
        var enter = event.key === 'Enter' || event.code === 'Enter' || event.code === 'NumpadEnter' || event.keyCode === 13;
        entry.imeKeyDown = !enter && composing && event.key === 'Process';
        if (event.key === 'Escape') { entry.submitAfterComposition = false; entry.imeKeyDown = false; }
        if (enter && !field.Multiline) {
          if (!composing) event.preventDefault();
          if (event.repeat || entry.enterDown) return;
          entry.enterDown = true;
          if (composing) {
            // IME가 마지막 글자를 확정하도록 기본 동작을 허용하고 채팅 전송을 예약한다.
            if (field.SubmitOnCompositionEnd) {
              entry.submitAfterComposition = true;
              if (!entry.composing) DrawBrowser.settleInput(entry);
            }
          } else entry.flags |= 1;
          return;
        }
        if (composing) return;
        if (event.key === 'Escape') { event.preventDefault(); entry.flags |= 2; }
        else if (event.key === 'Tab') { event.preventDefault(); entry.flags |= 8 | (event.shiftKey ? 16 : 0); }
      });
      element.addEventListener('keyup', function(event) {
        event.stopPropagation();
        var enter = event.key === 'Enter' || event.code === 'Enter' || event.code === 'NumpadEnter' || event.keyCode === 13;
        if (enter) {
          // 일부 IME는 keydown에서 물리 키를 숨기므로 Enter keyup으로만 전송 의도를 복원한다.
          if (entry.imeKeyDown && !entry.enterDown && field.SubmitOnCompositionEnd && !field.Multiline) {
            entry.submitAfterComposition = true;
          }
          entry.enterDown = false;
          if (entry.submitAfterComposition && !entry.composing) DrawBrowser.settleInput(entry);
        }
        entry.imeKeyDown = false;
      });
      element.addEventListener('keypress', function(event) { event.stopPropagation(); });
      element.addEventListener('blur', function() {
        if (DrawBrowser.input !== entry || entry.reparenting) return;
        entry.submitAfterComposition = false; entry.enterDown = false; entry.imeKeyDown = false; entry.flags |= 4;
      });
      element.addEventListener('focus', function() { entry.flags &= ~4; });
      DrawBrowser.overlayParent(element);
      DrawBrowser.layoutInput(entry);
      element.focus({preventScroll:true});
      if (!field.Multiline) element.select();
      return 1;
    },
    captureViewport: function(entry) {
      entry.keyboardBounds = null;
      entry.keyboardRestoreAt = null;
      var viewport = window.visualViewport;
      var rectangle = Module.canvas.getBoundingClientRect();
      var previous = DrawBrowser.keyboardViewport;
      var visibleHeight = viewport ? viewport.height : window.innerHeight;
      if (previous && Math.abs(window.innerWidth - previous.layoutWidth) <= previous.layoutWidth * .1
          && (!viewport || Math.abs(viewport.scale - 1) <= .05)
          && Math.max(previous.layoutHeight - window.innerHeight, previous.viewportHeight - visibleHeight)
            > Math.max(80, previous.viewportHeight * .15)) {
        entry.layoutWidth = previous.layoutWidth; entry.layoutHeight = previous.layoutHeight;
        entry.viewportHeight = previous.viewportHeight; entry.canvasHeight = previous.canvasHeight;
        return;
      }
      DrawBrowser.keyboardViewport = null;
      entry.layoutWidth = window.innerWidth;
      entry.layoutHeight = window.innerHeight;
      entry.viewportHeight = viewport ? viewport.height : window.innerHeight;
      entry.canvasHeight = rectangle.height;
    },
    renderGoogle: function(request) {
      if (DrawBrowser.google !== request || request.state !== 0) return;
      try {
        google.accounts.id.initialize({
          client_id: request.clientId,
          nonce: request.nonce,
          ux_mode: 'popup',
          auto_select: false,
          callback: function(response) {
            if (DrawBrowser.google !== request || request.state !== 0) return;
            if (!response || typeof response.credential !== 'string' || response.credential.length > 65536) {
              request.state = 3;
              return;
            }
            request.credential = response.credential;
            request.state = 1;
          }
        });
        google.accounts.id.renderButton(request.button, {theme:'outline', size:'large', width:280});
      } catch (_) { request.state = 3; }
    }
  },

  DrawBrowserServerUrl: function(namePointer, websocket, development) {
    try {
      var configuration = window.drawLiarConfig;
      var value = configuration && configuration[UTF8ToString(namePointer)];
      if (typeof value !== 'string' || value.length > 2048) return 0;
      var address = new URL(value);
      var expected = websocket ? 'wss:' : 'https:';
      var local = development && (location.hostname === 'localhost' || location.hostname === '127.0.0.1' || location.hostname === '[::1]');
      if (address.protocol !== expected && !(local && address.protocol === (websocket ? 'ws:' : 'http:'))) return 0;
      var origin = new URL(address.href);
      origin.protocol = address.protocol === 'wss:' ? 'https:' : address.protocol === 'ws:' ? 'http:' : address.protocol;
      if (origin.origin !== location.origin || address.username || address.password || address.hash || address.search) return 0;
      return DrawBrowser.string(address.href.replace(/\/$/, ''));
    } catch (_) { return 0; }
  },

  DrawBrowserRandom: function(pointer, length) {
    try {
      if (!window.isSecureContext || length <= 0 || length > 65536) return 0;
      crypto.getRandomValues(HEAPU8.subarray(pointer, pointer + length));
      return 1;
    } catch (_) { return 0; }
  },

  DrawBrowserGuestLoad: function() {
    try {
      if (!window.isSecureContext) return 0;
      return DrawBrowser.string(localStorage.getItem(DrawBrowser.guestKey) || '');
    } catch (_) { return 0; }
  },

  DrawBrowserGuestSave: function(payloadPointer, onlyIfMissing) {
    try {
      if (!window.isSecureContext) return 0;
      var payload = UTF8ToString(payloadPointer);
      if (!payload || payload.length > 4096) return 0;
      var existing = localStorage.getItem(DrawBrowser.guestKey);
      if (onlyIfMissing && existing) return DrawBrowser.string(existing);
      localStorage.setItem(DrawBrowser.guestKey, payload);
      return DrawBrowser.string(localStorage.getItem(DrawBrowser.guestKey));
    } catch (_) { return 0; }
  },

  DrawBrowserGuestDelete: function() {
    try { localStorage.removeItem(DrawBrowser.guestKey); return 1; }
    catch (_) { return 0; }
  },

  DrawBrowserTopicsLoad: function() {
    try { return DrawBrowser.string(localStorage.getItem(DrawBrowser.topicsKey) || ''); }
    catch (_) { return 0; }
  },

  DrawBrowserTopicsSave: function(pointer) {
    try { localStorage.setItem(DrawBrowser.topicsKey, UTF8ToString(pointer)); return 1; }
    catch (_) { return 0; }
  },

  DrawBrowserIsMobile: function() {
    return window.matchMedia('(pointer: coarse)').matches || window.innerWidth < 720 ? 1 : 0;
  },

  DrawBrowserRoomPageUrl: function() {
    try { return DrawBrowser.string(new URL('/games/liar-canvas', location.origin).href); }
    catch (_) { return 0; }
  },

  DrawBrowserRoomInvite: function() {
    try {
      var address = new URL(location.href);
      return DrawBrowser.string(address.searchParams.has('room') && address.href.length <= 2048 ? address.href : '');
    } catch (_) { return 0; }
  },

  DrawBrowserRoomInviteClear: function() {
    try {
      var address = new URL(location.href);
      if (!address.searchParams.has('room')) return;
      address.searchParams.delete('room');
      history.replaceState(history.state, '', address.pathname + address.search + address.hash);
    } catch (_) { }
  },

  DrawBrowserInputConfigure: function(pointer) {
    try {
      DrawBrowser.installChatShortcut();
      var configuration = JSON.parse(UTF8ToString(pointer));
      var fields = configuration.Fields;
      if (!Array.isArray(fields) || fields.length > 128) return;
      var shortcutToken = Number.isInteger(configuration.ChatShortcutToken) && configuration.ChatShortcutToken > 0 ? configuration.ChatShortcutToken : 0;
      if (DrawBrowser.chatShortcutToken !== shortcutToken) DrawBrowser.chatShortcutRequest = 0;
      DrawBrowser.chatShortcutToken = shortcutToken;
      var previous = DrawBrowser.inputFields;
      var current = {};
      fields.forEach(function(field) {
        if (!Number.isInteger(field.Id) || field.Id <= 0 || !Number.isFinite(field.X) || !Number.isFinite(field.Y)
            || !Number.isFinite(field.Width) || !Number.isFinite(field.Height) || field.Width <= 0 || field.Height <= 0) return;
        field.Limit = Math.max(0, Math.min(65536, Number.isInteger(field.Limit) ? field.Limit : 65536));
        current[field.Id] = field;
        if (DrawBrowser.input && DrawBrowser.input.id === field.Id && previous[field.Id]
            && previous[field.Id].Value !== field.Value && !DrawBrowser.input.composing) {
          DrawBrowser.input.element.value = DrawBrowser.inputValue(field.Value, field.Limit);
        }
      });
      DrawBrowser.inputFields = current;
      if (DrawBrowser.input && !current[DrawBrowser.input.id])
        DrawBrowser.closeInput(DrawBrowser.input.id, document.activeElement === DrawBrowser.input.element);
      DrawBrowser.layoutInput(DrawBrowser.input);
      if (!DrawBrowser.inputPointer) {
        DrawBrowser.inputPointer = function(event) {
          if (event.target !== Module.canvas || event.button > 0) return;
          var rectangle = Module.canvas.getBoundingClientRect();
          if (!rectangle.width || !rectangle.height) return;
          var x = (event.clientX - rectangle.left) / rectangle.width;
          var y = (event.clientY - rectangle.top) / rectangle.height;
          var ids = Object.keys(DrawBrowser.inputFields);
          for (var index = ids.length - 1; index >= 0; index--) {
            var field = DrawBrowser.inputFields[ids[index]];
            if (x >= field.HitX && x <= field.HitX + field.HitWidth && y >= field.HitY && y <= field.HitY + field.HitHeight) {
              DrawBrowser.openInput(field.Id);
              event.preventDefault();
              return;
            }
          }
        };
        document.addEventListener('pointerdown', DrawBrowser.inputPointer, true);
        DrawBrowser.inputResize = function() {
          var previous = DrawBrowser.keyboardViewport;
          var viewport = window.visualViewport;
          if (!DrawBrowser.input && previous && window.innerHeight >= previous.layoutHeight - 80
              && (!viewport || viewport.height >= previous.viewportHeight - 80)) DrawBrowser.keyboardViewport = null;
          DrawBrowser.layoutInput(DrawBrowser.input, true);
          if (DrawBrowser.google && DrawBrowser.google.element) DrawBrowser.overlayParent(DrawBrowser.google.element);
        };
        window.addEventListener('resize', DrawBrowser.inputResize);
        window.addEventListener('scroll', DrawBrowser.inputResize, true);
        document.addEventListener('fullscreenchange', DrawBrowser.inputResize);
        if (window.visualViewport) {
          window.visualViewport.addEventListener('resize', DrawBrowser.inputResize);
          window.visualViewport.addEventListener('scroll', DrawBrowser.inputResize);
        }
      }
    } catch (_) { }
  },

  DrawBrowserInputActive: function() { return DrawBrowser.input ? DrawBrowser.input.id : 0; },
  DrawBrowserKeyboardMetrics: function(pointer) {
    var entry = DrawBrowser.input;
    var viewport = window.visualViewport;
    if (!entry || !Module.canvas || !(window.matchMedia('(pointer: coarse)').matches || window.innerWidth < 720)) return 0;
    if (Math.abs(window.innerWidth - entry.layoutWidth) > entry.layoutWidth * .1) {
      DrawBrowser.captureViewport(entry);
      return 0;
    }
    if (viewport && Math.abs(viewport.scale - 1) > .05) {
      entry.keyboardBounds = null; entry.keyboardRestoreAt = null;
      DrawBrowser.keyboardViewport = null;
      return 0;
    }
    var visibleHeight = viewport ? viewport.height : window.innerHeight;
    var reduction = Math.max(entry.layoutHeight - window.innerHeight, entry.viewportHeight - visibleHeight,
      window.innerHeight - visibleHeight);
    var bounds = entry.keyboardBounds;
    if (!(reduction > Math.max(80, entry.viewportHeight * .15))) {
      if (bounds && document.activeElement === entry.element) {
        var now = typeof performance !== 'undefined' ? performance.now() : Date.now();
        if (entry.keyboardRestoreAt === null) entry.keyboardRestoreAt = now;
        // 입력 중 일시적인 viewport 복원은 키보드 배치를 유지한다.
        if (now - entry.keyboardRestoreAt >= 150) bounds = null;
      } else bounds = null;
      if (!bounds) {
        entry.keyboardBounds = null; entry.keyboardRestoreAt = null;
        DrawBrowser.keyboardViewport = null;
        return 0;
      }
    } else {
      entry.keyboardRestoreAt = null;
      bounds = entry.keyboardBounds || (entry.keyboardBounds = {top:0, bottom:0});
      bounds.top = viewport ? viewport.offsetTop : 0;
      bounds.bottom = bounds.top + visibleHeight;
    }
    var rectangle = Module.canvas.getBoundingClientRect();
    if (!(rectangle.height > 0)) return 0;
    DrawBrowser.keyboardViewport = {layoutWidth:entry.layoutWidth, layoutHeight:entry.layoutHeight,
      viewportHeight:entry.viewportHeight, canvasHeight:entry.canvasHeight};
    HEAPF32[pointer >> 2] = Math.max(1, entry.canvasHeight / rectangle.height);
    HEAPF32[(pointer >> 2) + 1] = Math.max(0, Math.min(1, (bounds.top - rectangle.top) / rectangle.height));
    HEAPF32[(pointer >> 2) + 2] = Math.max(0, Math.min(1, (rectangle.bottom - bounds.bottom) / rectangle.height));
    return 1;
  },
  DrawBrowserInputTakeChatShortcut: function() {
    var token = DrawBrowser.chatShortcutRequest;
    DrawBrowser.chatShortcutRequest = 0;
    return token;
  },
  DrawBrowserInputOpen: function(id) { return DrawBrowser.openInput(id); },
  DrawBrowserInputFocused: function(id) {
    return DrawBrowser.input && DrawBrowser.input.id === id && document.activeElement === DrawBrowser.input.element ? 1 : 0;
  },
  DrawBrowserInputState: function(id) {
    var entry = DrawBrowser.input;
    if (!entry || entry.id !== id) return 4;
    var flags = entry.flags | (entry.composing ? 32 : 0);
    if (document.activeElement === entry.element) flags &= ~4;
    entry.flags = 0;
    return flags;
  },
  DrawBrowserInputValue: function(id) {
    var entry = DrawBrowser.input;
    if (!entry || entry.id !== id) {
      var previous = DrawBrowser.closedInputs[id];
      return previous ? DrawBrowser.string(previous.value) : 0;
    }
    return DrawBrowser.string(DrawBrowser.inputValue(entry.element.value, entry.limit));
  },
  DrawBrowserInputSetValue: function(id, pointer) {
    var entry = DrawBrowser.input;
    var previous = DrawBrowser.closedInputs[id];
    var field = DrawBrowser.inputFields[id];
    var value = UTF8ToString(pointer);
    if (entry && entry.id === id) entry.element.value = DrawBrowser.inputValue(value, entry.limit);
    else if (previous) previous.value = DrawBrowser.inputValue(value, previous.limit);
    if (field) field.Value = DrawBrowser.inputValue(value, field.Limit);
  },
  DrawBrowserInputClose: function(id, focusCanvas) { DrawBrowser.closeInput(id, focusCanvas); delete DrawBrowser.closedInputs[id]; },
  DrawBrowserInputShutdown: function() {
    if (DrawBrowser.input) DrawBrowser.closeInput(DrawBrowser.input.id, false);
    if (DrawBrowser.inputPointer) document.removeEventListener('pointerdown', DrawBrowser.inputPointer, true);
    if (DrawBrowser.inputResize) {
      window.removeEventListener('resize', DrawBrowser.inputResize);
      window.removeEventListener('scroll', DrawBrowser.inputResize, true);
      document.removeEventListener('fullscreenchange', DrawBrowser.inputResize);
      if (window.visualViewport) {
        window.visualViewport.removeEventListener('resize', DrawBrowser.inputResize);
        window.visualViewport.removeEventListener('scroll', DrawBrowser.inputResize);
      }
    }
    DrawBrowser.inputFields = {}; DrawBrowser.closedInputs = {}; DrawBrowser.inputPointer = null; DrawBrowser.inputResize = null;
    DrawBrowser.keyboardViewport = null;
    DrawBrowser.chatShortcutToken = 0; DrawBrowser.chatShortcutRequest = 0;
  },

  DrawBrowserClipboardStart: function(pointer, read) {
    try {
      if (!navigator.clipboard || !window.isSecureContext) return -1;
      var id = DrawBrowser.nextClipboard++;
      var request = {state:0, value:''};
      DrawBrowser.clipboard[id] = request;
      var operation = read ? navigator.clipboard.readText() : navigator.clipboard.writeText(UTF8ToString(pointer));
      operation.then(function(value) {
        if (DrawBrowser.clipboard[id] !== request) return;
        request.value = read && typeof value === 'string' ? value : '';
        request.state = 1;
      }).catch(function() { if (DrawBrowser.clipboard[id] === request) request.state = 2; });
      return id;
    } catch (_) { return -1; }
  },

  DrawBrowserClipboardState: function(id) {
    var request = DrawBrowser.clipboard[id];
    return request ? request.state : 2;
  },

  DrawBrowserClipboardValue: function(id) {
    var request = DrawBrowser.clipboard[id];
    return request && request.state === 1 ? DrawBrowser.string(request.value) : 0;
  },

  DrawBrowserClipboardRelease: function(id) { delete DrawBrowser.clipboard[id]; },

  DrawBrowserSocketConnect: function(urlPointer) {
    try {
      var address = new URL(UTF8ToString(urlPointer));
      var origin = new URL(address.href);
      origin.protocol = address.protocol === 'wss:' ? 'https:' : 'http:';
      if ((address.protocol !== 'wss:' && address.protocol !== 'ws:') || origin.origin !== location.origin
          || address.username || address.password || address.hash) return -1;
      var socket = new WebSocket(address.href);
      var id = DrawBrowser.nextSocket++;
      var entry = {socket:socket, frames:[], offset:0, bytes:0, error:false, closeCode:1005, kicked:false};
      DrawBrowser.sockets[id] = entry;
      socket.onmessage = function(event) {
        if (DrawBrowser.sockets[id] !== entry || entry.error) return;
        if (typeof event.data !== 'string') {
          entry.error = true;
          socket.close(4000, 'Invalid message');
          return;
        }
        var frame = new TextEncoder().encode(event.data);
        if (frame.length > 1048576 || entry.frames.length >= 512 || entry.bytes + frame.length > 8388608) {
          entry.error = true;
          socket.close(4000, 'Receive limit');
          return;
        }
        entry.frames.push(frame);
        entry.bytes += frame.length;
      };
      socket.onerror = function() { entry.error = true; };
      socket.onclose = function(event) { entry.closeCode = event.code; entry.kicked = event.reason === 'RoomKicked'; };
      return id;
    } catch (_) { return -1; }
  },

  DrawBrowserSocketState: function(id) {
    var entry = DrawBrowser.sockets[id];
    return entry ? entry.socket.readyState : -1;
  },

  DrawBrowserSocketRead: function(id, pointer, offset, count) {
    var entry = DrawBrowser.sockets[id];
    if (!entry || entry.error) return -3;
    if (entry.frames.length === 0) return entry.socket.readyState === 3 ? -2 : -1;
    var frame = entry.frames[0];
    var length = Math.min(count, frame.length - entry.offset);
    HEAPU8.set(frame.subarray(entry.offset, entry.offset + length), pointer + offset);
    entry.offset += length;
    entry.bytes -= length;
    var end = entry.offset === frame.length;
    if (end) { entry.frames.shift(); entry.offset = 0; }
    return length * 2 + (end ? 1 : 0);
  },

  DrawBrowserSocketSend: function(id, pointer, offset, count) {
    var entry = DrawBrowser.sockets[id];
    if (!entry || entry.error || entry.socket.readyState !== 1 || count > 1048576
        || entry.socket.bufferedAmount + count > 8388608) return 0;
    try {
      entry.socket.send(new TextDecoder('utf-8', {fatal:true}).decode(HEAPU8.subarray(pointer + offset, pointer + offset + count)));
      return 1;
    } catch (_) { return 0; }
  },

  DrawBrowserSocketClose: function(id, code) {
    var entry = DrawBrowser.sockets[id];
    if (!entry) return 0;
    try {
      entry.socket.close(code === 1000 || code >= 3000 && code <= 4999 ? code : 4000, '');
      return 1;
    } catch (_) { return 0; }
  },

  DrawBrowserSocketCloseCode: function(id) {
    var entry = DrawBrowser.sockets[id];
    return entry ? entry.closeCode : 1005;
  },

  DrawBrowserSocketWasKicked: function(id) {
    var entry = DrawBrowser.sockets[id];
    return entry && entry.kicked ? 1 : 0;
  },

  DrawBrowserSocketRelease: function(id) {
    var entry = DrawBrowser.sockets[id];
    if (!entry) return;
    delete DrawBrowser.sockets[id];
    entry.socket.onmessage = entry.socket.onerror = entry.socket.onclose = null;
    entry.frames.length = 0;
    try { if (entry.socket.readyState < 2) entry.socket.close(4000, ''); } catch (_) { }
  },

  DrawBrowserGoogleStart: function(clientPointer, noncePointer, closePointer) {
    try {
      if (!window.isSecureContext) return -1;
      DrawBrowser.removeGoogle(DrawBrowser.google);
      var request = {id:DrawBrowser.nextGoogle++, state:0, credential:'', clientId:UTF8ToString(clientPointer), nonce:UTF8ToString(noncePointer)};
      DrawBrowser.google = request;
      var overlay = request.element = document.createElement('div');
      overlay.setAttribute('role', 'dialog');
      overlay.setAttribute('aria-modal', 'true');
      overlay.style.cssText = 'position:fixed;inset:0;z-index:2147483647;display:flex;align-items:center;justify-content:center;background:rgba(35,30,45,.6);';
      var panel = document.createElement('div');
      panel.style.cssText = 'position:relative;padding:56px 24px 32px;background:#fff9ed;border:2px solid #38304a;border-radius:16px;max-width:calc(100vw - 32px);box-sizing:border-box;';
      var close = document.createElement('button');
      close.type = 'button';
      close.textContent = '\u00d7';
      close.setAttribute('aria-label', UTF8ToString(closePointer));
      close.style.cssText = 'position:absolute;right:8px;top:8px;width:40px;height:40px;border:0;background:transparent;color:#38304a;font:28px sans-serif;cursor:pointer;';
      close.onclick = function() { request.state = 2; };
      request.button = document.createElement('div');
      panel.appendChild(close);
      panel.appendChild(request.button);
      overlay.appendChild(panel);
      DrawBrowser.overlayParent(overlay);
      overlay.onclick = function(event) { if (event.target === overlay) request.state = 2; };
      request.keyHandler = function(event) { if (event.key === 'Escape') { event.preventDefault(); request.state = 2; } };
      document.addEventListener('keydown', request.keyHandler);
      close.focus();
      request.timer = setTimeout(function() { if (request.state === 0) request.state = 2; }, 240000);
      if (window.google && google.accounts && google.accounts.id) DrawBrowser.renderGoogle(request);
      else {
        var script = document.getElementById('drawliar-google-identity');
        if (!script) {
          script = document.createElement('script');
          script.id = 'drawliar-google-identity';
          script.src = 'https://accounts.google.com/gsi/client';
          script.async = true;
          document.head.appendChild(script);
        }
        script.addEventListener('load', function() { DrawBrowser.renderGoogle(request); }, {once:true});
        script.addEventListener('error', function() {
          script.remove();
          if (DrawBrowser.google === request) request.state = 3;
        }, {once:true});
      }
      return request.id;
    } catch (_) { return -1; }
  },

  DrawBrowserGoogleState: function(id) {
    var request = DrawBrowser.google;
    return request && request.id === id ? request.state : 3;
  },

  DrawBrowserGoogleCredential: function(id) {
    var request = DrawBrowser.google;
    if (!request || request.id !== id || request.state !== 1) return 0;
    var credential = DrawBrowser.string(request.credential);
    request.credential = '';
    return credential;
  },

  DrawBrowserGoogleCancel: function(id) {
    var request = DrawBrowser.google;
    if (!request || request.id !== id) return;
    DrawBrowser.google = null;
    DrawBrowser.removeGoogle(request);
  },

  DrawBrowserAudioInit__deps: ['$jsAudioCreateCompressedSoundClip'],
  DrawBrowserAudioInit: function() {
    if (DrawBrowser.audioInitialized) return;
    DrawBrowser.audioInitialized = true;
    var createClip = jsAudioCreateCompressedSoundClip;
    jsAudioCreateCompressedSoundClip = function(audioData, soundType) {
      var clip = createClip(audioData, soundType);
      var createSource = clip.createSourceNode;
      clip.createSourceNode = function() {
        var source = createSource.call(this);
        var media = source.mediaElement;
        var play = media.play;
        var stopUnsupported = function() {
          source.playPromise = null;
          source.pauseRequested = false;
          source.isStopped = true;
          media.pause();
          var ended = source.onended;
          if (typeof ended === 'function') ended();
          source.release();
        };
        var handleFailure = function(error) {
          if (!error || error.name !== 'NotSupportedError') throw error;
          if (!clip.error) console.warn('이 브라우저에서 지원하지 않는 배경 음악을 건너뜁니다.', error);
          clip.error = true;
          stopUnsupported();
        };
        // Unity 압축 오디오의 재생 실패만 처리하고 다른 브라우저 오류는 유지한다.
        media.play = function() {
          if (clip.error) return Promise.resolve().then(stopUnsupported);
          try {
            var pending = play.call(this);
            return pending && typeof pending.catch === 'function' ? pending.catch(handleFailure) : pending;
          } catch (error) {
            if (!error || error.name !== 'NotSupportedError') throw error;
            return Promise.resolve().then(function() { handleFailure(error); });
          }
        };
        return source;
      };
      return clip;
    };
  }
};
autoAddDeps(DrawLiarBrowserLibrary, '$DrawBrowser');
mergeInto(LibraryManager.library, DrawLiarBrowserLibrary);
