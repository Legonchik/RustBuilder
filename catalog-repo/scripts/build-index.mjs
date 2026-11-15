// Проверяет все базы в bases/*.json и собирает index.json для приложения RustBuilder.
//   node scripts/build-index.mjs            — проверить и записать index.json
//   node scripts/build-index.mjs --check    — только проверить (для pull request'ов)
import { readFileSync, writeFileSync, readdirSync, statSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
const BASES = join(ROOT, "bases");
const checkOnly = process.argv.includes("--check");
const MAX_SIZE = 100 * 1024;

const TYPES = new Set(["foundation", "triangle", "wall", "doorway", "window", "halfwall", "frame", "floor", "roof", "stairs", "tc",
  "box", "furnace", "workbench", "repair", "bag", "locker"]);
const EDGE_TYPES = new Set(["wall", "doorway", "window", "halfwall", "frame"]);
const TIERS = new Set(["twig", "wood", "stone", "metal", "hqm"]);
const DOORS = new Set(["wood", "sheet", "armored", "garage", "double-wood", "double-sheet", "double-armored"]);
const LOCKS = new Set(["code", "key"]);

// Убирает однострочные и блочные комментарии вне строк (приложение их допускает)
function stripComments(s) {
  let out = "", inStr = false;
  for (let i = 0; i < s.length; i++) {
    const c = s[i], n = s[i + 1];
    if (inStr) {
      out += c;
      if (c === "\\") { out += n ?? ""; i++; }
      else if (c === '"') inStr = false;
    } else if (c === '"') { inStr = true; out += c; }
    else if (c === "/" && n === "/") { while (i < s.length && s[i] !== "\n") i++; out += "\n"; }
    else if (c === "/" && n === "*") { i += 2; while (i < s.length && !(s[i] === "*" && s[i + 1] === "/")) i++; i++; }
    else out += c;
  }
  return out.replace(/,(\s*[}\]])/g, "$1"); // висячие запятые
}

function validate(plan) {
  const errors = [];
  const err = (m) => errors.push(m);
  if (typeof plan.name !== "string" || !plan.name.trim()) err("нет поля name");
  if (plan.name?.length > 60) err("name длиннее 60 символов");
  if (plan.description?.length > 300) err("description длиннее 300 символов");
  if (plan.tier && !TIERS.has(plan.tier)) err(`неизвестный tier '${plan.tier}'`);
  if (!Array.isArray(plan.pieces) || plan.pieces.length === 0) { err("нет блоков (pieces)"); return errors; }
  if (plan.pieces.length > 2000) err("слишком много блоков (больше 2000)");

  const byId = new Map();
  for (const p of plan.pieces) {
    if (!p.id) { err(`у блока типа '${p.type}' нет id`); continue; }
    if (byId.has(p.id)) err(`повторяется id '${p.id}'`);
    byId.set(p.id, p);
    if (!TYPES.has(p.type)) err(`'${p.id}': неизвестный тип '${p.type}'`);
    if (p.tier && !TIERS.has(p.tier)) err(`'${p.id}': неизвестный tier '${p.tier}'`);
    if (p.door && !DOORS.has(p.door)) err(`'${p.id}': неизвестная дверь '${p.door}'`);
    if (p.lock && !LOCKS.has(p.lock)) err(`'${p.id}': неизвестный замок '${p.lock}'`);
  }
  if (!plan.pieces.some((p) => p.type === "foundation")) err("нужен хотя бы один квадратный фундамент");

  for (const p of plan.pieces) {
    if (p.type === "foundation" && p.on == null) continue;   // квадрат по сетке (x, y); с on/edge — прикреплён к стороне блока
    const host = byId.get(p.on);
    if (!host || !["foundation", "triangle"].includes(host.type)) { err(`'${p.id}' ссылается на несуществующий фундамент '${p.on}'`); continue; }
    if (p.type === "triangle" || p.type === "foundation" || EDGE_TYPES.has(p.type) || (p.type === "roof" && p.edge != null)) {
      const ok = host.type === "triangle" ? ["L", "R", "B"] : ["N", "E", "S", "W"];
      if (!ok.includes(String(p.edge).toUpperCase())) err(`'${p.id}': у '${host.id}' нет стороны '${p.edge}' (можно ${ok.join("/")})`);
    }
  }

  for (const [i, s] of (plan.steps ?? []).entries())
    for (const id of s.pieces ?? [])
      if (!byId.has(id)) err(`шаг ${i + 1} ссылается на несуществующий блок '${id}'`);

  // интерьеры проверяем вместе с базой: их мебель стоит на фундаментах базы
  if (plan.interiors != null && !Array.isArray(plan.interiors)) err("interiors должен быть списком");
  if ((plan.interiors ?? []).length > 10) err("больше 10 вариантов интерьера");
  for (const it of plan.interiors ?? []) {
    if (typeof it.name !== "string" || !it.name.trim()) err("у интерьера нет name");
    const merged = { ...plan, interiors: [], pieces: [...plan.pieces, ...(it.pieces ?? [])], steps: [...(plan.steps ?? []), ...(it.steps ?? [])] };
    for (const e of validate(merged)) err(`интерьер «${it.name}»: ${e}`);
  }
  return errors;
}

function firstAuthor(file) {
  try {
    const out = execFileSync("git", ["log", "--diff-filter=A", "--follow", "--format=%an|%as", "--", file], { cwd: ROOT, encoding: "utf8" });
    const last = out.trim().split("\n").filter(Boolean).pop();
    if (last) { const [author, date] = last.split("|"); return { author, date }; }
  } catch { /* не git-репозиторий — ок */ }
  return {};
}

const entries = [];
let failed = 0;
for (const name of readdirSync(BASES).filter((f) => f.endsWith(".json")).sort()) {
  const path = join(BASES, name);
  const rel = `bases/${name}`;
  if (statSync(path).size > MAX_SIZE) { console.error(`✗ ${rel}: файл больше 100 КБ`); failed++; continue; }
  let plan;
  try { plan = JSON.parse(stripComments(readFileSync(path, "utf8").replace(/^﻿/, ""))); }
  catch (e) { console.error(`✗ ${rel}: битый JSON — ${e.message}`); failed++; continue; }

  const errors = validate(plan);
  if (errors.length) { console.error(`✗ ${rel}:\n   - ${errors.join("\n   - ")}`); failed++; continue; }

  const git = firstAuthor(rel);
  const levels = plan.pieces.map((p) => (p.type === "foundation" || p.type === "triangle" ? 1 : p.floor ?? 1));
  entries.push({
    file: rel,
    name: plan.name,
    description: plan.description ?? "",
    author: plan.author || git.author || "",
    added: git.date || "",
    tier: plan.tier ?? "stone",
    foundations: plan.pieces.filter((p) => p.type === "foundation" || p.type === "triangle").length,
    floors: Math.max(...levels),
    steps: (plan.steps ?? []).length,
  });
  console.log(`✓ ${rel}`);
}

if (failed) { console.error(`\nОшибок в файлах: ${failed}`); process.exit(1); }
if (!checkOnly) {
  entries.sort((a, b) => (b.added || "").localeCompare(a.added || "") || a.name.localeCompare(b.name));
  writeFileSync(join(ROOT, "index.json"), JSON.stringify({ version: 1, bases: entries }, null, 2) + "\n");
  console.log(`\nindex.json: ${entries.length} баз`);
}
