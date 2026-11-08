'use strict';

const HOST = 'com.fastget.host';
const MIN_MEDIA_BYTES = 512 * 1024;
const MEDIA_EXT = /\.(mp4|m4v|webm|mkv|mov|flv|mp3|m4a|aac|ogg|opus|flac|wav)$/;
const SEGMENT_EXT = /\.(ts|m4s|cmfv|cmfa)$/;
// Sites that deliver video as range-limited chunks, so a captured URL is only a fragment.
const CHUNKED_HOSTS = /(^|\.)googlevideo\.com$/;

function native(msg) {
  return new Promise((resolve) => {
    chrome.runtime.sendNativeMessage(HOST, msg, (reply) => {
      if (chrome.runtime.lastError) resolve({ ok: false, error: chrome.runtime.lastError.message });
      else resolve(reply || { ok: false, error: 'No reply from FastGet' });
    });
  });
}

async function cookieHeader(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join('; ');
  } catch {
    return '';
  }
}

async function sendToApp({ url, fileName, referrer, kind }) {
  return native({
    cmd: 'add',
    url,
    fileName: fileName || undefined,
    referrer: referrer || undefined,
    kind: kind || undefined,
    cookie: (await cookieHeader(url)) || undefined,
    userAgent: navigator.userAgent,
  });
}

async function interceptEnabled() {
  const { intercept = true } = await chrome.storage.local.get('intercept');
  return intercept;
}

// ---- browser download takeover ----

chrome.downloads.onCreated.addListener(async (item) => {
  if (item.state !== 'in_progress' || item.byExtensionId === chrome.runtime.id) return;
  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url) || !(await interceptEnabled())) return;

  // Hold Chrome's copy until FastGet accepts it, so nothing is lost if the app isn't installed.
  await chrome.downloads.pause(item.id).catch(() => {});
  const reply = await sendToApp({ url, referrer: item.referrer });
  if (reply.ok) {
    await chrome.downloads.cancel(item.id).catch(() => {});
    await chrome.downloads.erase({ id: item.id }).catch(() => {});
  } else {
    await chrome.downloads.resume(item.id).catch(() => {});
  }
});

// ---- per-tab media list (kept in session storage because the service worker sleeps) ----

let queue = Promise.resolve();
const serialize = (fn) => (queue = queue.then(fn, fn));
const key = (tabId) => `media:${tabId}`;

async function readMedia(tabId) {
  const k = key(tabId);
  return (await chrome.storage.session.get(k))[k] || [];
}

function setBadge(tabId, count) {
  chrome.action.setBadgeBackgroundColor({ tabId, color: '#1d4ed8' }).catch(() => {});
  chrome.action.setBadgeText({ tabId, text: count ? String(count) : '' }).catch(() => {});
}

function addMedia(tabId, entry) {
  return serialize(async () => {
    const list = await readMedia(tabId);
    if (list.some((m) => m.url === entry.url || m.ignored === entry.url)) return;
    list.push(entry);
    await chrome.storage.session.set({ [key(tabId)]: list });
    setBadge(tabId, list.filter((m) => m.url).length);
  });
}

function clearMedia(tabId) {
  return serialize(async () => {
    await chrome.storage.session.remove(key(tabId));
    setBadge(tabId, 0);
  });
}

function header(headers, name) {
  const h = headers.find((x) => x.name.toLowerCase() === name);
  return h ? h.value || '' : '';
}

function classify(url, headers) {
  let parsed;
  try { parsed = new URL(url); } catch { return null; }
  if (!/^https?:$/.test(parsed.protocol) || CHUNKED_HOSTS.test(parsed.hostname)) return null;

  const path = parsed.pathname.toLowerCase();
  const type = header(headers, 'content-type').toLowerCase();

  if (path.endsWith('.m3u8') || type.includes('mpegurl')) return { kind: 'hls', size: 0 };
  if (path.endsWith('.mpd') || type.includes('dash+xml')) return { kind: 'dash', size: 0 };
  if (SEGMENT_EXT.test(path) || type.includes('mp2t')) return null;

  if (type.startsWith('video/') || type.startsWith('audio/') || MEDIA_EXT.test(path)) {
    const range = header(headers, 'content-range').match(/\/(\d+)$/);
    const size = range ? Number(range[1]) : Number(header(headers, 'content-length')) || 0;
    return size >= MIN_MEDIA_BYTES ? { kind: 'http', size } : null;
  }
  return null;
}

// A master playlist lists its quality variants; remember them so they don't show as separate videos.
async function ignoreHlsVariants(tabId, url) {
  try {
    const text = await (await fetch(url, { credentials: 'include' })).text();
    if (!text.includes('#EXT-X-STREAM-INF') && !text.includes('#EXT-X-MEDIA:')) return;
    const children = [];
    for (const line of text.split('\n').map((l) => l.trim())) {
      if (line && !line.startsWith('#')) children.push(new URL(line, url).href);
      for (const m of line.matchAll(/URI="([^"]+)"/g)) children.push(new URL(m[1], url).href);
    }
    await serialize(async () => {
      const k = key(tabId);
      let list = await readMedia(tabId);
      list = list.filter((m) => !children.includes(m.url));
      for (const c of children) list.push({ ignored: c });
      await chrome.storage.session.set({ [k]: list });
      setBadge(tabId, list.filter((m) => m.url).length);
    });
  } catch { /* the entry stays listed; harmless */ }
}

chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (details.tabId < 0) return;
    const found = classify(details.url, details.responseHeaders || []);
    if (!found) return;
    addMedia(details.tabId, { url: details.url, kind: found.kind, size: found.size, at: Date.now() }).then(() => {
      if (found.kind === 'hls') ignoreHlsVariants(details.tabId, details.url);
    });
  },
  { urls: ['<all_urls>'], types: ['media', 'xmlhttprequest', 'other'] },
  ['responseHeaders']
);

chrome.tabs.onRemoved.addListener((tabId) => { clearMedia(tabId); });
chrome.tabs.onUpdated.addListener((tabId, change) => { if (change.url) clearMedia(tabId); });

async function listMedia(tabId) {
  return (await readMedia(tabId)).filter((m) => m.url);
}

function titleToName(title) {
  return (title || '').replace(/[\\/:*?"<>|]+/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 120);
}

async function grab(tabId, media) {
  const tab = await chrome.tabs.get(tabId);
  const fileName = media.kind === 'http' ? undefined : titleToName(tab.title);
  return sendToApp({ url: media.url, fileName, referrer: tab.url, kind: media.kind });
}

function bestMedia(list) {
  return list.find((m) => m.kind === 'hls') || list.find((m) => m.kind === 'dash')
    || [...list].sort((a, b) => b.size - a.size)[0];
}

// ---- right-click menu ----

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: 'fg-link', title: 'Download with FastGet', contexts: ['link'] });
    chrome.contextMenus.create({ id: 'fg-media', title: 'Download with FastGet', contexts: ['video', 'audio', 'image'] });
  });
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const url = info.menuItemId === 'fg-link' ? info.linkUrl : info.srcUrl;
  if (url && /^https?:/i.test(url)) {
    await sendToApp({ url, referrer: info.pageUrl });
  } else if (tab) {
    // blob: sources are player-assembled streams; use what the network sniffer saw.
    const best = bestMedia(await listMedia(tab.id));
    if (best) await grab(tab.id, best);
  }
});

// ---- popup and page button ----

chrome.runtime.onMessage.addListener((msg, sender, respond) => {
  (async () => {
    const tabId = msg.tabId ?? sender.tab?.id;
    switch (msg.type) {
      case 'list': return respond(await listMedia(tabId));
      case 'grab': return respond(await grab(tabId, msg.media));
      case 'status': return respond({ reply: await native({ cmd: 'ping' }), intercept: await interceptEnabled() });
      case 'setIntercept':
        await chrome.storage.local.set({ intercept: !!msg.value });
        return respond({ ok: true });
      default: return respond(null);
    }
  })();
  return true;
});
