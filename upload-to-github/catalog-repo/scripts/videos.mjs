// Видео-гайды к базам. Игрок отправляет ссылку через форму issue «🎬 Видео-гайд к базе»,
// этот скрипт в GitHub Actions строго проверяет заявку, а в videos.json (и в программу)
// видео попадает ТОЛЬКО после того, как модератор поставит метку «video-approved».
//
//   MODE=check   — новая/изменённая заявка: автопроверка, метка video-pending или отказ и закрытие
//   MODE=approve — модератор поставил video-approved: добавить в videos.json, закрыть заявку
//   MODE=remove  — модератор поставил video-removed: убрать из videos.json
//
// Окружение: GITHUB_TOKEN, GITHUB_REPOSITORY, ISSUE_NUMBER, ACTOR, CATALOG_DIR (папка каталога в репозитории)
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");
const VIDEOS = join(ROOT, "videos.json");
const BANNED = join(ROOT, "video-banned.json");
const { GITHUB_TOKEN: token, GITHUB_REPOSITORY: repo, ISSUE_NUMBER, MODE, ACTOR } = process.env;
const issueNo = Number(ISSUE_NUMBER);

// ---------- Правила ----------
const MIN_ACCOUNT_DAYS = 14;        // аккаунт GitHub не моложе 2 недель — против одноразовых аккаунтов
const MAX_PENDING_PER_USER = 3;     // не больше 3 заявок на проверке от одного человека
const MAX_PER_USER_PER_BASE = 1;    // от одного автора — одно видео на базу
const MAX_ABOUT = 300;

// мат, оскорбления, 18+, скам: корни слов, проверяются в названии видео, канале и описании заявки
// (?<!\p{L}) / (?!\p{L}) — границы слова для кириллицы (\b в JS работает только с латиницей)
const BAD = [
  /х[уy][йиеяюё]/iu, /п[иi]зд/iu, /(?<!\p{L})(?:за|вы|у|до|на|по|от|при|раз|разъ|съ|подъ|объ|въ)?[её]б[аоуиеыёлнт]/iu,
  /бл[яa][дт]|(?<!\p{L})бля(?!\p{L})/iu, /(?<!\p{L})сук[аи](?!\p{L})/iu, /муд[аи]к/iu,
  /пид[оа]р|(?<!\p{L})педик|гандон|шлюх|(?<!\p{L})чмо(?!\p{L})|дебил|(?<!\p{L})даун(?!\p{L})|ублюд/iu, /fuck|shit|bitch|cunt|nigg|\bfag/iu,
  /порн|porn|\bsex\b|секс|xxx|18\+|nsfw|onlyfans|хентай|hentai|нюдс|nude/iu,
  /(?<!\p{L})чит(?:ы|ер\p{L}*|ам|ов|ом)?(?!\p{L})|\bcheat|aimbot|wallhack|\besp\b|макрос|\bmacro|no ?recoil|норекоил|инжект|inject/iu,
  /казино|casino|(?<!\p{L})ставк|букмекер|\bbet\b|рулетк|roulette|кейс\p{L}* бесплат|free ?skin|бесплатн\p{L}* скин|раздач\p{L}* скин|промокод|promo ?code|giveaway|розыгрыш/iu,
  /прода\p{L}* акк|купи\p{L}* акк|sell account|телеграм|t\.me\/|discord\.gg|vk\.cc|bit\.ly|clck\.ru/iu,
  /наркот|закладк|оружи\p{L}* куп|террор|свастик|нацис|зиг ?хайл/iu,
];
// маскировку вида «х*й», «п.и.з.д», «c@sino» тоже ловим: проверяем и исходный, и «склеенный» текст
const norm = (s) => (s ?? "").toLowerCase().replace(/ё/g, "е").replace(/0/g, "о").replace(/3/g, "з").replace(/@/g, "а").replace(/[_*.\-]+(?=\p{L})/gu, "");
export const isBad = (s) => BAD.some((rx) => rx.test(String(s ?? "").toLowerCase()) || rx.test(norm(s)));

/** Разбор ссылки: только YouTube и Rutube. Возвращает каноническую ссылку или null. */
export function parseVideoUrl(raw) {
  let u;
  try { u = new URL(String(raw ?? "").trim()); } catch { return null; }
  if (u.protocol !== "https:") return null;
  const host = u.hostname.replace(/^www\.|^m\./, "");
  if (host === "youtube.com") {
    const m = u.pathname === "/watch" ? u.searchParams.get("v") : (/^\/shorts\/([\w-]{11})$/.exec(u.pathname) ?? [])[1];
    return m && /^[\w-]{11}$/.test(m) ? { provider: "youtube", id: m, url: `https://www.youtube.com/watch?v=${m}` } : null;
  }
  if (host === "youtu.be") {
    const m = /^\/([\w-]{11})$/.exec(u.pathname);
    return m ? { provider: "youtube", id: m[1], url: `https://www.youtube.com/watch?v=${m[1]}` } : null;
  }
  if (host === "rutube.ru") {
    const m = /^\/(?:video|shorts)\/([0-9a-f]{32})\/?$/.exec(u.pathname);
    return m ? { provider: "rutube", id: m[1], url: `https://rutube.ru/video/${m[1]}/` } : null;
  }
  return null;
}

// ---------- GitHub API ----------
async function gh(method, path, body) {
  const res = await fetch(`https://api.github.com${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, Accept: "application/vnd.github+json", "User-Agent": "rustbuilder-videos" },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) throw new Error(`${method} ${path}: ${res.status} ${await res.text()}`);
  return res.status === 204 ? null : res.json();
}
const R = `/repos/${repo}`;
const comment = (text) => gh("POST", `${R}/issues/${issueNo}/comments`, { body: text });
const addLabel = (l) => gh("POST", `${R}/issues/${issueNo}/labels`, { labels: [l] });
const dropLabel = (l) => gh("DELETE", `${R}/issues/${issueNo}/labels/${encodeURIComponent(l)}`).catch(() => {});
const close = (reason = "completed") => gh("PATCH", `${R}/issues/${issueNo}`, { state: "closed", state_reason: reason });

/** Поля формы issue: «### Заголовок\n\nзначение» */
function fields(body) {
  const out = {};
  for (const part of (body ?? "").split(/^### /m).slice(1)) {
    const [head, ...rest] = part.split("\n");
    out[head.trim()] = rest.join("\n").trim().replace(/^_No response_$/, "");
  }
  return out;
}

async function oembed(v) {
  const url = v.provider === "youtube"
    ? `https://www.youtube.com/oembed?format=json&url=${encodeURIComponent(v.url)}`
    : `https://rutube.ru/api/oembed/?format=json&url=${encodeURIComponent(v.url)}`;
  const res = await fetch(url, { headers: { "User-Agent": "rustbuilder-videos" } });
  if (!res.ok) return null;   // видео удалено, приватное или запрещено встраивать
  const j = await res.json();
  return {
    title: String(j.title ?? "").slice(0, 150),
    channel: String(j.author_name ?? "").slice(0, 80),
    thumbnail: v.provider === "youtube" ? `https://i.ytimg.com/vi/${v.id}/mqdefault.jpg` : String(j.thumbnail_url ?? ""),
  };
}

const loadVideos = () => existsSync(VIDEOS) ? JSON.parse(readFileSync(VIDEOS, "utf8")) : { version: 1, videos: [] };
const saveVideos = (d) => writeFileSync(VIDEOS, JSON.stringify(d, null, 2) + "\n");

/** Все проверки заявки. Возвращает { problems, video, info, base } */
async function inspect(issue) {
  const f = fields(issue.body);
  const problems = [];
  const login = issue.user.login;
  const baseFile = (f["База"] ?? "").trim();
  const about = f["Коротко о видео"] ?? "";
  const rulesOk = (f["Правила"] ?? "").split("\n").filter((l) => /^- \[x\]/i.test(l.trim())).length >= 5;

  const banned = existsSync(BANNED) ? JSON.parse(readFileSync(BANNED, "utf8")) : [];
  if (banned.map((b) => b.toLowerCase()).includes(login.toLowerCase())) problems.push("автору запрещено отправлять видео");
  if (!rulesOk) problems.push("не отмечены все правила");

  const index = JSON.parse(readFileSync(join(ROOT, "index.json"), "utf8"));
  const base = index.bases.find((b) => b.file === baseFile);
  if (!base) problems.push(`нет такой базы в каталоге: «${baseFile || "пусто"}»`);

  const video = parseVideoUrl(f["Ссылка на видео"]);
  if (!video) problems.push("ссылка не принята: нужна прямая ссылка на видео YouTube (watch, youtu.be, shorts) или Rutube, https");

  if (about.length > MAX_ABOUT) problems.push(`описание длиннее ${MAX_ABOUT} символов`);
  if (/https?:\/\/|www\.|\.(ru|com|gg|me|cc|ly)\b/i.test(about)) problems.push("в описании не должно быть ссылок");

  const user = await gh("GET", `/users/${encodeURIComponent(login)}`);
  const days = (Date.now() - Date.parse(user.created_at)) / 864e5;
  if (days < MIN_ACCOUNT_DAYS) problems.push(`аккаунту GitHub меньше ${MIN_ACCOUNT_DAYS} дней`);

  let info = null;
  if (video) {
    info = await oembed(video);
    if (!info) problems.push("видео не открывается: удалено, приватное или запрещено встраивание");
  }
  const raw = [info?.title, info?.channel, about, issue.title].join(" \n ").toLowerCase();
  const hit = isBad(raw);
  if (hit) problems.push("в названии, канале или описании запрещённые слова (мат, 18+, читы, казино, скам)");

  const data = loadVideos();
  if (video && data.videos.some((v) => v.id === video.id && v.base === baseFile)) problems.push("это видео уже есть у этой базы");
  if (data.videos.filter((v) => v.by?.toLowerCase() === login.toLowerCase() && v.base === baseFile).length >= MAX_PER_USER_PER_BASE)
    problems.push("у тебя уже есть видео к этой базе");

  return { problems, video, info, base, about: about.slice(0, MAX_ABOUT), login };
}

async function isModerator(login) {
  const p = await gh("GET", `${R}/collaborators/${encodeURIComponent(login)}/permission`).catch(() => null);
  return ["admin", "maintain", "write"].includes(p?.permission);
}

// ---------- Режимы ----------
const issue = MODE ? await gh("GET", `${R}/issues/${issueNo}`) : null;

if (MODE === "check") {
  // не больше N заявок на проверке от одного человека
  const pending = await gh("GET", `${R}/issues?labels=video-pending&state=open&creator=${encodeURIComponent(issue.user.login)}&per_page=100`);
  const r = await inspect(issue);
  if (pending.filter((i) => i.number !== issueNo).length >= MAX_PENDING_PER_USER)
    r.problems.push(`у тебя уже ${MAX_PENDING_PER_USER} видео ждут проверки — дождись решения`);

  if (r.problems.length) {
    await dropLabel("video-pending");
    await addLabel("video-rejected");
    await comment(`❌ **Заявка отклонена автоматически.**\n\n${r.problems.map((p) => `- ${p}`).join("\n")}\n\nИсправь и отправь новую заявку через форму «🎬 Видео-гайд к базе».`);
    await close("not_planned");
  } else {
    await dropLabel("video-rejected");
    await addLabel("video-pending");
    await comment(`✅ **Автопроверка пройдена.** Видео: **${r.info.title}** (${r.info.channel}), база «${r.base.name}».\n\nТеперь видео посмотрит модератор. В программе оно появится только после одобрения.`);
  }
} else if (MODE === "approve") {
  if (!(await isModerator(ACTOR))) { await dropLabel("video-approved"); process.exit(0); }
  const r = await inspect(issue);   // перепроверяем: видео могли удалить или переименовать
  if (r.problems.length) {
    await dropLabel("video-approved");
    await comment(`⚠️ Не добавлено — проверка не прошла:\n${r.problems.map((p) => `- ${p}`).join("\n")}`);
    process.exit(0);
  }
  const data = loadVideos();
  data.videos = data.videos.filter((v) => v.issue !== issueNo);
  data.videos.push({
    base: r.base.file, provider: r.video.provider, id: r.video.id, url: r.video.url,
    title: r.info.title, channel: r.info.channel, thumbnail: r.info.thumbnail,
    about: r.about, by: r.login, issue: issueNo, added: new Date().toISOString().slice(0, 10),
  });
  saveVideos(data);
  await dropLabel("video-pending");
  await comment(`🎬 **Одобрено!** Видео появится у базы «${r.base.name}» в каталоге RustBuilder в течение пары минут.`);
  await close();
} else if (MODE === "remove") {
  if (!(await isModerator(ACTOR))) { await dropLabel("video-removed"); process.exit(0); }
  const data = loadVideos();
  const before = data.videos.length;
  data.videos = data.videos.filter((v) => v.issue !== issueNo);
  saveVideos(data);
  await dropLabel("video-approved");
  await comment(before !== data.videos.length ? "🗑 Видео убрано из каталога." : "Видео в каталоге не было.");
  if (issue.state === "open") await close("not_planned");
}
