// Ники авторов каталога: один ник — один аккаунт GitHub. Реестр — nicknames.json.
//
//   node scripts/nicks.mjs check     — pull request: ник свободен или принадлежит автору PR; чужие базы не трогаем
//                                      env: PR_AUTHOR, PR_ASSOC (author_association), BASE_SHA
//   node scripts/nicks.mjs register  — после слияния: новые ники закрепляются за аккаунтом, который прислал базу
//                                      env: GITHUB_TOKEN, GITHUB_REPOSITORY, CATALOG_DIR
//   node scripts/nicks.mjs grant     — заявка автора проекта «Выдать ник» (форма nick-grant)
//                                      env: GITHUB_TOKEN, GITHUB_REPOSITORY, ISSUE_NUMBER
import { readFileSync, writeFileSync, existsSync, readdirSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
const FILE = join(ROOT, "nicknames.json");
const { GITHUB_TOKEN: token, GITHUB_REPOSITORY: repo, CATALOG_DIR = "catalog-repo/" } = process.env;
const MODE = process.argv[2];

// Похожие буквы считаются одинаковыми: «Lеgо» с кириллицей = «Lego» с латиницей, «G00d» = «Good», пробелы и _ . - не важны.
const SAME = { "а": "a", "в": "b", "е": "e", "ё": "e", "к": "k", "м": "m", "н": "h", "о": "o", "р": "p", "с": "c", "т": "t", "у": "y", "х": "x", "0": "o", "1": "i", "l": "i", "|": "i" };
export const normNick = (s) => [...String(s ?? "").trim().toLowerCase()].map((c) => SAME[c] ?? c).join("").replace(/[\s_.\-]+/g, "");

export const loadNicks = () => existsSync(FILE) ? JSON.parse(readFileSync(FILE, "utf8")) : { version: 1, nicks: [] };
const saveNicks = (d) => {
  d.nicks.sort((a, b) => a.nick.localeCompare(b.nick, "ru"));
  writeFileSync(FILE, JSON.stringify(d, null, 2) + "\n");
};
export const findNick = (d, nick) => d.nicks.find((n) => normNick(n.nick) === normNick(nick));
const same = (a, b) => a && b && a.toLowerCase() === b.toLowerCase();

function nickProblem(nick) {
  const n = String(nick ?? "").trim();
  if (n.length < 2) return "ник короче 2 символов";
  if (n.length > 24) return "ник длиннее 24 символов";
  if (normNick(n).length < 2) return "ник из одних пробелов и знаков";
  return null;
}

const readPlan = (text) => { try { return JSON.parse(text.replace(/^﻿/, "")); } catch { return null; } };
const git = (...args) => execFileSync("git", args, { cwd: ROOT, encoding: "utf8" });

async function gh(method, path, body) {
  const res = await fetch(`https://api.github.com${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, Accept: "application/vnd.github+json", "User-Agent": "rustbuilder-nicks" },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) throw new Error(`${method} ${path}: ${res.status} ${await res.text()}`);
  return res.status === 204 ? null : res.json();
}

/** Проверка изменений PR. changes: [{status: A|M|D|Rxx, oldPath, newPath}], readOld/readNew(path) → база или null */
export function checkChanges(data, author, changes, readOld, readNew) {
  const errors = [];
  for (const { status, oldPath, newPath } of changes) {
    // была ли база раньше и чья она
    if (!status.startsWith("A")) {
      const old = readOld(oldPath);
      const owner = findNick(data, old?.author)?.github;
      if (!same(owner, author)) errors.push(`${oldPath}: это чужая база (автор «${old?.author ?? "?"}»), менять и удалять её может только автор`);
    }
    if (status.startsWith("D")) continue;
    const nick = readNew(newPath)?.author?.trim();
    if (!nick) { errors.push(`${newPath}: нет ника автора (поле author). Выкладывай базу из программы RustBuilder`); continue; }
    const p = nickProblem(nick);
    if (p) { errors.push(`${newPath}: ${p}`); continue; }
    const e = findNick(data, nick);
    if (e && !e.github) errors.push(`${newPath}: ник «${nick}» зарезервирован. Выбери в программе другой ник (👤 Профиль)`);
    else if (e && !same(e.github, author)) errors.push(`${newPath}: ник «${nick}» уже занят${e.nick !== nick ? ` (похож на «${e.nick}»)` : ""}. Выбери в программе другой ник (👤 Профиль)`);
    else console.log(`✓ ${newPath}: ник «${nick}» ${e ? "твой" : "свободен — закрепится за тобой после принятия"}`);
  }
  // в одном PR нельзя занять сразу несколько ников
  const nicks = new Set(changes.filter((c) => !c.status.startsWith("D")).map((c) => normNick(readNew(c.newPath)?.author)).filter(Boolean));
  if (nicks.size > 1) errors.push("в одном pull request базы под разными никами — выкладывай под одним");
  return errors;
}

// ---------- check: pull request ----------
if (MODE === "check") {
  const author = process.env.PR_AUTHOR;
  if (["OWNER", "MEMBER", "COLLABORATOR"].includes(process.env.PR_ASSOC)) {
    console.log(`✓ ${author} — участник проекта, проверка ников не нужна`);
    process.exit(0);
  }
  const base = process.env.BASE_SHA;
  const changes = git("diff", "--name-status", "--relative", base, "HEAD", "--", "bases/").trim().split("\n").filter(Boolean).map((line) => {
    const [status, ...paths] = line.split("\t");
    return { status, oldPath: paths[0], newPath: paths[paths.length - 1] };
  });
  const errors = checkChanges(loadNicks(), author, changes,
    (p) => readPlan(git("show", `${base}:${CATALOG_DIR}${p}`)),
    (p) => readPlan(readFileSync(join(ROOT, p), "utf8")));
  if (errors.length) {
    console.error(`✗ Проблемы с ником автора (@${author}):\n   - ${errors.join("\n   - ")}`);
    process.exit(1);
  }
}

// ---------- register: после слияния ----------
if (MODE === "register") {
  const data = loadNicks();
  let added = 0;
  for (const name of readdirSync(join(ROOT, "bases")).filter((f) => f.endsWith(".json"))) {
    const plan = readPlan(readFileSync(join(ROOT, "bases", name), "utf8"));
    const nick = plan?.author?.trim();
    if (!nick || nickProblem(nick) || findNick(data, nick)) continue;
    // кто прислал файл первым: pull request, а если его нет — автор коммита
    const commits = await gh("GET", `/repos/${repo}/commits?path=${encodeURIComponent(CATALOG_DIR + "bases/" + name)}&per_page=100`);
    const first = commits[commits.length - 1];
    if (!first) continue;
    const prs = await gh("GET", `/repos/${repo}/commits/${first.sha}/pulls`).catch(() => []);
    const login = prs.find((p) => p.merged_at)?.user?.login ?? first.author?.login;
    if (!login) continue;
    data.nicks.push({ nick, github: login, added: new Date().toISOString().slice(0, 10), source: "auto" });
    console.log(`✓ ник «${nick}» закреплён за @${login} (${name})`);
    added++;
  }
  if (added) saveNicks(data);
  else console.log("новых ников нет");
}

// ---------- grant: выдача ника автором проекта ----------
if (MODE === "grant") {
  const issueNo = Number(process.env.ISSUE_NUMBER);
  const R = `/repos/${repo}`;
  const issue = await gh("GET", `${R}/issues/${issueNo}`);
  const reply = async (text, ok) => {
    await gh("POST", `${R}/issues/${issueNo}/comments`, { body: text });
    await gh("PATCH", `${R}/issues/${issueNo}`, { state: "closed", state_reason: ok ? "completed" : "not_planned" });
  };
  const perm = await gh("GET", `${R}/collaborators/${encodeURIComponent(issue.user.login)}/permission`).catch(() => null);
  if (!["admin", "maintain", "write"].includes(perm?.permission)) {
    await reply("🔒 Выдавать ники может только автор проекта. Если хочешь закрепить за собой ник — просто выложи базу из программы: свободный ник закрепится за тобой автоматически.", false);
    process.exit(0);
  }
  const f = {};
  for (const part of (issue.body ?? "").split(/^### /m).slice(1)) {
    const [head, ...rest] = part.split("\n");
    f[head.trim()] = rest.join("\n").trim().replace(/^_No response_$/, "");
  }
  const nick = (f["Ник"] ?? "").trim();
  const login = (f["Аккаунт GitHub"] ?? "").trim().replace(/^@/, "").replace(/^https:\/\/github\.com\//, "").replace(/\/$/, "");
  const action = f["Что сделать"] ?? "";
  const problem = nickProblem(nick);
  if (problem) { await reply(`❌ ${problem}.`, false); process.exit(0); }

  const data = loadNicks();
  const existing = findNick(data, nick);
  let text;
  if (action.startsWith("Освободить")) {
    if (!existing) { await reply(`Ника «${nick}» нет в реестре — освобождать нечего.`, false); process.exit(0); }
    data.nicks = data.nicks.filter((n) => n !== existing);
    text = `🗑 Ник «${existing.nick}» освобождён${existing.github ? ` (был у @${existing.github})` : ""}.`;
  } else if (action.startsWith("Зарезервировать")) {
    if (existing) data.nicks = data.nicks.filter((n) => n !== existing);
    data.nicks.push({ nick, github: null, added: new Date().toISOString().slice(0, 10), source: "reserved" });
    text = `🔒 Ник «${nick}» зарезервирован: никто не сможет выложить базу под ним, пока ты его не выдашь.` + (existing?.github ? `\n\n⚠ Раньше он был у @${existing.github}.` : "");
  } else {
    if (!/^[A-Za-z0-9-]{1,39}$/.test(login)) { await reply("❌ Укажи логин GitHub: например `goodjoint` или `https://github.com/goodjoint`.", false); process.exit(0); }
    const user = await gh("GET", `/users/${encodeURIComponent(login)}`).catch(() => null);
    if (!user) { await reply(`❌ Аккаунт GitHub @${login} не найден.`, false); process.exit(0); }
    if (existing) data.nicks = data.nicks.filter((n) => n !== existing);
    data.nicks.push({ nick, github: user.login, added: new Date().toISOString().slice(0, 10), source: "grant" });
    text = `✅ Ник «${nick}» выдан @${user.login}. Теперь выкладывать базы под этим ником может только этот аккаунт.` +
      (existing?.github && !same(existing.github, user.login) ? `\n\n⚠ Раньше ник был у @${existing.github}.` : "");
  }
  saveNicks(data);
  await reply(text, true);
}
