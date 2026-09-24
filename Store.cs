using Microsoft.Data.Sqlite;

namespace EpubReview;

public sealed class Store : IDisposable
{
    private readonly SqliteConnection _db;
    private SqliteTransaction? _tx;

    public Store(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
        _db = new SqliteConnection(cs);
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS materials(
              id TEXT PRIMARY KEY,
              kind TEXT NOT NULL,
              name TEXT NOT NULL,
              bytes BLOB NOT NULL,
              sha256 TEXT NOT NULL,
              media_type TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              id TEXT UNIQUE,
              parent_seq INTEGER,
              kind TEXT NOT NULL,
              payload TEXT NOT NULL,
              created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS derivations(
              id TEXT PRIMARY KEY,
              event_seq INTEGER NOT NULL,
              rule_version TEXT NOT NULL,
              input_digest TEXT NOT NULL,
              parents TEXT NOT NULL,
              output TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS kv(
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS journal(
              id TEXT PRIMARY KEY,
              state TEXT NOT NULL,
              payload TEXT NOT NULL,
              created TEXT NOT NULL);
            """);
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        cmd.ExecuteNonQuery();
    }

    public void WithTransaction(Action body)
    {
        if (_tx is not null) { body(); return; }
        using var tx = _db.BeginTransaction();
        _tx = tx;
        try
        {
            body();
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        finally
        {
            _tx = null;
        }
    }

    public void AddMaterial(Material material)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = "INSERT OR IGNORE INTO materials(id,kind,name,bytes,sha256,media_type) VALUES($id,$kind,$name,$bytes,$sha,$mt)";
        Bind(cmd, material);
        cmd.ExecuteNonQuery();
        var existing = GetMaterial(material.Id);
        if (existing is not null && existing.Sha256 != material.Sha256)
            throw new InvalidOperationException($"材料身份冲突: {material.Id} 已存在但 SHA-256 不一致");
    }

    private static void Bind(SqliteCommand cmd, Material m)
    {
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$kind", m.Kind);
        cmd.Parameters.AddWithValue("$name", m.Name);
        cmd.Parameters.AddWithValue("$bytes", m.Bytes);
        cmd.Parameters.AddWithValue("$sha", m.Sha256);
        cmd.Parameters.AddWithValue("$mt", m.MediaType);
    }

    public Material? GetMaterial(string id)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = "SELECT id,kind,name,bytes,sha256,media_type FROM materials WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ReadMaterial(r);
    }

    public List<Material> AllMaterials()
    {
        var list = new List<Material>();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = "SELECT id,kind,name,bytes,sha256,media_type FROM materials ORDER BY kind,name";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMaterial(r));
        return list;
    }

    private static Material ReadMaterial(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), (byte[])r[3], r.GetString(4), r.GetString(5));

    public EventRecord AppendEvent(string kind, string payload, long? parentSeq)
    {
        var created = DateTimeOffset.UtcNow.ToString("O");
        long seq;
        using (var cmd = _db.CreateCommand())
        {
            cmd.Transaction = _tx;
            cmd.CommandText = "INSERT INTO events(id,parent_seq,kind,payload,created) VALUES(NULL,$p,$k,$pl,$c); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$p", (object?)parentSeq ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$pl", payload);
            cmd.Parameters.AddWithValue("$c", created);
            seq = (long)cmd.ExecuteScalar()!;
        }
        var id = $"evt-{seq:D4}";
        using (var cmd = _db.CreateCommand())
        {
            cmd.Transaction = _tx;
            cmd.CommandText = "UPDATE events SET id=$id WHERE seq=$seq";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$seq", seq);
            cmd.ExecuteNonQuery();
        }
        return new EventRecord(seq, id, parentSeq, kind, payload, created);
    }

    public void InsertEventPreserving(EventRecord e)
    {
        using var check = _db.CreateCommand();
        check.Transaction = _tx;
        check.CommandText = "SELECT id,payload FROM events WHERE seq=$seq";
        check.Parameters.AddWithValue("$seq", e.Seq);
        using (var r = check.ExecuteReader())
        {
            if (r.Read())
            {
                if (r.GetString(0) != e.Id || r.GetString(1) != e.Payload)
                    throw new InvalidOperationException($"事件 seq={e.Seq} 与现有记录冲突，拒绝半写合并");
                return;
            }
        }
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = "INSERT INTO events(seq,id,parent_seq,kind,payload,created) VALUES($seq,$id,$p,$k,$pl,$c)";
        cmd.Parameters.AddWithValue("$seq", e.Seq);
        cmd.Parameters.AddWithValue("$id", e.Id);
        cmd.Parameters.AddWithValue("$p", (object?)e.ParentSeq ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", e.Kind);
        cmd.Parameters.AddWithValue("$pl", e.Payload);
        cmd.Parameters.AddWithValue("$c", e.CreatedAt);
        cmd.ExecuteNonQuery();
    }

    public List<EventRecord> Events(long? upTo = null)
    {
        var list = new List<EventRecord>();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = "SELECT seq,id,parent_seq,kind,payload,created FROM events WHERE ($u IS NULL OR seq<=$u) ORDER BY seq";
        cmd.Parameters.AddWithValue("$u", (object?)upTo ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new EventRecord(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2),
                r.GetString(3), r.GetString(4), r.GetString(5)));
        return list;
    }

    public EventRecord? LastEvent(string kind, long? upTo = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = "SELECT seq,id,parent_seq,kind,payload,created FROM events WHERE kind=$k AND ($u IS NULL OR seq<=$u) ORDER BY seq DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$u", (object?)upTo ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new EventRecord(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2),
            r.GetString(3), r.GetString(4), r.GetString(5)) : null;
    }

    public void PutDerivation(Derivation d)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO derivations(id,event_seq,rule_version,input_digest,parents,output)
            VALUES($id,$es,$rv,$idg,$parents,$out)
            """;
        cmd.Parameters.AddWithValue("$id", d.Id);
        cmd.Parameters.AddWithValue("$es", d.EventSeq);
        cmd.Parameters.AddWithValue("$rv", d.RuleVersion);
        cmd.Parameters.AddWithValue("$idg", d.InputDigest);
        cmd.Parameters.AddWithValue("$parents", d.ParentsJson);
        cmd.Parameters.AddWithValue("$out", d.OutputJson);
        cmd.ExecuteNonQuery();
    }

    public Derivation? GetDerivation(string id)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = "SELECT id,event_seq,rule_version,input_digest,parents,output FROM derivations WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Derivation(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5));
    }

    public List<Derivation> AllDerivations()
    {
        var list = new List<Derivation>();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = "SELECT id,event_seq,rule_version,input_digest,parents,output FROM derivations ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Derivation(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)));
        return list;
    }

    public string? GetKv(string key)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = "SELECT value FROM kv WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetKv(string key, string value)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = "INSERT OR REPLACE INTO kv(key,value) VALUES($k,$v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public void DeleteKv(string key)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = "DELETE FROM kv WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    public List<KeyValuePair<string, string>> AllKv()
    {
        var list = new List<KeyValuePair<string, string>>();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = "SELECT key,value FROM kv ORDER BY key";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1)));
        return list;
    }

    public void JournalBegin(string id, string payload)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO journal(id,state,payload,created) VALUES($id,'started',$p,$c)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$p", payload);
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void JournalDone(string id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM journal WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public List<string> RecoverJournal()
    {
        var ids = new List<string>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM journal WHERE state='started' ORDER BY id";
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
        }
        foreach (var id in ids)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM journal WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        return ids;
    }

    public void Reset()
    {
        WithTransaction(() =>
        {
            Exec("DELETE FROM materials; DELETE FROM events; DELETE FROM derivations; DELETE FROM kv; DELETE FROM journal;");
            Exec("DELETE FROM sqlite_sequence WHERE name IN ('events');");
        });
    }

    public void Dispose() => _db.Dispose();
}
