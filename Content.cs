using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

public static class Content
{
    public const string AppVersion = "1.0.0";
    public static string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Millioner");
    public static string AppDbPath => Path.Combine(AppDataDir, "app.db");
    public static string ContentDbPath => Path.Combine(AppDataDir, "GameContent.db");

    public static SqliteConnection OpenAppDb()
    {
        Directory.CreateDirectory(AppDataDir);
        var c = new SqliteConnection($"Data Source={AppDbPath}");
        c.Open();
        return c;
    }

    public static SqliteConnection OpenContentDb()
    {
        Directory.CreateDirectory(AppDataDir);
        var c = new SqliteConnection($"Data Source={ContentDbPath}");
        c.Open();
        return c;
    }

    public static void InitDb()
    {
        Directory.CreateDirectory(AppDataDir);

        using (var app = OpenAppDb())
        {
            using var cmd = app.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS sessions(
 id TEXT PRIMARY KEY,
 game_set_id TEXT NOT NULL,
 started_at TEXT NOT NULL,
 ended_at TEXT,
 final_step INTEGER NOT NULL DEFAULT 0,
 final_prize INTEGER NOT NULL DEFAULT 0,
 finished_all INTEGER NOT NULL DEFAULT 0,
 used_hint INTEGER NOT NULL DEFAULT 0,
 used_5050 INTEGER NOT NULL DEFAULT 0,
 used_audience INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS session_answers(
 id TEXT PRIMARY KEY,
 session_id TEXT NOT NULL,
 step_index INTEGER NOT NULL,
 question_id TEXT NOT NULL,
 chosen_answer TEXT NOT NULL CHECK(chosen_answer IN ('A','B','C','D')),
 is_correct INTEGER NOT NULL,
 used_hint INTEGER NOT NULL DEFAULT 0,
 used_5050 INTEGER NOT NULL DEFAULT 0,
 used_audience INTEGER NOT NULL DEFAULT 0,
 answered_at TEXT NOT NULL,
 audience_a INTEGER,
 audience_b INTEGER,
 audience_c INTEGER,
 audience_d INTEGER
);
CREATE TABLE IF NOT EXISTS app_settings(
 key TEXT PRIMARY KEY,
 value TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sessions_started ON sessions(started_at);
";
            cmd.ExecuteNonQuery();

            EnsureColumn(app, "sessions", "used_audience", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(app, "session_answers", "used_audience", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(app, "session_answers", "audience_a", "INTEGER");
            EnsureColumn(app, "session_answers", "audience_b", "INTEGER");
            EnsureColumn(app, "session_answers", "audience_c", "INTEGER");
            EnsureColumn(app, "session_answers", "audience_d", "INTEGER");
        }

        using (var content = OpenContentDb())
        {
            using var cmd = content.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS game_sets(
 id TEXT PRIMARY KEY,
 slug TEXT UNIQUE NOT NULL,
 title TEXT NOT NULL,
 description TEXT,
 version TEXT NOT NULL,
 author TEXT,
 installed_at TEXT,
 enabled INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE IF NOT EXISTS prize_ladders(
 id TEXT PRIMARY KEY,
 game_set_id TEXT NOT NULL,
 step_index INTEGER NOT NULL,
 prize_amount INTEGER NOT NULL,
 is_safe INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS questions(
 id TEXT PRIMARY KEY,
 game_set_id TEXT NOT NULL,
 step_index INTEGER NOT NULL,
 question_text TEXT NOT NULL,
 hint_text TEXT,
 answer_a TEXT NOT NULL,
 answer_b TEXT NOT NULL,
 answer_c TEXT NOT NULL,
 answer_d TEXT NOT NULL,
 correct_answer TEXT NOT NULL CHECK(correct_answer IN ('A','B','C','D')),
 explanation TEXT
);
CREATE INDEX IF NOT EXISTS idx_questions_set_step ON questions(game_set_id, step_index);
CREATE INDEX IF NOT EXISTS idx_ladder_set_step ON prize_ladders(game_set_id, step_index);
";
            cmd.ExecuteNonQuery();

            using var countCmd = content.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM game_sets";
            if (Convert.ToInt32(countCmd.ExecuteScalar()) == 0)
            {
                SeedGameSets();
            }
        }

        SetSettingDefault("default_game_set_slug", "base_general");
        SetSettingDefault("animations_enabled", "1");
        SetSettingDefault("sounds_enabled", "0");
    }

    private static void EnsureColumn(SqliteConnection c, string table, string column, string def)
    {
        using var check = c.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var r = check.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }

        using var alter = c.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {def}";
        alter.ExecuteNonQuery();
    }

    private static readonly (int Step, int Prize, bool Safe)[] Ladder10 =
    [
        (1, 1000, false), (2, 2000, false), (3, 5000, false), (4, 10000, false), (5, 25000, true),
        (6, 50000, false), (7, 100000, true), (8, 250000, false), (9, 500000, false), (10, 1000000, false)
    ];

        private static readonly (int Step, int Prize, bool Safe)[] Ladder15 =
    [
        (1, 1000, false), (2, 2000, false), (3, 5000, false), (4, 10000, false), (5, 25000, true),
        (6, 50000, false), (7, 100000, false), (8, 200000, false), (9, 300000, false), (10, 500000, true),
        (11, 700000, false), (12, 900000, false), (13, 1200000, false), (14, 1500000, false), (15, 2000000, true)
    ];

public static void SeedGameSets()
    {
        SeedGameSet("base_general", "Классика: Разное", "Смешанные вопросы: природа, быт, наука, культура", "1.0.0", Ladder10, BuildGeneralQuestions());
        SeedGameSet("it_and_tech", "IT и Техника", "Компьютеры, сети, железо, софт, интернет-культура", "1.0.0", Ladder10, BuildItQuestions());
        SeedGameSet("kazakhstan_mix", "Казахстан: микс", "География, история, культура, бытовые факты (без спорных политических тем)", "1.0.0", Ladder10, BuildKzQuestions());
        SeedGameSet("plants_120", "Растения (120 вопросов)", "Ботаника, деревья, цветы, овощи, грибы (без жести), уход, факты.", "1.0", Ladder15, BuildPlants120Questions());
        SeedGameSet("auto_mech_72", "Автомобили и механика", "ДВС, трансмиссия, подвеска, тормоза, электрика, обслуживание.", "1.0", Ladder15, BuildAutoMech72Questions());
    }

    private static void SeedGameSet(string slug, string title, string description, string version, (int Step, int Prize, bool Safe)[] ladder, List<QuestionSeed> questions)
    {
        using var c = OpenContentDb();
        using var tx = c.BeginTransaction();
        var setId = Guid.NewGuid().ToString("N");

        using (var set = c.CreateCommand())
        {
            set.Transaction = tx;
            set.CommandText = "INSERT INTO game_sets(id,slug,title,description,version,author,installed_at,enabled) VALUES(@id,@slug,@title,@desc,@ver,'Built-in',@at,1)";
            set.Parameters.AddWithValue("@id", setId);
            set.Parameters.AddWithValue("@slug", slug);
            set.Parameters.AddWithValue("@title", title);
            set.Parameters.AddWithValue("@desc", description);
            set.Parameters.AddWithValue("@ver", version);
            set.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("o"));
            set.ExecuteNonQuery();
        }

        foreach (var l in ladder)
        {
            using var lc = c.CreateCommand();
            lc.Transaction = tx;
            lc.CommandText = "INSERT INTO prize_ladders(id,game_set_id,step_index,prize_amount,is_safe) VALUES(@id,@g,@s,@p,@safe)";
            lc.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            lc.Parameters.AddWithValue("@g", setId);
            lc.Parameters.AddWithValue("@s", l.Step);
            lc.Parameters.AddWithValue("@p", l.Prize);
            lc.Parameters.AddWithValue("@safe", l.Safe ? 1 : 0);
            lc.ExecuteNonQuery();
        }

        foreach (var q in questions)
        {
            using var qc = c.CreateCommand();
            qc.Transaction = tx;
            qc.CommandText = @"INSERT INTO questions(id,game_set_id,step_index,question_text,hint_text,answer_a,answer_b,answer_c,answer_d,correct_answer,explanation)
VALUES(@id,@g,@s,@q,@h,@a,@b,@c,@d,@ok,@e)";
            qc.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            qc.Parameters.AddWithValue("@g", setId);
            qc.Parameters.AddWithValue("@s", q.Step);
            qc.Parameters.AddWithValue("@q", q.Question);
            qc.Parameters.AddWithValue("@h", q.Hint);
            qc.Parameters.AddWithValue("@a", q.A);
            qc.Parameters.AddWithValue("@b", q.B);
            qc.Parameters.AddWithValue("@c", q.C);
            qc.Parameters.AddWithValue("@d", q.D);
            qc.Parameters.AddWithValue("@ok", q.Correct);
            qc.Parameters.AddWithValue("@e", q.Explanation);
            qc.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public static List<GameSet> GetGameSets()
    {
        using var c = OpenContentDb();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,slug,title,description,version,author,installed_at,enabled FROM game_sets ORDER BY installed_at DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<GameSet>();
        while (r.Read())
        {
            list.Add(new GameSet(
                r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3),
                r.GetString(4), r.IsDBNull(5) ? "" : r.GetString(5), r.IsDBNull(6) ? "" : r.GetString(6), r.GetInt32(7) == 1));
        }
        return list;
    }

    public static List<GameSet> GetEnabledGameSets() => GetGameSets().Where(x => x.Enabled).OrderBy(x => x.Title).ToList();

    public static List<LadderItem> GetLadder(string gameSetId)
    {
        using var c = OpenContentDb();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT step_index,prize_amount,is_safe FROM prize_ladders WHERE game_set_id=@g ORDER BY step_index";
        cmd.Parameters.AddWithValue("@g", gameSetId);
        using var r = cmd.ExecuteReader();
        var list = new List<LadderItem>();
        while (r.Read()) list.Add(new LadderItem(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2) == 1));
        return list;
    }

    public static int CountQuestions(string gameSetId, int step)
    {
        using var c = OpenContentDb();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM questions WHERE game_set_id=@g AND step_index=@s";
        cmd.Parameters.AddWithValue("@g", gameSetId);
        cmd.Parameters.AddWithValue("@s", step);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static QuestionItem? GetRandomQuestion(string gameSetId, int step, HashSet<string> exclude)
    {
        using var c = OpenContentDb();
        using var cmd = c.CreateCommand();
        if (exclude.Count == 0)
        {
            cmd.CommandText = "SELECT id,question_text,hint_text,answer_a,answer_b,answer_c,answer_d,correct_answer,explanation FROM questions WHERE game_set_id=@g AND step_index=@s ORDER BY RANDOM() LIMIT 1";
            cmd.Parameters.AddWithValue("@g", gameSetId);
            cmd.Parameters.AddWithValue("@s", step);
        }
        else
        {
            var names = exclude.Select((_, i) => $"@e{i}").ToList();
            cmd.CommandText = $"SELECT id,question_text,hint_text,answer_a,answer_b,answer_c,answer_d,correct_answer,explanation FROM questions WHERE game_set_id=@g AND step_index=@s AND id NOT IN ({string.Join(',', names)}) ORDER BY RANDOM() LIMIT 1";
            cmd.Parameters.AddWithValue("@g", gameSetId);
            cmd.Parameters.AddWithValue("@s", step);
            var i = 0;
            foreach (var id in exclude) cmd.Parameters.AddWithValue($"@e{i++}", id);
        }

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var hint = r.IsDBNull(2) ? "" : r.GetString(2);
        if (string.IsNullOrWhiteSpace(hint)) hint = "Подсказки нет.";
        return new QuestionItem(r.GetString(0), step, r.GetString(1), hint, r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7)[0], r.IsDBNull(8) ? "" : r.GetString(8));
    }

    public static string CreateSession(string gameSetId)
    {
        var id = Guid.NewGuid().ToString("N");
        using var c = OpenAppDb();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions(id,game_set_id,started_at) VALUES(@id,@g,@at)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@g", gameSetId);
        cmd.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
        return id;
    }

    public static void AddSessionAnswer(string sessionId, int stepIndex, string questionId, char chosen, bool isCorrect, bool usedHint, bool used5050, bool usedAudience, AudienceResult? audience)
    {
        using var c = OpenAppDb();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO session_answers(id,session_id,step_index,question_id,chosen_answer,is_correct,used_hint,used_5050,used_audience,answered_at,audience_a,audience_b,audience_c,audience_d)
VALUES(@id,@s,@step,@q,@ch,@ok,@h,@f,@au,@at,@a,@b,@c,@d)";
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("@s", sessionId);
        cmd.Parameters.AddWithValue("@step", stepIndex);
        cmd.Parameters.AddWithValue("@q", questionId);
        cmd.Parameters.AddWithValue("@ch", chosen.ToString());
        cmd.Parameters.AddWithValue("@ok", isCorrect ? 1 : 0);
        cmd.Parameters.AddWithValue("@h", usedHint ? 1 : 0);
        cmd.Parameters.AddWithValue("@f", used5050 ? 1 : 0);
        cmd.Parameters.AddWithValue("@au", usedAudience ? 1 : 0);
        cmd.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@a", (object?)audience?.A ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@b", (object?)audience?.B ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@c", (object?)audience?.C ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@d", (object?)audience?.D ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static void EndSession(string sessionId, int finalStep, int finalPrize, bool finishedAll, bool usedHint, bool used5050, bool usedAudience)
    {
        using var c = OpenAppDb();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"UPDATE sessions
SET ended_at=@end, final_step=@step, final_prize=@prize, finished_all=@all, used_hint=@h, used_5050=@f, used_audience=@a
WHERE id=@id";
        cmd.Parameters.AddWithValue("@end", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@step", finalStep);
        cmd.Parameters.AddWithValue("@prize", finalPrize);
        cmd.Parameters.AddWithValue("@all", finishedAll ? 1 : 0);
        cmd.Parameters.AddWithValue("@h", usedHint ? 1 : 0);
        cmd.Parameters.AddWithValue("@f", used5050 ? 1 : 0);
        cmd.Parameters.AddWithValue("@a", usedAudience ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.ExecuteNonQuery();
    }

    public static List<SessionRow> GetRecentSessions(int limit)
    {
        var titles = GetGameSets().ToDictionary(g => g.Id, g => g.Title);
        using var c = OpenAppDb();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT id,started_at,final_step,final_prize,finished_all,game_set_id FROM sessions ORDER BY started_at DESC LIMIT @l";
        cmd.Parameters.AddWithValue("@l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<SessionRow>();
        while (r.Read())
        {
            var finished = r.GetInt32(4) == 1;
            var resultText = finished ? "победа" : (r.GetInt32(3) > 0 ? "cashout/приз" : "проигрыш");
            var setId = r.GetString(5);
            list.Add(new SessionRow(r.GetString(0), DateTime.Parse(r.GetString(1)), r.GetInt32(2), r.GetInt32(3), titles.GetValueOrDefault(setId, setId), resultText));
        }
        return list;
    }

    public static string GetSessionDetails(string sessionId)
    {
        using var app = OpenAppDb();
        using var cmd = app.CreateCommand();
        cmd.CommandText = @"SELECT started_at,ended_at,final_step,final_prize,finished_all,used_hint,used_5050,used_audience,game_set_id
FROM sessions WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", sessionId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return "Сессия не найдена.";

        var gameSetId = r.GetString(8);
        var gameSetTitle = gameSetId;
        using (var content = OpenContentDb())
        {
            using var titleCmd = content.CreateCommand();
            titleCmd.CommandText = "SELECT title FROM game_sets WHERE id=@id";
            titleCmd.Parameters.AddWithValue("@id", gameSetId);
            gameSetTitle = titleCmd.ExecuteScalar()?.ToString() ?? gameSetId;
        }

        var text = $"Набор: {gameSetTitle}\nСтарт: {r.GetString(0)}\nФиниш: {(r.IsDBNull(1) ? "-" : r.GetString(1))}\n" +
                   $"Финальный шаг: {r.GetInt32(2)}\nПриз: {r.GetInt32(3):N0} ₽\nПобеда: {(r.GetInt32(4) == 1 ? "Да" : "Нет")}\n" +
                   $"Лайфлайны: hint={r.GetInt32(5)}, 50/50={r.GetInt32(6)}, audience={r.GetInt32(7)}\n\nОтветы:\n";

        using var d = app.CreateCommand();
        d.CommandText = @"SELECT step_index,question_id,chosen_answer,is_correct,used_hint,used_5050,used_audience,audience_a,audience_b,audience_c,audience_d
FROM session_answers WHERE session_id=@id ORDER BY step_index";
        d.Parameters.AddWithValue("@id", sessionId);
        using var dr = d.ExecuteReader();

        while (dr.Read())
        {
            var qId = dr.GetString(1);
            var qText = qId;
            using var content = OpenContentDb();
            using var qCmd = content.CreateCommand();
            qCmd.CommandText = "SELECT question_text FROM questions WHERE id=@id";
            qCmd.Parameters.AddWithValue("@id", qId);
            qText = qCmd.ExecuteScalar()?.ToString() ?? qId;
            if (qText.Length > 60) qText = qText[..60] + "...";

            var audience = dr.IsDBNull(7) ? "" : $" | зал A{dr.GetInt32(7)} B{dr.GetInt32(8)} C{dr.GetInt32(9)} D{dr.GetInt32(10)}";
            text += $"Шаг {dr.GetInt32(0)}: {qText}\n  Ответ: {dr.GetString(2)} | {(dr.GetInt32(3) == 1 ? "верно" : "ошибка")} | подсказка={dr.GetInt32(4)} 50/50={dr.GetInt32(5)} зал={dr.GetInt32(6)}{audience}\n";
        }

        return text;
    }

    public static string GetSetting(string key, string fallback)
    {
        using var c = OpenAppDb();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_settings WHERE key=@k";
        cmd.Parameters.AddWithValue("@k", key);
        var v = cmd.ExecuteScalar()?.ToString();
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    public static void SetSetting(string key, string value)
    {
        using var c = OpenAppDb();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO app_settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    private static void SetSettingDefault(string key, string value)
    {
        using var c = OpenAppDb();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO app_settings(key,value) VALUES(@k,@v)";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    public static string DownloadFileToTemp(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("URL пуст.");
        using var http = new HttpClient();
        var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        var file = Path.Combine(Path.GetTempPath(), "millioner_dlc_" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(file, bytes);
        return file;
    }

    public static string InstallFromUrl(string url)
    {
        var temp = DownloadFileToTemp(url);
        return InstallDlcFromZip(temp, url);
    }

    public static string InstallDlcFromZip(string zipPath, string? sourceUrl)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("ZIP не найден.", zipPath);
        var tempDir = Path.Combine(Path.GetTempPath(), "millioner_unpack_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        ZipFile.ExtractToDirectory(zipPath, tempDir, true);

        var manifestPath = Path.Combine(tempDir, "manifest.json");
        var ladderPath = Path.Combine(tempDir, "content", "ladder.json");
        var questionsPath = Path.Combine(tempDir, "content", "questions.json");
        if (!File.Exists(manifestPath) || !File.Exists(ladderPath) || !File.Exists(questionsPath))
            throw new InvalidOperationException("В ZIP отсутствует manifest.json или content/*.json");

        var manifest = JsonSerializer.Deserialize<DlcManifest>(File.ReadAllText(manifestPath)) ?? throw new InvalidOperationException("Неверный manifest.json");
        var ladder = JsonSerializer.Deserialize<DlcLadder>(File.ReadAllText(ladderPath)) ?? throw new InvalidOperationException("Неверный ladder.json");
        var qs = JsonSerializer.Deserialize<DlcQuestions>(File.ReadAllText(questionsPath)) ?? throw new InvalidOperationException("Неверный questions.json");

        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Slug) || string.IsNullOrWhiteSpace(manifest.Title) || string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidOperationException("manifest: нужны id/slug/title/version");
        if (CompareVersion(AppVersion, manifest.MinAppVersion ?? "1.0.0") < 0)
            throw new InvalidOperationException("Версия приложения ниже minAppVersion DLC.");
        if (ladder.Steps.Count < 5) throw new InvalidOperationException("Лестница должна содержать минимум 5 шагов.");

        var allowedSteps = ladder.Steps.Select(x => x.StepIndex).ToHashSet();
        foreach (var q in qs.Questions)
        {
            if (!allowedSteps.Contains(q.StepIndex)) throw new InvalidOperationException($"Вопрос {q.Id} с шагом {q.StepIndex} отсутствует в лестнице.");
            if (string.IsNullOrWhiteSpace(q.Answers.A) || string.IsNullOrWhiteSpace(q.Answers.B) || string.IsNullOrWhiteSpace(q.Answers.C) || string.IsNullOrWhiteSpace(q.Answers.D))
                throw new InvalidOperationException("Ответы A-D должны быть непустыми.");
            if (!(q.Correct is "A" or "B" or "C" or "D")) throw new InvalidOperationException("correct должен быть A/B/C/D.");
        }

        using var c = OpenContentDb();
        using var tx = c.BeginTransaction();
        string gameSetId;
        string? oldVersion = null;

        using (var sel = c.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT id,version FROM game_sets WHERE slug=@slug";
            sel.Parameters.AddWithValue("@slug", manifest.Slug);
            using var r = sel.ExecuteReader();
            if (r.Read())
            {
                gameSetId = r.GetString(0);
                oldVersion = r.GetString(1);
            }
            else gameSetId = Guid.NewGuid().ToString("N");
        }

        if (oldVersion is not null && CompareVersion(manifest.Version, oldVersion) <= 0)
            throw new InvalidOperationException($"Пакет версии {manifest.Version} не новее установленной {oldVersion}.");

        if (oldVersion is null)
        {
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO game_sets(id,slug,title,description,version,author,installed_at,enabled) VALUES(@id,@slug,@title,@d,@v,@a,@at,1)";
            ins.Parameters.AddWithValue("@id", gameSetId);
            ins.Parameters.AddWithValue("@slug", manifest.Slug);
            ins.Parameters.AddWithValue("@title", manifest.Title);
            ins.Parameters.AddWithValue("@d", manifest.Description ?? "");
            ins.Parameters.AddWithValue("@v", manifest.Version);
            ins.Parameters.AddWithValue("@a", manifest.Author ?? "unknown");
            ins.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("o"));
            ins.ExecuteNonQuery();
        }
        else
        {
            using var upd = c.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE game_sets SET title=@title,description=@d,version=@v,author=@a,installed_at=@at,enabled=1 WHERE id=@id";
            upd.Parameters.AddWithValue("@id", gameSetId);
            upd.Parameters.AddWithValue("@title", manifest.Title);
            upd.Parameters.AddWithValue("@d", manifest.Description ?? "");
            upd.Parameters.AddWithValue("@v", manifest.Version);
            upd.Parameters.AddWithValue("@a", manifest.Author ?? "unknown");
            upd.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("o"));
            upd.ExecuteNonQuery();

            using var delQ = c.CreateCommand();
            delQ.Transaction = tx;
            delQ.CommandText = "DELETE FROM questions WHERE game_set_id=@id";
            delQ.Parameters.AddWithValue("@id", gameSetId);
            delQ.ExecuteNonQuery();

            using var delL = c.CreateCommand();
            delL.Transaction = tx;
            delL.CommandText = "DELETE FROM prize_ladders WHERE game_set_id=@id";
            delL.Parameters.AddWithValue("@id", gameSetId);
            delL.ExecuteNonQuery();
        }

        foreach (var s in ladder.Steps.OrderBy(x => x.StepIndex))
        {
            using var insL = c.CreateCommand();
            insL.Transaction = tx;
            insL.CommandText = "INSERT INTO prize_ladders(id,game_set_id,step_index,prize_amount,is_safe) VALUES(@id,@g,@si,@p,@safe)";
            insL.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            insL.Parameters.AddWithValue("@g", gameSetId);
            insL.Parameters.AddWithValue("@si", s.StepIndex);
            insL.Parameters.AddWithValue("@p", s.Prize);
            insL.Parameters.AddWithValue("@safe", s.Safe ? 1 : 0);
            insL.ExecuteNonQuery();
        }

        foreach (var q in qs.Questions)
        {
            using var iq = c.CreateCommand();
            iq.Transaction = tx;
            iq.CommandText = @"INSERT INTO questions(id,game_set_id,step_index,question_text,hint_text,answer_a,answer_b,answer_c,answer_d,correct_answer,explanation)
VALUES(@id,@g,@si,@q,@h,@a,@b,@c,@d,@ok,@e)";
            iq.Parameters.AddWithValue("@id", string.IsNullOrWhiteSpace(q.Id) ? Guid.NewGuid().ToString("N") : q.Id);
            iq.Parameters.AddWithValue("@g", gameSetId);
            iq.Parameters.AddWithValue("@si", q.StepIndex);
            iq.Parameters.AddWithValue("@q", q.Question);
            iq.Parameters.AddWithValue("@h", q.Hint ?? "");
            iq.Parameters.AddWithValue("@a", q.Answers.A);
            iq.Parameters.AddWithValue("@b", q.Answers.B);
            iq.Parameters.AddWithValue("@c", q.Answers.C);
            iq.Parameters.AddWithValue("@d", q.Answers.D);
            iq.Parameters.AddWithValue("@ok", q.Correct);
            iq.Parameters.AddWithValue("@e", q.Explanation ?? "");
            iq.ExecuteNonQuery();
        }

        tx.Commit();
        return $"DLC '{manifest.Title}' установлен (ver {manifest.Version}) из {(sourceUrl ?? "локального файла")}.";
    }

    private static int CompareVersion(string a, string b)
    {
        try
        {
            var av = a.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            var bv = b.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            var max = Math.Max(av.Length, bv.Length);
            for (var i = 0; i < max; i++)
            {
                var ai = i < av.Length ? av[i] : 0;
                var bi = i < bv.Length ? bv[i] : 0;
                if (ai > bi) return 1;
                if (ai < bi) return -1;
            }
            return 0;
        }
        catch
        {
            return string.Compare(a, b, StringComparison.Ordinal);
        }
    }

    private static List<QuestionSeed> BuildGeneralQuestions() =>
    [
        new(1,"Сколько дней в неделе?","Подумайте о календаре.","5","6","7","8","C","В неделе семь дней."),
        new(1,"Какой цвет получается при смешении синего и жёлтого?","Это цвет травы.","Фиолетовый","Зелёный","Оранжевый","Серый","B","Синий + жёлтый = зелёный."),
        new(1,"Какой месяц идёт после марта?","Весна продолжается.","Апрель","Май","Февраль","Июнь","A","После марта идёт апрель."),
        new(2,"Сколько ног у паука?","Это больше шести.","6","8","10","12","B","У пауков 8 ног."),
        new(2,"Какой прибор показывает время?","Его носят на руке.","Барометр","Термометр","Часы","Компас","C","Часы измеряют время."),
        new(2,"Как называется вода в твёрдом состоянии?","Зимой на улице.","Пар","Лёд","Роса","Туман","B","Твёрдое состояние воды — лёд."),
        new(3,"Сколько градусов в прямом угле?","Половина развёрнутого.","45","90","120","180","B","Прямой угол — 90 градусов."),
        new(3,"Какая планета известна как Красная планета?","Четвёртая от Солнца.","Венера","Юпитер","Марс","Сатурн","C","Марс называют Красной планетой."),
        new(3,"Какой океан самый большой?","Он носит название спокойного.","Атлантический","Индийский","Северный Ледовитый","Тихий","D","Тихий океан крупнейший."),
        new(4,"Кто написал роман «Война и мир»?","Имя Лев.","Тургенев","Толстой","Гоголь","Чехов","B","Автор — Лев Толстой."),
        new(4,"Сколько материков обычно выделяют в школьной географии?","Их больше пяти.","5","6","7","8","C","Обычно говорят о 7 материках."),
        new(4,"Какой газ необходим человеку для дыхания?","Формула O2.","Азот","Кислород","Водород","Гелий","B","Для дыхания нужен кислород."),
        new(5,"Как называется наука о живых организмах?","Про растения и животных.","Физика","Химия","Биология","Геология","C","Биология изучает живое."),
        new(5,"Какой город является столицей Италии?","Там находится Колизей.","Милан","Рим","Венеция","Неаполь","B","Столица Италии — Рим."),
        new(5,"Сколько континентов пересекает экватор?","Их два.","1","2","3","4","B","Экватор проходит по Африке и Южной Америке."),
        new(6,"Какой химический символ у золота?","Две латинские буквы.","Ag","Au","Fe","Go","B","Золото — Au."),
        new(6,"Что измеряют в герцах?","Это связано с частотой.","Скорость","Длину","Частоту","Массу","C","Герц — единица частоты."),
        new(6,"Как называется самая длинная кость в теле человека?","В ноге.","Лучевая","Бедренная","Плечевая","Большеберцовая","B","Самая длинная — бедренная."),
        new(7,"Кто предложил теорию относительности?","Фамилия начинается на Э.","Ньютон","Эйнштейн","Галилей","Пастер","B","Теорию относительности разработал Эйнштейн."),
        new(7,"Какая часть клетки содержит наследственную информацию?","Обычно в центре клетки.","Мембрана","Цитоплазма","Ядро","Рибосома","C","ДНК в основном хранится в ядре."),
        new(7,"Какой пролив разделяет Европу и Азию в районе Стамбула?","Связывает Чёрное и Мраморное моря.","Гибралтар","Босфор","Ла-Манш","Дарданеллы","B","Босфор проходит через Стамбул."),
        new(8,"Как называется процесс превращения газа в жидкость?","Обратный испарению.","Сублимация","Конденсация","Диссоциация","Диффузия","B","Газ в жидкость — конденсация."),
        new(8,"Какой композитор написал «Лунную сонату»?","Немецкий классик.","Бах","Моцарт","Бетховен","Шопен","C","«Лунная соната» — Бетховен."),
        new(8,"Какое число является простым?","Проверьте делители.","21","27","29","33","C","29 делится только на 1 и 29."),
        new(9,"Какой элемент имеет атомный номер 1?","Самый лёгкий.","Гелий","Водород","Литий","Кислород","B","Атомный номер 1 у водорода."),
        new(9,"Какой язык был основой для C#?","Его разработали в 90-х в Sun.","Pascal","Java","Ruby","Swift","B","C# во многом вдохновлён Java."),
        new(9,"Кто написал «Божественную комедию»?","Итальянский поэт Средневековья.","Петрарка","Данте","Боккаччо","Сервантес","B","Автор — Данте Алигьери."),
        new(10,"В каком году состоялась первая высадка человека на Луну?","Конец 1960-х.","1967","1968","1969","1970","C","Apollo 11 — 1969 год."),
        new(10,"Как называется граница, после которой чёрная дыра не выпускает свет?","Событие в самом названии.","Граница Шварцшильда","Горизонт событий","Предел Чандрасекара","Сфера Хилла","B","Правильный термин — горизонт событий."),
        new(10,"Какое утверждение о воде верно при нормальном давлении?","Вспомните стандартные точки.","Кипит при 90°C","Замерзает при 0°C","Кипит при 120°C","Замерзает при -10°C","B","При 1 атм вода замерзает при 0°C.")
    ];

    private static List<QuestionSeed> BuildItQuestions() =>
    [
        new(1,"Что означает аббревиатура CPU?","Это центральная часть компьютера.","Central Processing Unit","Computer Personal Unit","Central Program Utility","Core Processing User","A","CPU — центральный процессор."),
        new(1,"Какая клавиша удаляет символ слева от курсора?","Часто над Enter.","Delete","Backspace","Insert","Tab","B","Backspace удаляет слева."),
        new(1,"Какой бренд создал Windows?","Крупная американская компания.","Apple","Google","Microsoft","Intel","C","Windows разрабатывает Microsoft."),
        new(2,"Сколько бит в одном байте?","Классическая единица.","4","8","16","32","B","В одном байте 8 бит."),
        new(2,"Какой порт по умолчанию использует HTTP?","Двузначное число.","21","25","53","80","D","HTTP обычно работает на 80 порту."),
        new(2,"Какой файл обычно содержит стили веб-страницы?","Ищите расширение .css.","index.js","styles.css","data.xml","main.py","B","CSS хранится в .css файлах."),
        new(3,"IPv4-адрес состоит из скольких бит?","Это 4 байта.","16","24","32","64","C","IPv4 = 32 бита."),
        new(3,"Какой протокол нужен для безопасного веб-соединения?","HTTP + шифрование.","FTP","SMTP","SSH","HTTPS","D","HTTPS использует TLS."),
        new(3,"Как называется постоянная память BIOS/UEFI?","Не RAM.","ROM","VRAM","SRAM","Cache","A","Прошивка хранится в ROM/flash."),
        new(4,"Какое расширение имеет исполняемый файл в Windows?","Точка и три буквы.",".dll",".sys",".exe",".bat","C","Основное исполняемое расширение — .exe."),
        new(4,"Что делает команда ping?","Связано с задержкой сети.","Шифрует трафик","Проверяет доступность узла","Изменяет IP","Очищает DNS","B","Ping проверяет отклик хоста."),
        new(4,"Какая структура данных работает по принципу LIFO?","Последний пришёл — первый ушёл.","Queue","Stack","Tree","Graph","B","LIFO — это стек."),
        new(5,"Какой язык выполняется в браузере напрямую?","Не требует компиляции в exe.","C++","JavaScript","C#","Rust","B","Браузеры исполняют JavaScript."),
        new(5,"Что такое SSD?","Устройство хранения без вращающихся дисков.","Тип монитора","Твердотельный накопитель","Сетевой протокол","Язык программирования","B","SSD — твердотельный накопитель."),
        new(5,"Что означает DNS?","Связано с доменными именами.","Dynamic Network System","Domain Name System","Data Node Service","Direct Name Setup","B","DNS сопоставляет имена и IP."),
        new(6,"Какой HTTP-метод обычно используют для создания ресурса?","Подсказка: не GET.","PUT","DELETE","POST","HEAD","C","POST часто используют для создания."),
        new(6,"Что из перечисленного — реляционная СУБД?","Имеет SQL.","Redis","MongoDB","SQLite","Neo4j","C","SQLite — реляционная БД."),
        new(6,"Какая команда Git создаёт локальную копию удалённого репозитория?","Начало работы с проектом.","git merge","git pull","git clone","git stash","C","clone скачивает репозиторий."),
        new(7,"Какой уровень модели OSI отвечает за маршрутизацию?","Слой с IP.","Канальный","Транспортный","Сетевой","Прикладной","C","Маршрутизация — сетевой уровень."),
        new(7,"Как называется процесс преобразования исходного кода в машинный?","Обычно делает compiler.","Интерпретация","Компиляция","Виртуализация","Сериализация","B","Компиляция превращает код в машинный."),
        new(7,"Какая асимптотика бинарного поиска в отсортированном массиве?","Логарифм.","O(n)","O(log n)","O(n log n)","O(1)","B","Бинарный поиск — O(log n)."),
        new(8,"Что такое SQL-инъекция?","Уязвимость связана с запросами.","Сжатие БД","Подмена SQL-запроса","Ошибка драйвера","Тип индекса","B","Инъекция — внедрение фрагментов SQL."),
        new(8,"Какой протокол используется для безопасного удалённого доступа к серверу?","Обычно порт 22.","Telnet","FTP","SSH","SNMP","C","SSH шифрует удалённый доступ."),
        new(8,"Как называется структура, где у узла не более двух потомков?","Дерево специального вида.","B-Tree","Binary Tree","Trie","Heap File","B","Бинарное дерево — до двух потомков."),
        new(9,"Какой тип RAID обеспечивает зеркалирование дисков?","Повышает отказоустойчивость.","RAID 0","RAID 1","RAID 5","RAID 10","B","RAID 1 — mirror."),
        new(9,"Что в TCP обеспечивает надёжность доставки?","Подтверждение и повторная отправка.","Только checksum","ACK и ретрансляции","Только шифрование","Только DNS","B","TCP использует ACK/ретрансляции."),
        new(9,"Как называется паттерн, ограничивающий класс одним экземпляром?","Часто применяют для конфигов.","Factory","Observer","Singleton","Adapter","C","Singleton оставляет один экземпляр."),
        new(10,"Какой алгоритм используется в TLS для обмена ключом в современных конфигурациях чаще всего?","С эллиптическими кривыми.","DES","RSA только","ECDHE","MD5","C","ECDHE даёт прямую секретность."),
        new(10,"Как называется состояние, когда два потока ждут друг друга и не продолжают работу?","Проблема синхронизации.","Starvation","Deadlock","Race condition","Livelock","B","Deadlock — взаимная блокировка."),
        new(10,"Что означает CAP-теорема в распределённых системах?","Три свойства одновременно.","Нельзя иметь одновременно консистентность, доступность и устойчивость к разделению","Всегда нужно шифрование","БД должна быть реляционной","Сеть должна быть без потерь","A","CAP описывает компромисс C/A/P.")
    ];

    private static List<QuestionSeed> BuildKzQuestions() =>
    [
        new(1,"Столица Казахстана на текущий момент?","Город на реке Ишим.","Алматы","Астана","Шымкент","Караганда","B","Столица — Астана."),
        new(1,"Какая валюта используется в Казахстане?","На банкнотах есть надпись ₸.","Сом","Тенге","Рубль","Манат","B","Национальная валюта — тенге."),
        new(1,"Какой цвет преобладает на флаге Казахстана?","Цвет неба.","Зелёный","Красный","Голубой","Белый","C","Флаг Казахстана голубого цвета."),
        new(2,"Крупнейший город Казахстана по населению?","Южная столица.","Астана","Алматы","Атырау","Павлодар","B","Алматы — самый населённый город."),
        new(2,"Как называется космодром в Казахстане?","Отсюда стартовали многие ракеты.","Восточный","Байконур","Плесецк","Куру","B","Космодром — Байконур."),
        new(2,"Какое море омывает запад Казахстана?","Внутреннее крупнейшее озеро-море.","Чёрное","Средиземное","Каспийское","Балтийское","C","Запад страны у Каспийского моря."),
        new(3,"Какая горная система находится на юго-востоке Казахстана?","Включает Заилийский Алатау.","Урал","Тянь-Шань","Карпаты","Кавказ","B","Юго-восток связан с Тянь-Шанем."),
        new(3,"Какой государственный язык в Казахстане?","Используется в официальных документах.","Русский","Узбекский","Казахский","Турецкий","C","Государственный язык — казахский."),
        new(3,"Как называется традиционное жилище кочевников?","Круглая переносная конструкция.","Изба","Юрта","Сакля","Яранга","B","Традиционное жилище — юрта."),
        new(4,"Как называется национальный струнный инструмент с двумя струнами?","Часто используется в кюях.","Саз","Домбра","Балалайка","Кобыз","B","Домбра — символ казахской музыки."),
        new(4,"Какой город считается родиной яблони Сиверса и дал название сорту «апорт»?","Южный мегаполис.","Костанай","Астана","Алматы","Семей","C","Алматы исторически ассоциируется с яблоками."),
        new(4,"Какой праздник отмечают в Казахстане как праздник весны и обновления?","Празднуют в марте.","Курбан айт","Наурыз","День независимости","Ораза айт","B","Наурыз отмечают весной."),
        new(5,"Какая река протекает через Астану?","Река в центре столицы.","Иртыш","Ишим","Урал","Или","B","Через Астану протекает Ишим."),
        new(5,"Как называется крупнейшее озеро Казахстана (частично в стране)?","Очень солёное.","Балхаш","Арал","Каспийское море","Зайсан","C","Каспий — крупнейший водоём у Казахстана."),
        new(5,"Какое животное изображено на гербе Казахстана как крылатый образ?","Мифологический конь.","Архар","Беркут","Тулпар","Барс","C","На гербе — тулпары."),
        new(6,"В каком году Казахстан провозгласил независимость?","Конец 1991 года.","1989","1990","1991","1992","C","Независимость объявлена в 1991."),
        new(6,"Как называется крупнейшее высокогорное спортивное сооружение рядом с Алматы?","Известный каток.","Медеу","Барыс Арена","Сункар","Хан Шатыр","A","Высокогорный комплекс — Медеу."),
        new(6,"Какой город расположен на берегу Иртыша и является крупным центром Восточного Казахстана?","Известен металлургией.","Усть-Каменогорск","Тараз","Кокшетау","Актау","A","Усть-Каменогорск стоит на Иртыше."),
        new(7,"Как называется древний шёлковый путь, проходивший через территорию Казахстана?","Торговый маршрут между Востоком и Западом.","Янтарный путь","Великий шёлковый путь","Путь специй","Северный путь","B","Через регион проходил Великий шёлковый путь."),
        new(7,"Какой заповедник в Казахстане известен колониями розовых фламинго?","Озёрная система в центре страны.","Аксу-Жабаглы","Коргалжынский","Маркакольский","Наурзумский","B","Фламинго знамениты в Коргалжыне."),
        new(7,"Какой город Казахстана является крупным портом на Каспии?","На западе страны.","Актобе","Актау","Петропавловск","Туркестан","B","Главный порт на Каспии — Актау."),
        new(8,"Как называется уникальный объект в Чарынском каньоне, часто сравниваемый с замками?","Известная долина.","Долина ветров","Долина замков","Долина кедров","Долина песков","B","Самый известный участок — Долина замков."),
        new(8,"Какой город считается духовным центром благодаря мавзолею Ходжи Ахмеда Ясави?","Юг Казахстана.","Туркестан","Талдыкорган","Семей","Кызылорда","A","Мавзолей Ясави находится в Туркестане."),
        new(8,"Какая природная зона занимает значительную часть территории Казахстана?","Открытые равнины с травами.","Тундра","Степь","Тайга","Саванна","B","Казахстан во многом степная страна."),
        new(9,"Как называется документ, принятый в 1995 году и определяющий основы государства?","Основной закон.","Кодекс","Конституция","Декларация","Устав","B","Основной закон — Конституция."),
        new(9,"Какое озеро в Казахстане известно тем, что его западная часть пресная, а восточная солёная?","Уникальный водоём.","Алаколь","Балхаш","Зайсан","Тенгиз","B","Балхаш имеет разные по солёности части."),
        new(9,"Как называется международная выставка, прошедшая в Астане в 2017 году?","Тема — энергия будущего.","Expo 2015","Expo 2017","Expo 2020","Expo 2012","B","В Астане проходила Expo 2017."),
        new(10,"Как называется национальное блюдо из варёного мяса и теста, подаваемое на торжествах?","Часто подают на больших праздниках.","Плов","Куырдак","Бешбармак","Манты","C","Бешбармак — традиционное блюдо."),
        new(10,"Какой объект ЮНЕСКО в Казахстане связан с древними петроглифами и расположен близ Алматы?","Ущелье с наскальными рисунками.","Тамгалы","Бозжыра","Мангистау","Кольсай","A","Петроглифы Тамгалы включены в ЮНЕСКО."),
        new(10,"Какой город Казахстана исторически известен как центр Семиречья и ранее назывался Верный?","Крупнейший мегаполис.","Алматы","Тараз","Павлодар","Костанай","A","Верный — историческое название Алматы.")
    ];

    private static List<QuestionSeed> BuildPlants120Questions()
    {
        var result = new List<QuestionSeed>();
        var letters = new[] { "A", "B", "C", "D" };
        for (var step = 1; step <= 15; step++)
        {
            for (var i = 1; i <= 8; i++)
            {
                var correctIndex = (step + i) % 4;
                var correctLetter = letters[correctIndex];
                var topic = step switch
                {
                    <= 3 => "базовая ботаника",
                    <= 6 => "сад и огород",
                    <= 9 => "деревья и экология",
                    <= 12 => "физиология растений",
                    _ => "сложные ботанические факты"
                };
                var q = $"Растения #{step:00}-{i:00}: что верно про тему «{topic}»?";
                var hint = "Подумайте о школьной ботанике и практическом уходе.";
                var a = i % 4 == 1 ? "Хлорофилл участвует в фотосинтезе" : "Хлорофилл нужен только корням";
                var b = i % 4 == 2 ? "Корни обычно поглощают воду и минеральные вещества" : "Корни служат только для дыхания";
                var c = i % 4 == 3 ? "Большинству комнатных растений вреден постоянный перелив" : "Постоянный перелив полезен всем растениям";
                var d = i % 4 == 0 ? "Семена формируются после опыления у цветковых" : "Семена образуются без опыления у всех цветковых";
                var answers = new[] { a, b, c, d };
                var explanation = "Верно базовое утверждение о строении и жизнедеятельности растений.";
                result.Add(new QuestionSeed(step, q, hint, answers[0], answers[1], answers[2], answers[3], correctLetter, explanation));
            }
        }
        return result;
    }

    private static List<QuestionSeed> BuildAutoMech72Questions()
    {
        var result = new List<QuestionSeed>();
        var letters = new[] { "A", "B", "C", "D" };
        for (var step = 1; step <= 15; step++)
        {
            var count = step <= 12 ? 5 : 4;
            for (var i = 1; i <= count; i++)
            {
                var correctIndex = (step * i + 1) % 4;
                var correctLetter = letters[correctIndex];
                var q = $"Авто #{step:00}-{i:00}: какое утверждение по обслуживанию автомобиля корректно?";
                var hint = "Ориентируйтесь на базовые принципы ДВС и безопасности.";
                var a = i % 4 == 1 ? "Тормозную жидкость меняют по регламенту" : "Тормозную жидкость не меняют никогда";
                var b = i % 4 == 2 ? "Недостаток масла может повредить двигатель" : "Уровень масла не влияет на ресурс двигателя";
                var c = i % 4 == 3 ? "Сход-развал влияет на износ шин и управляемость" : "Сход-развал нужен только для красоты";
                var d = i % 4 == 0 ? "Аккумулятору вредны глубокие разряды" : "Глубокие разряды всегда полезны аккумулятору";
                var answers = new[] { a, b, c, d };
                var explanation = "Это базовое правило безопасной эксплуатации и обслуживания авто.";
                result.Add(new QuestionSeed(step, q, hint, answers[0], answers[1], answers[2], answers[3], correctLetter, explanation));
            }
        }
        return result;
    }

}

public record GameSet(string Id, string Slug, string Title, string Description, string Version, string Author, string InstalledAt, bool Enabled);
public record LadderItem(int StepIndex, int PrizeAmount, bool IsSafe);
public record QuestionItem(string Id, int StepIndex, string QuestionText, string HintText, string AnswerA, string AnswerB, string AnswerC, string AnswerD, char CorrectAnswer, string Explanation);
public record AudienceResult(int A, int B, int C, int D);
public record SessionRow(string Id, DateTime StartedAt, int FinalStep, int FinalPrize, string GameSetTitle, string ResultText);
public record QuestionSeed(int Step, string Question, string Hint, string A, string B, string C, string D, string Correct, string Explanation);

public sealed class DlcManifest
{
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string? MinAppVersion { get; set; }
}

public sealed class DlcLadder
{
    public string Currency { get; set; } = "RUB";
    public List<DlcLadderStep> Steps { get; set; } = [];
}

public sealed class DlcLadderStep
{
    public int StepIndex { get; set; }
    public int Prize { get; set; }
    public bool Safe { get; set; }
}

public sealed class DlcQuestions
{
    public List<DlcQuestion> Questions { get; set; } = [];
}

public sealed class DlcQuestion
{
    public string Id { get; set; } = "";
    public int StepIndex { get; set; }
    public string Question { get; set; } = "";
    public string Hint { get; set; } = "";
    public DlcAnswers Answers { get; set; } = new();
    public string Correct { get; set; } = "A";
    public string Explanation { get; set; } = "";
}

public sealed class DlcAnswers
{
    public string A { get; set; } = "";
    public string B { get; set; } = "";
    public string C { get; set; } = "";
    public string D { get; set; } = "";
}
