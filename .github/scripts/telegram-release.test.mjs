import test from 'node:test';
import assert from 'node:assert/strict';
import { buildMessages, destination, sendMessages } from './telegram-release.mjs';

const release = {
  name: 'CtxRelay v1.2.0', tag_name: 'v1.2.0',
  html_url: 'https://github.com/example/dash/releases/tag/v1.2.0',
  body: '# CtxRelay 1.2.0\n\nShort introduction.\n\n## Added\n- **Remote** control.\n\n## Fixed\n- Keep `a_b` intact.',
  assets: [
    { name: 'CtxRelay_Setup_1.2.0.exe', browser_download_url: 'https://example.com/setup.exe' },
    { name: 'CtxRelay_1.2.0_portable.zip', browser_download_url: 'https://example.com/portable.zip' },
  ],
};

test('test is the default; prod is explicit and unknown modes fail closed', () => {
  assert.deepEqual(destination(), { chat_id: '-1004208623621', message_thread_id: 1144 });
  assert.deepEqual(destination('prod'), { chat_id: '-1002225536706', message_thread_id: 366 });
  assert.throws(() => destination('production'), /mode/i);
});

test('release introduction and complete sections stay visible with working download URLs', () => {
  const [message] = buildMessages(release);
  assert.match(message.rich_message.markdown, /^# CtxRelay v1\.2\.0\n\nShort introduction\./);
  assert.match(message.rich_message.markdown, /## Added\n\n- \*\*Remote\*\* control\./);
  assert.doesNotMatch(message.rich_message.markdown, /<details>|<summary>/);
  assert.match(message.rich_message.markdown, /Keep `a_b` intact/);
  assert.equal(message.reply_markup.inline_keyboard[0][0].url, 'https://example.com/setup.exe');
  assert.equal(message.reply_markup.inline_keyboard[0][1].url, 'https://example.com/portable.zip');
});

test('a flat changelog stays visible and missing files do not produce download buttons', () => {
  const [message] = buildMessages({ ...release, body: '# CtxRelay 1.2.0\n\n- First\n- Second', assets: [] });
  assert.match(message.rich_message.markdown, /^# CtxRelay v1\.2\.0\n\n- First\n- Second$/);
  assert.match(message.rich_message.markdown, /- First\n- Second/);
  assert.equal(message.reply_markup.inline_keyboard.flat().length, 2);
});

test('CtxRelay executable downloads and product site are linked', () => {
  const [message] = buildMessages({ ...release, assets: [
    { name: 'CtxRelay.exe', browser_download_url: 'https://example.com/CtxRelay.exe' },
  ] });
  const buttons = message.reply_markup.inline_keyboard.flat();
  assert.equal(buttons.find(b => b.text === 'CtxRelay.exe').url, 'https://example.com/CtxRelay.exe');
  assert.equal(buttons.find(b => b.text === 'Документация').url, 'https://ctxrelay.z3n.pro/');
});

test('headings in code fences stay in the same section', () => {
  const [message] = buildMessages({ ...release, body: '## Added\n```sh\n# example\n```\nEnd.' });
  assert.equal((message.rich_message.markdown.match(/^## Added$/gm) || []).length, 1);
  assert.match(message.rich_message.markdown, /```sh\n# example\n```\nEnd\./);
});

test('download sections are omitted while buttons and following changes remain', () => {
  const body = '## Added\n- Keep this.\n\n## Downloads\n- Installer description.\n\n## Fixed\n- Keep this too.';
  const [message] = buildMessages({ ...release, body });
  assert.doesNotMatch(message.rich_message.markdown, /Downloads|Installer description/);
  assert.match(message.rich_message.markdown, /Keep this\./);
  assert.match(message.rich_message.markdown, /Keep this too\./);
  assert.equal(message.reply_markup.inline_keyboard[0][0].url, 'https://example.com/setup.exe');
});

test('several short sections fit together without hiding download-related changes', () => {
  const body = '## Added\n- A\n\n## Fixed\n- B\n\n## Download fixes\n- C\n\n## Загрузки\n- Duplicate\n\n## Notes\n- D';
  const messages = buildMessages({ ...release, body });
  assert.equal(messages.length, 1);
  assert.match(messages[0].rich_message.markdown, /## Download fixes\n\n- C/);
  assert.doesNotMatch(messages[0].rich_message.markdown, /Загрузки|Duplicate/);
});

test('long sections are split without losing text or leaving unbalanced code fences', () => {
  const lines = Array.from({ length: 500 }, (_, i) => `line ${i}: ${'x'.repeat(90)}`);
  const messages = buildMessages({ ...release, body: `## Code\n\n\`\`\`txt\n${lines.join('\n')}\n\`\`\`` });
  assert.ok(messages.length > 1);
  const combined = messages.map(m => m.rich_message.markdown).join('\n');
  for (const line of lines) assert.equal(combined.split(line + '\n').length - 1, 1);
  for (const message of messages) {
    assert.ok(message.rich_message.markdown.length < 30000);
    assert.equal((message.rich_message.markdown.match(/^```/gm) || []).length % 2, 0);
    assert.doesNotMatch(message.rich_message.markdown, /<details>|<summary>/);
  }
});

test('raw release HTML is escaped while Markdown is retained', () => {
  const [message] = buildMessages({ ...release, body: '## Added </summary>\nLiteral </details> & <tag>.' });
  assert.match(message.rich_message.markdown, /Added &lt;\/summary&gt;/);
  assert.match(message.rich_message.markdown, /Literal &lt;\/details&gt; &amp; &lt;tag&gt;/);
});

test('escaped HTML still fits the message limit and code keeps literal characters', () => {
  const messages = buildMessages({ ...release, body: '## Data\n' + '&'.repeat(14000) });
  for (const message of messages) assert.ok(message.rich_message.markdown.length < 30000);
  const [code] = buildMessages({ ...release, body: '## Code\n```xml\n<tag>&value</tag>\n```' });
  assert.match(code.rich_message.markdown, /```xml\n<tag>&value<\/tag>\n```/);
});

test('empty notes still produce a release link; drafts cannot be announced', () => {
  assert.match(buildMessages({ ...release, body: '' })[0].rich_message.markdown, /v1\.2\.0/);
  assert.throws(() => buildMessages({ ...release, draft: true }), /draft/i);
});

test('sender delivers a rich message to the chosen topic', async () => {
  const requests = [];
  const fetcher = async (url, options) => {
    requests.push({ url, body: JSON.parse(options.body) });
    return { ok: true, json: async () => ({ ok: true, result: { message_id: 45 } }) };
  };
  await sendMessages('fake-token', buildMessages(release), destination('prod'), fetcher);
  assert.equal(requests.length, 1);
  assert.equal(requests[0].url, 'https://api.telegram.org/botfake-token/sendRichMessage');
  assert.equal(requests[0].body.chat_id, '-1002225536706');
  assert.equal(requests[0].body.message_thread_id, 366);
  assert.match(requests[0].body.rich_message.markdown, /## Added/);
});

test('unknown delivery is not retried and does not leak the token', async () => {
  let attempts = 0;
  const fetcher = async () => { attempts++; throw new Error('https://api.telegram.org/botfake-token'); };
  await assert.rejects(sendMessages('fake-token', buildMessages(release), destination(), fetcher), error => {
    assert.match(error.message, /delivery unknown/);
    assert.doesNotMatch(error.message, /fake-token/);
    return true;
  });
  assert.equal(attempts, 1);
});

test('Telegram API rejection fails the run and redacts its error', async () => {
  const fetcher = async () => ({ ok: false, status: 400,
    json: async () => ({ ok: false, description: 'rejected fake-token' }) });
  await assert.rejects(sendMessages('fake-token', buildMessages(release), destination(), fetcher), error => {
    assert.match(error.message, /HTTP 400/);
    assert.doesNotMatch(error.message, /fake-token/);
    return true;
  });
  await assert.rejects(sendMessages('', buildMessages(release), destination(), fetcher), /not configured/);
});
