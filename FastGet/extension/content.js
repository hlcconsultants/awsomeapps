'use strict';

// Shows a small "Download" button on the largest visible video once the network sniffer has found a stream.
(() => {
  const MIN_W = 200, MIN_H = 120;
  let host, button, menu, current = null;

  function build() {
    host = document.createElement('div');
    host.style.cssText = 'position:fixed;z-index:2147483647;top:0;left:0;display:none;';
    const root = host.attachShadow({ mode: 'closed' });
    const style = document.createElement('style');
    style.textContent = `
      button{font:600 12px system-ui,sans-serif;color:#fff;background:#1d4ed8;border:0;border-radius:6px;padding:6px 10px;cursor:pointer;opacity:.85}
      button:hover{opacity:1}
      .menu{position:absolute;top:32px;right:0;background:#fff;border-radius:6px;box-shadow:0 4px 16px #0005;min-width:220px;max-height:240px;overflow:auto}
      .menu div{font:12px system-ui,sans-serif;color:#111;padding:8px 10px;cursor:pointer;white-space:nowrap}
      .menu div:hover{background:#e8efff}`;
    button = document.createElement('button');
    button.textContent = '⬇ Download video';
    button.addEventListener('click', onClick);
    menu = document.createElement('div');
    menu.className = 'menu';
    menu.style.display = 'none';
    root.append(style, button, menu);
    document.documentElement.appendChild(host);
  }

  const send = (msg) => new Promise((res) => {
    try { chrome.runtime.sendMessage(msg, (r) => { void chrome.runtime.lastError; res(r); }); }
    catch { res(null); } // extension was reloaded
  });

  const label = (m) => {
    const size = m.size ? ` – ${(m.size / 1048576).toFixed(1)} MB` : '';
    return m.kind === 'http' ? `Video file${size}` : `${m.kind.toUpperCase()} stream (best quality)`;
  };

  async function grab(media) {
    menu.style.display = 'none';
    const reply = await send({ type: 'grab', media });
    button.textContent = reply && reply.ok ? '✓ Sent to FastGet' : 'FastGet is not available';
    setTimeout(() => { button.textContent = '⬇ Download video'; }, 2500);
  }

  async function onClick() {
    const list = (await send({ type: 'list' })) || [];
    if (list.length === 1) return grab(list[0]);
    menu.replaceChildren(...list.map((m) => {
      const row = document.createElement('div');
      row.textContent = label(m);
      row.addEventListener('click', () => grab(m));
      return row;
    }));
    menu.style.display = menu.style.display === 'none' ? 'block' : 'none';
  }

  function largestVideo() {
    let best = null, area = 0;
    for (const v of document.querySelectorAll('video')) {
      const r = v.getBoundingClientRect();
      if (r.width < MIN_W || r.height < MIN_H || r.bottom < 0 || r.top > innerHeight) continue;
      if (r.width * r.height > area) { area = r.width * r.height; best = v; }
    }
    return best;
  }

  async function update() {
    if (document.hidden) return;
    const video = largestVideo();
    if (!video) {
      current = null;
      if (host) host.style.display = 'none';
      return;
    }
    if (video !== current) {
      const list = (await send({ type: 'list' })) || [];
      if (!list.length) return;
      current = video;
    }
    if (!host) build();
    const r = video.getBoundingClientRect();
    host.style.display = 'block';
    host.style.top = `${Math.max(4, r.top + 8)}px`;
    host.style.left = `${Math.max(4, r.right - 140)}px`;
  }

  setInterval(update, 1000);
})();
