import { readFileSync, writeFileSync, mkdirSync, appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export function destination(mode = 'test', env = {}) {
  if (!['test', 'prod'].includes(mode)) throw new Error(`Unknown Telegram mode: ${mode}`);
  const prefix = `TELEGRAM_${mode.toUpperCase()}_`;
  const chat_id = env[prefix + 'CHAT_ID'] || (mode === 'test' ? '-1004208623621' : '-1002225536706');
  const topic = env[prefix + 'TOPIC_ID'] || (mode === 'test' ? '1144' : '366');
  if (!/^-100\d+$/.test(chat_id) || !/^\d+$/.test(topic) || Number(topic) < 1)
    throw new Error('Invalid Telegram group or topic ID');
  return { chat_id, message_thread_id: Number(topic) };
}

const escapeHtml = text => text.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');

function safeMarkdown(text) {
  let fence = null;
  return text.split('\n').map(line => {
    const marker = line.match(/^\s{0,3}(`{3,}|~{3,})(.*)$/);
    if (marker) {
      if (!fence) fence = marker[1];
      else if (marker[1][0] === fence[0] && marker[1].length >= fence.length && !marker[2].trim()) fence = null;
      return line;
    }
    if (fence) return line;
    // Escape raw HTML outside inline code without changing Markdown formatting.
    return line.split(/(`+[^`]*`+)/g).map(part => part.startsWith('`') ? part : escapeHtml(part)).join('');
  }).join('\n');
}

function sections(body) {
  const result = [{ title: '', lines: [] }];
  let fence = null;
  for (const line of body.replace(/\r\n?/g, '\n').split('\n')) {
    const marker = line.match(/^\s{0,3}(`{3,}|~{3,})/);
    if (marker) {
      if (!fence) fence = marker[1];
      else if (marker[1][0] === fence[0] && marker[1].length >= fence.length) fence = null;
    }
    const heading = !fence && !marker && line.match(/^#{1,2}\s+(.+?)\s*#*$/);
    if (heading) result.push({ title: heading[1], lines: [] });
    else result.at(-1).lines.push(line);
  }
  return result.map(s => ({ title: s.title, text: s.lines.join('\n').trim() }));
}

// Keep each fragment below both Telegram's character and block limits.
// Close/reopen fenced code when a long section crosses a message boundary.
function fragments(text) {
  const parts = [];
  let current = '', fence = null, lineCount = 0;
  for (const original of text.split('\n')) {
    // A single huge line still needs a bounded payload (Unicode code points).
    const chars = original.match(/&(?:amp|lt|gt);|[\s\S]/gu) || [];
    const lines = [];
    do { lines.push(chars.splice(0, 2000).join('')); } while (chars.length);
    for (const line of lines) {
      if (current.length + line.length > 18000 || lineCount >= 150) {
        parts.push(current + (fence ? '\n' + fence.close : ''));
        current = fence ? fence.open + '\n' : '';
        lineCount = 0;
      }
      current += line + '\n';
      lineCount++;
      const marker = line.match(/^\s{0,3}(`{3,}|~{3,})(.*)$/);
      if (marker) {
        if (!fence) fence = { open: line, close: marker[1] };
        else if (marker[1][0] === fence.close[0] && marker[1].length >= fence.close.length && !marker[2].trim()) fence = null;
      }
    }
  }
  if (current.trim()) parts.push(current.trimEnd() + (fence ? '\n' + fence.close : ''));
  return parts;
}

function keyboard(release) {
  const downloads = [];
  const assets = release.assets || [];
  const installer = assets.find(a => /setup.*\.exe$/i.test(a.name));
  const portable = assets.find(a => /portable.*\.zip$/i.test(a.name));
  const executable = assets.find(a => /^CtxRelay\.exe$/i.test(a.name));
  if (installer) downloads.push({ text: 'Установщик', url: installer.browser_download_url });
  if (portable) downloads.push({ text: 'Portable', url: portable.browser_download_url });
  if (executable) downloads.push({ text: 'CtxRelay.exe', url: executable.browser_download_url });
  return { inline_keyboard: [
    ...(downloads.length ? [downloads] : []),
    [{ text: 'Полный релиз', url: release.html_url }, { text: 'Документация', url: 'https://ctxrelay.z3n.pro/' }],
  ] };
}

export function buildMessages(release) {
  if (release.draft) throw new Error('Cannot announce a draft release');
  const title = release.name || `CtxRelay ${release.tag_name}`;
  let parsed = sections(release.body || '');
  // Strip only a redundant opening product/version heading, preserving its text.
  const first = parsed.findIndex(s => s.title);
  if (first >= 0 && !parsed[0].text && /^CtxRelay\s+v?\d/i.test(parsed[first].title)) {
    parsed[first].title = '';
    parsed = parsed.slice(first);
  }
  let intro = '';
  if (!parsed[0].title) {
    const paragraphs = parsed[0].text.split(/\n\s*\n/);
    if (paragraphs[0] && !/^\s*(?:[-*+] |\d+[.)] |```|~~~)/.test(paragraphs[0]) && paragraphs[0].length <= 700) {
      intro = paragraphs.shift();
      parsed[0].text = paragraphs.join('\n\n');
    }
  }
  const header = `# ${escapeHtml(title)}\n\n${intro ? safeMarkdown(intro) + '\n\n' : ''}`;
  const blocks = parsed.filter(s => s.text && !/^(downloads?|загрузки|скачать|скачивание)$/i.test(s.title.trim()))
    .flatMap(s => fragments(safeMarkdown(s.text)).map(part =>
      `${s.title ? `## ${escapeHtml(s.title)}\n\n` : ''}${part}`));
  const messages = [];
  let markdown = header;
  let count = 0;
  for (const block of blocks) {
    const lines = block.split('\n').length;
    if (markdown.length + block.length > 24000 || count + lines > 350) {
      messages.push({ rich_message: { markdown: markdown.trim() }, reply_markup: keyboard(release) });
      markdown = header;
      count = 0;
    }
    markdown += block + '\n\n';
    count += lines;
  }
  messages.push({ rich_message: { markdown: markdown.trim() }, reply_markup: keyboard(release) });
  return messages;
}

export async function sendMessages(token, messages, target, fetcher = fetch) {
  if (!token) throw new Error('TELEGRAM_BOT_TOKEN is not configured');
  for (let i = 0; i < messages.length; i++) {
    let response;
    try {
      response = await fetcher(`https://api.telegram.org/bot${token}/sendRichMessage`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ...target, ...messages[i] }),
        signal: AbortSignal.timeout(30000),
      });
    } catch {
      // Do not print fetch errors: they may contain the bot token in the URL.
      throw new Error(`Telegram message ${i + 1}: network error; delivery unknown. Do not blindly rerun.`);
    }
    let result;
    try { result = await response.json(); } catch { result = {}; }
    if (!response.ok || !result.ok) {
      const description = String(result.description || 'Invalid Telegram response').replaceAll(token, '[redacted]');
      throw new Error(`Telegram message ${i + 1}: HTTP ${response.status}; ${description}. Earlier messages may already be sent.`);
    }
    console.log(`Telegram message ${i + 1}/${messages.length} sent: ${result.result.message_id}`);
    if (i + 1 < messages.length) await new Promise(resolve => setTimeout(resolve, 3100));
  }
}

async function main() {
  const mode = process.env.TELEGRAM_MODE || 'test';
  const target = destination(mode, process.env);
  const output = 'artifacts/telegram-release';
  if (process.argv.includes('--send')) {
    const saved = JSON.parse(readFileSync(`${output}/payload.json`, 'utf8'));
    if (saved.mode !== mode || JSON.stringify(saved.target) !== JSON.stringify(target))
      throw new Error('Telegram destination changed after preview');
    await sendMessages(process.env.TELEGRAM_BOT_TOKEN, saved.messages, target);
    return;
  }
  let release;
  const fileIndex = process.argv.indexOf('--release-json');
  if (fileIndex >= 0) release = JSON.parse(readFileSync(process.argv[fileIndex + 1], 'utf8').replace(/^\uFEFF/, ''));
  else {
    const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'));
    release = event.release;
    if (!release) {
      const tag = process.env.RELEASE_TAG;
      const suffix = tag ? `tags/${encodeURIComponent(tag)}` : 'latest';
      const response = await fetch(`https://api.github.com/repos/${process.env.GITHUB_REPOSITORY}/releases/${suffix}`, {
        headers: { Authorization: `Bearer ${process.env.GH_TOKEN}`, Accept: 'application/vnd.github+json' },
        signal: AbortSignal.timeout(30000),
      });
      if (!response.ok) throw new Error(`GitHub release lookup failed: HTTP ${response.status}`);
      release = await response.json();
    }
  }
  const messages = buildMessages(release);
  mkdirSync(output, { recursive: true });
  writeFileSync(`${output}/payload.json`, JSON.stringify({ mode, target, messages }, null, 2));
  const preview = messages.map(m => m.rich_message.markdown).join('\n\n---\n\n');
  writeFileSync(`${output}/preview.md`, preview);
  if (process.env.GITHUB_STEP_SUMMARY)
    appendFileSync(process.env.GITHUB_STEP_SUMMARY, `\nTelegram: **${mode}**, group \`${target.chat_id}\`, topic \`${target.message_thread_id}\`.\n\n${preview}\n`);
  console.log(`Prepared ${messages.length} rich message(s) for ${mode}: ${target.chat_id}/${target.message_thread_id}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch(error => { console.error(error.message); process.exitCode = 1; });
}
