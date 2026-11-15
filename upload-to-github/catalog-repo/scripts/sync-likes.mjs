// Лайки баз — реакции 👍 на issue. Для каждой базы из index.json заводим issue с меткой «base»
// и записываем его номер в index.json (поле "issue"). Приложение читает число 👍 и ❤ через GitHub API,
// а кнопка «♥» открывает страницу issue, где игрок ставит 👍 своим аккаунтом GitHub.
// Запускается в GitHub Actions после build-index.mjs: GITHUB_TOKEN и GITHUB_REPOSITORY берутся из окружения.
import { readFileSync, writeFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");
const INDEX = join(ROOT, "index.json");
const repo = process.env.GITHUB_REPOSITORY;
const token = process.env.GITHUB_TOKEN;
if (!repo || !token) { console.log("Нет GITHUB_REPOSITORY/GITHUB_TOKEN — пропускаю лайки."); process.exit(0); }

const LABEL = "base";
const marker = (file) => `<!-- rustbuilder-base: ${file} -->`;

async function gh(method, path, body) {
  const res = await fetch(`https://api.github.com/repos/${repo}${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: "application/vnd.github+json",
      "X-GitHub-Api-Version": "2022-11-28",
      "User-Agent": "rustbuilder-catalog",
    },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok && !(method === "POST" && path === "/labels" && res.status === 422))
    throw new Error(`${method} ${path}: ${res.status} ${await res.text()}`);
  return res.status === 204 ? null : res.json();
}

await gh("POST", "/labels", { name: LABEL, color: "a970ff", description: "Страница базы: ставь 👍, если база понравилась" });

// все issue с меткой base (и открытые, и закрытые)
const issues = [];
for (let page = 1; ; page++) {
  const batch = await gh("GET", `/issues?labels=${LABEL}&state=all&per_page=100&page=${page}`);
  issues.push(...batch.filter((i) => !i.pull_request));
  if (batch.length < 100) break;
}
const byFile = new Map();
for (const i of issues) {
  const m = /rustbuilder-base: (\S+)/.exec(i.body ?? "");
  if (m && !byFile.has(m[1])) byFile.set(m[1], i);
}

const index = JSON.parse(readFileSync(INDEX, "utf8"));
let created = 0;
for (const e of index.bases) {
  let issue = byFile.get(e.file);
  if (!issue) {
    issue = await gh("POST", "/issues", {
      title: `♥ ${e.name}`,
      labels: [LABEL],
      body: [
        marker(e.file),
        `### ${e.name}`,
        e.description || "",
        "",
        `Автор: **${e.author || "—"}** · фундаментов: ${e.foundations} · этажей: ${e.floors}`,
        "",
        "👍 **Понравилась база? Поставь 👍 этому посту** — это лайк в каталоге RustBuilder.",
        "💬 В комментариях можно написать отзыв: как база держит рейд, что поменять.",
        "",
        `Файл: [${e.file}](../blob/main/${process.env.CATALOG_DIR ?? ""}${e.file})`,
      ].join("\n"),
    });
    created++;
  }
  e.issue = issue.number;
}
writeFileSync(INDEX, JSON.stringify(index, null, 2) + "\n");
console.log(`Лайки: ${index.bases.length} баз, новых страниц: ${created}`);
