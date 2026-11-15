// Генератор баз RustBuilder: популярные типы построек, описанные кодом.
// Сам считает внешние стены, раскладывает по шагам и пишет JSON.
//   node tools/gen-bases.mjs
// Результат: RustBuilder/Builds/*.json (встроенные в приложение) и catalog-repo/bases/*.json (каталог).
import { writeFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
const OUT_APP = join(ROOT, "RustBuilder", "Builds");
const OUT_CATALOG = join(ROOT, "catalog-repo", "bases");
const H = Math.sqrt(3) / 2;

// ---------- геометрия (та же, что в приложении) ----------

class Base {
  constructor(name, description, tier = "stone") {
    Object.assign(this, { name, description, tier, author: "RustBuilder" });
    this.pieces = [];
    this.steps = [];
    this.polys = new Map();      // id -> вершины
    this.kind = new Map();       // id -> "sq" | "tri"
    this.at = new Map();         // "x,y" -> id квадрата
    this.n = {};
  }

  id(prefix) { this.n[prefix] = (this.n[prefix] ?? 0) + 1; return `${prefix}${this.n[prefix]}`; }

  sq(x, y) {
    const id = this.id("f");
    this.pieces.push({ id, type: "foundation", x, y });
    this.polys.set(id, [[x, y], [x + 1, y], [x + 1, y + 1], [x, y + 1]]);
    this.kind.set(id, "sq");
    this.at.set(`${x},${y}`, id);
    return id;
  }

  tri(on, edge) {
    const id = this.id("t");
    const [a, b] = this.edge(on, edge);
    const mid = [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
    const d = [b[0] - a[0], b[1] - a[1]];
    const n = [d[1], -d[0]];
    this.pieces.push({ id, type: "triangle", on, edge });
    this.polys.set(id, [b, a, [mid[0] + n[0] * H, mid[1] + n[1] * H]]);
    this.kind.set(id, "tri");
    return id;
  }

  edgeNames(id) { return this.kind.get(id) === "tri" ? ["B", "L", "R"] : ["N", "E", "S", "W"]; }

  edge(id, name) {
    const v = this.polys.get(id);
    const i = this.edgeNames(id).indexOf(name);
    if (i < 0) throw new Error(`нет стороны ${name} у ${id}`);
    return [v[i], v[(i + 1) % v.length]];
  }

  key(id, name) {
    const [a, b] = this.edge(id, name);
    return `${Math.round((a[0] + b[0]) * 500)},${Math.round((a[1] + b[1]) * 500)}`;
  }

  /** Внешние стороны группы фундаментов: [{on, edge}] */
  perimeter(ids) {
    const count = new Map();
    const owner = [];
    for (const id of ids)
      for (const e of this.edgeNames(id)) {
        const k = this.key(id, e);
        count.set(k, (count.get(k) ?? 0) + 1);
        if (e !== "B") owner.push({ on: id, edge: e, k });
      }
    return owner.filter((o) => count.get(o.k) === 1).map(({ on, edge }) => ({ on, edge }));
  }

  /** Общая сторона двух фундаментов */
  between(a, b) {
    for (const e of this.edgeNames(a)) for (const f of this.edgeNames(b))
      if (this.key(a, e) === this.key(b, f)) return { on: a, edge: e };
    throw new Error(`${a} и ${b} не соседи`);
  }

  sqAt(x, y) { const id = this.at.get(`${x},${y}`); if (!id) throw new Error(`нет квадрата ${x},${y}`); return id; }
  rect(x0, y0, w, h) { const r = []; for (let y = y0; y < y0 + h; y++) for (let x = x0; x < x0 + w; x++) r.push(this.sq(x, y)); return r; }

  add(type, props) {
    const prefix = { wall: "w", doorway: "d", window: "o", halfwall: "h", floor: "c", stairs: "s", tc: "tc" }[type];
    const id = this.id(prefix);
    const p = { id, type, ...props };
    if (p.floor === 1 && type !== "floor") delete p.floor;
    this.pieces.push(p);
    return id;
  }

  /** Стены по списку сторон; special: ключ стороны -> {type, door, lock, entrance} */
  walls(edges, floor = 1, special = new Map()) {
    const ids = [];
    for (const e of edges) {
      const { type = "wall", ...extra } = special.get(this.key(e.on, e.edge)) ?? {};
      ids.push(this.add(type, { on: e.on, edge: e.edge, floor, ...extra }));
    }
    return ids;
  }

  floors(ids, floor, skip = []) { return ids.filter((id) => !skip.includes(id)).map((on) => this.add("floor", { on, floor })); }
  step(text, pieces, floor) { this.steps.push({ text, pieces, ...(floor ? { floor } : {}) }); }

  toJSON() {
    const clean = (p) => Object.fromEntries(Object.entries(p).filter(([, v]) => v !== undefined));
    return { name: this.name, description: this.description, author: this.author, tier: this.tier,
             pieces: this.pieces.map(clean), steps: this.steps };
  }
}

const door = (b, on, edge, props) => [b.key(on, edge), { type: "doorway", ...props }];
const win = (b, on, edge) => [b.key(on, edge), { type: "window" }];
const special = (...pairs) => new Map(pairs);
const UPGRADE = (tier) => `Улучши всё до ${{ stone: "камня", metal: "металла", hqm: "МВК", wood: "дерева" }[tier]} молотком (ПКМ по блоку → улучшить). Закрой двери и поставь коды.`;

// ---------- базы ----------

const bases = {};

// 1. Соло 1x1
{
  const b = new Base("Соло 1x1", "Самый быстрый старт для одного: дешёвый, ставится за минуту", "stone");
  const f = b.sq(0, 0);
  const tc = b.add("tc", { on: f });
  const sp = special(door(b, f, "S", { door: "wood", lock: "key", entrance: true }));
  const w = b.walls(b.perimeter([f]), 1, sp);
  const roof = b.floors([f], 2);
  b.step("Поставь квадратный фундамент.", [f]);
  b.step("Поставь шкаф (TC) в угол, подальше от входа.", [tc]);
  b.step("Стены вокруг и дверной проём с дверью.", w);
  b.step("Крыша.", roof);
  b.step(UPGRADE(b.tier), [], 1);
  bases["03_solo_1x1"] = b;
}

// 2. Тришка: квадрат + 4 треугольника
{
  const b = new Base("Тришка (квадрат + 4 треугольника)", "Компактная соло-база: много углов, шкаф в центре", "stone");
  const f = b.sq(0, 0);
  const t = ["N", "E", "S", "W"].map((e) => b.tri(f, e));
  const tc = b.add("tc", { on: f });
  const sp = special(door(b, t[2], "R", { door: "sheet", lock: "code", entrance: true }));
  const w = b.walls(b.perimeter([f, ...t]), 1, sp);
  const roof = b.floors([f, ...t], 2);
  b.step("Поставь квадратный фундамент в центре.", [f]);
  b.step("Приставь треугольные фундаменты ко всем четырём сторонам.", t);
  b.step("Шкаф в центр.", [tc]);
  b.step("Стены по всем внешним сторонам треугольников. Одна из них — дверной проём (вход).", w);
  b.step("Крыша над всеми фундаментами.", roof);
  b.step(UPGRADE(b.tier), [], 1);
  bases["04_solo_trishka"] = b;
}

// 3. 3x1 со шлюзом, 2 этажа
{
  const b = new Base("3x1 шлюз + 2 этаж", "Вытянутая база: шлюз на входе, лестница, лут наверху", "stone");
  const [f1, f2, f3] = b.rect(0, 0, 3, 1);
  const tc = b.add("tc", { on: f3 });
  const sp1 = special(door(b, f1, "W", { door: "sheet", lock: "code", entrance: true }));
  const w1 = b.walls(b.perimeter([f1, f2, f3]), 1, sp1);
  const e12 = b.between(f1, f2);
  const inner = b.add("doorway", { ...e12, door: "sheet", lock: "code" });
  const st = b.add("stairs", { on: f2, floor: 1 });
  const c2 = b.floors([f1, f2, f3], 2, [f2]);
  const w2 = b.walls(b.perimeter([f1, f2, f3]), 2, special(win(b, f1, "W"), win(b, f3, "E")));
  const roof = b.floors([f1, f2, f3], 3);
  b.step("Три квадратных фундамента в ряд.", [f1, f2, f3]);
  b.step("Шкаф в дальний фундамент.", [tc]);
  b.step("Стены первого этажа. Вход — с короткой стороны первого фундамента.", w1);
  b.step("Шлюз: проём с дверью между первым и вторым фундаментом. Теперь чтобы войти, нужно пройти две двери.", [inner]);
  b.step("Лестница в среднем фундаменте.", [st]);
  b.step("Перекрытия второго этажа (над лестницей не ставь).", c2);
  b.step("Стены второго этажа, два окна по краям.", w2);
  b.step("Крыша.", roof);
  b.step(UPGRADE(b.tier), [], 1);
  bases["05_3x1_airlock"] = b;
}

// 4. 2x2 трёхэтажка с лестницами
{
  const b = new Base("2x2 трёхэтажная", "Классика для соло/дуо: шлюз, лестница, три этажа", "stone");
  const [a, bb, c, d] = b.rect(0, 0, 2, 2);       // a(0,0) b(1,0) c(0,1) d(1,1)
  const tc = b.add("tc", { on: bb });
  const all = [a, bb, c, d];
  const w1 = b.walls(b.perimeter(all), 1, special(door(b, c, "S", { door: "sheet", lock: "code", entrance: true })));
  const air1 = b.add("wall", b.between(c, d));
  const air2 = b.add("doorway", { ...b.between(c, a), door: "sheet", lock: "code" });
  const s1 = b.add("stairs", { on: d, floor: 1 });
  const c2 = b.floors(all, 2, [d]);
  const w2 = b.walls(b.perimeter(all), 2, special(win(b, a, "N")));
  const s2 = b.add("stairs", { on: d, floor: 2 });
  const c3 = b.floors(all, 3, [d]);
  const w3 = b.walls(b.perimeter(all), 3, special(win(b, a, "W"), win(b, bb, "E")));
  const roof = b.floors(all, 4);
  b.step("Четыре фундамента квадратом 2x2.", all);
  b.step("Шкаф в верхний правый фундамент.", [tc]);
  b.step("Стены первого этажа, вход — внизу слева.", w1);
  b.step("Шлюз в нижнем левом фундаменте: стена справа и проём с дверью сверху.", [air1, air2]);
  b.step("Лестница в нижнем правом фундаменте.", [s1]);
  b.step("Перекрытия второго этажа (кроме клетки с лестницей).", c2);
  b.step("Стены второго этажа.", w2);
  b.step("Вторая лестница — над первой.", [s2]);
  b.step("Перекрытия третьего этажа (кроме клетки с лестницей).", c3);
  b.step("Стены третьего этажа, два окна.", w3);
  b.step("Крыша.", roof);
  b.step(UPGRADE(b.tier), [], 1);
  bases["06_2x2_three_floors"] = b;
}

// 5. 2x2 с сотами (honeycomb) — ядро 2x2 + кольцо фундаментов вокруг
{
  const b = new Base("2x2 с сотами", "Ядро 2x2 + кольцо сот вокруг: рейдить дороже в 2–3 раза", "stone");
  const ring = [], core = [];
  for (let y = 0; y < 4; y++) for (let x = 0; x < 4; x++) {
    const id = b.sq(x, y);
    (x >= 1 && x <= 2 && y >= 1 && y <= 2 ? core : ring).push(id);
  }
  const all = [...core, ...ring];
  const entry = b.sqAt(0, 1);
  const tc = b.add("tc", { on: b.sqAt(2, 2) });
  const coreW1 = b.walls(b.perimeter(core), 1, special(door(b, b.sqAt(1, 1), "W", { door: "sheet", lock: "code" })));
  const st = b.add("stairs", { on: b.sqAt(1, 2), floor: 1 });
  const outerW1 = b.walls(b.perimeter(all), 1, special(door(b, entry, "W", { door: "sheet", lock: "code", entrance: true })));
  const c2 = b.floors(all, 2, [b.sqAt(1, 2)]);
  const coreW2 = b.walls(b.perimeter(core), 2);
  const outerW2 = b.walls(b.perimeter(all), 2);
  const roof = b.floors(all, 3);
  b.step("Ядро: 4 фундамента квадратом 2x2.", core);
  b.step("Шкаф в ядро, в дальний угол от входа.", [tc]);
  b.step("Стены ядра первого этажа. Одна — проём с дверью (вход в ядро).", coreW1);
  b.step("Лестница в ядре.", [st]);
  b.step("Соты: кольцо из 12 фундаментов вокруг ядра.", ring);
  b.step("Внешние стены сот. Вход — снаружи напротив двери ядра (получается шлюз).", outerW1);
  b.step("Перекрытия второго этажа над ядром и сотами (кроме лестницы).", c2);
  b.step("Стены ядра второго этажа.", coreW2);
  b.step("Внешние стены сот второго этажа.", outerW2);
  b.step("Крыша.", roof);
  b.step(UPGRADE(b.tier) + " Внешние стены сот лучше сразу в металл.", [], 1);
  bases["07_2x2_honeycomb"] = b;
}

// 6. 2x2 с площадкой для стрельбы на крыше
{
  const b = new Base("2x2 + стрелковая крыша", "2x2 с площадкой на крыше: полустены, чтобы отстреливаться", "stone");
  const all = b.rect(0, 0, 2, 2);
  const [a, bb, c, d] = all;
  const tc = b.add("tc", { on: a });
  const w1 = b.walls(b.perimeter(all), 1, special(door(b, d, "S", { door: "sheet", lock: "code", entrance: true })));
  const air = [b.add("wall", b.between(d, c)), b.add("doorway", { ...b.between(d, bb), door: "sheet", lock: "code" })];
  const s1 = b.add("stairs", { on: c, floor: 1 });
  const c2 = b.floors(all, 2, [c]);
  const w2 = b.walls(b.perimeter(all), 2);
  const s2 = b.add("stairs", { on: c, floor: 2 });
  const c3 = b.floors(all, 3, [c]);
  const hw = b.perimeter(all).map((e) => b.add("halfwall", { ...e, floor: 3 }));
  b.step("Фундаменты 2x2.", all);
  b.step("Шкаф в верхний левый угол.", [tc]);
  b.step("Стены первого этажа, вход — снизу справа.", w1);
  b.step("Шлюз в нижнем правом фундаменте.", air);
  b.step("Лестница.", [s1]);
  b.step("Перекрытия второго этажа.", c2);
  b.step("Стены второго этажа.", w2);
  b.step("Лестница на крышу.", [s2]);
  b.step("Пол крыши.", c3);
  b.step("Полустены по краю крыши — из-за них удобно стрелять.", hw);
  b.step(UPGRADE(b.tier), [], 1);
  bases["08_2x2_shooting_roof"] = b;
}

// 7. 3x3 для трио
{
  const b = new Base("3x3 для трио", "Просторная база на 2–3 человек: шлюз, комната со шкафом в центре", "stone");
  const all = b.rect(0, 0, 3, 3);
  const S = (x, y) => b.sqAt(x, y);
  const tc = b.add("tc", { on: S(1, 1) });
  const w1 = b.walls(b.perimeter(all), 1, special(door(b, S(0, 2), "S", { door: "sheet", lock: "code", entrance: true }), win(b, S(2, 0), "E")));
  const air = [b.add("wall", b.between(S(0, 2), S(1, 2))), b.add("doorway", { ...b.between(S(0, 2), S(0, 1)), door: "sheet", lock: "code" })];
  const center = [
    b.add("doorway", { ...b.between(S(1, 1), S(0, 1)), door: "armored", lock: "code" }),
    b.add("wall", b.between(S(1, 1), S(1, 0))),
    b.add("wall", b.between(S(1, 1), S(2, 1))),
    b.add("wall", b.between(S(1, 1), S(1, 2))),
  ];
  const s1 = b.add("stairs", { on: S(2, 0), floor: 1 });
  const c2 = b.floors(all, 2, [S(2, 0)]);
  const w2 = b.walls(b.perimeter(all), 2, special(win(b, S(0, 0), "N"), win(b, S(2, 2), "S"), win(b, S(0, 1), "W")));
  const roof = b.floors(all, 3);
  b.step("9 фундаментов квадратом 3x3.", all);
  b.step("Шкаф в центральный фундамент.", [tc]);
  b.step("Внешние стены первого этажа. Вход — в нижнем левом углу.", w1);
  b.step("Шлюз в нижнем левом фундаменте: стена справа, проём с дверью сверху.", air);
  b.step("Комната со шкафом: стены вокруг центра, вход слева через бронированную дверь.", center);
  b.step("Лестница в верхнем правом углу.", [s1]);
  b.step("Перекрытия второго этажа.", c2);
  b.step("Стены второго этажа с окнами.", w2);
  b.step("Крыша.", roof);
  b.step(UPGRADE(b.tier), [], 1);
  bases["09_3x3_trio"] = b;
}

// 8. Клан 4x4 с сотами
{
  const b = new Base("Клан 4x4 с сотами", "Большая база для клана: ядро 4x4 в 3 этажа + кольцо сот", "metal");
  const ring = [], core = [];
  for (let y = 0; y < 6; y++) for (let x = 0; x < 6; x++) {
    const id = b.sq(x, y);
    (x >= 1 && x <= 4 && y >= 1 && y <= 4 ? core : ring).push(id);
  }
  const all = [...core, ...ring];
  const S = (x, y) => b.sqAt(x, y);
  const tc = b.add("tc", { on: S(4, 4) });
  const coreW1 = b.walls(b.perimeter(core), 1, special(door(b, S(1, 2), "W", { door: "armored", lock: "code" })));
  const split = [1, 2, 3, 4].map((y) => y === 3
    ? b.add("doorway", { ...b.between(S(2, y), S(3, y)), door: "armored", lock: "code" })
    : b.add("wall", b.between(S(2, y), S(3, y))));
  const st1 = b.add("stairs", { on: S(1, 4), floor: 1 });
  const outerW1 = b.walls(b.perimeter(all), 1, special(door(b, S(0, 2), "W", { door: "sheet", lock: "code", entrance: true })));
  const c2 = b.floors(all, 2, [S(1, 4)]);
  const coreW2 = b.walls(b.perimeter(core), 2);
  const outerW2 = b.walls(b.perimeter(all), 2);
  const st2 = b.add("stairs", { on: S(1, 4), floor: 2 });
  const c3 = b.floors([...core, ...ring], 3, [S(1, 4)]);
  const coreW3 = b.walls(b.perimeter(core), 3, special(win(b, S(2, 1), "N"), win(b, S(4, 3), "E")));
  const roof = b.floors(core, 4);
  b.step("Ядро: 16 фундаментов квадратом 4x4.", core);
  b.step("Шкаф в нижний правый угол ядра.", [tc]);
  b.step("Стены ядра. Вход в ядро слева — бронированная дверь.", coreW1);
  b.step("Перегородка посередине ядра с проходом: две комнаты, лут в дальней.", split);
  b.step("Лестница в нижнем левом углу ядра.", [st1]);
  b.step("Соты: кольцо из 20 фундаментов вокруг ядра.", ring);
  b.step("Внешние стены сот. Вход снаружи — напротив двери ядра.", outerW1);
  b.step("Перекрытия второго этажа.", c2);
  b.step("Стены ядра второго этажа.", coreW2);
  b.step("Внешние стены сот второго этажа.", outerW2);
  b.step("Лестница на третий этаж.", [st2]);
  b.step("Перекрытия третьего этажа (над сотами это их крыша).", c3);
  b.step("Стены ядра третьего этажа с двумя окнами.", coreW3);
  b.step("Крыша ядра.", roof);
  b.step(UPGRADE(b.tier) + " Если хватает МВК — внешние стены сот в МВК.", [], 1);
  bases["10_clan_4x4_honeycomb"] = b;
}

// ---------- запись ----------

mkdirSync(OUT_APP, { recursive: true });
mkdirSync(OUT_CATALOG, { recursive: true });
for (const [file, b] of Object.entries(bases)) {
  const json = JSON.stringify(b.toJSON(), null, 2) + "\n";
  writeFileSync(join(OUT_APP, file + ".json"), json);
  writeFileSync(join(OUT_CATALOG, file.replace(/^\d+_/, "").replace(/_/g, "-") + ".json"), json);
  console.log(`${file}: ${b.pieces.length} блоков, ${b.steps.length} шагов`);
}
