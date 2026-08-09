import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const version = fs.readFileSync(path.join(projectRoot, "VERSION"), "utf8").trim();
const changelog = fs.readFileSync(path.join(projectRoot, "CHANGELOG.md"), "utf8").replaceAll("\r\n", "\n");
const escapedVersion = version.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const headingPattern = new RegExp(`^## \\[${escapedVersion}\\](?: - [^\\n]+)?$`, "m");
const heading = headingPattern.exec(changelog);

if (!heading) {
  throw new Error(`CHANGELOG.md 中没有找到版本 ${version} 的二级标题。`);
}

const bodyStart = heading.index + heading[0].length;
const remaining = changelog.slice(bodyStart);
const nextHeading = remaining.search(/^## \[/m);
const notes = (nextHeading >= 0 ? remaining.slice(0, nextHeading) : remaining).trim();

if (!notes) {
  throw new Error(`CHANGELOG.md 中版本 ${version} 没有发行说明。`);
}

process.stdout.write(`${notes}\n`);
