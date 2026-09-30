// Freebuff контроллер мультиинстанса — скрипт слияния эстафеты сессий.
//
// Запускается штатным Freebuff resources/bun/bun.exe: bun:sqlite читает базы двух
// экземпляров напрямую, а выбранные сессии (threads + messages + queue_items +
// auto_run_decision_receipts + thread_deliveries) копируются из базы-источника
// в базу-приёмник. Сам контроллер остаётся однофайловым exe без зависимости от SQLite.
//
// Использование:
//   bun handover-merge.js list  <srcDb>
//   bun handover-merge.js merge <srcDb> <dstDb> <idsJson|@ids.json> [renamesJson|@renames.json]
// ids/renames передаются как JSON или как "@путь" (контроллер идёт через файл,
// обходя экранирование в командной строке).
// Выводит одну строку JSON (UTF-8, stdout):
//   {"ok":true,"action":"list","threads":[...]}
//   {"ok":true,"action":"merge","copied":[...],"skipped":[...]}
//   {"ok":false,"error":"..."}
// Любая проблема с путями отдаёт ok:false; merge выполняется одной транзакцией,
// при сбое откатывается целиком.
//
// Правила копирования:
// - Идемпотентность: thread id, уже существующие в базе-приёмнике, всегда
//   пропускаются, перезаписи нет.
// - Развязка рабочих областей: thread ссылается на projects с тем же root_path в
//   базе-приёмнике (если строки нет, она создаётся автоматически — это и есть
//   project_id для внешнего ключа сессии); какую рабочую область открыл
//   источник, а какую приёмник — не важно.
// - Приватное состояние движка обнуляется (turn_state / harness_state /
//   реестр auto_run / токены sponsored / freebuff_instance_id / непрочитанные
//   attention / world_snapshot), поэтому принявший сессию аккаунт продолжает
//   с чистого состояния «свободен» и не наследует runtime-долги предыдущего.
// - Белый список колонок: все INSERT пишут только реально существующие в
//   базе-приёмнике колонки (пересечение по PRAGMA), поэтому при смене версии
//   Freebuff битый SQL не собирается; миграцию колонок самой базы-приёмника
//   делает upgrade-процесс orchestrator при старте.

var Database = globalThis.Database || require("bun:sqlite").Database;

function out(obj) {
  process.stdout.write(JSON.stringify(obj) + "\n");
}

// argv[i]: JSON строкой или "@file" (JSON читается из файла).
function argJson(i, fallback) {
  var v = process.argv[i];
  if (!v) return fallback;
  if (v.charCodeAt(0) === 64) {
    var fs = require("fs");
    return JSON.parse(fs.readFileSync(v.slice(1), "utf8"));
  }
  return JSON.parse(v);
}

function die(msg) {
  out({ ok: false, error: String(msg) });
  process.exit(0); // контроллер разбирает только JSON из stdout, код выхода не важен
}

// Открытие только на чтение; если имя опции у bun не совпало — откатываемся на
// обычное (файл остаётся читаемым).
function openRO(path) {
  try {
    return new Database(path, { readonly: true });
  } catch (e) {
    return new Database(path);
  }
}

function tableCols(db, table) {
  return db.query("PRAGMA table_info(" + table + ")").all().map(function (c) {
    return c.name;
  });
}

// Сужаем rowObj до колонок, присутствующих в dstCols, и делаем INSERT OR IGNORE.
function insertRow(db, table, rowObj, dstCols) {
  var cols = [];
  var params = {};
  for (var k in rowObj) {
    if (dstCols.indexOf(k) < 0) continue;
    cols.push(k);
    params["$" + k] = rowObj[k];
  }
  if (cols.length === 0) return;
  var q = "INSERT OR IGNORE INTO " + table + " (" + cols.join(", ") +
    ") VALUES (" + cols.map(function (c) { return "$" + c; }).join(", ") + ")";
  db.query(q).run(params);
}

function listThreads(srcPath) {
  var src = openRO(srcPath);
  try {
    var counts = {};
    var mc = src.query(
      "SELECT thread_id, COUNT(*) AS n FROM messages GROUP BY thread_id"
    );
    for (var r of mc.all()) counts[r.thread_id] = r.n;
    var threads = [];
    var rows = src.query(
      "SELECT id, title, status, turn_state, model, project_path, updated_at" +
      " FROM threads ORDER BY updated_at DESC"
    ).all();
    for (var t of rows) {
      threads.push({
        id: t.id,
        title: t.title,
        status: t.status,
        turnState: t.turn_state,
        model: t.model,
        projectPath: t.project_path,
        messages: counts[t.id] || 0,
        updated: t.updated_at,
      });
    }
    out({ ok: true, action: "list", threads: threads });
  } finally {
    src.close();
  }
}

// Проверяем, что в базе-приёмнике есть строка projects с этим root_path, и
// возвращаем её id. Обычно экземпляр-приёмник уже открывал ту же рабочую
// область и строка на месте; если нет — добавляем (предпочитая project_id
//   источника: он выводится из пути и на одной машине не меняется; при
//   занятом id берём случайный).
function ensureProject(dst, rootPath, preferredId) {
  var found = dst
    .query("SELECT id FROM projects WHERE root_path = $p")
    .get({ $p: rootPath });
  if (found) return found.id;
  if (preferredId) {
    try {
      dst.query(
        "INSERT OR IGNORE INTO projects (id, root_path, default_branch, created_at)" +
        " VALUES ($id, $rp, $db, $ca)"
      ).run({ $id: preferredId, $rp: rootPath, $db: "main", $ca: Date.now() });
    } catch (e) { }
    found = dst
      .query("SELECT id FROM projects WHERE root_path = $p")
      .get({ $p: rootPath });
    if (found) return found.id;
  }
  var nid = crypto.randomUUID();
  dst.query(
    "INSERT INTO projects (id, root_path, default_branch, created_at)" +
    " VALUES ($id, $rp, $db, $ca)"
  ).run({ $id: nid, $rp: rootPath, $db: "main", $ca: Date.now() });
  return nid;
}

function mergeThreads(srcPath, dstPath, ids, renames) {
  if (!Array.isArray(ids) || ids.length === 0) die("нет сессий для переноса");
  if (!dstPath || dstPath === srcPath) die("база-приёмник отсутствует или совпадает с источником");
  if (!renames || typeof renames !== "object") renames = {};

  var src = openRO(srcPath);
  var dst = new Database(dstPath);
  var copied = [];
  var skipped = [];
  try {
    var srcThreadCols = tableCols(src, "threads");
    var dstThreadCols = tableCols(dst, "threads");
    var dstMsgCols = tableCols(dst, "messages");
    var dstQueueCols = tableCols(dst, "queue_items");
    var dstReceiptCols = tableCols(dst, "auto_run_decision_receipts");
    var dstDelivCols = tableCols(dst, "thread_deliveries");

    var projCache = {};
    var dstThreadStmt = null; // набор колонок у строк разный, собираем построчно

    dst.transaction(function () {
      for (var id of ids) {
        var th = src
          .query("SELECT * FROM threads WHERE id = $id")
          .get({ $id: id });
        if (!th) {
          skipped.push(id);
          continue;
        }
        var exists = dst
          .query("SELECT 1 FROM threads WHERE id = $id")
          .get({ $id: id });
        if (exists) {
          skipped.push(id); // идемпотентность: сессия с тем же id не перезаписывается
          continue;
        }

        var row = {};
        for (var col of srcThreadCols) row[col] = th[col];

        // Обнуляем приватное состояние движка; если в базе-приёмнике нет таких
        // колонок, insertRow отбросит их сам.
        row.project_id = ensureProject(dst, th.project_path, th.project_id);
        row.turn_state = "idle";
        row.queue_paused = 0;
        row.auto_run = 0;
        row.auto_run_started_at = null;
        row.auto_run_pass_count = 0;
        row.auto_run_refinement_count = 0;
        row.auto_run_decision_count = 0;
        row.auto_run_stopped_note = null;
        row.auto_run_stopped_at = null;
        row.harness_state = null;
        row.harness_state_id = null;
        row.world_snapshot = null;
        row.freebuff_instance_id = null;
        row.sponsored = null;
        row.sponsored_run_token = null;
        row.sponsored_settled_at = null;
        row.sponsored_terminal_reports = null;
        row.sponsored_terminal_ack_at = null;
        row.pending_briefs = null;
        row.pending_briefs_diagnostic_key = null;
        row.attention_acknowledged_revision = row.attention_revision || 0;
        row.attention_reason = null;
        row.attention_at = null;
        row.last_turn_outcome = null;
        if (renames[id]) row.title = String(renames[id]).slice(0, 200);
        row.updated_at = Date.now();

        insertRow(dst, "threads", row, dstThreadCols);
        copied.push(id);

        for (var m of src
          .query("SELECT * FROM messages WHERE thread_id = $id ORDER BY seq")
          .all({ $id: id })) {
          insertRow(dst, "messages", m, dstMsgCols);
        }

        for (var qi of src
          .query("SELECT * FROM queue_items WHERE thread_id = $id")
          .all({ $id: id })) {
          var st = String(qi.state || "").toLowerCase();
          if (st === "running" || st === "claimed") continue; // остатки runtime от прошлого аккаунта
          insertRow(dst, "queue_items", qi, dstQueueCols);
        }

        for (var rc of src
          .query(
            "SELECT * FROM auto_run_decision_receipts WHERE thread_id = $id"
          )
          .all({ $id: id })) {
          insertRow(dst, "auto_run_decision_receipts", rc, dstReceiptCols);
        }

        for (var dv of src
          .query("SELECT * FROM thread_deliveries WHERE thread_id = $id")
          .all({ $id: id })) {
          insertRow(dst, "thread_deliveries", dv, dstDelivCols);
        }
      }
    })();
  } finally {
    try { src.close(); } catch (e) { }
    try { dst.close(); } catch (e) { }
  }
  out({ ok: true, action: "merge", copied: copied, skipped: skipped });
}

try {
  var mode = process.argv[2];
  if (mode === "list") {
    if (!process.argv[3]) die("не указан путь к базе-источнику");
    listThreads(process.argv[3]);
  } else if (mode === "merge") {
    if (!process.argv[3] || !process.argv[4]) die("не указаны пути к базе-источнику и базе-приёмнику");
    mergeThreads(
      process.argv[3],
      process.argv[4],
      argJson(5, []),
      argJson(6, {})
    );
  } else {
    die("unknown mode: " + mode);
  }
} catch (e) {
  die(e && e.message ? e.message : String(e));
}
