#!/usr/bin/env node
// tools/update-floor.json is the one-shot release floor switch. When its
// minVersion is non-null the workflow passes that exact value as
// UPDATE_MIN_VERSION for the next release; later runs leave the file alone and
// this tool carries the previous manifest floor forward indefinitely.
import { createHash } from "node:crypto";
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import path from "node:path";

const { positional, minVersion: cliMinVersion } = parseArgs(process.argv.slice(2));
const [artifactRoot, existingPath, outputPath] = positional;

if (!artifactRoot || !existingPath || !outputPath) {
  console.error("Usage: node tools/update-download-manifest.mjs [--min-version <version>] <artifact-root> <existing-manifest.json> <output-manifest.json>");
  process.exit(2);
}

const baseUrl = process.env.DOWNLOAD_BASE_URL || "https://downloads.openhpsdrzeus.com";
const files = walk(artifactRoot).filter((file) => isDownload(file));
if (files.length === 0) {
  console.error(`No downloadable artifacts found under ${artifactRoot}`);
  process.exit(1);
}

const version = process.env.ZEUS_DOWNLOAD_VERSION || inferVersion(files);
if (!version) {
  console.error("Could not infer Zeus version from artifact filenames.");
  process.exit(1);
}

const manifest = loadManifest(existingPath);
const minVersion = resolveMinVersion(manifest.minVersion, cliMinVersion);
const assets = files
  .map((file) => assetFor(file, version, baseUrl))
  .filter(Boolean)
  .sort(assetSort);

if (assets.length === 0) {
  console.error(`No recognized Zeus artifacts found for ${version}.`);
  process.exit(1);
}

const versionEntry = {
  version,
  channel: process.env.ZEUS_DOWNLOAD_CHANNEL || "main",
  publishedAt: new Date().toISOString(),
  minVersion,
  source: {
    branch: process.env.ZEUS_DOWNLOAD_BRANCH || process.env.GITHUB_REF_NAME || "main",
    commit: process.env.GITHUB_SHA || null,
    runUrl: process.env.GITHUB_SERVER_URL && process.env.GITHUB_REPOSITORY && process.env.GITHUB_RUN_ID
      ? `${process.env.GITHUB_SERVER_URL}/${process.env.GITHUB_REPOSITORY}/actions/runs/${process.env.GITHUB_RUN_ID}`
      : null,
  },
  assets,
  // Rendered HTML of this release's CHANGELOG section, surfaced in the website
  // download panel. Populated only when the publish job renders it (tagged
  // public releases); null otherwise. Older versions keep whatever they were
  // published with — this tool only sets it on the new entry.
  notesHtml: loadNotesHtml(),
};

const nextManifest = {
  schema: 1,
  updatedAt: versionEntry.publishedAt,
  latest: version,
  minVersion,
  versions: [versionEntry],
};

writeFileSync(outputPath, `${JSON.stringify(nextManifest, null, 2)}\n`, "utf8");
writeFileSync(path.join(path.dirname(outputPath), "latest.json"), `${JSON.stringify(versionEntry, null, 2)}\n`, "utf8");

function walk(root) {
  const result = [];
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    const fullPath = path.join(root, entry.name);
    if (entry.isDirectory()) {
      result.push(...walk(fullPath));
    } else if (entry.isFile()) {
      result.push(fullPath);
    }
  }
  return result;
}

function isDownload(file) {
  const name = path.basename(file).toLowerCase();
  return name.endsWith(".exe") || name.endsWith(".pkg") || name.endsWith(".tar.gz") || name.endsWith(".appimage");
}

function loadManifest(file) {
  if (!existsSync(file)) {
    return { schema: 1, versions: [] };
  }

  try {
    const parsed = JSON.parse(readFileSync(file, "utf8"));
    return {
      schema: 1,
      minVersion: normalizeMinVersion(parsed.minVersion),
      versions: Array.isArray(parsed.versions) ? parsed.versions : [],
    };
  } catch {
    return { schema: 1, versions: [] };
  }
}

// Read the pre-rendered release-notes HTML (produced by the publish job from
// CHANGELOG.md) if the publish step pointed us at one. Trusted content — it is
// our own CHANGELOG rendered by the CI, not user input. Returns null when no
// notes were rendered (e.g. an empty CHANGELOG section), so the website simply
// omits the panel rather than showing an empty box.
function loadNotesHtml() {
  const file = process.env.ZEUS_RELEASE_NOTES_HTML_FILE;
  if (!file || !existsSync(file)) return null;
  const html = readFileSync(file, "utf8").trim();
  return html.length > 0 ? html : null;
}

function inferVersion(filesToInspect) {
  for (const file of filesToInspect) {
    const name = path.basename(file);
    const match =
      name.match(/^openhpsdr-zeus-(.+)-win-(?:x64|arm64)-setup\.exe$/i) ||
      name.match(/^openhpsdr-zeus-(.+)-linux-(?:x64|arm64)\.tar\.gz$/i) ||
      name.match(/^openhpsdr-zeus-(.+)-macos-(?:arm64|x64)\.pkg$/i) ||
      name.match(/^OpenhpsdrZeus(?:-Server)?-(.+)-linux-(?:x86_64|aarch64)\.AppImage$/);
    if (match) return match[1];
  }
  return null;
}

function assetFor(file, versionValue, urlBase) {
  const filename = path.basename(file);
  const stats = statSync(file);
  const sha256 = createHash("sha256").update(readFileSync(file)).digest("hex");
  const common = {
    filename,
    url: `${urlBase}/versions/${encodeURIComponent(versionValue)}/${encodeURIComponent(filename)}`,
    size: stats.size,
    sha256,
  };

  let match = filename.match(/^openhpsdr-zeus-(.+)-win-(x64|arm64)-setup\.exe$/i);
  if (match) {
    return {
      ...common,
      platform: "windows",
      arch: match[2],
      kind: "installer",
      label: `Windows ${match[2].toUpperCase()} installer`,
    };
  }

  match = filename.match(/^openhpsdr-zeus-(.+)-macos-(arm64|x64)\.pkg$/i);
  if (match) {
    return {
      ...common,
      platform: "macos",
      arch: match[2].toLowerCase(),
      kind: "pkg",
      label: `macOS ${match[2].toLowerCase() === "arm64" ? "Apple Silicon" : "Intel"} package installer`,
    };
  }

  match = filename.match(/^openhpsdr-zeus-(.+)-linux-(x64|arm64)\.tar\.gz$/i);
  if (match) {
    return {
      ...common,
      platform: "linux",
      arch: match[2],
      kind: "tarball",
      label: `Linux ${match[2]} tarball`,
    };
  }

  match = filename.match(/^OpenhpsdrZeus(-Server)?-(.+)-linux-(x86_64|aarch64)\.AppImage$/);
  if (match) {
    const arch = match[3] === "x86_64" ? "x64" : "arm64";
    const mode = match[1] ? "server" : "desktop";
    return {
      ...common,
      platform: "linux",
      arch,
      kind: "appimage",
      mode,
      label: `Linux ${arch} ${mode} AppImage`,
    };
  }

  return null;
}

function assetSort(a, b) {
  const platformOrder = { windows: 0, macos: 1, linux: 2 };
  const archOrder = { x64: 0, arm64: 1 };
  const kindOrder = { installer: 0, pkg: 0, appimage: 1, tarball: 2 };
  return (
    (platformOrder[a.platform] ?? 9) - (platformOrder[b.platform] ?? 9) ||
    (archOrder[a.arch] ?? 9) - (archOrder[b.arch] ?? 9) ||
    (kindOrder[a.kind] ?? 9) - (kindOrder[b.kind] ?? 9) ||
    a.filename.localeCompare(b.filename)
  );
}

function parseArgs(args) {
  const positional = [];
  let minVersion;
  for (let i = 0; i < args.length; i += 1) {
    const arg = args[i];
    if (arg === "--min-version") {
      i += 1;
      minVersion = args[i];
    } else if (arg.startsWith("--min-version=")) {
      minVersion = arg.slice("--min-version=".length);
    } else {
      positional.push(arg);
    }
  }
  return { positional, minVersion };
}

function resolveMinVersion(previous, cliValue) {
  if (cliValue !== undefined) return normalizeMinVersion(cliValue);
  if (process.env.UPDATE_MIN_VERSION !== undefined) {
    return normalizeMinVersion(process.env.UPDATE_MIN_VERSION);
  }
  return normalizeMinVersion(previous);
}

function normalizeMinVersion(value) {
  if (typeof value !== "string") return null;
  const trimmed = value.trim();
  if (!trimmed || trimmed.toLowerCase() === "null") return null;
  return trimmed;
}
