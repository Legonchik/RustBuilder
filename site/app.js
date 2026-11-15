"use strict";
// RustBuilder — сайт: каталог, аккаунты (Discord / почта), профили, медиа, админка.
// Данные: каталог баз — index.json из GitHub; аккаунты, лайки, базы с сайта — Supabase.

const C = window.RB_CONFIG;
const RAW = `https://raw.githubusercontent.com/${C.repo}/main/catalog-repo/`;
const READY = !/ВСТАВЬ/.test(C.supabaseUrl + C.supabaseKey) && !!window.supabase;
const sb = READY ? window.supabase.createClient(C.supabaseUrl, C.supabaseKey, {
  auth: { flowType: "pkce", detectSessionInUrl: true, persistSession: true, autoRefreshToken: true },
}) : null;

const S = { user: null, me: null, gh: null, likes: new Map(), myLikes: new Set(), myFavs: new Set(), authChecked: false };

// ─── мелочи ───────────────────────────────────────────────────────────────────
function h(tag, props, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v == null || v === false) continue;
    if (k === "class") e.className = v;
    else if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
    else e.setAttribute(k, v === true ? "" : v);
  }
  for (const k of kids.flat(Infinity)) if (k != null && k !== false) e.append(k instanceof Node ? k : String(k));
  return e;
}
const $ = (s) => document.querySelector(s);
function toast(t) {
  const el = $("#toast"); el.textContent = t; el.classList.add("show");
  clearTimeout(toast.t); toast.t = setTimeout(() => el.classList.remove("show"), 3500);
}
function store(k, v) { try { if (v === undefined) return sessionStorage.getItem(k); if (v === null) sessionStorage.removeItem(k); else sessionStorage.setItem(k, v); } catch { return null; } }
const go = (hash) => { if (location.hash === hash) render(); else location.hash = hash; };
const plural = (n, f) => f[(n % 10 === 1 && n % 100 !== 11) ? 0 : (n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 10 || n % 100 >= 20)) ? 1 : 2];
const date = (d) => d ? new Date(d).toLocaleDateString("ru-RU", { day: "numeric", month: "long", year: "numeric" }) : "";

// Ник: те же правила, что в программе, на GitHub и в базе (похожие буквы склеиваются).
const LOOK = { "а": "a", "в": "b", "е": "e", "ё": "e", "к": "k", "м": "m", "н": "h", "о": "o", "р": "p", "с": "c", "т": "t", "у": "y", "х": "x", "0": "o", "1": "i", "l": "i", "|": "i" };
const norm = (n) => [...String(n || "").toLowerCase()].map((c) => LOOK[c] ?? c).join("").replace(/[\s_.-]+/g, "");

function errText(e) {
  const m = (e && (e.message || e.error_description || e.msg)) || String(e);
  if (/NICK_RESERVED/.test(m)) return "Этот ник зарезервирован. Если он твой — напиши автору проекта, он его выдаст.";
  if (e?.code === "23505" || /duplicate key/.test(m)) return "Этот ник уже занят (или очень похож на занятый).";
  if (/profiles_nick_check/.test(m)) return "Ник: от 2 до 24 символов — буквы, цифры, пробел, _ . -";
  if (/NO_NICK/.test(m)) return "Сначала придумай ник в профиле.";
  if (/TOO_MANY_PENDING/.test(m)) return "У тебя уже 3 базы на проверке — подожди, пока их посмотрят.";
  if (/Invalid login credentials/.test(m)) return "Неверная почта или пароль.";
  if (/Email not confirmed/.test(m)) return "Почта не подтверждена — открой письмо и нажми ссылку в нём.";
  if (/already registered/.test(m)) return "Эта почта уже зарегистрирована — просто войди.";
  if (/Password should be/.test(m)) return "Пароль слишком короткий — минимум 6 символов.";
  if (/rate limit|too many/i.test(m)) return "Слишком много попыток, подожди пару минут.";
  if (/Failed to fetch|NetworkError/.test(m)) return "Нет связи с сервером. Проверь интернет.";
  return m;
}

const TIERS = { twig: "Солома", wood: "Дерево", stone: "Камень", metal: "Металл", hqm: "МВК", armored: "МВК" };
const LINKS = {
  youtube: ["YouTube", ["youtube.com", "youtu.be"]], twitch: ["Twitch", ["twitch.tv"]], tiktok: ["TikTok", ["tiktok.com"]],
  telegram: ["Telegram", ["t.me", "telegram.me"]], vk: ["VK", ["vk.com", "vkvideo.ru"]], discord: ["Discord", ["discord.gg", "discord.com"]],
};
function safeLink(kind, url) {
  try {
    const u = new URL(url); const host = u.hostname.replace(/^www\.|^m\./, "");
    return u.protocol === "https:" && LINKS[kind]?.[1].some((d) => host === d || host.endsWith("." + d)) ? u.href : null;
  } catch { return null; }
}

function avatar(p, cls = "") {
  const letter = (p?.nick || "?").trim().charAt(0).toUpperCase();
  const ok = p?.avatar_url && /^https:\/\/(cdn\.discordapp\.com|media\.discordapp\.net)\//.test(p.avatar_url);
  return ok ? h("img", { class: "ava " + cls, src: p.avatar_url, alt: "", loading: "lazy", referrerpolicy: "no-referrer" })
            : h("span", { class: "ava " + cls }, letter);
}
function badges(p) {
  return [p?.role === "admin" && h("span", { class: "badge badge--admin" }, "АВТОР ПРОЕКТА"),
          p?.role === "media" && h("span", { class: "badge badge--media" }, "МЕДИА")];
}
const userLink = (nick, p) => nick ? h("a", { class: "author", href: "#/u/" + encodeURIComponent(nick) }, avatar(p || { nick }), nick, badges(p)) : h("span", { class: "author" }, "без автора");

// ─── данные ───────────────────────────────────────────────────────────────────
async function loadGh() {
  if (S.gh) return S.gh;
  try {
    const j = await (await fetch(RAW + "index.json", { cache: "no-cache" })).json();
    S.gh = (j.bases || []).map((b) => ({ ...b, key: "gh:" + b.file, src: "gh", ghLikes: b.likes || 0 }));
  } catch { S.gh = []; }
  return S.gh;
}
const SUB_COLS = "id,name,description,tier,foundations,floors,steps,status,reject_reason,created_at,author,profiles(nick,role,avatar_url)";
const fromSub = (s) => ({ ...s, key: "s:" + s.id, src: "site", author: s.profiles?.nick, prof: s.profiles, added: s.created_at });
async function loadSubs(mod) {
  if (!sb) return [];
  const { data, error } = await mod(sb.from("submissions").select(SUB_COLS).order("created_at", { ascending: false }));
  if (error) { console.warn(error); return []; }
  return data.map(fromSub);
}
async function loadLikes() {
  if (!sb) return;
  const { data } = await sb.from("like_counts").select("base,n");
  S.likes = new Map((data || []).map((x) => [x.base, x.n]));
}
async function refreshMe() {
  S.me = null; S.myLikes = new Set(); S.myFavs = new Set();
  if (!sb || !S.user) return;
  const [p, l, f] = await Promise.all([
    sb.from("profiles").select("*").eq("id", S.user.id).maybeSingle(),
    sb.from("likes").select("base").eq("user_id", S.user.id),
    sb.from("favorites").select("base"),
  ]);
  S.me = p.data || { id: S.user.id, nick: null };
  S.myLikes = new Set((l.data || []).map((x) => x.base));
  S.myFavs = new Set((f.data || []).map((x) => x.base));
}
async function allBases() {
  const [gh, site] = await Promise.all([loadGh(), loadSubs((q) => q.eq("status", "approved")), loadLikes()]);
  return [...site, ...gh];
}
const likeCount = (b) => (b.ghLikes || 0) + (S.likes.get(b.key) || 0);

async function download(b) {
  try {
    let text;
    if (b.src === "gh") text = await (await fetch(RAW + b.file)).text();
    else {
      const { data, error } = await sb.from("submissions").select("data").eq("id", b.id).single();
      if (error) throw error;
      text = JSON.stringify(data.data, null, 2);
    }
    const name = (b.src === "gh" ? b.file.split("/").pop() : (b.name || "base").replace(/[^\p{L}\p{N}]+/gu, "-").toLowerCase() + ".json");
    const a = h("a", { href: URL.createObjectURL(new Blob([text], { type: "application/json" })), download: name });
    document.body.append(a); a.click(); a.remove();
    toast("Скачано. В RustBuilder: Библиотека → «Загрузить свою базу…»");
  } catch (e) { toast(errText(e)); }
}
function needLogin() { toast("Войди, чтобы ставить лайки и сохранять базы"); store("rb_after", location.hash); go("#/login"); }
async function toggle(set, table, key, btn, onChange) {
  if (!S.user) return needLogin();
  btn.disabled = true;
  const had = set.has(key);
  const { error } = had ? await sb.from(table).delete().eq("user_id", S.user.id).eq("base", key)
                        : await sb.from(table).insert({ base: key });
  btn.disabled = false;
  if (error) return toast(errText(error));
  had ? set.delete(key) : set.add(key);
  if (table === "likes") S.likes.set(key, (S.likes.get(key) || 0) + (had ? -1 : 1));
  onChange();
}

function baseCard(b, extra) {
  const likeBtn = h("button", { class: "btn btn--sm", title: "Нравится" });
  const favBtn = h("button", { class: "btn btn--sm", title: "В избранное" });
  const paint = () => {
    likeBtn.textContent = "♥ " + likeCount(b); likeBtn.classList.toggle("on", S.myLikes.has(b.key));
    favBtn.textContent = S.myFavs.has(b.key) ? "★" : "☆"; favBtn.classList.toggle("on", S.myFavs.has(b.key));
  };
  likeBtn.onclick = () => toggle(S.myLikes, "likes", b.key, likeBtn, paint);
  favBtn.onclick = () => toggle(S.myFavs, "favorites", b.key, favBtn, paint);
  paint();
  const desc = (b.description || "").length > 170 ? b.description.slice(0, 168) + "…" : b.description;
  return h("article", { class: "card" },
    h("h3", null, b.name),
    h("div", { class: "meta" },
      h("span", { class: "tag" }, TIERS[b.tier] || "Камень"),
      b.foundations != null && h("span", { class: "tag" }, `${b.foundations} ${plural(b.foundations, ["фундамент", "фундамента", "фундаментов"])}`),
      b.floors != null && h("span", { class: "tag" }, `${b.floors} ${plural(b.floors, ["этаж", "этажа", "этажей"])}`),
      b.steps != null && h("span", { class: "tag" }, `${b.steps} ${plural(b.steps, ["шаг", "шага", "шагов"])}`)),
    desc && h("p", null, desc),
    userLink(b.author, b.prof),
    extra,
    h("div", { class: "card__foot" }, likeBtn, favBtn,
      h("button", { class: "btn btn--sm", onclick: () => download(b) }, "⬇ Скачать")));
}
const grid = (items, empty) => items.length ? h("div", { class: "grid" }, items) : h("div", { class: "empty" }, empty);

// ─── шапка ────────────────────────────────────────────────────────────────────
function renderAccount() {
  const box = $("#account"); box.replaceChildren();
  $("#navAdmin").hidden = S.me?.role !== "admin";
  if (!sb) return;
  if (S.user) box.append(h("a", { class: "author", href: S.me?.nick ? "#/u/" + encodeURIComponent(S.me.nick) : "#/me" },
                           avatar(S.me), h("span", { class: "nick" }, S.me?.nick || "Профиль")),
                         h("a", { class: "btn btn--sm btn--ghost", href: "#/me", title: "Настройки профиля" }, "⚙"));
  else box.append(h("a", { class: "btn btn--sm btn--primary", href: "#/login" }, "Войти"));
}

// ─── страницы ─────────────────────────────────────────────────────────────────
const PAGES = {};

PAGES.catalog = async (v) => {
  const q = h("input", { type: "search", placeholder: "Поиск: название, автор, описание…" });
  const sort = h("select", null, h("option", { value: "likes" }, "Популярные"), h("option", { value: "new" }, "Новые"), h("option", { value: "name" }, "По названию"));
  const only = h("select", null, h("option", { value: "all" }, "Все базы"), h("option", { value: "fav" }, "★ Избранное"), h("option", { value: "site" }, "Выложенные на сайте"));
  const list = h("div", null, h("div", { class: "empty" }, "Загружаю каталог…"));
  v.append(h("h1", null, "Каталог баз"),
    h("p", { class: "sub" }, "Скачай базу и открой её в RustBuilder: Библиотека → «Загрузить свою базу…». Лайкай понравившиеся и сохраняй в избранное."),
    h("div", { class: "toolbar" }, q, sort, only, h("a", { class: "btn btn--primary", href: "#/upload" }, "＋ Выложить базу")), list);
  const bases = await allBases();
  const draw = () => {
    if (only.value === "fav" && !S.user) { only.value = "all"; return needLogin(); }
    const t = q.value.trim().toLowerCase();
    let items = bases.filter((b) => (!t || [b.name, b.author, b.description].some((x) => (x || "").toLowerCase().includes(t)))
      && (only.value !== "fav" || S.myFavs.has(b.key)) && (only.value !== "site" || b.src === "site"));
    items.sort(sort.value === "name" ? (a, b) => a.name.localeCompare(b.name, "ru")
      : sort.value === "new" ? (a, b) => String(b.added || "").localeCompare(String(a.added || ""))
      : (a, b) => likeCount(b) - likeCount(a));
    list.replaceChildren(grid(items.map((b) => baseCard(b)), only.value === "fav" ? "В избранном пока пусто — жми ☆ на базах." : "Ничего не нашлось."));
  };
  q.oninput = draw; sort.onchange = draw; only.onchange = draw; draw();
};

PAGES.media = async (v) => {
  v.append(h("h1", null, "Медиа"),
    h("p", { class: "sub" }, "Ютуберы, стримеры и авторы, которые делают контент по Rust. Делаешь видео или стримы? Напиши автору проекта — выдадим значок «Медиа»."));
  if (!sb) return v.append(h("div", { class: "empty" }, "Раздел заработает, когда подключим аккаунты."));
  const { data } = await sb.from("profiles").select("nick,avatar_url,role,bio,links").eq("role", "media").not("nick", "is", null).order("created_at");
  v.append(peopleList(data || [], "Пока никого — скоро здесь появятся первые медиа."));
};
function peopleList(rows, empty) {
  return rows.length ? h("div", { class: "people" }, rows.map((p) =>
    h("a", { class: "person", href: "#/u/" + encodeURIComponent(p.nick) }, avatar(p),
      h("div", null, h("strong", null, p.nick), " ", badges(p), h("small", null, (p.bio || "").slice(0, 60)))))) : h("div", { class: "empty" }, empty);
}

PAGES.u = async (v, nick) => {
  if (!sb) return v.append(h("div", { class: "empty" }, "Профили заработают, когда подключим аккаунты."));
  const { data: p } = await sb.from("profiles").select("id,nick,avatar_url,bio,links,role,created_at").eq("nick_norm", norm(nick)).maybeSingle();
  if (!p) return v.append(h("h1", null, "Игрок не найден"), h("p", { class: "sub" }, `Ника «${nick}» на сайте нет.`), h("a", { class: "btn", href: "#/catalog" }, "В каталог"));
  document.title = p.nick + " — RustBuilder";
  const links = Object.entries(p.links || {}).map(([k, url]) => [k, safeLink(k, url)]).filter(([, u]) => u);
  v.append(h("div", { class: "prof" }, avatar(p, "ava--lg"),
    h("div", null,
      h("h1", null, p.nick, badges(p)),
      h("p", { class: "sub", style: "margin:0" }, p.bio || "", p.bio ? h("br") : null, h("small", null, "На сайте с " + date(p.created_at))),
      links.length > 0 && h("div", { class: "links" }, links.map(([k, u]) => h("a", { class: "btn btn--sm", href: u, target: "_blank", rel: "noopener nofollow ugc" }, LINKS[k][0] + " ↗"))),
      S.me?.id === p.id && h("div", { class: "links" }, h("a", { class: "btn btn--sm", href: "#/me" }, "⚙ Редактировать профиль")))));
  const box = h("div", null, h("div", { class: "empty" }, "Загружаю базы…"));
  v.append(h("h2", null, "Базы автора"), box);
  const [gh, site] = await Promise.all([loadGh(), loadSubs((q) => q.eq("status", "approved").eq("author", p.id)), loadLikes()]);
  const mine = [...site, ...gh.filter((b) => norm(b.author) === norm(p.nick))];
  const total = mine.reduce((s, b) => s + likeCount(b), 0);
  box.replaceChildren(mine.length ? h("p", { class: "sub" }, `${mine.length} ${plural(mine.length, ["база", "базы", "баз"])} · ♥ ${total}`) : "",
    grid(mine.map((b) => baseCard(b)), "Пока ни одной базы."));
};

PAGES.login = (v) => {
  if (!sb) return v.append(h("div", { class: "empty" }, "Вход скоро заработает."));
  if (S.user) return go("#/me");
  let mode = "in";
  const email = h("input", { type: "email", autocomplete: "email", placeholder: "you@mail.ru", required: true });
  const pass = h("input", { type: "password", autocomplete: "current-password", placeholder: "минимум 6 символов", minlength: 6, required: true });
  const err = h("p", { class: "err" }); const btn = h("button", { class: "btn btn--primary btn--big", type: "submit", style: "width:100%;margin-top:18px" });
  const tIn = h("button", { class: "btn btn--sm", type: "button" }, "Вход"), tUp = h("button", { class: "btn btn--sm", type: "button" }, "Регистрация");
  const forgot = h("a", { href: "#", class: "hint", style: "display:inline-block;margin-top:12px" }, "Забыл пароль?");
  const setMode = (m) => { mode = m; tIn.classList.toggle("on", m === "in"); tUp.classList.toggle("on", m === "up"); btn.textContent = m === "in" ? "Войти" : "Зарегистрироваться"; pass.autocomplete = m === "in" ? "current-password" : "new-password"; err.textContent = ""; forgot.hidden = m !== "in"; };
  tIn.onclick = () => setMode("in"); tUp.onclick = () => setMode("up"); setMode("in");
  const back = location.origin + location.pathname;
  forgot.onclick = async (e) => {
    e.preventDefault();
    if (!email.value) return (err.textContent = "Впиши почту, и мы пришлём ссылку для нового пароля.");
    const { error } = await sb.auth.resetPasswordForEmail(email.value.trim(), { redirectTo: back });
    err.textContent = error ? errText(error) : "Готово — проверь почту.";
  };
  const form = h("form", { onsubmit: async (e) => {
    e.preventDefault(); err.textContent = ""; btn.disabled = true;
    const creds = { email: email.value.trim(), password: pass.value };
    const { data, error } = mode === "in" ? await sb.auth.signInWithPassword(creds)
      : await sb.auth.signUp({ ...creds, options: { emailRedirectTo: back } });
    btn.disabled = false;
    if (error) return (err.textContent = errText(error));
    if (mode === "up" && !data.session) { err.textContent = ""; v.replaceChildren(h("div", { class: "panel auth" }, h("h2", null, "Проверь почту ✉"), h("p", { class: "sub" }, `Мы отправили письмо на ${creds.email}. Нажми ссылку в нём — и ты в аккаунте. Письма нет? Загляни в «Спам».`))); }
  } }, tIn, " ", tUp, h("label", null, "Почта"), email, h("label", null, "Пароль"), pass, btn, err, forgot);
  v.append(h("div", { class: "panel auth" },
    h("h2", null, "Вход в RustBuilder"),
    h("p", { class: "sub" }, "Аккаунт нужен, чтобы выкладывать базы, ставить лайки и вести свой профиль."),
    h("button", { class: "btn btn--discord btn--big", style: "width:100%", onclick: async () => {
      store("rb_after", store("rb_after") || "#/me");
      const { error } = await sb.auth.signInWithOAuth({ provider: "discord", options: { redirectTo: back } });
      if (error) toast(errText(error));
    } }, "Войти через Discord"),
    h("p", { class: "hint" }, "Если Discord не открывается — войди по почте."),
    h("div", { class: "or" }, "или по почте"), form));
};

PAGES.reset = (v) => {
  if (!S.user) return go("#/login");
  const pass = h("input", { type: "password", autocomplete: "new-password", minlength: 6, required: true }); const err = h("p", { class: "err" });
  v.append(h("form", { class: "panel auth", onsubmit: async (e) => {
    e.preventDefault();
    const { error } = await sb.auth.updateUser({ password: pass.value });
    if (error) return (err.textContent = errText(error));
    toast("Пароль обновлён"); go("#/me");
  } }, h("h2", null, "Новый пароль"), h("label", null, "Придумай новый пароль"), pass,
    h("button", { class: "btn btn--primary", style: "margin-top:16px" }, "Сохранить"), err));
};

PAGES.me = async (v) => {
  if (!sb) return v.append(h("div", { class: "empty" }, "Аккаунты скоро заработают."));
  if (!S.user) { store("rb_after", "#/me"); return go("#/login"); }
  const me = S.me || {};
  const nick = h("input", { value: me.nick || "", maxlength: 24, placeholder: "например, GoodJoint", required: true });
  const bio = h("textarea", { maxlength: 300, placeholder: "Пара слов о себе: сервер, клан, стиль игры…" }); bio.value = me.bio || "";
  const linkInputs = Object.fromEntries(Object.keys(LINKS).map((k) => [k, h("input", { type: "url", value: me.links?.[k] || "", placeholder: "https://" + LINKS[k][1][0] + "/…" })]));
  const err = h("p", { class: "err" });
  const form = h("form", { onsubmit: async (e) => {
    e.preventDefault(); err.textContent = "";
    const links = {};
    for (const [k, inp] of Object.entries(linkInputs)) {
      const val = inp.value.trim(); if (!val) continue;
      if (!safeLink(k, val)) return (err.textContent = `Ссылка ${LINKS[k][0]} должна начинаться с https://${LINKS[k][1][0]}/`);
      links[k] = val;
    }
    const upd = { nick: nick.value.trim(), bio: bio.value.trim() || null, links };
    if (upd.nick === me.nick) delete upd.nick;
    const { error } = await sb.from("profiles").update(upd).eq("id", S.user.id);
    if (error) return (err.textContent = errText(error));
    await refreshMe(); renderAccount(); toast("Профиль сохранён");
    if (!me.nick && store("rb_after")) { const a = store("rb_after"); store("rb_after", null); return go(a); }
    render();
  } },
    h("label", null, "Ник"), nick,
    h("p", { class: "hint" }, "Ник — подпись к твоим базам. Похожие ники считаются одинаковыми: «G00dJoint» и «GoodJoint» с русскими «о» — это один ник."),
    h("label", null, "О себе"), bio,
    h("div", { class: "two" }, Object.entries(linkInputs).map(([k, inp]) => h("div", null, h("label", null, LINKS[k][0]), inp))),
    h("button", { class: "btn btn--primary", style: "margin-top:18px" }, me.nick ? "Сохранить" : "Готово"), err);

  v.append(h("div", { class: "prof" }, avatar(me, "ava--lg"),
      h("div", null, h("h1", null, me.nick || "Придумай ник", badges(me)),
        h("p", { class: "sub", style: "margin:0" }, S.user.email || "", " · ",
          h("a", { href: "#", onclick: async (e) => { e.preventDefault(); await sb.auth.signOut(); go("#/"); } }, "Выйти")))),
    h("div", { class: "panel" }, h("h2", null, me.nick ? "Профиль" : "Сначала — ник"),
      !me.nick && h("p", { class: "sub" }, "Без ника нельзя выкладывать базы. Его увидят все в каталоге."), form,
      me.role === "user" && h("p", { class: "hint", style: "margin-top:18px" }, "Делаешь видео или стримы по Rust? Напиши автору проекта — выдадим значок «Медиа», и ты попадёшь в раздел «Медиа».")));
  if (!me.nick) return;

  const mineBox = h("div", null, "Загружаю…"), favBox = h("div", null, "Загружаю…");
  v.append(h("div", { class: "panel" }, h("div", { class: "sec__head", style: "margin-bottom:10px" }, h("h2", null, "Мои базы на сайте"), h("a", { class: "btn btn--primary", href: "#/upload" }, "＋ Выложить базу")), mineBox),
           h("div", { class: "panel" }, h("h2", null, "Избранное"), favBox));
  const subs = await loadSubs((q) => q.eq("author", S.user.id));
  mineBox.replaceChildren(subs.length ? h("div", null, subs.map((s) => h("div", { class: "row" },
    h("div", { class: "grow" }, h("strong", null, s.name), h("br"), h("small", { class: "hint" }, date(s.created_at)),
      s.status === "rejected" && s.reject_reason && h("div", { class: "err", style: "margin:4px 0 0" }, "Причина: " + s.reject_reason)),
    h("span", { class: "tag " + (s.status === "approved" ? "tag--ok" : s.status === "rejected" ? "tag--bad" : "tag--warn") },
      s.status === "approved" ? "В каталоге" : s.status === "rejected" ? "Отклонена" : "На проверке"),
    h("button", { class: "btn btn--sm btn--danger", onclick: async () => {
      if (!confirm(`Удалить «${s.name}» с сайта? Это нельзя отменить.`)) return;
      const { error } = await sb.from("submissions").delete().eq("id", s.id);
      error ? toast(errText(error)) : render();
    } }, "Удалить")))) : h("p", { class: "sub" }, "Ты ещё ничего не выкладывал."));
  const all = await allBases();
  favBox.replaceChildren(grid(all.filter((b) => S.myFavs.has(b.key)).map((b) => baseCard(b)), "Жми ☆ на базах в каталоге — они появятся здесь."));
};

PAGES.upload = (v) => {
  if (!sb) return v.append(h("div", { class: "empty" }, "Скоро заработает."));
  if (!S.user) { store("rb_after", "#/upload"); return go("#/login"); }
  if (!S.me?.nick) { toast("Сначала придумай ник"); store("rb_after", "#/upload"); return go("#/me"); }
  let plan = null;
  const file = h("input", { type: "file", accept: ".json,application/json" });
  const name = h("input", { maxlength: 60, required: true }), desc = h("textarea", { maxlength: 500 });
  const info = h("div", { class: "meta", style: "margin-top:10px" }), err = h("p", { class: "err" });
  const send = h("button", { class: "btn btn--primary btn--big", disabled: true, style: "margin-top:18px" }, "Отправить на проверку");
  file.onchange = async () => {
    plan = null; send.disabled = true; err.textContent = ""; info.replaceChildren();
    const f = file.files[0]; if (!f) return;
    if (f.size > 100 * 1024) return (err.textContent = "Файл больше 100 КБ — это точно схема базы?");
    try {
      const p = JSON.parse((await f.text()).replace(/^﻿/, ""));
      if (typeof p.name !== "string" || !Array.isArray(p.pieces) || !p.pieces.length || !Array.isArray(p.steps) || !p.steps.length) throw 0;
      if (p.pieces.some((x) => typeof x?.type !== "string") || p.steps.some((x) => typeof x?.text !== "string")) throw 0;
      plan = p;
    } catch { return (err.textContent = "Это не схема RustBuilder. Нужен .json из программы: Библиотека → база → «Экспорт…»."); }
    name.value = plan.name.slice(0, 60); desc.value = (plan.description || "").slice(0, 500);
    const st = stats(plan);
    info.append(h("span", { class: "tag" }, TIERS[st.tier] || "Камень"), h("span", { class: "tag" }, st.foundations + " фунд."), h("span", { class: "tag" }, st.floors + " эт."), h("span", { class: "tag" }, st.steps + " шагов"));
    send.disabled = false;
  };
  v.append(h("h1", null, "Выложить базу"),
    h("p", { class: "sub" }, "Базу проверит автор проекта, после этого она появится в каталоге под твоим ником."),
    h("form", { class: "panel", onsubmit: async (e) => {
      e.preventDefault(); if (!plan) return;
      send.disabled = true; err.textContent = "";
      const data = { ...plan, name: name.value.trim(), description: desc.value.trim(), author: S.me.nick };
      const { error } = await sb.from("submissions").insert({ name: data.name, description: data.description || null, ...stats(plan), data });
      send.disabled = false;
      if (error) return (err.textContent = errText(error));
      toast("Отправлено! Статус — в твоём профиле."); go("#/me");
    } },
      h("label", null, "Файл базы (.json)"), file,
      h("p", { class: "hint" }, "Как получить файл: в RustBuilder открой «Библиотека», выбери свою базу и нажми «Экспорт…»."), info,
      h("label", null, "Название"), name, h("label", null, "Описание"), desc,
      h("p", { class: "hint" }, "Без ссылок и рекламы. До 3 баз на проверке одновременно."), send, err));
};
function stats(p) {
  const fnd = p.pieces.filter((x) => x.type === "foundation" || x.type === "triangle").length;
  const levels = p.pieces.map((x) => (x.type === "foundation" || x.type === "triangle" ? 1 : Number(x.floor) || 1));
  return { tier: String(p.tier || "stone").slice(0, 20), foundations: fnd, floors: Math.max(1, ...levels), steps: p.steps.length };
}

PAGES.admin = async (v) => {
  if (S.me?.role !== "admin") return v.append(h("div", { class: "empty" }, "Сюда может заходить только автор проекта."));
  v.append(h("h1", null, "Админка"), h("p", { class: "sub" }, "Проверка баз, значки «Медиа» и ники."));
  // 1. базы на проверке
  const pend = h("div", null, "Загружаю…");
  v.append(h("div", { class: "panel" }, h("h2", null, "Базы на проверке"), pend));
  const drawPending = async () => {
    const subs = await loadSubs((q) => q.eq("status", "pending"));
    pend.replaceChildren(subs.length ? h("div", null, subs.map((s) => h("div", { class: "row" },
      h("div", { class: "grow" }, h("strong", null, s.name), " · ", userLink(s.author, s.prof), h("br"),
        h("small", { class: "hint" }, `${s.foundations} фунд. · ${s.floors} эт. · ${s.steps} шагов · ${date(s.created_at)}`),
        s.description && h("p", { class: "hint" }, s.description)),
      h("button", { class: "btn btn--sm", onclick: () => download(s) }, "⬇ Проверить в программе"),
      h("button", { class: "btn btn--sm btn--primary", onclick: async () => { const { error } = await sb.rpc("admin_review", { sub: s.id, new_status: "approved" }); error ? toast(errText(error)) : (toast("Принята"), drawPending()); } }, "Принять"),
      h("button", { class: "btn btn--sm btn--danger", onclick: async () => {
        const reason = prompt("Причина отказа (автор её увидит):", "Схема не собирается в программе"); if (reason === null) return;
        const { error } = await sb.rpc("admin_review", { sub: s.id, new_status: "rejected", reason }); error ? toast(errText(error)) : (toast("Отклонена"), drawPending());
      } }, "Отклонить")))) : h("p", { class: "sub" }, "Пусто — всё проверено ✓"));
  };
  drawPending();

  // 2. игроки: медиа и ники
  const q = h("input", { type: "search", placeholder: "Ник игрока (можно часть)" }); const res = h("div");
  const search = async () => {
    let req = sb.from("profiles").select("id,nick,role,avatar_url,created_at").order("created_at", { ascending: false }).limit(30);
    req = q.value.trim() ? req.ilike("nick", `%${q.value.trim().replace(/[%_]/g, "")}%`) : req.eq("role", "media");
    const { data } = await req;
    res.replaceChildren(...(data || []).map((p) => {
      const role = h("select", { style: "width:auto", disabled: p.role === "admin" },
        h("option", { value: "user" }, "Игрок"), h("option", { value: "media" }, "★ Медиа"), p.role === "admin" && h("option", { value: "admin" }, "Автор проекта"));
      role.value = p.role;
      role.onchange = async () => { const { error } = await sb.rpc("admin_set_role", { target: p.id, new_role: role.value }); toast(error ? errText(error) : "Готово"); };
      return h("div", { class: "row" }, h("div", { class: "grow" }, userLink(p.nick, p)), role,
        h("button", { class: "btn btn--sm", onclick: async () => {
          const n = prompt(`Новый ник для «${p.nick}» (можно зарезервированный):`, p.nick || ""); if (!n) return;
          const { error } = await sb.rpc("admin_set_nick", { target: p.id, new_nick: n }); toast(error ? errText(error) : "Ник выдан"); if (!error) search();
        } }, "Выдать ник"));
    }));
    if (!res.children.length) res.append(h("p", { class: "sub" }, q.value.trim() ? "Никого не нашёл." : "Медиа пока нет. Найди игрока по нику и выдай значок."));
  };
  q.oninput = () => { clearTimeout(q.t); q.t = setTimeout(search, 300); };
  v.append(h("div", { class: "panel" }, h("h2", null, "Игроки: медиа и ники"),
    h("p", { class: "sub" }, "Чтобы отдать игроку зарезервированный ник: он регистрируется с любым ником → ищешь его здесь → «Выдать ник»."), q, res));
  search();

  // 3. резерв ников
  const rlist = h("div"), rn = h("input", { placeholder: "Ник" }), rnote = h("input", { placeholder: "Заметка (необязательно)" });
  const drawRes = async () => {
    const { data } = await sb.from("reserved_nicks").select("*").order("created_at", { ascending: false });
    rlist.replaceChildren(...(data || []).map((r) => h("div", { class: "row" }, h("strong", { class: "grow" }, r.nick, " ", h("small", { class: "hint" }, r.note || "")),
      h("button", { class: "btn btn--sm btn--danger", onclick: async () => { await sb.from("reserved_nicks").delete().eq("nick_norm", r.nick_norm); drawRes(); } }, "Освободить"))));
  };
  v.append(h("div", { class: "panel" }, h("h2", null, "Зарезервированные ники"),
    h("p", { class: "sub" }, "Эти ники никто не может взять сам — только получить от тебя."),
    h("form", { class: "toolbar", onsubmit: async (e) => {
      e.preventDefault(); if (!rn.value.trim()) return;
      const { error } = await sb.from("reserved_nicks").upsert({ nick_norm: norm(rn.value), nick: rn.value.trim(), note: rnote.value.trim() || null });
      if (error) return toast(errText(error)); rn.value = rnote.value = ""; drawRes();
    } }, rn, rnote, h("button", { class: "btn btn--primary" }, "Зарезервировать")),
    h("button", { class: "btn btn--sm", onclick: async () => {
      try {
        const j = await (await fetch(RAW + "nicknames.json", { cache: "no-cache" })).json();
        const rows = (j.nicks || []).map((n) => ({ nick_norm: norm(n.nick), nick: n.nick, note: n.github ? "GitHub: " + n.github : "резерв с GitHub" }));
        const { error } = await sb.from("reserved_nicks").upsert(rows, { onConflict: "nick_norm", ignoreDuplicates: true });
        toast(error ? errText(error) : `Подтянуто ников: ${rows.length}`); drawRes();
      } catch (e) { toast(errText(e)); }
    } }, "⟳ Подтянуть ники из каталога GitHub"), rlist));
  drawRes();
};

// ─── роутер ───────────────────────────────────────────────────────────────────
async function render() {
  const [, route = "", arg] = (location.hash || "#/").split("/");
  document.querySelectorAll("[data-nav]").forEach((a) => a.classList.toggle("on", a.dataset.nav === route));
  document.title = "RustBuilder";
  const home = !route || !PAGES[route];
  $("#home").hidden = !home; $("#view").hidden = home;
  if (home) return homeExtras();
  const v = h("div"); $("#view").replaceChildren(v); window.scrollTo(0, 0);
  try { await PAGES[route](v, arg ? decodeURIComponent(arg) : ""); }
  catch (e) { console.error(e); v.append(h("p", { class: "err" }, errText(e))); }
}
async function homeExtras() {
  const gh = await loadGh();
  if (gh.length) $("#basesCount").textContent = `${gh.length} ${plural(gh.length, ["готовая база", "готовые базы", "готовых баз"])}`;
  if (!sb) return;
  const { data } = await sb.from("profiles").select("nick,avatar_url,role,bio").eq("role", "media").not("nick", "is", null).limit(8);
  $("#homeMedia").hidden = !data?.length;
  if (data?.length) $("#homeMediaList").replaceChildren(peopleList(data, ""));
}

$("#dl").href = C.download; $("#donate").href = C.donate;
window.addEventListener("hashchange", render);
if (sb) {
  sb.auth.onAuthStateChange((ev, session) => {
    // Внутри колбэка нельзя ждать другие запросы Supabase — выходим из него через setTimeout.
    setTimeout(async () => {
      const was = S.user?.id; S.user = session?.user || null;
      if (ev === "PASSWORD_RECOVERY") { await refreshMe(); renderAccount(); return go("#/reset"); }
      if (was === S.user?.id && S.authChecked) return;
      S.authChecked = true;
      await refreshMe(); renderAccount();
      if (/[?&]code=/.test(location.search)) history.replaceState(null, "", location.pathname + location.hash);
      if (ev === "SIGNED_IN" && !was) {
        if (S.me && !S.me.nick) { toast("Добро пожаловать! Придумай ник"); return go("#/me"); }
        const after = store("rb_after"); store("rb_after", null);
        if (after || location.hash.startsWith("#/login")) return go(after || "#/me");
      }
      render();
    }, 0);
  });
} else render();
renderAccount();
