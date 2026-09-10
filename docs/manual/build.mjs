// Build the ANAN Core Operator's Manual PDF from the chapter sources.
// Cross-platform successor to build.sh's body (build.sh is now a thin wrapper
// around this): the Pages deploy runs from Windows, where a bash-only builder
// meant the hosted client could never carry the manual. Same semantics:
//   node build.mjs [output.pdf]     (default: build/Zeus-Operator-Manual.pdf)
// Env:
//   CHROME                    — explicit path to a Chromium-family binary
//   MANUAL_CHROME_EXTRA_FLAGS — extra print-engine flags (CI: --no-sandbox
//                               --disable-dev-shm-usage)
//   MANUAL_SKIP_PDF=1         — assemble HTML only (dev/test without Chrome)
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const OUTPDF = resolve(process.argv[2] || join(HERE, 'build', 'Zeus-Operator-Manual.pdf'));
mkdirSync(join(HERE, 'build'), { recursive: true });

// Markdown engine (kept out of git; see .gitignore).
if (!existsSync(join(HERE, 'node_modules', 'marked'))) {
  execFileSync('npm', ['install', '--silent', '--no-audit', '--no-fund'], {
    cwd: HERE, stdio: 'inherit', shell: process.platform === 'win32',
  });
}

execFileSync(process.execPath, [join(HERE, 'assemble.mjs')], { stdio: 'inherit' });

if (process.env.MANUAL_SKIP_PDF === '1') {
  console.log('MANUAL_SKIP_PDF=1 — assembled HTML only, no PDF printed.');
  process.exit(0);
}

// Locate a Chromium-family browser for the print engine. $CHROME wins; then
// the platform's standard install paths; then PATH lookups.
function which(cmd) {
  const probe = process.platform === 'win32' ? ['where', [cmd]] : ['sh', ['-c', `command -v ${cmd}`]];
  const r = spawnSync(probe[0], probe[1], { encoding: 'utf8' });
  const hit = (r.stdout || '').split(/\r?\n/).find(Boolean);
  return r.status === 0 && hit ? hit.trim() : '';
}
const pf = process.env['PROGRAMFILES'] || 'C:\\Program Files';
const pf86 = process.env['PROGRAMFILES(X86)'] || 'C:\\Program Files (x86)';
const candidates = [
  process.env.CHROME || '',
  ...(process.platform === 'win32' ? [
    join(pf, 'Google', 'Chrome', 'Application', 'chrome.exe'),
    join(pf86, 'Google', 'Chrome', 'Application', 'chrome.exe'),
    join(pf, 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
    join(pf86, 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
  ] : [
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/Applications/Chromium.app/Contents/MacOS/Chromium',
    '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
    which('google-chrome'),
    which('chromium'),
    which('chromium-browser'),
  ]),
].filter(Boolean);
const CHROME = candidates.find((c) => existsSync(c));
if (!CHROME) {
  console.error('ERROR: no Chrome/Chromium/Edge found for PDF printing. Set CHROME=<path>.');
  process.exit(1);
}

const extra = (process.env.MANUAL_CHROME_EXTRA_FLAGS || '').split(/\s+/).filter(Boolean);
const html = pathToFileURL(join(HERE, 'build', 'Zeus-Operator-Manual.html')).href;
execFileSync(CHROME, [
  '--headless', '--disable-gpu', '--no-pdf-header-footer', '--virtual-time-budget=4000',
  ...extra, `--print-to-pdf=${OUTPDF}`, html,
], { stdio: 'inherit' });

console.log(`ANAN Core Operator's Manual -> ${OUTPDF}`);
