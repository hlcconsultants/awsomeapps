'use strict';

const send = (msg) => chrome.runtime.sendMessage(msg);

function describe(m) {
  if (m.kind !== 'http') return `${m.kind.toUpperCase()} stream`;
  let name = 'Media file';
  try { name = decodeURIComponent(new URL(m.url).pathname.split('/').pop()) || name; } catch {}
  return m.size ? `${name} (${(m.size / 1048576).toFixed(1)} MB)` : name;
}

(async () => {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });

  const status = await send({ type: 'status' });
  document.getElementById('status').textContent = status.reply.ok ? 'Connected' : 'App not found';

  const toggle = document.getElementById('intercept');
  toggle.checked = status.intercept;
  toggle.addEventListener('change', () => send({ type: 'setIntercept', value: toggle.checked }));

  const media = (await send({ type: 'list', tabId: tab.id })) || [];
  document.getElementById('empty').style.display = media.length ? 'none' : 'block';

  const list = document.getElementById('list');
  for (const m of media) {
    const row = document.createElement('div');
    row.className = 'row';
    const name = document.createElement('span');
    name.className = 'name';
    name.textContent = describe(m);
    name.title = m.url;
    const btn = document.createElement('button');
    btn.textContent = 'Download';
    btn.addEventListener('click', async () => {
      const reply = await send({ type: 'grab', tabId: tab.id, media: m });
      btn.textContent = reply && reply.ok ? 'Sent' : 'Failed';
    });
    row.append(name, btn);
    list.appendChild(row);
  }
})();
