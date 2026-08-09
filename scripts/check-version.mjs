import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const read = relativePath => fs.readFileSync(path.join(projectRoot, relativePath), "utf8");
const version = read("VERSION").trim();
const semverPattern = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;

if (!semverPattern.test(version)) {
  throw new Error(`VERSION 不是有效的语义化版本号：${version}`);
}

const packageJson = JSON.parse(read("knowledge-reader/package.json"));
const packageLock = JSON.parse(read("knowledge-reader/package-lock.json"));
const tauriConfig = JSON.parse(read("knowledge-reader/src-tauri/tauri.conf.json"));
const cargoVersion = read("knowledge-reader/src-tauri/Cargo.toml").match(/^version = "([^"]+)"$/m)?.[1];
const appStreamVersion = read("packaging/linux/io.github.tangsyau.feishu-wiki-exporter.metainfo.xml")
  .match(/<release version="([^"]+)"/u)?.[1];

const values = new Map([
  ["knowledge-reader/package.json", packageJson.version],
  ["knowledge-reader/package-lock.json", packageLock.version],
  ["knowledge-reader/package-lock.json packages['']", packageLock.packages?.[""]?.version],
  ["knowledge-reader/src-tauri/Cargo.toml", cargoVersion],
  ["knowledge-reader/src-tauri/tauri.conf.json", tauriConfig.version],
  ["packaging/linux AppStream metadata", appStreamVersion]
]);

const mismatches = [...values].filter(([, value]) => value !== version);
if (mismatches.length > 0) {
  const details = mismatches.map(([file, value]) => `${file}: ${value ?? "缺失"}`).join("\n");
  throw new Error(`以下版本号与 VERSION（${version}）不一致：\n${details}`);
}

if (!read("Directory.Build.props").includes("$(MSBuildThisFileDirectory)VERSION")) {
  throw new Error("Directory.Build.props 没有从根目录 VERSION 读取 .NET 版本号。");
}

const escapedVersion = version.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
if (!new RegExp(`^## \\[${escapedVersion}\\](?: - [^\\n]+)?$`, "m").test(read("CHANGELOG.md"))) {
  throw new Error(`CHANGELOG.md 中没有找到版本 ${version} 的发行说明。`);
}

if (process.env.GITHUB_REF_TYPE === "tag" && process.env.GITHUB_REF_NAME !== `v${version}`) {
  throw new Error(`Git 标签 ${process.env.GITHUB_REF_NAME} 与 VERSION（v${version}）不一致。`);
}

console.log(`Version ${version}: OK`);
