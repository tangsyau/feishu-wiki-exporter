import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const nextVersion = process.argv[2];
const semverPattern = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;
if (!nextVersion || !semverPattern.test(nextVersion)) {
  console.error("用法：node scripts/set-version.mjs <语义化版本号，例如 0.1.1>");
  process.exit(2);
}

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const resolve = relativePath => path.join(projectRoot, relativePath);
const read = relativePath => fs.readFileSync(resolve(relativePath), "utf8");
const write = (relativePath, contents) => fs.writeFileSync(resolve(relativePath), contents);
const writeJson = (relativePath, value) => write(relativePath, `${JSON.stringify(value, null, 2)}\n`);

write("VERSION", `${nextVersion}\n`);

const packageJson = JSON.parse(read("knowledge-reader/package.json"));
packageJson.version = nextVersion;
writeJson("knowledge-reader/package.json", packageJson);

const packageLock = JSON.parse(read("knowledge-reader/package-lock.json"));
packageLock.version = nextVersion;
if (packageLock.packages?.[""]) {
  packageLock.packages[""].version = nextVersion;
}
writeJson("knowledge-reader/package-lock.json", packageLock);

const tauriConfig = JSON.parse(read("knowledge-reader/src-tauri/tauri.conf.json"));
tauriConfig.version = nextVersion;
writeJson("knowledge-reader/src-tauri/tauri.conf.json", tauriConfig);

const cargoPath = "knowledge-reader/src-tauri/Cargo.toml";
write(cargoPath, read(cargoPath).replace(/^version = "[^"]+"$/m, `version = "${nextVersion}"`));

const metadataPath = "packaging/linux/io.github.tangsyau.feishu-wiki-exporter.metainfo.xml";
const releaseDate = new Date().toISOString().slice(0, 10);
write(
  metadataPath,
  read(metadataPath).replace(
    /<release version="[^"]+" date="[^"]+" \/>/u,
    `<release version="${nextVersion}" date="${releaseDate}" />`
  )
);

console.log(`已将项目版本更新为 ${nextVersion}。`);
