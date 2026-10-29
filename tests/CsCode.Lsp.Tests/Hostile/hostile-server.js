#!/usr/bin/env node
// A deliberately hostile LSP server used by the CsCode.Lsp hostile-load suite. It speaks just enough LSP over stdio to be started, and then
// misbehaves according to HOSTILE_MODE:
//   normal  answers everything promptly (control)
//   cpu     spawns CPU-burning child processes, otherwise normal
//   slow    answers every request (shutdown included) only after 5 seconds
//   flood   after `initialized` writes ~100 MB of window/logMessage + publishDiagnostics notifications, otherwise normal
//   stderr  after `initialized` writes ~100 MB to stderr, otherwise normal
//   giant   completion returns 50,000 items, semanticTokens returns 1,000,000 tokens
//   silent  answers initialize, never answers anything else (not even shutdown)
//   crash   spawns a grandchild `sleep`, reports its pid on stderr, then SIGKILLs itself right after `initialized`
'use strict';
const { spawn } = require('child_process');

const mode = process.env.HOSTILE_MODE || 'normal';
let buffer = Buffer.alloc(0);

function frame(obj) {
  const body = Buffer.from(typeof obj === 'string' ? obj : JSON.stringify(obj));
  return Buffer.concat([Buffer.from(`Content-Length: ${body.length}\r\n\r\n`), body]);
}
function send(obj) { process.stdout.write(frame(obj)); }
function respond(id, result) { send({ jsonrpc: '2.0', id, result }); }
function log(line) { process.stderr.write(line + '\n'); }

const capabilities = {
  textDocumentSync: 2,
  hoverProvider: true,
  completionProvider: { triggerCharacters: ['.'] },
  semanticTokensProvider: { legend: { tokenTypes: ['class', 'function', 'variable'], tokenModifiers: [] }, full: true },
};

process.stdin.on('data', (chunk) => {
  buffer = Buffer.concat([buffer, chunk]);
  for (;;) {
    const headerEnd = buffer.indexOf('\r\n\r\n');
    if (headerEnd < 0) return;
    const header = buffer.slice(0, headerEnd).toString('ascii');
    const m = /Content-Length: (\d+)/i.exec(header);
    if (!m) { buffer = buffer.slice(headerEnd + 4); continue; }
    const len = parseInt(m[1], 10);
    if (buffer.length < headerEnd + 4 + len) return;
    const body = buffer.slice(headerEnd + 4, headerEnd + 4 + len).toString('utf8');
    buffer = buffer.slice(headerEnd + 4 + len);
    let msg;
    try { msg = JSON.parse(body); } catch { continue; }
    handle(msg);
  }
});
process.stdin.on('end', () => process.exit(0));

function handle(msg) {
  switch (msg.method) {
    case 'initialize':
      respond(msg.id, { capabilities, serverInfo: { name: 'hostile-' + mode, version: '1' } });
      return;
    case 'initialized':
      onInitialized();
      return;
    case 'shutdown':
      if (mode === 'silent') return;
      if (mode === 'slow') { setTimeout(() => respond(msg.id, null), 5000); return; }
      respond(msg.id, null);
      return;
    case 'exit':
      process.exit(0);
      return;
    default:
      if (msg.id !== undefined) onRequest(msg);
  }
}

function onInitialized() {
  log(`hostile-server pid=${process.pid} mode=${mode}`);
  if (mode === 'cpu') {
    for (let i = 0; i < 2; i++) {
      const burner = spawn(process.execPath, ['-e', 'for(;;){}'], { stdio: 'ignore' });
      log(`burner ${burner.pid}`);
    }
  } else if (mode === 'flood') {
    flood();
  } else if (mode === 'stderr') {
    stderrFlood();
  } else if (mode === 'crash') {
    const sleeper = spawn('sleep', ['600'], { stdio: 'ignore' });
    log(`grandchild ${sleeper.pid}`);
    // a hard crash: the grandchild is orphaned (re-parented) and must still be reaped by the client
    setTimeout(() => process.kill(process.pid, 'SIGKILL'), 50);
  }
}

function onRequest(msg) {
  if (mode === 'silent') return;
  const reply = () => respond(msg.id, resultFor(msg));
  if (mode === 'slow') setTimeout(reply, 5000);
  else reply();
}

function resultFor(msg) {
  switch (msg.method) {
    case 'textDocument/hover':
      return { contents: { kind: 'plaintext', value: 'ok' } };
    case 'textDocument/completion': {
      const n = mode === 'giant' ? 50000 : 3;
      const items = new Array(n);
      for (let i = 0; i < n; i++) items[i] = { label: 'item' + i, kind: 5, detail: 'detail text for item ' + i };
      return { isIncomplete: false, items };
    }
    case 'textDocument/semanticTokens/full': {
      const tokens = mode === 'giant' ? 1000000 : 10;
      const data = new Array(tokens * 5);
      for (let i = 0; i < tokens; i++) { data[i * 5] = i % 50 === 0 ? 1 : 0; data[i * 5 + 1] = (i * 3) % 80; data[i * 5 + 2] = 3; data[i * 5 + 3] = i % 3; data[i * 5 + 4] = 0; }
      return { resultId: 'r' + Date.now(), data };
    }
    default:
      return null;
  }
}

function stderrFlood() {
  const target = 100 * 1024 * 1024;
  let written = 0;
  let i = 0;
  const line = 'x'.repeat(1000);
  function step() {
    let ok = true;
    while (ok && written < target) {
      i++;
      const s = line + i + '\n';
      written += s.length;
      ok = process.stderr.write(s);
    }
    if (written >= target) { log('stderr-done ' + i); return; }
    process.stderr.once('drain', step);
  }
  step();
}

function flood() {
  const target = 100 * 1024 * 1024;
  let written = 0;
  let i = 0;
  const filler = 'x'.repeat(900);
  function step() {
    let ok = true;
    while (ok && written < target) {
      i++;
      const msg = i % 2 === 0
        ? { jsonrpc: '2.0', method: 'window/logMessage', params: { type: 4, message: filler + i } }
        : { jsonrpc: '2.0', method: 'textDocument/publishDiagnostics', params: { uri: 'file:///flood/f' + (i % 8000) + '.hostile', diagnostics: [{ range: { start: { line: 0, character: 0 }, end: { line: 0, character: 1 } }, message: filler.slice(0, 200) + i }] } };
      const f = frame(msg);
      written += f.length;
      ok = process.stdout.write(f);
    }
    if (written >= target) { log('flood-done ' + i); return; }
    process.stdout.once('drain', step);
  }
  step();
}
