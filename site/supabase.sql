-- RustBuilder: аккаунты сайта.
-- Supabase → SQL Editor → New query → вставить ВЕСЬ файл → Run.
-- Можно запускать повторно: ничего не сломает и данные не сотрёт.

-- ─── Ник: нормализация как в программе и на GitHub ───────────────────────────
-- Похожие буквы склеиваются: «GооdJoint» (русские о) = «G00dJoint» = «goodjoint».
create or replace function public.norm_nick(n text) returns text
language sql immutable as $$
  select case when n is null then null else
    regexp_replace(translate(lower(n), 'авекмнорстухё01l|', 'abekmhopctyxeoiii'), '[[:space:]_.-]+', '', 'g')
  end
$$;

-- ─── Профили ─────────────────────────────────────────────────────────────────
create table if not exists public.profiles (
  id          uuid primary key references auth.users on delete cascade,
  nick        text check (nick ~ '^[A-Za-zА-Яа-яЁё0-9_. -]{2,24}$'),
  nick_norm   text generated always as (public.norm_nick(nick)) stored unique,
  avatar_url  text,
  bio         text check (char_length(bio) <= 300),
  links       jsonb not null default '{}'::jsonb check (pg_column_size(links) < 2000),
  role        text not null default 'user' check (role in ('user','media','admin')),
  pro         boolean not null default false,          -- на будущее (про-статус), пока не используется
  created_at  timestamptz not null default now()
);

-- Ники, которые никто не может взять сам (выдаёт только админ).
create table if not exists public.reserved_nicks (
  nick_norm  text primary key,
  nick       text not null,
  note       text,
  created_at timestamptz not null default now()
);
insert into public.reserved_nicks (nick_norm, nick, note) values
  (public.norm_nick('RustBuilder'), 'RustBuilder', 'проект'),
  (public.norm_nick('Claude'),      'Claude',      'автор базовых схем'),
  (public.norm_nick('Legonchik'),   'Legonchik',   'автор проекта'),
  (public.norm_nick('Admin'),       'Admin',       'служебный'),
  (public.norm_nick('Moderator'),   'Moderator',   'служебный'),
  (public.norm_nick('GoodJoint'),   'GoodJoint',   'резерв по заявке #17')
on conflict do nothing;

create or replace function public.is_admin() returns boolean
language sql stable security definer set search_path = public as $$
  select exists (select 1 from public.profiles where id = auth.uid() and role = 'admin')
$$;

-- Новый пользователь → пустой профиль (ник он придумает сам). Аватар берём из Discord.
create or replace function public.handle_new_user() returns trigger
language plpgsql security definer set search_path = public as $$
begin
  insert into public.profiles (id, avatar_url)
  values (new.id, nullif(new.raw_user_meta_data->>'avatar_url', ''))
  on conflict (id) do nothing;
  return new;
end $$;
drop trigger if exists on_auth_user_created on auth.users;
create trigger on_auth_user_created after insert on auth.users
  for each row execute function public.handle_new_user();

-- Зарезервированный ник может поставить только админ (или ты сам из SQL Editor).
create or replace function public.check_nick() returns trigger
language plpgsql security definer set search_path = public as $$
begin
  if new.nick is distinct from old.nick and new.nick is not null
     and auth.uid() is not null and not public.is_admin()
     and exists (select 1 from public.reserved_nicks r where r.nick_norm = public.norm_nick(new.nick)) then
    raise exception 'NICK_RESERVED';
  end if;
  return new;
end $$;
drop trigger if exists profiles_check_nick on public.profiles;
create trigger profiles_check_nick before update on public.profiles
  for each row execute function public.check_nick();

alter table public.profiles enable row level security;
drop policy if exists "profiles read" on public.profiles;
create policy "profiles read" on public.profiles for select using (true);
drop policy if exists "profiles update own" on public.profiles;
create policy "profiles update own" on public.profiles for update
  using (id = auth.uid()) with check (id = auth.uid());
-- Сам себе пользователь меняет только ник, описание и ссылки. Роль, про-статус и аватар — нет.
revoke insert, update, delete on public.profiles from anon, authenticated;
grant update (nick, bio, links) on public.profiles to authenticated;

alter table public.reserved_nicks enable row level security;
drop policy if exists "reserved read" on public.reserved_nicks;
create policy "reserved read" on public.reserved_nicks for select using (true);
drop policy if exists "reserved admin" on public.reserved_nicks;
create policy "reserved admin" on public.reserved_nicks for all using (public.is_admin()) with check (public.is_admin());

-- ─── Лайки и избранное ───────────────────────────────────────────────────────
-- base: 'gh:bases/файл.json' — база из каталога GitHub, 's:<id>' — база, выложенная на сайте.
create table if not exists public.likes (
  user_id    uuid not null default auth.uid() references public.profiles on delete cascade,
  base       text not null check (char_length(base) <= 200),
  created_at timestamptz not null default now(),
  primary key (user_id, base)
);
create table if not exists public.favorites (
  user_id    uuid not null default auth.uid() references public.profiles on delete cascade,
  base       text not null check (char_length(base) <= 200),
  created_at timestamptz not null default now(),
  primary key (user_id, base)
);
alter table public.likes enable row level security;
alter table public.favorites enable row level security;
drop policy if exists "likes read" on public.likes;
create policy "likes read" on public.likes for select using (true);
drop policy if exists "likes own" on public.likes;
create policy "likes own" on public.likes for insert with check (user_id = auth.uid());
drop policy if exists "likes delete own" on public.likes;
create policy "likes delete own" on public.likes for delete using (user_id = auth.uid());
drop policy if exists "fav read own" on public.favorites;
create policy "fav read own" on public.favorites for select using (user_id = auth.uid());
drop policy if exists "fav own" on public.favorites;
create policy "fav own" on public.favorites for insert with check (user_id = auth.uid());
drop policy if exists "fav delete own" on public.favorites;
create policy "fav delete own" on public.favorites for delete using (user_id = auth.uid());

create or replace view public.like_counts as
  select base, count(*)::int as n from public.likes group by base;
grant select on public.like_counts to anon, authenticated;

-- ─── Базы, выложенные на сайте (с модерацией) ────────────────────────────────
create table if not exists public.submissions (
  id            uuid primary key default gen_random_uuid(),
  author        uuid not null default auth.uid() references public.profiles on delete cascade,
  name          text not null check (char_length(name) between 2 and 60),
  description   text check (char_length(description) <= 500),
  tier          text check (char_length(tier) <= 20),
  foundations   int, floors int, steps int,
  data          jsonb not null check (pg_column_size(data) < 400000),
  status        text not null default 'pending' check (status in ('pending','approved','rejected')),
  reject_reason text check (char_length(reject_reason) <= 300),
  created_at    timestamptz not null default now(),
  updated_at    timestamptz not null default now()
);
create index if not exists submissions_status on public.submissions (status, created_at desc);

create or replace function public.submissions_guard() returns trigger
language plpgsql security definer set search_path = public as $$
begin
  if public.is_admin() or auth.uid() is null then
    new.updated_at := now();
    return new;
  end if;
  if tg_op = 'INSERT' then
    if not exists (select 1 from public.profiles where id = auth.uid() and nick is not null) then
      raise exception 'NO_NICK';
    end if;
    if (select count(*) from public.submissions where author = auth.uid() and status = 'pending') >= 3 then
      raise exception 'TOO_MANY_PENDING';
    end if;
    new.author := auth.uid();
  end if;
  -- Любая правка автором отправляет базу на повторную проверку.
  new.status := 'pending';
  new.reject_reason := null;
  new.updated_at := now();
  return new;
end $$;
drop trigger if exists submissions_guard on public.submissions;
create trigger submissions_guard before insert or update on public.submissions
  for each row execute function public.submissions_guard();

alter table public.submissions enable row level security;
drop policy if exists "subs read" on public.submissions;
create policy "subs read" on public.submissions for select
  using (status = 'approved' or author = auth.uid() or public.is_admin());
drop policy if exists "subs insert" on public.submissions;
create policy "subs insert" on public.submissions for insert with check (author = auth.uid());
drop policy if exists "subs update own" on public.submissions;
create policy "subs update own" on public.submissions for update
  using (author = auth.uid()) with check (author = auth.uid());
drop policy if exists "subs delete" on public.submissions;
create policy "subs delete" on public.submissions for delete using (author = auth.uid() or public.is_admin());
revoke insert, update on public.submissions from anon, authenticated;
grant insert (name, description, tier, foundations, floors, steps, data) on public.submissions to authenticated;
grant update (name, description, tier, foundations, floors, steps, data) on public.submissions to authenticated;

-- ─── Админка ─────────────────────────────────────────────────────────────────
create or replace function public.admin_set_role(target uuid, new_role text) returns void
language plpgsql security definer set search_path = public as $$
begin
  if not public.is_admin() then raise exception 'NOT_ADMIN'; end if;
  if new_role not in ('user','media') then raise exception 'BAD_ROLE'; end if;
  update public.profiles set role = new_role where id = target and role <> 'admin';
end $$;

create or replace function public.admin_set_nick(target uuid, new_nick text) returns void
language plpgsql security definer set search_path = public as $$
begin
  if not public.is_admin() then raise exception 'NOT_ADMIN'; end if;
  update public.profiles set nick = nullif(trim(new_nick), '') where id = target;
end $$;

create or replace function public.admin_review(sub uuid, new_status text, reason text default null) returns void
language plpgsql security definer set search_path = public as $$
begin
  if not public.is_admin() then raise exception 'NOT_ADMIN'; end if;
  if new_status not in ('approved','rejected','pending') then raise exception 'BAD_STATUS'; end if;
  update public.submissions set status = new_status, reject_reason = left(reason, 300) where id = sub;
end $$;

revoke execute on function public.admin_set_role(uuid, text), public.admin_set_nick(uuid, text),
  public.admin_review(uuid, text, text) from anon;

-- ─── Сделать себя админом ────────────────────────────────────────────────────
-- Один раз, ПОСЛЕ того как зарегистрируешься на сайте. Впиши свою почту и запусти только эти строки:
--
-- update public.profiles set role = 'admin', nick = 'Legonchik'
--   where id = (select id from auth.users where email = 'ТВОЯ_ПОЧТА');
