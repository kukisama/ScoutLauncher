/**
 * Scout 汉化外挂 —— 可注入的运行时翻译引擎（纯 JS，无依赖）。
 *
 * 这是"南极星式外挂汉化"在 Electron/Chromium 时代的实现：不改任何程序文件，
 * 通过 CDP 把这段脚本注入到已运行的渲染进程里，拦截进入 DOM 的界面文字，
 * 按映射表替换成中文。原程序的 app.asar / exe 一个字节都不动，完整性校验与
 * 数字签名保持完好。
 *
 * 用法：启动器会把下面代码中的占位符 token 替换成真实字典 JSON，再整体交给
 * CDP 的 Runtime.evaluate 执行。（占位符是紧跟 "var DICT =" 后面的那个标识符。）
 *
 * 安全同 src/i18n-zh 的引擎：
 *  - 精确匹配，只翻译列进字典的固定界面文案；
 *  - 跳过 code/pre/textarea/[contenteditable]/svg/[data-no-i18n]，绝不碰用户输入与模型输出；
 *  - fail-safe：未知字符串保持英文，任何异常吞掉，绝不弄崩应用。
 */
(function () {
  "use strict";

  // 启动器注入时会把这一行替换为：var DICT = { "English": "中文", ... };
  var DICT = __ZH_DICT__;

  if (window.__scoutZhActive) {
    // 已注入过：只刷新字典并重扫一遍，避免重复挂 observer。
    window.__scoutZhActive.updateDict(DICT);
    return "already-active: re-swept";
  }

  var SKIP_TAGS = { SCRIPT: 1, STYLE: 1, CODE: 1, PRE: 1, TEXTAREA: 1, NOSCRIPT: 1, SVG: 1 };
  var ATTRS = ["placeholder", "title", "aria-label", "alt"];

  // ── 内容区永不翻译（护栏）──────────────────────────────────────────
  // 这些容器装的是"用户输入 / 模型输出 / 用户文档正文"——绝不是固定界面文案。
  // 汉化包只翻译程序作者写死在源码里的 UI 文案，永远不碰这些内容区，
  // 既避免误翻用户/模型的文字，也避免把内容里恰好等于某个 UI 短语的词替换掉。
  //
  // 关键：禁区只圈住"真正的模型/用户正文"子容器，而不是整条消息 article。
  // 一条 assistant 消息里，状态标签（Processing / Thinking… / Running tools… /
  // Used N tools）与真正的模型输出是并列的兄弟节点：前者是程序写死的 UI 骨架
  // （在 message-meta 的 CollapsibleSection label 里），后者才在 message-text /
  // *-content 里。因此我们不再把整块 message-assistant / message-user 拉黑，
  // 只拉黑正文子容器，这样状态标签能翻译、模型与用户正文仍受保护。
  // 注意：聊天输入框（chat-input）不在此列——其真正的输入区是 contentEditable，
  // 已被下方 isContentEditable 护栏保护；占位符是独立的 aria-hidden 兄弟节点，可安全翻译。
  var CONTENT_DENY = [
    '[data-testid="message-text"]', // 用户/模型消息正文
    '[data-testid="message-user-bubble"]', // 用户气泡（含文件名、图片）
    '[data-testid="message-assistant-bubble"]', // 模型气泡
    '[data-testid="reasoning-content"]', // 流式推理正文（模型思考）
    '[data-testid="committed-reasoning-content"]', // 已完成推理正文
    '[data-testid="tool-status-line"]', // 工具调用描述（动态、来自工具）
    '[data-testid="committed-tool-calls-content"]', // 已完成工具调用描述
    '[data-testid="markdown"]',
    "[data-no-i18n]",
  ].join(",");

  // ── 控件属性白名单（禁区内的 UI 骨架例外）──────────────────────────
  // 禁区保护的是"模型/用户正文文本节点"。但第三方 markdown 渲染器（streamdown）
  // 在正文区里插入的复制/下载/全屏等控件按钮，其 aria-label / title 永远是程序
  // 骨架文案，绝不含模型内容。因此对这一小撮"已知控件标签"，即便元素落在禁区内，
  // 也照常翻译 aria-label / title（仅这两个属性，且值必须命中下表 + 字典）。
  // 绝不放开 alt/placeholder（可能是模型图片描述），也绝不翻译禁区里的文本节点。
  var CONTROL_LABELS = {
    "Copy table": 1,
    "Copy code": 1,
    "Copy": 1,
    "Copied": 1,
    "Copy message": 1,
    "Copy to clipboard": 1,
    "Copied!": 1,
    "Download": 1,
    "Download table": 1,
    "Fullscreen": 1,
    "Enter fullscreen": 1,
    "Exit fullscreen": 1,
    "Expand": 1,
    "Collapse": 1,
  };
  var CONTROL_ATTRS = ["aria-label", "title"];

  function translateControlAttrs(el) {
    if (!el || el.nodeType !== Node.ELEMENT_NODE || !el.getAttribute) return;
    for (var i = 0; i < CONTROL_ATTRS.length; i++) {
      var a = CONTROL_ATTRS[i];
      var cur = el.getAttribute(a);
      if (!cur) continue;
      var key = cur.trim();
      if (!CONTROL_LABELS[key]) continue;
      var zh = dict[key];
      if (typeof zh === "string" && zh && zh !== key) el.setAttribute(a, zh);
    }
  }

  function sweepControls(root) {
    try {
      if (!root || root.nodeType !== Node.ELEMENT_NODE) return;
      if (root.matches && root.matches("[aria-label],[title]")) translateControlAttrs(root);
      var found = root.querySelectorAll ? root.querySelectorAll("[aria-label],[title]") : [];
      for (var i = 0; i < found.length; i++) translateControlAttrs(found[i]);
    } catch (e) {
      /* fail-safe */
    }
  }

  // 静态字典按整串精确匹配，无法覆盖"Used 3 tools"这类内插了数字的标签。
  // 这里用极少量、锚定收尾（^…$）的正则兜底，只针对程序写死的固定句式，
  // 捕获组回填。顺序在字典精确匹配之后、记漏翻之前，避免误伤与误记。
  var REGEX_RULES = [
    { re: /^Used (\d+) tools?$/, zh: function (m) { return "已用 " + m[1] + " 个工具"; } },
    { re: /^Thought for (\d+)s$/, zh: function (m) { return "思考了 " + m[1] + " 秒"; } },
    { re: /^Thought for (\d+)m (\d+)s$/, zh: function (m) { return "思考了 " + m[1] + " 分 " + m[2] + " 秒"; } },
    // 相对时间（formatDate：Just now / Nm ago / Nh ago / Nd ago），活动列表、同步状态等广泛使用。
    { re: /^Just now$/, zh: function () { return "刚刚"; } },
    { re: /^(\d+)m ago$/, zh: function (m) { return m[1] + " 分钟前"; } },
    { re: /^(\d+)h ago$/, zh: function (m) { return m[1] + " 小时前"; } },
    { re: /^(\d+)d ago$/, zh: function (m) { return m[1] + " 天前"; } },
    // 云同步状态行（模板串，整句是单个文本节点）。
    { re: /^Last synced Just now$/, zh: function () { return "刚刚同步"; } },
    { re: /^Last synced (\d+)m ago$/, zh: function (m) { return "上次同步于 " + m[1] + " 分钟前"; } },
    { re: /^Last synced (\d+)h ago$/, zh: function (m) { return "上次同步于 " + m[1] + " 小时前"; } },
    { re: /^Last synced (\d+)d ago$/, zh: function (m) { return "上次同步于 " + m[1] + " 天前"; } },
  ];

  function tryRegex(key) {
    for (var i = 0; i < REGEX_RULES.length; i++) {
      var m = REGEX_RULES[i].re.exec(key);
      if (m) return REGEX_RULES[i].zh(m);
    }
    return undefined;
  }

  var dict = DICT;

  // ── 漏翻收集器（内容安全）───────────────────────────────────────────
  // 记录字典里没有、但"长得像界面标签"的字符串，供启动器回收并写入日志，
  // 用于二次翻译。绝不采集用户输入 / 模型输出：
  //   1) 引擎本就跳过 code/pre/textarea/[contenteditable]/svg/[data-no-i18n]；
  //   2) isChromeCandidate() 再按长度与形状严格过滤（短标签才收）。
  // 收集结果只存内存（window.__scoutZhMisses），由启动器主动读取，不落任何网络。
  var misses = (window.__scoutZhMisses = window.__scoutZhMisses || {});

  function isChromeCandidate(s) {
    var t = s.trim();
    if (t.length < 2 || t.length > 40) return false;
    if (!/[A-Za-z]/.test(t)) return false; // 必须含字母
    if (/[\u4e00-\u9fff]/.test(t)) return false; // 含中文：多半是已翻译的译文或中文内容，不回收
    if (t.split(/\s+/).length > 6) return false; // 标签是短语，不是长句
    if (/^[A-Z0-9_]+$/.test(t)) return false; // 常量标识符
    if (/^[a-z][a-zA-Z0-9]*$/.test(t)) return false; // 单个 camelCase token
    if (/^https?:\/\//.test(t) || t.charAt(0) === "/" || t.charAt(0) === "#") return false;
    if (/[{}<>$\\;=]/.test(t)) return false; // 代码/表达式字符
    if (t.indexOf("()") >= 0) return false;
    if (/^[\d\s.,:;!?%$/+\-()]+$/.test(t)) return false; // 纯标点/数字
    if (t.indexOf(",") >= 0) return false; // 含逗号多为个性化整句（如带用户名的问候语），不回收
    if (t.indexOf("_") >= 0) return false; // 多为账号/标识符（如 penzhang_microsoft）
    return true;
  }

  function recordMiss(trimmed) {
    if (!isChromeCandidate(trimmed)) return;
    misses[trimmed] = (misses[trimmed] || 0) + 1;
  }

  function shouldSkip(el) {
    var start = el;
    while (el) {
      if (SKIP_TAGS[el.tagName]) return true;
      if (el.hasAttribute && el.hasAttribute("data-no-i18n")) return true;
      if (el.isContentEditable) return true;
      el = el.parentElement;
    }
    // 内容区护栏：命中即跳过（closest 会沿祖先链匹配）。
    if (start && start.closest && start.closest(CONTENT_DENY)) return true;
    return false;
  }

  // 解析字典条目 —— 支持三种写法（向后兼容）：
  //   1) "译文"                         全局：任意位置精确匹配即翻译（无歧义的界面词用这个）
  //   2) { zh:"译文", scope:"选择器", not:"选择器" }
  //                                     作用域绑定：只有当文本节点位于 scope 容器内、
  //                                     且不在 not 容器内时才翻译（"地址+短语"，消歧义）
  //   3) [ 条目, 条目, ... ]            多作用域：按顺序取第一个命中 scope 的；
  //                                     不带 scope 的条目作为兜底（放最后）
  function resolveEntry(entry, el) {
    if (typeof entry === "string") return entry;
    if (Array.isArray(entry)) {
      for (var i = 0; i < entry.length; i++) {
        var r = resolveEntry(entry[i], el);
        if (r !== undefined) return r;
      }
      return undefined;
    }
    if (entry && typeof entry === "object") {
      if (entry.scope && !(el && el.closest && el.closest(entry.scope))) return undefined;
      if (entry.not && el && el.closest && el.closest(entry.not)) return undefined;
      return entry.zh;
    }
    return undefined;
  }

  function lookup(value, el) {
    var key = value.trim();
    if (!key) return undefined;
    var entry = dict[key];
    if (entry !== undefined) {
      // 键在字典里：交给 resolveEntry 判定当前位置该不该翻、翻成什么。
      // 若 scope 不匹配（该词在此处不该翻），返回 undefined 且不记漏翻——这是有意的。
      var zh = resolveEntry(entry, el);
      if (zh && zh !== key) return zh;
      return undefined;
    }
    // 键完全不在字典：先试正则规则（动态标签），再交给收集器。
    var rx = tryRegex(key);
    if (rx) return rx;
    if (key.length <= 60 && key.indexOf("\n") < 0) recordMiss(key);
    return undefined;
  }

  function translateTextNode(node) {
    var raw = node.nodeValue;
    if (!raw) return;
    var zh = lookup(raw, node.parentElement);
    if (!zh) return;
    var lead = raw.slice(0, raw.length - raw.trimStart().length);
    var trail = raw.slice(raw.trimEnd().length);
    node.nodeValue = lead + zh + trail;
  }

  function translateAttr(el, attr) {
    var cur = el.getAttribute(attr);
    if (!cur) return;
    var zh = lookup(cur, el);
    if (zh) el.setAttribute(attr, zh);
  }

  function translateAllAttrs(el) {
    for (var i = 0; i < ATTRS.length; i++) translateAttr(el, ATTRS[i]);
  }

  function sweep(root) {
    try {
      if (root.nodeType === Node.TEXT_NODE) {
        if (!shouldSkip(root.parentElement)) translateTextNode(root);
        return;
      }
      if (root.nodeType !== Node.ELEMENT_NODE) return;
      // 控件白名单：即便 root 落在禁区内，也先翻译其中已知控件按钮的 aria-label/title。
      sweepControls(root);
      if (shouldSkip(root)) return;

      translateAllAttrs(root);
      var attrEls = root.querySelectorAll("[placeholder],[title],[aria-label],[alt]");
      for (var i = 0; i < attrEls.length; i++) {
        if (!shouldSkip(attrEls[i])) translateAllAttrs(attrEls[i]);
      }

      var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
        acceptNode: function (n) {
          return shouldSkip(n.parentElement)
            ? NodeFilter.FILTER_REJECT
            : NodeFilter.FILTER_ACCEPT;
        },
      });
      var t = walker.nextNode();
      while (t) {
        translateTextNode(t);
        t = walker.nextNode();
      }
    } catch (e) {
      /* fail-safe：绝不让翻译打断渲染 */
    }
  }

  var observer = new MutationObserver(function (muts) {
    for (var i = 0; i < muts.length; i++) {
      var m = muts[i];
      if (m.type === "characterData") {
        var tn = m.target;
        if (tn.nodeType === Node.TEXT_NODE && !shouldSkip(tn.parentElement)) translateTextNode(tn);
      } else if (m.type === "attributes") {
        var el = m.target;
        if (m.attributeName && el.nodeType === Node.ELEMENT_NODE) {
          if (!shouldSkip(el)) translateAttr(el, m.attributeName);
          else translateControlAttrs(el); // 禁区内也翻控件白名单标签
        }
      } else {
        for (var j = 0; j < m.addedNodes.length; j++) sweep(m.addedNodes[j]);
      }
    }
  });

  function start() {
    sweep(document.body);
    observer.observe(document.documentElement, {
      subtree: true,
      childList: true,
      characterData: true,
      attributes: true,
      attributeFilter: ATTRS,
    });
  }

  window.__scoutZhActive = {
    updateDict: function (d) {
      dict = d;
      sweep(document.body);
    },
    stop: function () {
      observer.disconnect();
      window.__scoutZhActive = null;
    },
  };

  if (document.body) start();
  else document.addEventListener("DOMContentLoaded", start, { once: true });

  return "scout-zh injected: " + Object.keys(dict).length + " entries";
})();
