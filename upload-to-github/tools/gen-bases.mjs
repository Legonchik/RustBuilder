// Генератор баз RustBuilder: популярные типы построек, описанные кодом.
// Сам считает внешние стены, раскладывает по шагам и пишет JSON.
//   node tools/gen-bases.mjs
// Результат: RustBuilder/Builds/*.json (встроенные в приложение) и catalog-repo/bases/*.json (каталог).
import { writeFileSync, mkdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
const OUT_APP = join(ROOT, "RustBuilder", "Builds");
const OUT_CATALOG = join(ROOT, "catalog-repo", "bases");
const H = Math.sqrt(3) / 2;

// ---------- геометрия (та же, что в приложении) ----------

class Base {
  constructor(name, description, tier = "stone") {
    Object.assign(this, { name, description, tier, author: "Claude" });
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
             pieces: this.pieces.map(clean), steps: this.steps, ...(this.interiors ? { interiors: this.interiors } : {}) };
  }
}

const door = (b, on, edge, props) => [b.key(on, edge), { type: "doorway", ...props }];
const special = (...pairs) => new Map(pairs);

// Правила стройки, которых держится генератор:
//  • Окон без бойницы нет вообще: пустой оконный проём — это дыра в дом.
//  • Бойницы — только в базах с внешним слоем (буфферки, щупальца, соты). В стартовых базах стены глухие:
//    каждое окно — лишнее слабое место, а стартер должен быть дешёвым и закрытым.
//  • Этаж с бойницами отделён от лута дверью: подъём на нём стоит в «будке» со стенами и дверью.
//    Сломали бойницу — дальше будки всё равно не пройти.
//  • Выход на крышу — тоже будка с дверью и потолком. Открытая дыра над подъёмом = вход в дом с крыши.
//  • Подъём — в тупиковой угловой клетке: не в аирлоке, не на пути от входа к шкафу.
//    Подъёмы стоят друг над другом, над подъёмом нет перекрытия, мебель на клетку с подъёмом не ставится.

/** Будка вокруг подъёма на этаже level: стены на всех сторонах клетки, проём с дверью в сторону клетки doorTo */
function stairCabin(b, tile, level, doorTo, withTop) {
  const doorEdge = b.between(tile, doorTo).edge;
  const walls = b.edgeNames(tile).filter((e) => e !== "B").map((e) => e === doorEdge
    ? b.add("doorway", { on: tile, edge: e, floor: level, door: "sheet", lock: "code" })
    : b.add("wall", { on: tile, edge: e, floor: level }));
  const top = withTop ? [b.add("floor", { on: tile, floor: level + 1 })] : [];
  return { walls, top };
}

const tileKeys = (b, tile) => new Set(b.edgeNames(tile).map((e) => b.key(tile, e)));

/**
 * Отстрел на этаже level (только для баз с буфферками/щупальцами/сотами): оконные проёмы с бойницами
 * по периметру — горизонтальные и вертикальные через одну. Подъём на этом этаже — в будке с дверью.
 */
function shooting(b, ids, level, stairTile, doorTo) {
  const skip = tileKeys(b, stairTile);
  const wins = b.perimeter(ids).filter((e) => !skip.has(b.key(e.on, e.edge)))
    .map((e, i) => b.add("window", { ...e, floor: level, embrasure: i % 2 === 0 ? "horizontal" : "vertical" }));
  const cabin = stairCabin(b, stairTile, level, doorTo, false);
  return {
    steps(prefix = "") {
      b.step(prefix + "Будка подъёма: стены вокруг клетки с подъёмом и проём с дверью и кодом. Если сломают бойницу — дальше этой двери к луту не пройти.", cabin.walls);
      b.step(prefix + SHOOT, wins);
    },
  };
}
const SHOOT = "Отстрел: оконные проёмы по периметру, в каждом бойница — через одну горизонтальная (широкий обзор) и вертикальная (целиться вниз, под стены). Пустых окон не оставляй — это дыра в дом.";

/** Отстрелочная крыша: подъём с верхнего этажа, будка выхода с дверью и потолком, пол крыши и полустены по краю */
function shootRoof(b, ids, level, stairsOn, doorTo) {
  const st = b.add("stairs", { on: stairsOn, floor: level - 1 });
  const roof = b.floors(ids, level, [stairsOn]);
  const cabin = stairCabin(b, stairsOn, level, doorTo, true);
  const skip = tileKeys(b, stairsOn);
  const hw = b.perimeter(ids).filter((e) => !skip.has(b.key(e.on, e.edge))).map((e) => b.add("halfwall", { ...e, floor: level }));
  return {
    steps() {
      b.step("Подъём на крышу — ровно над предыдущим.", [st]);
      b.step("Пол крыши над всеми клетками, кроме клетки с подъёмом.", roof);
      b.step("Будка выхода на крышу: стены вокруг подъёма, проём с дверью и кодом в сторону крыши, сверху потолок. Без неё крыша — открытый вход в дом.", [...cabin.walls, ...cabin.top]);
      b.step("Полустены по краю крыши. Присел — ты в укрытии, встал — стреляешь сверху.", hw);
    },
  };
}

// ---------- интерьер ----------

const ITEM_STEP = {
  box: "Большие ящики по углам клеток — сюда весь лут.",
  furnace: "Печки. Оставь к ним проход — их часто открываешь.",
  workbench: "Верстак — крафтишь прямо рядом с лутом.",
  repair: "Ремонтный верстак — чинить броню и оружие.",
  bag: "Спальники — по одному на каждого игрока, чтобы возрождаться дома.",
  locker: "Шкафчики для одежды — переодеться за секунду после смерти.",
};

/**
 * Три варианта мебели. living — клетки, где можно ставить, по порядку важности: { on, floor, slots }.
 * players — на сколько человек база.
 */
function makeInteriors(living, players) {
  const spots = living.flatMap(({ on, floor = 1, slots }) => Array.from({ length: slots }, (_, slot) => ({ on, floor, slot })));
  const C = spots.length;
  const bags = Array(players).fill("bag");                       // спальники — в каждом варианте, по одному на игрока
  const fill = (head) => [...head, ...Array(Math.max(0, C - head.length)).fill("box")].slice(0, C);
  const variants = [
    { name: "Лутовая", description: `Спальники на ${players} чел., остальное — ящики под лут.`, items: fill([...bags]) },
    { name: "Крафт и плавка", description: `Спальники, верстак, ремонтный верстак и печки, остальное — ящики.`,
      items: fill([...bags, "workbench", "furnace", ...(C >= 6 ? ["repair"] : []), ...(C >= 9 ? ["furnace"] : [])]) },
    { name: "Для команды", description: `Спальники и шкафчики на ${players} чел., верстак, печка и ящики.`,
      items: fill([...bags, ...Array(Math.min(players, 3)).fill("locker"), "workbench", "furnace"]) },
  ];
  const multiFloor = new Set(spots.map((s) => s.floor)).size > 1;
  return variants.map((v, vi) => {
    const pieces = v.items.map((type, i) => {
      const { on, floor, slot } = spots[i];
      return { id: `i${vi + 1}_${type}${i + 1}`, type, on, ...(floor > 1 ? { floor } : {}), slot };
    });
    const steps = [];
    for (const type of Object.keys(ITEM_STEP))
      for (const floor of [...new Set(pieces.filter((p) => p.type === type).map((p) => p.floor ?? 1))].sort()) {
        const ids = pieces.filter((p) => p.type === type && (p.floor ?? 1) === floor).map((p) => p.id);
        steps.push({ text: ITEM_STEP[type] + (multiFloor ? ` Этаж ${floor}.` : ""), pieces: ids });
      }
    return { name: v.name, description: v.description, pieces, steps };
  });
}

/** Клетки для мебели: в квадрат влезает 2 больших предмета вдоль стен (нужен проход), в треугольник — 1 */
const L = (b, on, floor = 1, slots) => ({ on, floor, slots: slots ?? (b.kind.get(on) === "tri" ? 1 : 2) });

// ---------- щупальца ----------

const TENTACLE_STEPS = [
  "Щупальца: фундаменты-коридоры из базы наружу, по два квадрата в линию.",
  "Стены щупалец — окна с бойницами по бокам и в торце, вход из базы — проём с дверью. Отсюда простреливаешь подходы со всех сторон, не выходя из дома.",
  "Потолки над щупальцами.",
];

const DIRS = { N: [0, -1], E: [1, 0], S: [0, 1], W: [-1, 0] };

/**
 * Щупальце: коридор из n квадратов наружу от стороны квадрата ядра.
 * Вход из ядра — проём с дверью, по бокам и в торце — окна с бойницами, сверху потолок.
 * Возвращает { squares, doorKey, build() } — doorKey нужен, чтобы стена ядра на этой стороне стала дверным проёмом.
 */
function tentacle(b, from, dir, n = 2) {
  const f = b.pieces.find((p) => p.id === from);
  const [dx, dy] = DIRS[dir];
  const squares = Array.from({ length: n }, (_, i) => b.sq(f.x + dx * (i + 1), f.y + dy * (i + 1)));
  const doorKey = b.key(from, dir);
  return {
    squares,
    door: [doorKey, { type: "doorway", door: "sheet", lock: "code" }],
    build() {
      const edges = b.perimeter(squares).filter((e) => b.key(e.on, e.edge) !== doorKey);
      const walls = edges.map((e, i) => b.add("window", { ...e, embrasure: i % 2 === 0 ? "horizontal" : "vertical" }));
      const roof = b.floors(squares, 2);
      return { walls, roof };
    },
  };
}

// ---------- треугольный аирлок и буфферки ----------

function near(p, q) { return Math.abs(p[0] - q[0]) < 1e-6 && Math.abs(p[1] - q[1]) < 1e-6; }

/** Сторона треугольника (L/R), которая выходит из точки v */
function triSideAt(b, t, v) {
  return ["L", "R"].find((e) => b.edge(t, e).some((p) => near(p, v)));
}

/**
 * Полоса буфферок вдоль одной стороны ядра: треугольник на каждой клетке + треугольники-заполнители
 * в щелях между ними. segs — по порядку вдоль стороны: { on, edge } или { tri } (готовый треугольник, например аирлок).
 */
function bufferStrip(b, segs) {
  const tris = segs.map((s) => s.tri ?? b.tri(s.on, s.edge));
  const fillers = [];
  for (let i = 0; i < tris.length - 1; i++) {
    const [p, q] = b.edge(tris[i + 1], "B");
    const shared = b.edge(tris[i], "B").find((v) => near(v, p) || near(v, q));
    fillers.push(b.tri(tris[i], triSideAt(b, tris[i], shared)));
  }
  return { tris, fillers };
}

/** Треугольный аирлок на стороне квадрата: внешняя дверь на стороне side ("left"/"right" — как видно на схеме снаружи) */
function triAirlock(b, on, edge, side) {
  const t = b.tri(on, edge);
  // L/R считаются, если смотреть от основания наружу; снаружи (лицом к базе) лево и право меняются местами
  const outer = side === "left" ? "R" : "L";
  const inner = outer === "R" ? "L" : "R";
  return { t, outer, inner };
}


const UPGRADE = (tier) => `Улучши всё до ${{ stone: "камня", metal: "металла", hqm: "МВК", wood: "дерева" }[tier]} молотком (ПКМ по блоку → улучшить). Закрой двери и поставь коды.`;
const sheet = { door: "sheet", lock: "code" };
/** Дверной проём с дверью между двумя соседними фундаментами */
const doorBetween = (b, a, c, props = sheet, floor = 1) => b.add("doorway", { ...b.between(a, c), floor, ...props });
const wallBetween = (b, a, c, floor = 1) => b.add("wall", { ...b.between(a, c), floor });

// ---------- базы ----------
// minDepth — сколько стен/дверей/перекрытий минимум должен сломать рейдер снаружи до шкафа (считает raidDepth ниже,
// с учётом крыши и шахты подъёма). Стартеры — 1: шкаф у наружной стены, это нормально для первого часа.

const bases = {};

// Соло 1x1 — стартер
{
  const b = new Base("Соло 1x1", "Самый быстрый старт для одного: одна клетка, один этаж, без окон и подъёма. Дёшево и закрыто — переезжай в базу побольше, когда накопишь камня", "stone");
  b.minDepth = 1;
  const f = b.sq(0, 0);
  const tc = b.add("tc", { on: f });
  const w = b.walls(b.perimeter([f]), 1, special(door(b, f, "S", { door: "wood", lock: "key", entrance: true })));
  const roof = b.floors([f], 2);
  b.step("Поставь квадратный фундамент.", [f]);
  b.step("Сразу поставь шкаф (TC) — пока никто не застроил место.", [tc]);
  b.step("Стены вокруг и дверной проём с дверью и замком. Окон в стартере не делай: каждое окно — лишняя дыра.", w);
  b.step("Потолок.", roof);
  b.step(UPGRADE(b.tier) + " Деревянную дверь поменяй на металлическую, как только хватит металла.", [], 1);
  b.interiors = makeInteriors([L(b, f, 1, 2)], 1);
  bases["03_solo_1x1"] = b;
}

// Стартер 2x1
{
  const b = new Base("Стартер 2x1", "Первый дом за пару минут: 2 фундамента, один этаж, без окон и подъёма. Потом рядом строй базу посерьёзнее", "stone");
  b.minDepth = 1;
  const [f1, f2] = b.rect(0, 0, 2, 1);
  const tc = b.add("tc", { on: f2 });
  const w = b.walls(b.perimeter([f1, f2]), 1, special(door(b, f1, "S", { ...sheet, entrance: true })));
  const roof = b.floors([f1, f2], 2);
  b.step("Два квадратных фундамента в ряд.", [f1, f2]);
  b.step("Шкаф (TC) сразу, в дальний от входа фундамент.", [tc]);
  b.step("Стены по периметру, вход — дверной проём внизу слева, металлическая дверь и кодовый замок. Без окон.", w);
  b.step("Потолок.", roof);
  b.step(UPGRADE(b.tier), [], 1);
  b.interiors = makeInteriors([L(b, f2), L(b, f1, 1, 1)], 1);
  bases["01_starter_2x1"] = b;
}

// Тришка: шкаф в центральном квадрате, треугольники вокруг — как буфер
{
  const b = new Base("Тришка (квадрат + 4 треугольника)", "Соло-бункер в один этаж: шкаф в центре за двумя стенами, вход через треугольник-аирлок, двойная крыша над шкафом", "stone");
  b.minDepth = 2;
  const f = b.sq(0, 0);
  const t = ["N", "E", "S", "W"].map((e) => b.tri(f, e));
  const tc = b.add("tc", { on: f });
  const outer = b.walls(b.perimeter([f, ...t]), 1, special(door(b, t[2], "R", { ...sheet, entrance: true })));
  const inner = [doorBetween(b, f, t[2]), wallBetween(b, f, t[0]), wallBetween(b, f, t[1]), wallBetween(b, f, t[3])];
  const c2 = b.floors([f, ...t], 2);
  const cap = [...["N", "E", "S", "W"].map((e) => b.add("wall", { on: f, edge: e, floor: 2 })), ...b.floors([f], 3)];
  b.step("Квадратный фундамент в центре.", [f]);
  b.step("Треугольные фундаменты ко всем четырём сторонам.", t);
  b.step("Шкаф в центральный квадрат.", [tc]);
  b.step("Внешние стены треугольников. Вход — дверной проём на нижнем треугольнике.", outer);
  b.step("Стены центрального квадрата: к шкафу ведёт только дверь из нижнего треугольника — это аирлок. Три других треугольника закрыты и работают как буфер: до шкафа две стены с любой стороны.", inner);
  b.step("Потолки над всем.", c2);
  b.step("Вторая крыша над шкафом: стены вокруг центра на крыше и потолок сверху. Теперь и сверху до шкафа два слоя.", cap);
  b.step(UPGRADE(b.tier), [], 1);
  b.interiors = makeInteriors([L(b, f), L(b, t[0]), L(b, t[1]), L(b, t[3])], 1);
  bases["04_solo_trishka"] = b;
}

// 2x1 с треугольным аирлоком — два варианта входа (стартер)
for (const [file, side, sideRu] of [["00a_2x1_tri_airlock_left", "left", "слева"], ["00b_2x1_tri_airlock_right", "right", "справа"]]) {
  const b = new Base(`2x1 треугольный аирлок (вход ${sideRu})`, "Стандартный старт: 2x1, аирлок из треугольника, лут на втором этаже, отстрелочная крыша с будкой. Без окон — нет лишних дыр", "stone");
  b.minDepth = 1;
  const [f1, f2] = b.rect(0, 0, 2, 1);
  const { t, outer } = triAirlock(b, f1, "S", side);
  const tc = b.add("tc", { on: f1 });
  const coreW = b.walls(b.perimeter([f1, f2]).filter((e) => !(e.on === f1 && e.edge === "S")));
  const innerDoor = doorBetween(b, f1, t);
  const triW = b.walls(b.perimeter([f1, f2, t]).filter((e) => e.on === t), 1, special(door(b, t, outer, { ...sheet, entrance: true })));
  const stDoor = doorBetween(b, f1, f2);
  const st = b.add("stairs", { on: f2, floor: 1 });
  const c2 = b.floors([f1, t], 2);
  const w2 = b.walls(b.perimeter([f1, f2, t]), 2);
  const roof = shootRoof(b, [f1, f2, t], 3, f2, f1);
  b.step("Два квадратных фундамента в ряд (2x1).", [f1, f2]);
  b.step("Треугольный фундамент к длинной стороне первого квадрата — это аирлок.", [t]);
  b.step("Шкаф в первый квадрат, сразу за аирлоком.", [tc]);
  b.step("Стены вокруг 2x1. Сторону, к которой примыкает треугольник, пока не трогай.", coreW);
  b.step("Дверной проём между квадратом и треугольником + дверь и код. Это внутренняя дверь аирлока.", [innerDoor]);
  b.step(`Стороны треугольника: вход ${sideRu} (проём, дверь, код), другая сторона — стена.`, triW);
  b.step("Дверь во второй квадрат — там будет подъём. Подъём в тупике, а не у входа и не на пути к шкафу.", [stDoor]);
  b.step("Подъём во втором квадрате.", [st]);
  b.step("Перекрытия второго этажа над шкафом и аирлоком (над подъёмом не ставь).", c2);
  b.step("Стены второго этажа — глухие, без окон.", w2);
  roof.steps();
  b.step(UPGRADE(b.tier) + " Пока закрыта одна из двух дверей аирлока, в базу не забежать.", [], 1);
  b.interiors = makeInteriors([L(b, f1), L(b, f1, 2), L(b, t, 2)], 1);
  bases[file] = b;
}

// 2x1 с аирлоком и треугольными буфферками
{
  const b = new Base("2x1 аирлок + буфферки", "2x1 с треугольным аирлоком и буфферками: до шкафа две стены с любой стороны. Лут на втором этаже, отстрелочная крыша с будкой", "stone");
  b.minDepth = 2;
  const [f1, f2] = b.rect(0, 0, 2, 1);
  const { t: air, outer } = triAirlock(b, f1, "S", "left");
  const tc = b.add("tc", { on: f1 });
  const north = bufferStrip(b, [{ on: f1, edge: "N" }, { on: f2, edge: "N" }]);
  const east = bufferStrip(b, [{ on: f2, edge: "E" }]);
  const south = bufferStrip(b, [{ tri: air }, { on: f2, edge: "S" }]);
  const west = bufferStrip(b, [{ on: f1, edge: "W" }]);
  const bufTris = [...north.tris, ...east.tris, ...south.tris.slice(1), ...west.tris];
  const fillers = [...north.fillers, ...south.fillers];
  const allBuf = [...bufTris, ...fillers];
  const coreW = b.walls(b.perimeter([f1, f2]).filter((e) => !(e.on === f1 && e.edge === "S")));
  const innerDoor = doorBetween(b, f1, air);
  const airSep = b.add("wall", b.between(air, south.fillers[0]));
  const outerW = b.walls(b.perimeter([f1, f2, air, ...allBuf]), 1, special(door(b, air, outer, { ...sheet, entrance: true })));
  const stDoor = doorBetween(b, f1, f2);
  const st = b.add("stairs", { on: f2, floor: 1 });
  const c2 = b.floors([f1, air, ...allBuf], 2);
  const w2 = b.walls(b.perimeter([f1, f2]), 2);
  const roof = shootRoof(b, [f1, f2], 3, f2, f1);
  b.step("Два квадратных фундамента в ряд (2x1).", [f1, f2]);
  b.step("Треугольник-аирлок к нижней стороне первого квадрата.", [air]);
  b.step("Шкаф в первый квадрат.", [tc]);
  b.step("Стены вокруг 2x1 (кроме стороны с аирлоком).", coreW);
  b.step("Внутренняя дверь аирлока: проём между квадратом и треугольником, дверь, код.", [innerDoor]);
  b.step("Буфферки: треугольные фундаменты ко всем остальным сторонам 2x1.", bufTris);
  b.step("Треугольники-заполнители в щели между буфферками — получается сплошная полоса.", fillers);
  b.step("Стена между аирлоком и буфферкой рядом с ним.", [airSep]);
  b.step("Внешние стены буфферок и аирлока. Вход — на левой стороне аирлока.", outerW);
  b.step("Дверь во второй квадрат — там подъём, в тупике.", [stDoor]);
  b.step("Подъём во втором квадрате.", [st]);
  b.step("Потолки над шкафом, аирлоком и буфферками (над подъёмом не ставь).", c2);
  b.step("Стены второго этажа ядра — глухие. Буфферки снаружи, бойниц в ядре нет.", w2);
  roof.steps();
  b.step(UPGRADE(b.tier) + " Буфферки можно оставить в камне, ядро потом — в металл.", [], 1);
  b.interiors = makeInteriors([L(b, f1), L(b, f1, 2)], 1);
  bases["00c_2x1_airlock_buffers"] = b;
}

// 3x1: аирлок, шкаф в середине между двумя треугольниками-буфферками
{
  const b = new Base("3x1 аирлок + 2 этаж", "Вытянутая база: аирлок на входе, шкаф в середине за двумя стенами (сбоку треугольники-буфферки), подъём в дальней клетке, лут на втором этаже, крыша с будкой", "stone");
  b.minDepth = 2;
  const [f1, f2, f3] = b.rect(0, 0, 3, 1);
  const tN = b.tri(f2, "N"), tS = b.tri(f2, "S");
  const tc = b.add("tc", { on: f2 });
  const w1 = b.walls(b.perimeter([f1, f2, f3, tN, tS]), 1, special(door(b, f1, "W", { ...sheet, entrance: true })));
  const buf = [wallBetween(b, f2, tN), wallBetween(b, f2, tS)];
  const inner = doorBetween(b, f1, f2);
  const stDoor = doorBetween(b, f2, f3);
  const st = b.add("stairs", { on: f3, floor: 1 });
  const c2 = b.floors([f1, f2, tN, tS], 2);
  const w2 = b.walls(b.perimeter([f1, f2, f3]), 2);
  const roof = shootRoof(b, [f1, f2, f3], 3, f3, f2);
  b.step("Три квадратных фундамента в ряд.", [f1, f2, f3]);
  b.step("Два треугольника к средней клетке — сверху и снизу. Это буфферки у шкафа.", [tN, tS]);
  b.step("Шкаф в среднюю клетку.", [tc]);
  b.step("Внешние стены. Вход — с короткой стороны первого фундамента.", w1);
  b.step("Стены между шкафом и буфферками: треугольники остаются закрытыми пустыми ячейками.", buf);
  b.step("Аирлок: проём с дверью между первой и средней клеткой. Чтобы войти, нужно пройти две двери.", [inner]);
  b.step("Дверь в дальнюю клетку — там подъём, в тупике.", [stDoor]);
  b.step("Подъём в дальней клетке.", [st]);
  b.step("Перекрытия второго этажа (над подъёмом не ставь).", c2);
  b.step("Стены второго этажа — глухие.", w2);
  roof.steps();
  b.step(UPGRADE(b.tier), [], 1);
  b.interiors = makeInteriors([L(b, f2), L(b, f1, 2), L(b, f2, 2)], 2);
  bases["05_3x1_airlock"] = b;
}

// 2x2 трёхэтажка: шкаф в углу, который прикрывают два щупальца
{
  const b = new Base("2x2 трёхэтажная + щупальца", "Классика для соло/дуо: аирлок, три этажа, щупальца с бойницами, шкаф в углу за щупальцами — до него две стены с любой стороны. Отстрел на третьем этаже и крыша с будкой", "stone");
  b.minDepth = 2;
  const [a, bb, c, d] = b.rect(0, 0, 2, 2);       // a(0,0) b(1,0) c(0,1) d(1,1)
  const all = [a, bb, c, d];
  const tc = b.add("tc", { on: a });
  const arms = [tentacle(b, a, "N"), tentacle(b, a, "W"), tentacle(b, bb, "E")];
  const w1 = b.walls(b.perimeter(all), 1, special(door(b, c, "S", { ...sheet, entrance: true }), ...arms.map((t) => t.door)));
  const armParts = arms.map((t) => t.build());
  const air = [wallBetween(b, c, d), doorBetween(b, c, a)];
  const tcDoor = doorBetween(b, a, bb);
  const s1 = b.add("stairs", { on: d, floor: 1 });
  const c2 = b.floors(all, 2, [d]);
  const w2 = b.walls(b.perimeter(all), 2);
  const s2 = b.add("stairs", { on: d, floor: 2 });
  const c3 = b.floors(all, 3, [d]);
  const sh = shooting(b, all, 3, d, bb);
  const roof = shootRoof(b, all, 4, d, bb);
  b.step("Четыре фундамента квадратом 2x2.", all);
  b.step("Шкаф в верхний левый фундамент — его с двух сторон прикроют щупальца.", [tc]);
  b.step("Стены первого этажа, вход — внизу слева. Где будут щупальца — проёмы с дверью.", w1);
  b.step("Аирлок в нижнем левом фундаменте: стена справа и проём с дверью наверх, к шкафу.", air);
  b.step("Дверь из комнаты шкафа в правую половину базы.", [tcDoor]);
  b.step(TENTACLE_STEPS[0], arms.flatMap((t) => t.squares));
  b.step(TENTACLE_STEPS[1], armParts.flatMap((p) => p.walls));
  b.step(TENTACLE_STEPS[2], armParts.flatMap((p) => p.roof));
  b.step("Подъём в нижнем правом фундаменте — тупиковый угол, не на пути к шкафу.", [s1]);
  b.step("Перекрытия второго этажа (кроме клетки с подъёмом).", c2);
  b.step("Стены второго этажа — глухие.", w2);
  b.step("Второй подъём — ровно над первым.", [s2]);
  b.step("Перекрытия третьего этажа (кроме клетки с подъёмом).", c3);
  sh.steps("Третий этаж. ");
  roof.steps();
  b.step(UPGRADE(b.tier), [], 1);
  b.interiors = makeInteriors([L(b, a), L(b, bb), L(b, a, 2), L(b, bb, 2), L(b, c, 2)], 2);
  bases["06_2x2_three_floors"] = b;
}

// 2x2 с сотами (honeycomb) — ядро 2x2 + кольцо фундаментов вокруг
{
  const b = new Base("2x2 с сотами", "Ядро 2x2 + кольцо сот вокруг: до шкафа две стены с любой стороны. Отстрел над ядром, крыша с будкой", "stone");
  b.minDepth = 2;
  const ring = [], core = [];
  for (let y = 0; y < 4; y++) for (let x = 0; x < 4; x++) {
    const id = b.sq(x, y);
    (x >= 1 && x <= 2 && y >= 1 && y <= 2 ? core : ring).push(id);
  }
  const all = [...core, ...ring];
  const S = (x, y) => b.sqAt(x, y);
  const entry = S(0, 1);
  const tc = b.add("tc", { on: S(2, 2) });
  const coreW1 = b.walls(b.perimeter(core), 1, special(door(b, S(1, 1), "W", sheet)));
  const tcRoom = [doorBetween(b, S(2, 2), S(2, 1)), wallBetween(b, S(2, 2), S(1, 2))];
  const st = b.add("stairs", { on: S(1, 2), floor: 1 });
  const outerW1 = b.walls(b.perimeter(all), 1, special(door(b, entry, "W", { ...sheet, entrance: true })));
  const c2 = b.floors(all, 2, [S(1, 2)]);
  const coreW2 = b.walls(b.perimeter(core), 2);
  const outerW2 = b.walls(b.perimeter(all), 2);
  const st2 = b.add("stairs", { on: S(1, 2), floor: 2 });
  const c3 = b.floors(all, 3, [S(1, 2)]);
  const sh = shooting(b, core, 3, S(1, 2), S(1, 1));
  const roof = shootRoof(b, core, 4, S(1, 2), S(1, 1));
  b.step("Ядро: 4 фундамента квадратом 2x2.", core);
  b.step("Шкаф в ядро, в дальний угол от входа.", [tc]);
  b.step("Стены ядра первого этажа. Одна — проём с дверью (вход в ядро).", coreW1);
  b.step("Комната шкафа: дверь сверху и стена слева. С подъёма к шкафу — только через эту дверь.", tcRoom);
  b.step("Подъём в ядре, в углу у входа — не на пути к шкафу.", [st]);
  b.step("Соты: кольцо из 12 фундаментов вокруг ядра.", ring);
  b.step("Внешние стены сот. Вход — снаружи напротив двери ядра (получается аирлок).", outerW1);
  b.step("Перекрытия второго этажа над ядром и сотами (кроме подъёма).", c2);
  b.step("Стены ядра второго этажа.", coreW2);
  b.step("Внешние стены сот второго этажа.", outerW2);
  b.step("Второй подъём — ровно над первым.", [st2]);
  b.step("Перекрытия третьего этажа: над сотами это крыша, над ядром — пол отстрела.", c3);
  sh.steps("Над ядром. ");
  roof.steps();
  b.step(UPGRADE(b.tier) + " Внешние стены сот лучше сразу в металл.", [], 1);
  b.interiors = makeInteriors([L(b, S(2, 2)), L(b, S(2, 1)), L(b, S(1, 1)),
                               L(b, S(2, 2), 2), L(b, S(2, 1), 2), L(b, S(1, 1), 2)], 2);
  bases["07_2x2_honeycomb"] = b;
}

// 2x2 с буфферками и стрелковой крышей
{
  const b = new Base("2x2 буфферки + стрелковая крыша", "2x2 с буфферками, треугольным аирлоком и площадкой на крыше: до шкафа две стены с любой стороны, выход на крышу через будку с дверью", "stone");
  b.minDepth = 2;
  const all = b.rect(0, 0, 2, 2);
  const [a, bb, c, d] = all;
  const tc = b.add("tc", { on: a });
  const { t: triAir, outer } = triAirlock(b, d, "S", "right");
  const north = bufferStrip(b, [{ on: a, edge: "N" }, { on: bb, edge: "N" }]);
  const east = bufferStrip(b, [{ on: bb, edge: "E" }, { on: d, edge: "E" }]);
  const south = bufferStrip(b, [{ on: c, edge: "S" }, { tri: triAir }]);
  const west = bufferStrip(b, [{ on: a, edge: "W" }, { on: c, edge: "W" }]);
  const bufTris = [...north.tris, ...east.tris, south.tris[0], ...west.tris];
  const fillers = [...north.fillers, ...east.fillers, ...south.fillers, ...west.fillers];
  const allBuf = [...bufTris, ...fillers];
  const w1 = b.walls(b.perimeter(all), 1, special(door(b, d, "S", sheet)));
  const air = [wallBetween(b, d, c), doorBetween(b, d, bb)];
  const stDoor = doorBetween(b, a, c);
  const airSep = b.add("wall", b.between(triAir, south.fillers[0]));
  const outerW = b.walls(b.perimeter([...all, triAir, ...allBuf]), 1, special(door(b, triAir, outer, { ...sheet, entrance: true })));
  const bufRoof = b.floors([triAir, ...allBuf], 2);
  const s1 = b.add("stairs", { on: c, floor: 1 });
  const c2 = b.floors(all, 2, [c]);
  const w2 = b.walls(b.perimeter(all), 2);
  const roof = shootRoof(b, all, 3, c, a);
  b.step("Фундаменты 2x2.", all);
  b.step("Шкаф в верхний левый угол.", [tc]);
  b.step("Стены первого этажа. Снизу справа — проём с дверью: туда будет вести треугольный аирлок.", w1);
  b.step("Внутренний аирлок в нижнем правом фундаменте: стена слева, дверь наверх.", air);
  b.step("Дверь от шкафа вниз, в клетку с подъёмом. Подъём в тупике — с крыши к шкафу только через две двери.", [stDoor]);
  b.step("Треугольный аирлок к нижней стороне нижнего правого квадрата.", [triAir]);
  b.step("Буфферки: треугольники ко всем остальным сторонам ядра.", bufTris);
  b.step("Заполнители в щели между буфферками.", fillers);
  b.step("Стена между аирлоком и соседней буфферкой.", [airSep]);
  b.step("Внешние стены буфферок и аирлока. Вход — на правой стороне аирлока.", outerW);
  b.step("Крыша над аирлоком и буфферками.", bufRoof);
  b.step("Подъём в нижнем левом квадрате.", [s1]);
  b.step("Перекрытия второго этажа (над подъёмом не ставь).", c2);
  b.step("Стены второго этажа — глухие.", w2);
  roof.steps();
  b.step(UPGRADE(b.tier), [], 1);
  b.interiors = makeInteriors([L(b, a), L(b, bb), L(b, a, 2), L(b, bb, 2), L(b, d, 2)], 2);
  bases["08_2x2_shooting_roof"] = b;
}

// 3x3 для трио: шкаф в центральной комнате
{
  const b = new Base("3x3 для трио", "Просторная база на 2–3 человек: аирлок, шкаф в центральной комнате за бронированной дверью, глухие стены, отстрелочная крыша с будкой", "stone");
  b.minDepth = 2;
  const all = b.rect(0, 0, 3, 3);
  const S = (x, y) => b.sqAt(x, y);
  const tc = b.add("tc", { on: S(1, 1) });
  const w1 = b.walls(b.perimeter(all), 1, special(door(b, S(0, 2), "S", { ...sheet, entrance: true })));
  const air = [wallBetween(b, S(0, 2), S(1, 2)), doorBetween(b, S(0, 2), S(0, 1))];
  const center = [
    doorBetween(b, S(1, 1), S(0, 1), { door: "armored", lock: "code" }),
    wallBetween(b, S(1, 1), S(1, 0)),
    wallBetween(b, S(1, 1), S(2, 1)),
    wallBetween(b, S(1, 1), S(1, 2)),
  ];
  const s1 = b.add("stairs", { on: S(2, 0), floor: 1 });
  const c2 = b.floors(all, 2, [S(2, 0)]);
  const w2 = b.walls(b.perimeter(all), 2);
  const roof = shootRoof(b, all, 3, S(2, 0), S(1, 0));
  b.step("9 фундаментов квадратом 3x3.", all);
  b.step("Шкаф в центральный фундамент.", [tc]);
  b.step("Внешние стены первого этажа — глухие. Вход — в нижнем левом углу.", w1);
  b.step("Аирлок в нижнем левом фундаменте: стена справа, проём с дверью сверху.", air);
  b.step("Комната со шкафом: стены вокруг центра, вход слева через бронированную дверь.", center);
  b.step("Подъём в верхнем правом углу — в стороне от аирлока и от шкафа.", [s1]);
  b.step("Перекрытия второго этажа (над подъёмом не ставь).", c2);
  b.step("Стены второго этажа — глухие.", w2);
  roof.steps();
  b.step(UPGRADE(b.tier), [], 1);
  b.interiors = makeInteriors([L(b, S(1, 1)), L(b, S(1, 0)), L(b, S(2, 1)), L(b, S(2, 2)),
                               L(b, S(0, 0), 2), L(b, S(1, 0), 2), L(b, S(1, 1), 2), L(b, S(2, 2), 2)], 3);
  bases["09_3x3_trio"] = b;
}

// Клан 4x4 с сотами: шкаф во внутренней клетке ядра, за тремя слоями
{
  const b = new Base("Клан 4x4 с сотами", "Большая база для клана: ядро 4x4 в 3 этажа + кольцо сот. Шкаф во внутренней комнате ядра — до него три слоя с любой стороны. Отстрел на 4 этаже, крыша с будкой", "metal");
  b.minDepth = 3;
  const ring = [], core = [];
  for (let y = 0; y < 6; y++) for (let x = 0; x < 6; x++) {
    const id = b.sq(x, y);
    (x >= 1 && x <= 4 && y >= 1 && y <= 4 ? core : ring).push(id);
  }
  const all = [...core, ...ring];
  const S = (x, y) => b.sqAt(x, y);
  const tc = b.add("tc", { on: S(3, 3) });
  const coreW1 = b.walls(b.perimeter(core), 1, special(door(b, S(1, 2), "W", { door: "armored", lock: "code" })));
  const split = [1, 2, 3, 4].map((y) => y === 2
    ? doorBetween(b, S(2, y), S(3, y), { door: "armored", lock: "code" })
    : wallBetween(b, S(2, y), S(3, y)));
  const tcRoom = [doorBetween(b, S(3, 3), S(3, 2), { door: "armored", lock: "code" }), wallBetween(b, S(3, 3), S(4, 3)), wallBetween(b, S(3, 3), S(3, 4))];
  const st1 = b.add("stairs", { on: S(1, 4), floor: 1 });
  const stairwell = [doorBetween(b, S(1, 4), S(2, 4)), wallBetween(b, S(1, 4), S(1, 3))];
  const outerW1 = b.walls(b.perimeter(all), 1, special(door(b, S(0, 2), "W", { ...sheet, entrance: true })));
  const c2 = b.floors(all, 2, [S(1, 4)]);
  const coreW2 = b.walls(b.perimeter(core), 2);
  const vault = [S(4, 3), S(3, 4), S(3, 2), S(2, 3)].map((n) => wallBetween(b, S(3, 3), n, 2));
  const outerW2 = b.walls(b.perimeter(all), 2);
  const st2 = b.add("stairs", { on: S(1, 4), floor: 2 });
  const c3 = b.floors(all, 3, [S(1, 4)]);
  const coreW3 = b.walls(b.perimeter(core), 3);
  const st3 = b.add("stairs", { on: S(1, 4), floor: 3 });
  const c4 = b.floors(core, 4, [S(1, 4)]);
  const sh = shooting(b, core, 4, S(1, 4), S(2, 4));
  const roof = shootRoof(b, core, 5, S(1, 4), S(2, 4));
  b.step("Ядро: 16 фундаментов квадратом 4x4.", core);
  b.step("Шкаф во внутреннюю клетку ядра — справа внизу от центра. Она нигде не касается сот.", [tc]);
  b.step("Стены ядра. Вход в ядро слева — бронированная дверь.", coreW1);
  b.step("Перегородка посередине ядра с бронированной дверью: две половины.", split);
  b.step("Комната шкафа: бронированная дверь сверху, стены справа и снизу.", tcRoom);
  b.step("Подъём в нижнем левом углу ядра — далеко от шкафа.", [st1]);
  b.step("Лестничная клетка: стена сверху и дверь справа. Если пробьют верхние этажи, по шахте подъёма сразу в зал не спуститься.", stairwell);
  b.step("Соты: кольцо из 20 фундаментов вокруг ядра.", ring);
  b.step("Внешние стены сот. Вход снаружи — напротив двери ядра.", outerW1);
  b.step("Перекрытия второго этажа.", c2);
  b.step("Стены ядра второго этажа.", coreW2);
  b.step("Закрытая ячейка над шкафом: 4 стены вокруг клетки над комнатой шкафа. Сверху к шкафу теперь тоже три слоя.", vault);
  b.step("Внешние стены сот второго этажа.", outerW2);
  b.step("Подъём на третий этаж — ровно над первым.", [st2]);
  b.step("Перекрытия третьего этажа (над сотами это их крыша).", c3);
  b.step("Стены ядра третьего этажа — глухие.", coreW3);
  b.step("Третий подъём — на этаж отстрела.", [st3]);
  b.step("Пол отстрела над ядром (над подъёмом не ставь).", c4);
  sh.steps("Четвёртый этаж. ");
  roof.steps();
  b.step(UPGRADE(b.tier) + " Если хватает МВК — внешние стены сот в МВК.", [], 1);
  b.interiors = makeInteriors([
    L(b, S(3, 3)), L(b, S(4, 4)), L(b, S(4, 3)), L(b, S(3, 4)), L(b, S(4, 2)), L(b, S(3, 2)),
    L(b, S(1, 1)), L(b, S(2, 1)), L(b, S(3, 1), 2), L(b, S(4, 1), 2), L(b, S(4, 4), 2), L(b, S(3, 4), 2),
  ], 5);
  bases["10_clan_4x4_honeycomb"] = b;
}

// 2x2 с аирлоком и треугольными буфферками
{
  const b = new Base("2x2 аирлок + буфферки", "2x2 с треугольным аирлоком и буфферками по всему периметру: до шкафа две стены с любой стороны. Отстрел на третьем этаже, крыша с будкой", "stone");
  b.minDepth = 2;
  const [a, bb, c, d] = b.rect(0, 0, 2, 2);        // a(0,0) b(1,0) c(0,1) d(1,1)
  const { t: air, outer } = triAirlock(b, c, "S", "left");
  const tc = b.add("tc", { on: bb });
  const north = bufferStrip(b, [{ on: a, edge: "N" }, { on: bb, edge: "N" }]);
  const east = bufferStrip(b, [{ on: bb, edge: "E" }, { on: d, edge: "E" }]);
  const south = bufferStrip(b, [{ tri: air }, { on: d, edge: "S" }]);
  const west = bufferStrip(b, [{ on: a, edge: "W" }, { on: c, edge: "W" }]);
  const bufTris = [...north.tris, ...east.tris, ...south.tris.slice(1), ...west.tris];
  const fillers = [...north.fillers, ...east.fillers, ...south.fillers, ...west.fillers];
  const allBuf = [...bufTris, ...fillers];
  const core = [a, bb, c, d];
  const coreW = b.walls(b.perimeter(core).filter((e) => !(e.on === c && e.edge === "S")));
  const innerDoor = doorBetween(b, c, air);
  const tcRoom = [doorBetween(b, bb, a), wallBetween(b, bb, d)];
  const airSep = b.add("wall", b.between(air, south.fillers[0]));
  const outerW = b.walls(b.perimeter([...core, air, ...allBuf]), 1, special(door(b, air, outer, { ...sheet, entrance: true })));
  const st = b.add("stairs", { on: a, floor: 1 });
  const c2 = b.floors(core, 2, [a]);
  const w2 = b.walls(b.perimeter(core), 2);
  const roof2 = b.floors([air, ...allBuf], 2);
  const st2 = b.add("stairs", { on: a, floor: 2 });
  const c3 = b.floors(core, 3, [a]);
  const sh = shooting(b, core, 3, a, bb);
  const roof4 = shootRoof(b, core, 4, a, bb);
  b.step("Четыре фундамента квадратом 2x2.", core);
  b.step("Треугольник-аирлок к нижней стороне нижнего левого квадрата.", [air]);
  b.step("Шкаф в верхний правый квадрат.", [tc]);
  b.step("Стены вокруг 2x2 (кроме стороны с аирлоком).", coreW);
  b.step("Внутренняя дверь аирлока: проём, дверь, код.", [innerDoor]);
  b.step("Комната шкафа: дверь слева, стена снизу.", tcRoom);
  b.step("Буфферки: треугольники ко всем остальным сторонам ядра.", bufTris);
  b.step("Заполнители в щели между буфферками на каждой стороне.", fillers);
  b.step("Стена между аирлоком и соседней буфферкой.", [airSep]);
  b.step("Внешние стены буфферок и аирлока. Вход — на левой стороне аирлока.", outerW);
  b.step("Подъём в верхнем левом квадрате.", [st]);
  b.step("Перекрытия второго этажа ядра (кроме подъёма).", c2);
  b.step("Стены второго этажа ядра — глухие.", w2);
  b.step("Крыша над аирлоком и буфферками.", roof2);
  b.step("Второй подъём — ровно над первым.", [st2]);
  b.step("Перекрытия третьего этажа ядра (над подъёмом не ставь).", c3);
  sh.steps("Третий этаж. ");
  roof4.steps();
  b.step(UPGRADE(b.tier), [], 1);
  b.interiors = makeInteriors([L(b, bb), L(b, d), L(b, bb, 2), L(b, c, 2), L(b, d, 2)], 2);
  bases["00d_2x2_airlock_buffers"] = b;
}

// 2x2 + аирлок + щупальца: шкаф в углу между щупальцем и треугольником-буфферкой
{
  const b = new Base("2x2 + аирлок + щупальца", "2x2 с аирлоком и щупальцами по бокам: шкаф в углу за щупальцем и треугольником-буфферкой, до него две стены с любой стороны. Отстрел на втором этаже, крыша с будкой", "stone");
  b.minDepth = 2;
  const [f1, f2, f3, f4] = b.rect(0, 0, 2, 2);     // f1(0,0) f2(1,0) f3(0,1) f4(1,1)
  const all = [f1, f2, f3, f4];
  const t1 = b.tri(f1, "N");
  const tc = b.add("tc", { on: f1 });
  const arms = [tentacle(b, f1, "W"), tentacle(b, f2, "E")];
  const shell = [...all, t1];
  const outerW = b.walls(b.perimeter(shell).filter((e) => !(e.on === f3 && e.edge === "W")), 1, special(...arms.map((t) => t.door)));
  const buf = wallBetween(b, f1, t1);
  const air = [wallBetween(b, f3, f4), doorBetween(b, f3, f1)];
  const entry = b.add("doorway", { on: f3, edge: "W", ...sheet, entrance: true });
  const tcDoor = doorBetween(b, f1, f2);
  const armParts = arms.map((t) => t.build());
  const s1 = b.add("stairs", { on: f4, floor: 1 });
  const c2 = b.floors(shell, 2, [f4]);
  const sh = shooting(b, shell, 2, f4, f2);
  const roof = shootRoof(b, shell, 3, f4, f2);
  b.step("Поставь 4 квадратных фундамента квадратом 2x2.", all);
  b.step("Треугольный фундамент сверху слева — буфферка над шкафом.", [t1]);
  b.step("Шкаф (TC) в верхний левый квадрат.", [tc]);
  b.step("Стены по всему периметру. Западную сторону нижнего левого фундамента пока не трогай, там будет вход. Где будут щупальца — проёмы с дверью.", outerW);
  b.step("Стена между шкафом и треугольником: треугольник остаётся закрытой пустой ячейкой.", [buf]);
  b.step("Аирлок в нижнем левом фундаменте: стена справа и проём с дверью наверх, к шкафу.", air);
  b.step("Входной дверной проём на западе, дверь и кодовый замок.", [entry]);
  b.step("Дверь от шкафа в правую половину базы.", [tcDoor]);
  b.step(TENTACLE_STEPS[0], arms.flatMap((t) => t.squares));
  b.step(TENTACLE_STEPS[1], armParts.flatMap((p) => p.walls));
  b.step(TENTACLE_STEPS[2], armParts.flatMap((p) => p.roof));
  b.step("Подъём в нижнем правом фундаменте — тупиковый угол, не на пути к шкафу.", [s1]);
  b.step("Перекрытия второго этажа над всем, кроме подъёма.", c2);
  sh.steps("Второй этаж. ");
  roof.steps();
  b.step("Улучши всё до камня. Внешние стены потом можно улучшить до металла.", [], 1);
  b.interiors = makeInteriors([L(b, f1), L(b, f2), L(b, f1, 2), L(b, f3, 2)], 2);
  bases["02_bunker_2x2_airlock"] = b;
}

// ---------- проверки ----------

const EDGE_TYPES = ["wall", "doorway", "window", "halfwall", "frame"];

/**
 * Сколько минимум блоков надо сломать снаружи до шкафа. Клетка на каждом уровне — узел.
 * Стена, дверь, окно с бойницей — 1; полустена и проём без двери — 0 (перелезть/пройти);
 * перекрытие между уровнями — 1, дыра над подъёмом — 0. Над самым верхним уровнем — открытое небо.
 */
function raidDepth(b) {
  const tc = b.pieces.find((p) => p.type === "tc");
  const tiles = [...b.polys.keys()];
  const level = (p) => p.floor ?? 1;
  const top = Math.max(1, ...b.pieces.map(level)) + 1;
  const cost = new Map();
  for (const p of b.pieces) if (EDGE_TYPES.includes(p.type)) {
    const c = p.type === "halfwall" || ((p.type === "doorway" || p.type === "frame") && !p.door) || (p.type === "window" && !p.embrasure) ? 0 : 1;
    cost.set(`${b.key(p.on, p.edge)}@${level(p)}`, { c, id: p.id });
  }
  const floorAt = new Map(b.pieces.filter((p) => p.type === "floor" || p.type === "roof").map((p) => [`${p.on}@${p.floor}`, p.id]));
  const byKey = new Map();
  for (const t of tiles) for (const e of b.edgeNames(t)) {
    const k = b.key(t, e);
    if (!byKey.has(k)) byKey.set(k, []);
    byKey.get(k).push(t);
  }
  const edge = (k, L) => cost.get(`${k}@${L}`) ?? { c: 0 };
  function* nb(node) {
    if (node === "out") {
      for (const t of tiles) {
        for (let L = 1; L <= top; L++) for (const e of b.edgeNames(t)) {
          const k = b.key(t, e);
          if (byKey.get(k).length === 1) yield [`${t}@${L}`, edge(k, L)];
        }
        yield [`${t}@${top}`, { c: 0 }];
      }
      return;
    }
    const [t, Ls] = node.split("@");
    const L = +Ls;
    for (const e of b.edgeNames(t)) {
      const k = b.key(t, e);
      const others = byKey.get(k).filter((o) => o !== t);
      if (others.length === 0) yield ["out", edge(k, L)];
      for (const o of others) yield [`${o}@${L}`, edge(k, L)];
    }
    if (L < top) { const id = floorAt.get(`${t}@${L + 1}`); yield [`${t}@${L + 1}`, { c: id ? 1 : 0, id }]; }
    if (L > 1) { const id = floorAt.get(`${t}@${L}`); yield [`${t}@${L - 1}`, { c: id ? 1 : 0, id }]; }
  }
  const dist = new Map([["out", 0]]), prev = new Map(), done = new Set();
  const goal = `${tc.on}@${level(tc)}`;
  while (true) {
    let best = null;
    for (const [n, d] of dist) if (!done.has(n) && (best === null || d < dist.get(best))) best = n;
    if (best === null) return { depth: Infinity, path: [] };
    if (best === goal) break;
    done.add(best);
    for (const [n, { c, id }] of nb(best)) {
      const nd = dist.get(best) + c;
      if (!dist.has(n) || nd < dist.get(n)) { dist.set(n, nd); prev.set(n, { from: best, id: c ? id : null }); }
    }
  }
  const path = [];
  for (let n = goal; prev.has(n); n = prev.get(n).from) if (prev.get(n).id) path.unshift(prev.get(n).id);
  return { depth: dist.get(goal), path };
}

function validate(file, b) {
  const level = (p) => p.floor ?? 1;
  const all = [...b.pieces, ...(b.interiors ?? []).flatMap((i) => i.pieces)];
  const stairs = new Set(b.pieces.filter((p) => p.type === "stairs").map((p) => `${p.on}@${level(p)}`));
  const err = (m) => { throw new Error(`${file}: ${m}`); };
  const seen = new Map();
  for (const p of b.pieces.filter((q) => EDGE_TYPES.includes(q.type))) {
    const k = `${b.key(p.on, p.edge)}@${level(p)}`;
    if (seen.has(k)) err(`на одной стороне два блока: ${seen.get(k)} и ${p.id}`);
    seen.set(k, p.id);
  }
  for (const p of all) {
    if (p.type === "window" && !p.embrasure) err(`окно ${p.id} без бойницы — это дыра в дом`);
    if ((p.type === "tc" || ITEM_TYPES.has(p.type)) && stairs.has(`${p.on}@${level(p)}`)) err(`${p.type} ${p.id} стоит на клетке с подъёмом`);
    if (p.type === "floor" && stairs.has(`${p.on}@${p.floor - 1}`)) err(`перекрытие ${p.id} над подъёмом`);
  }
  // с последнего подъёма выход закрыт: вокруг клетки стены (будка), если выше — крыша с полустенами
  for (const s of b.pieces.filter((p) => p.type === "stairs")) {
    const up = level(s) + 1;
    if (stairs.has(`${s.on}@${up}`)) continue;
    const roofLevel = b.pieces.some((p) => p.type === "halfwall" && level(p) === up);
    const enclosed = b.pieces.filter((p) => p.on === s.on && level(p) === up && ["wall", "doorway"].includes(p.type)).length >= 4;
    if (roofLevel && !enclosed) err(`подъём ${s.id} выходит на крышу без будки с дверью`);
  }
  if (b.pieces.some((p) => p.embrasure) && b.minDepth < 2) err("бойницы в стартере без буфферок");
  const { depth, path } = raidDepth(b);
  const where = (id) => {
    const p = b.pieces.find((q) => q.id === id);
    const f = b.pieces.find((q) => q.id === p.on);
    return `${p.type} на ${f?.x ?? p.on},${f?.y ?? ""}${p.edge ? " " + p.edge : ""} эт.${level(p)}`;
  };
  if (depth < b.minDepth) err(`до шкафа всего ${depth} (нужно ${b.minDepth}): ${path.map(where).join(" → ")}`);
  return depth;
}
const ITEM_TYPES = new Set(["box", "furnace", "workbench", "repair", "bag", "locker"]);

// ---------- запись ----------

mkdirSync(OUT_APP, { recursive: true });
mkdirSync(OUT_CATALOG, { recursive: true });
// все встроенные базы придуманы Claude: автор «Claude», в приложении у них значок

for (const [file, b] of Object.entries(bases)) {
  const depth = validate(file, b);
  const json = JSON.stringify(b.toJSON(), null, 2) + "\n";
  writeFileSync(join(OUT_APP, file + ".json"), json);
  writeFileSync(join(OUT_CATALOG, file.replace(/^\d+[a-z]?_/, "").replace(/_/g, "-") + ".json"), json);
  console.log(`${file}: ${b.pieces.length} блоков, ${b.steps.length} шагов, до шкафа ${depth}`);
}
