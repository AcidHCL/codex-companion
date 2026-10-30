using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DesktopPet;

internal sealed record ChatStoredImage(string Name, string Base64);
internal sealed record ChatStoredTurn(long Sequence, string User, string Assistant, string Model,
    IReadOnlyList<ChatStoredImage> Images, IReadOnlyList<PetWebSource> Sources, string? WebWarning, DateTimeOffset CreatedUtc);
internal sealed record ChatConversation(string Id, string Endpoint, string PersonaKey, string Model, string Title,
    DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, string Draft, IReadOnlyList<ChatStoredImage> DraftImages)
{
    public bool HasDraft { get; init; }
}
internal sealed record ChatSharedMemory(string Id, string Endpoint, string Title, string Content, bool Pinned, DateTimeOffset UpdatedUtc);

internal interface IChatMemoryStore : IDisposable
{
    // Library rows deliberately omit draft payloads/images. GetConversation loads them on selection.
    IReadOnlyList<ChatConversation> ListConversations(string endpoint, string personaKey);
    ChatConversation CreateConversation(string endpoint, string personaKey, string model);
    ChatConversation? GetConversation(string id);
    void SaveDraft(string id, string text, IReadOnlyList<ChatStoredImage> images);
    void RenameConversation(string id, string title);
    void DeleteConversation(string id);
    long AppendTurn(string id, ChatStoredTurn turn);
    // Each page is chronological; beforeSequence is exclusive. Older pages are never deleted.
    IReadOnlyList<ChatStoredTurn> ReadTurns(string id, int limit = 50, long? beforeSequence = null);
    string? GetActiveConversationId(string endpoint, string personaKey);
    void SetActiveConversation(string id);
    IReadOnlyList<ChatSharedMemory> ListMemories(string endpoint);
    ChatSharedMemory UpsertMemory(ChatSharedMemory memory);
    void DeleteMemory(string id);
}

internal sealed class ChatMemoryStoreException : Exception
{
    public ChatMemoryStoreException(Exception inner) : base(L.T("本地对话数据库暂时无法读取或保存；原文件已保留，请勿删除数据库。", "The local chat database could not be read or saved. Original files are preserved; do not delete the database."), inner) { }
}

// One small SQLite transaction commits each complete turn. Sensitive values are DPAPI
// ciphertext before SQLite sees them, including in its journal/WAL. This protects against
// accidental disclosure/other Windows accounts, not software running as the same user.
internal sealed class ChatMemoryStore : IChatMemoryStore
{
    private const int SchemaVersion = 1;
    private const int ApplicationId = 0x4149504D;
    private const int MaximumImageCharacters = 16 * 1024 * 1024;
    private const int MaximumPayloadBytes = 40 * 1024 * 1024;
    public const int MaximumMemoryTitleCharacters = 80;
    public const int MaximumMemoryContentCharacters = 1000;
    public const int MaximumMemories = 2000;
    private static string NewTitle => L.T("新对话", "New conversation");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private bool disposed;
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet", "chat-memory.db");
    internal string DatabasePath { get; }

    // Tests and transient windows must opt in explicitly before accessing user data.
    public ChatMemoryStore(string databasePath = ":memory:")
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException(L.T("数据库路径不能为空。", "The database path cannot be empty."), nameof(databasePath));
        DatabasePath = databasePath == ":memory:" ? databasePath : Path.GetFullPath(databasePath);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = DatabasePath == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 3, ForeignKeys = true
        }.ToString());
        try
        {
            if (DatabasePath != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            connection.Open();
            int version = Convert.ToInt32(Scalar(null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
            int application = Convert.ToInt32(Scalar(null, "PRAGMA application_id;"), CultureInfo.InvariantCulture);
            if (version > SchemaVersion || (version > 0 && application != ApplicationId) || (application != 0 && application != ApplicationId))
                throw new InvalidDataException("Unsupported chat database version or identity.");
            if (version == 0 && Convert.ToInt64(Scalar(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';"), CultureInfo.InvariantCulture) != 0)
                throw new InvalidDataException("Existing database has no recognized schema version.");
            Execute(null, "PRAGMA busy_timeout=3000; PRAGMA trusted_schema=OFF; PRAGMA temp_store=MEMORY; PRAGMA secure_delete=ON;");
            if (DatabasePath != ":memory:") Execute(null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
            if (version == 0) InitializeSchema();
            // Validate tables before accepting a connection, without rewriting existing data.
            _ = Scalar(null, "SELECT COUNT(*) FROM conversations;");
            _ = Scalar(null, "SELECT COUNT(*) FROM turns;");
            _ = Scalar(null, "SELECT COUNT(*) FROM shared_memories;");
            _ = Scalar(null, "SELECT COUNT(*) FROM active_conversations;");
        }
        catch (Exception ex)
        {
            connection.Dispose();
            if (IsStorageException(ex)) throw new ChatMemoryStoreException(ex);
            throw;
        }
    }

    private void InitializeSchema()
    {
        using var transaction = connection.BeginTransaction();
        Execute(transaction, """
            CREATE TABLE conversations (
                id TEXT PRIMARY KEY NOT NULL, endpoint TEXT NOT NULL, persona_key TEXT NOT NULL, model TEXT NOT NULL,
                title BLOB NOT NULL, title_auto INTEGER NOT NULL DEFAULT 1,
                created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, draft BLOB NOT NULL, has_draft INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX conversations_scope ON conversations(endpoint, persona_key, updated_utc DESC);
            CREATE TABLE turns (
                conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                sequence INTEGER NOT NULL, payload BLOB NOT NULL, PRIMARY KEY(conversation_id, sequence)
            );
            CREATE TABLE active_conversations (
                endpoint TEXT NOT NULL, persona_key TEXT NOT NULL,
                conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                PRIMARY KEY(endpoint, persona_key)
            );
            CREATE TABLE shared_memories (
                id TEXT PRIMARY KEY NOT NULL, endpoint TEXT NOT NULL, payload BLOB NOT NULL,
                pinned INTEGER NOT NULL, updated_utc TEXT NOT NULL
            );
            CREATE INDEX memories_scope ON shared_memories(endpoint, pinned DESC, updated_utc DESC);
            PRAGMA application_id=1095323725;
            PRAGMA user_version=1;
            """);
        transaction.Commit();
    }

    public IReadOnlyList<ChatConversation> ListConversations(string endpoint, string personaKey)
    {
        endpoint = ScopeEndpoint(endpoint); personaKey = ValidText(personaKey, 256, false, nameof(personaKey));
        return Locked(() =>
        {
            using var command = Command(null, "SELECT id,endpoint,persona_key,model,title,created_utc,updated_utc,has_draft FROM conversations WHERE endpoint=$endpoint AND persona_key=$persona ORDER BY updated_utc DESC,id;", ("$endpoint", endpoint), ("$persona", personaKey));
            using var reader = command.ExecuteReader();
            var result = new List<ChatConversation>();
            while (reader.Read()) result.Add(new ChatConversation(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                Decode<string>(reader.GetFieldValue<byte[]>(4)), Date(reader.GetString(5)), Date(reader.GetString(6)), "", Array.Empty<ChatStoredImage>()) { HasDraft = reader.GetBoolean(7) });
            return (IReadOnlyList<ChatConversation>)result;
        });
    }

    public ChatConversation CreateConversation(string endpoint, string personaKey, string model)
    {
        endpoint = ScopeEndpoint(endpoint); personaKey = ValidText(personaKey, 256, false, nameof(personaKey)); model = ValidModel(model);
        return Locked(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var item = new ChatConversation(Guid.NewGuid().ToString("N"), endpoint, personaKey, model, NewTitle, now, now, "", Array.Empty<ChatStoredImage>());
            using var transaction = connection.BeginTransaction();
            Execute(transaction, "INSERT INTO conversations(id,endpoint,persona_key,model,title,created_utc,updated_utc,draft) VALUES($id,$endpoint,$persona,$model,$title,$now,$now,$draft);",
                ("$id", item.Id), ("$endpoint", endpoint), ("$persona", personaKey), ("$model", model), ("$title", Encode(NewTitle)), ("$now", Stamp(now)), ("$draft", Encode(new DraftPayload("", Array.Empty<ChatStoredImage>()))));
            SetActive(transaction, item.Id, endpoint, personaKey);
            transaction.Commit();
            return item;
        });
    }

    public ChatConversation? GetConversation(string id)
    {
        id = ValidId(id);
        return Locked(() =>
        {
            using var command = Command(null, "SELECT id,endpoint,persona_key,model,title,created_utc,updated_utc,draft,has_draft FROM conversations WHERE id=$id;", ("$id", id));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var draft = Decode<DraftPayload>(reader.GetFieldValue<byte[]>(7));
            ValidateImages(draft.Images); ValidText(draft.Text, 12000, true, "draft");
            return new ChatConversation(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Decode<string>(reader.GetFieldValue<byte[]>(4)),
                Date(reader.GetString(5)), Date(reader.GetString(6)), draft.Text, draft.Images) { HasDraft = reader.GetBoolean(8) };
        });
    }

    public void SaveDraft(string id, string text, IReadOnlyList<ChatStoredImage> images)
    {
        id = ValidId(id); text = ValidText(text, 12000, true, nameof(text)); ValidateImages(images);
        Locked(() =>
        {
            byte[] payload = Encode(new DraftPayload(text, images));
            int changed = Execute(null, "UPDATE conversations SET draft=$draft,has_draft=$has,updated_utc=$now WHERE id=$id;", ("$draft", payload), ("$has", text.Length != 0 || images.Count != 0), ("$now", Stamp(DateTimeOffset.UtcNow)), ("$id", id));
            RequireChanged(changed); return true;
        });
    }

    public void RenameConversation(string id, string title)
    {
        id = ValidId(id); title = ValidText(title?.Trim(), 80, false, nameof(title));
        Locked(() =>
        {
            RequireChanged(Execute(null, "UPDATE conversations SET title=$title,title_auto=0,updated_utc=$now WHERE id=$id;", ("$title", Encode(title)), ("$now", Stamp(DateTimeOffset.UtcNow)), ("$id", id)));
            return true;
        });
    }

    public void DeleteConversation(string id)
    {
        id = ValidId(id);
        Locked(() => { Execute(null, "DELETE FROM conversations WHERE id=$id;", ("$id", id)); return true; });
    }

    public long AppendTurn(string id, ChatStoredTurn turn)
    {
        id = ValidId(id); ArgumentNullException.ThrowIfNull(turn);
        ValidateTurn(turn);
        return Locked(() =>
        {
            using var transaction = connection.BeginTransaction();
            if (Scalar(transaction, "SELECT id FROM conversations WHERE id=$id;", ("$id", id)) is null) throw new InvalidOperationException(L.T("对话已不存在，请重新选择对话。", "This conversation no longer exists. Select another conversation."));
            long sequence = Convert.ToInt64(Scalar(transaction, "SELECT COALESCE(MAX(sequence),0)+1 FROM turns WHERE conversation_id=$id;", ("$id", id)), CultureInfo.InvariantCulture);
            var now = DateTimeOffset.UtcNow;
            // Full page text was transient retrieval evidence, not permanent conversation memory.
            var sanitized = turn with { Sequence = sequence, Sources = turn.Sources.Select(source => source with { Text = "" }).ToArray(), CreatedUtc = turn.CreatedUtc == default ? now : turn.CreatedUtc.ToUniversalTime() };
            Execute(transaction, "INSERT INTO turns(conversation_id,sequence,payload) VALUES($id,$sequence,$payload);", ("$id", id), ("$sequence", sequence), ("$payload", Encode(sanitized)));
            Execute(transaction, "UPDATE conversations SET model=$model,updated_utc=$now,draft=$draft,has_draft=0,title=CASE WHEN title_auto=1 AND $sequence=1 THEN $title ELSE title END WHERE id=$id;",
                ("$model", turn.Model), ("$now", Stamp(now)), ("$draft", Encode(new DraftPayload("", Array.Empty<ChatStoredImage>()))), ("$sequence", sequence), ("$title", Encode(DeriveTitle(turn.User))), ("$id", id));
            transaction.Commit();
            return sequence;
        });
    }

    public IReadOnlyList<ChatStoredTurn> ReadTurns(string id, int limit = 50, long? beforeSequence = null)
    {
        id = ValidId(id);
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), L.T("每页须为 1–100 轮。", "Each page must contain 1–100 turns."));
        if (beforeSequence is <= 0) throw new ArgumentOutOfRangeException(nameof(beforeSequence));
        return Locked(() =>
        {
            using var command = Command(null, "SELECT sequence,payload FROM turns WHERE conversation_id=$id AND ($before IS NULL OR sequence<$before) ORDER BY sequence DESC LIMIT $limit;",
                ("$id", id), ("$before", beforeSequence), ("$limit", limit));
            using var reader = command.ExecuteReader();
            var result = new List<ChatStoredTurn>();
            while (reader.Read())
            {
                var turn = Decode<ChatStoredTurn>(reader.GetFieldValue<byte[]>(1));
                ValidateTurn(turn);
                result.Add(turn with { Sequence = reader.GetInt64(0) });
            }
            result.Reverse(); return (IReadOnlyList<ChatStoredTurn>)result;
        });
    }

    public string? GetActiveConversationId(string endpoint, string personaKey)
    {
        endpoint = ScopeEndpoint(endpoint); personaKey = ValidText(personaKey, 256, false, nameof(personaKey));
        return Locked(() => Scalar(null, "SELECT conversation_id FROM active_conversations WHERE endpoint=$endpoint AND persona_key=$persona;", ("$endpoint", endpoint), ("$persona", personaKey)) as string);
    }

    public void SetActiveConversation(string id)
    {
        id = ValidId(id);
        Locked(() =>
        {
            using var transaction = connection.BeginTransaction();
            string endpoint, persona;
            using (var command = Command(transaction, "SELECT endpoint,persona_key FROM conversations WHERE id=$id;", ("$id", id)))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new InvalidOperationException(L.T("对话已不存在，请重新选择对话。", "This conversation no longer exists. Select another conversation."));
                endpoint = reader.GetString(0); persona = reader.GetString(1);
            }
            SetActive(transaction, id, endpoint, persona); transaction.Commit(); return true;
        });
    }

    private void SetActive(SqliteTransaction transaction, string id, string endpoint, string persona) => Execute(transaction,
        "INSERT INTO active_conversations(endpoint,persona_key,conversation_id) VALUES($endpoint,$persona,$id) ON CONFLICT(endpoint,persona_key) DO UPDATE SET conversation_id=excluded.conversation_id;",
        ("$endpoint", endpoint), ("$persona", persona), ("$id", id));

    public IReadOnlyList<ChatSharedMemory> ListMemories(string endpoint)
    {
        endpoint = ScopeEndpoint(endpoint);
        return Locked(() =>
        {
            using var command = Command(null, "SELECT id,endpoint,payload,pinned,updated_utc FROM shared_memories WHERE endpoint=$endpoint ORDER BY pinned DESC,updated_utc DESC,id LIMIT $limit;", ("$endpoint", endpoint), ("$limit", MaximumMemories));
            using var reader = command.ExecuteReader();
            var result = new List<ChatSharedMemory>();
            while (reader.Read())
            {
                var payload = Decode<MemoryPayload>(reader.GetFieldValue<byte[]>(2));
                result.Add(new ChatSharedMemory(reader.GetString(0), reader.GetString(1), payload.Title, payload.Content, reader.GetBoolean(3), Date(reader.GetString(4))));
            }
            return (IReadOnlyList<ChatSharedMemory>)result;
        });
    }

    public ChatSharedMemory UpsertMemory(ChatSharedMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        string id = string.IsNullOrEmpty(memory.Id) ? Guid.NewGuid().ToString("N") : ValidId(memory.Id);
        string endpoint = ScopeEndpoint(memory.Endpoint);
        string title = ValidText(memory.Title?.Trim(), MaximumMemoryTitleCharacters, false, nameof(memory.Title));
        string content = ValidText(memory.Content?.Trim(), MaximumMemoryContentCharacters, false, nameof(memory.Content));
        return Locked(() =>
        {
            using var transaction = connection.BeginTransaction();
            string? existingEndpoint = Scalar(transaction, "SELECT endpoint FROM shared_memories WHERE id=$id;", ("$id", id)) as string;
            if (existingEndpoint != null && existingEndpoint != endpoint) throw new InvalidOperationException(L.T("记忆不能移动到另一台服务器；请在目标服务器下另建记忆。", "Memories cannot be moved to another server. Create a new memory on the destination server."));
            if (existingEndpoint == null && Convert.ToInt64(Scalar(transaction, "SELECT COUNT(*) FROM shared_memories WHERE endpoint=$endpoint;", ("$endpoint", endpoint)), CultureInfo.InvariantCulture) >= MaximumMemories)
                throw new InvalidOperationException(L.T("此服务器的记忆已达到 2000 条，请先整理或删除旧记忆。", "This server has reached 2,000 memories. Organize or delete old memories first."));
            var now = DateTimeOffset.UtcNow;
            Execute(transaction, "INSERT INTO shared_memories(id,endpoint,payload,pinned,updated_utc) VALUES($id,$endpoint,$payload,$pinned,$now) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload,pinned=excluded.pinned,updated_utc=excluded.updated_utc;",
                ("$id", id), ("$endpoint", endpoint), ("$payload", Encode(new MemoryPayload(title, content))), ("$pinned", memory.Pinned), ("$now", Stamp(now)));
            transaction.Commit();
            return new ChatSharedMemory(id, endpoint, title, content, memory.Pinned, now);
        });
    }

    public void DeleteMemory(string id)
    {
        id = ValidId(id);
        Locked(() => { Execute(null, "DELETE FROM shared_memories WHERE id=$id;", ("$id", id)); return true; });
    }

    private T Locked<T>(Func<T> action)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try { return action(); }
            catch (Exception ex) when (IsStorageException(ex)) { throw new ChatMemoryStoreException(ex); }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            connection.Dispose();
        }
    }

    private SqliteCommand Command(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        return command;
    }
    private int Execute(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] values) { using var command = Command(transaction, sql, values); return command.ExecuteNonQuery(); }
    private object? Scalar(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] values) { using var command = Command(transaction, sql, values); return command.ExecuteScalar(); }
    private static string ScopeEndpoint(string endpoint) => OllamaEndpoint.Parse(endpoint).AbsoluteUri.TrimEnd('/');
    private static string Stamp(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Date(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);
    private static void RequireChanged(int rows) { if (rows != 1) throw new InvalidOperationException(L.T("对话已不存在，请重新选择对话。", "This conversation no longer exists. Select another conversation.")); }
    private static string ValidId(string value) => Guid.TryParseExact(value, "N", out var id) ? id.ToString("N") : throw new ArgumentException(L.T("无效的对话或记忆标识。", "Invalid conversation or memory ID."), nameof(value));
    private static string ValidModel(string value) => OllamaModels.IsSupported(value) ? value : throw new ArgumentException(L.T("无效的模型名称。", "Invalid model name."), nameof(value));
    private static string ValidText(string? value, int maximum, bool allowEmpty, string parameter)
    {
        if (value == null || value.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Contains('\0')) throw new ArgumentException(L.T("内容为空、过长或含无效字符。", "Content is empty, too long, or contains invalid characters."), parameter);
        return value;
    }
    private static string DeriveTitle(string user)
    {
        string title = string.Join(" ", user.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Length == 0) title = L.T("图片对话", "Image conversation");
        if (title.Length > 80) title = title[..79] + "…";
        return title;
    }
    private static void ValidateImages(IReadOnlyList<ChatStoredImage> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count > 2) throw new ArgumentException(L.T("每轮最多保存两张图片。", "Each turn can store at most two images."), nameof(images));
        foreach (var image in images)
        {
            ArgumentNullException.ThrowIfNull(image);
            ValidText(image.Name, 256, false, nameof(image.Name));
            ValidText(image.Base64, MaximumImageCharacters, false, nameof(image.Base64));
            // Validation without allocating another decoded copy of the image.
            if (image.Base64.Length % 4 != 0 || image.Base64.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=')))
                throw new ArgumentException(L.T("图片编码无效。", "Invalid image encoding."), nameof(images));
            int padding = image.Base64.EndsWith("==", StringComparison.Ordinal) ? 2 : image.Base64.EndsWith('=') ? 1 : 0;
            if (image.Base64[..(image.Base64.Length - padding)].Contains('=')) throw new ArgumentException(L.T("图片编码无效。", "Invalid image encoding."), nameof(images));
        }
    }
    private static void ValidateTurn(ChatStoredTurn turn)
    {
        ValidText(turn.User, 12000, true, nameof(turn.User)); ValidText(turn.Assistant, 512 * 1024, false, nameof(turn.Assistant)); ValidModel(turn.Model);
        ValidateImages(turn.Images);
        if (string.IsNullOrWhiteSpace(turn.User) && turn.Images.Count == 0) throw new ArgumentException(L.T("对话问题不能为空。", "A conversation question cannot be empty."), nameof(turn));
        ArgumentNullException.ThrowIfNull(turn.Sources);
        if (turn.Sources.Count > 20) throw new ArgumentException(L.T("引用来源数量过多。", "Too many sources."), nameof(turn));
        foreach (var source in turn.Sources)
        {
            ArgumentNullException.ThrowIfNull(source); ValidText(source.Title, 2000, true, nameof(source.Title)); ValidText(source.Url, 8000, false, nameof(source.Url));
            if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException(L.T("引用来源必须为网页链接。", "Sources must use webpage URLs."), nameof(turn));
        }
        if (turn.WebWarning != null) ValidText(turn.WebWarning, 8000, true, nameof(turn.WebWarning));
    }
    private static bool IsStorageException(Exception ex) => ex is SqliteException or IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException or FormatException;
    private static byte[] Encode<T>(T value)
    {
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        try
        {
            if (clear.Length > MaximumPayloadBytes) throw new ArgumentException(L.T("对话附件过大，无法保存。", "The conversation attachment is too large to save."));
            return ChatDataProtection.Transform(clear, true, MaximumPayloadBytes + 4096);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    private static T Decode<T>(byte[] encrypted)
    {
        if (encrypted.Length > MaximumPayloadBytes + 4096) throw new InvalidDataException("Protected payload exceeds limit.");
        byte[] clear = ChatDataProtection.Transform(encrypted, false, MaximumPayloadBytes + 4096);
        try { return JsonSerializer.Deserialize<T>(clear, JsonOptions) ?? throw new InvalidDataException("Missing protected payload."); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    private sealed record DraftPayload(string Text, IReadOnlyList<ChatStoredImage> Images);
    private sealed record MemoryPayload(string Title, string Content);
}

internal static class ChatDataProtection
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DesktopPet/ConversationDatabase/v1");
    internal static byte[] Transform(byte[] input, bool protect, int maximumBytes)
    {
        if (input.Length is <= 0 || input.Length > maximumBytes) throw new InvalidDataException("Invalid protected payload length.");
        var inputPin = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyPin = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var inputBlob = new DataBlob { Length = input.Length, Data = inputPin.AddrOfPinnedObject() };
        var entropyBlob = new DataBlob { Length = Entropy.Length, Data = entropyPin.AddrOfPinnedObject() };
        DataBlob output = default;
        try
        {
            bool success = protect ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            if (output.Length <= 0 || output.Length > maximumBytes) throw new InvalidDataException("Invalid protected payload length.");
            byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                // Zero native plaintext without an unsafe block or a second full-size allocation.
                var zeros = new byte[Math.Min(4096, Math.Max(1, output.Length))];
                for (int offset = 0; offset < output.Length; offset += zeros.Length) Marshal.Copy(zeros, 0, IntPtr.Add(output.Data, offset), Math.Min(zeros.Length, output.Length - offset));
                _ = LocalFree(output.Data);
            }
            entropyPin.Free(); inputPin.Free();
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
