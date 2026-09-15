using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Companion;

public sealed record CompanionTimelineItem(string MessageId, string Role, string Status, string RevisionId, string Text, string Kind, string Author, string Reason);
public sealed record CompanionSpan(string SourceId, long StartSample, long EndSample);

/// <summary>Multi-source conversation text has its own revisions; legacy one-source revisions remain unchanged.</summary>
public sealed class CompanionArchive(ArchiveRepository repository)
{
    public ArchiveRepository Repository => repository;
    public void RegisterSession(Session session, string model) => Execute("INSERT INTO companion_sessions(session_id,model) VALUES($id,$model)", ("$id", session.SessionId), ("$model", model));
    public void SetThread(string sessionId, CompanionThread thread) => Execute("UPDATE companion_sessions SET backend_thread_id=$thread,model=$model WHERE session_id=$id", ("$thread", thread.Id), ("$model", thread.Model), ("$id", sessionId));
    public void EnsureCloudAllowed(string sessionId)
    {
        var session = repository.GetSession(sessionId) ?? throw new InvalidOperationException("Session missing.");
        if (session.EndedAt is not null || session.PrivacyMode != PrivacyMode.Normal || !repository.HasGrantedConsent(sessionId, ConsentScope.CloudConversation)) throw new InvalidOperationException("對話已停止或未同意雲端對話。");
        using var c = repository.Archive.OpenConnection();
        using var q = c.CreateCommand(); q.CommandText = "SELECT blocked FROM companion_sessions WHERE session_id=$id"; q.Parameters.AddWithValue("$id", sessionId);
        if (Convert.ToInt32(q.ExecuteScalar() ?? 1) != 0) throw new InvalidOperationException("原聲已撤回或刪除，請開始新對話。");
    }
    public void Block(string sessionId) => Execute("UPDATE companion_sessions SET blocked=1 WHERE session_id=$id", ("$id", sessionId));

    public (string MessageId, string RevisionId, Turn Turn) AddMessage(string sessionId, string role, string text, string kind, string status = "saved")
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Session, role, text and kind are required.");
        if (role is not ("participant" or "assistant" or "system")) throw new ArgumentException("Unsupported companion message role.", nameof(role));
        var now = DateTimeOffset.UtcNow;
        var turnId = Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid().ToString("N");
        var revisionId = Guid.NewGuid().ToString("N");
        var turn = default(Turn)!;
        using var connection = repository.Archive.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var sessionCheck = connection.CreateCommand())
        {
            sessionCheck.Transaction = transaction;
            sessionCheck.CommandText = "SELECT COUNT(*) FROM sessions WHERE session_id=$id";
            sessionCheck.Parameters.AddWithValue("$id", sessionId);
            if (Convert.ToInt32(sessionCheck.ExecuteScalar()) != 1) throw new InvalidOperationException("Session was not found.");
        }
        int sequence;
        using (var next = connection.CreateCommand())
        {
            next.Transaction = transaction;
            next.CommandText = "SELECT COALESCE(MAX(sequence_number) + 1, 0) FROM turns WHERE session_id=$session";
            next.Parameters.AddWithValue("$session", sessionId);
            sequence = Convert.ToInt32(next.ExecuteScalar());
        }
        turn = new Turn(turnId, sessionId, sequence, role, now, now, now);
        using (var turnInsert = connection.CreateCommand())
        {
            turnInsert.Transaction = transaction;
            turnInsert.CommandText = "INSERT INTO turns(turn_id,session_id,sequence_number,speaker_type,started_at,ended_at,created_at) VALUES($id,$session,$sequence,$speaker,$started,$ended,$created)";
            turnInsert.Parameters.AddWithValue("$id", turn.TurnId);
            turnInsert.Parameters.AddWithValue("$session", turn.SessionId);
            turnInsert.Parameters.AddWithValue("$sequence", turn.SequenceNumber);
            turnInsert.Parameters.AddWithValue("$speaker", turn.SpeakerType);
            turnInsert.Parameters.AddWithValue("$started", now.ToString("O"));
            turnInsert.Parameters.AddWithValue("$ended", now.ToString("O"));
            turnInsert.Parameters.AddWithValue("$created", now.ToString("O"));
            turnInsert.ExecuteNonQuery();
        }
        using (var messageInsert = connection.CreateCommand())
        {
            messageInsert.Transaction = transaction;
            messageInsert.CommandText = "INSERT INTO companion_messages(message_id,session_id,turn_id,role,status,created_at) VALUES($id,$session,$turn,$role,$status,$time)";
            messageInsert.Parameters.AddWithValue("$id", id);
            messageInsert.Parameters.AddWithValue("$session", sessionId);
            messageInsert.Parameters.AddWithValue("$turn", turnId);
            messageInsert.Parameters.AddWithValue("$role", role);
            messageInsert.Parameters.AddWithValue("$status", status);
            messageInsert.Parameters.AddWithValue("$time", now.ToString("O"));
            messageInsert.ExecuteNonQuery();
        }
        var author = role == "assistant" ? "model" : role == "system" ? "app" : kind == "typed" ? "participant" : "recognizer";
        using (var revisionInsert = connection.CreateCommand())
        {
            revisionInsert.Transaction = transaction;
            revisionInsert.CommandText = "INSERT INTO companion_text_versions(revision_id,message_id,parent_revision_id,text,kind,author,reason,created_at) VALUES($id,$message,NULL,$text,$kind,$author,'original',$time)";
            revisionInsert.Parameters.AddWithValue("$id", revisionId);
            revisionInsert.Parameters.AddWithValue("$message", id);
            revisionInsert.Parameters.AddWithValue("$text", text);
            revisionInsert.Parameters.AddWithValue("$kind", kind);
            revisionInsert.Parameters.AddWithValue("$author", author);
            revisionInsert.Parameters.AddWithValue("$time", now.ToString("O"));
            revisionInsert.ExecuteNonQuery();
        }
        transaction.Commit();
        return (id, revisionId, turn);
    }

    public string AddRevision(string messageId, string text, string kind, string author, string reason, string? parent)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Text, author and reason are required.");
        using var c = repository.Archive.OpenConnection();
        if (parent is not null)
        {
            using var check = c.CreateCommand(); check.CommandText = "SELECT COUNT(*) FROM companion_text_versions WHERE revision_id=$id AND message_id=$message";
            check.Parameters.AddWithValue("$id", parent); check.Parameters.AddWithValue("$message", messageId);
            if (Convert.ToInt32(check.ExecuteScalar()) != 1) throw new InvalidDataException("Revision parent belongs to another message.");
        }
        var id = Guid.NewGuid().ToString("N");
        Execute("INSERT INTO companion_text_versions VALUES($id,$message,$parent,$text,$kind,$author,$reason,$time)", ("$id", id), ("$message", messageId), ("$parent", parent), ("$text", text), ("$kind", kind), ("$author", author), ("$reason", reason), ("$time", DateTimeOffset.UtcNow.ToString("O")));
        return id;
    }

    public void AddTranscriptSegments(string messageId, IReadOnlyList<SpeechSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        using var connection = repository.Archive.OpenConnection();
        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.StartMs < 0 || segment.EndMs <= segment.StartMs || string.IsNullOrWhiteSpace(segment.Text))
                throw new InvalidDataException("Speech segment timestamps and text must be valid.");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO companion_transcript_segments(segment_id,message_id,sequence_number,start_ms,end_ms,text,created_at) VALUES($id,$message,$sequence,$start,$end,$text,$created)";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$message", messageId);
            command.Parameters.AddWithValue("$sequence", i);
            command.Parameters.AddWithValue("$start", segment.StartMs);
            command.Parameters.AddWithValue("$end", segment.EndMs);
            command.Parameters.AddWithValue("$text", segment.Text);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<SpeechSegment> TranscriptSegments(string messageId)
    {
        using var connection = repository.Archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT start_ms,end_ms,text FROM companion_transcript_segments WHERE message_id=$message ORDER BY sequence_number";
        command.Parameters.AddWithValue("$message", messageId);
        using var reader = command.ExecuteReader();
        var result = new List<SpeechSegment>();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
        return result;
    }

    public void MarkMessage(string id, string status, CompanionReply? reply = null, string? inputRevision = null)
        => Execute("UPDATE companion_messages SET status=$status,backend_turn_id=COALESCE($turn,backend_turn_id),request_id=COALESCE($request,request_id),input_revision_id=COALESCE($input,input_revision_id) WHERE message_id=$id", ("$status", status), ("$turn", reply?.TurnId), ("$request", reply?.RequestId), ("$input", inputRevision), ("$id", id));

    public void AddChunk(SourceMetadata source, long start, long count)
        => Execute("INSERT INTO companion_chunks VALUES($source,$session,$start,$count)", ("$source", source.SourceId), ("$session", source.SessionId), ("$start", start), ("$count", count));

    public void AddSpan(string messageId, CompanionSpan span)
    {
        using var c = repository.Archive.OpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM companion_chunks c JOIN companion_messages m ON m.session_id=c.session_id WHERE m.message_id=$message AND c.source_id=$source AND $start>=0 AND $end>$start AND $end<=c.sample_count";
        q.Parameters.AddWithValue("$message", messageId); q.Parameters.AddWithValue("$source", span.SourceId); q.Parameters.AddWithValue("$start", span.StartSample); q.Parameters.AddWithValue("$end", span.EndSample);
        if (Convert.ToInt32(q.ExecuteScalar()) != 1) throw new InvalidDataException("Audio span must belong to this session and fit the source.");
        Execute("INSERT INTO companion_spans VALUES($message,$source,$start,$end)", ("$message", messageId), ("$source", span.SourceId), ("$start", span.StartSample), ("$end", span.EndSample));
    }

    public string StartPlayback(string message, string revision, string asset, long start)
    {
        var id = Guid.NewGuid().ToString("N");
        Execute("INSERT INTO companion_playback VALUES($id,$message,$asset,$revision,$start,NULL,'playing')", ("$id", id), ("$message", message), ("$asset", asset), ("$revision", revision), ("$start", start));
        return id;
    }
    public void EndPlayback(string id, long end, string status) => Execute("UPDATE companion_playback SET end_sample=$end,status=$status WHERE playback_id=$id", ("$end", end), ("$status", status), ("$id", id));
    public IReadOnlyList<CompanionTimelineItem> Timeline(string sessionId)
    {
        using var c = repository.Archive.OpenConnection(); using var q = c.CreateCommand();
        q.CommandText = "SELECT m.message_id,m.role,m.status,r.revision_id,r.text,r.kind,r.author,r.reason FROM companion_messages m JOIN companion_text_versions r ON r.message_id=m.message_id JOIN turns t ON t.turn_id=m.turn_id WHERE m.session_id=$session ORDER BY t.sequence_number,r.created_at,r.rowid";
        q.Parameters.AddWithValue("$session", sessionId); using var reader = q.ExecuteReader(); var items = new List<CompanionTimelineItem>();
        while (reader.Read()) items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        return items;
    }
    public IReadOnlyList<CompanionSpan> Spans(string messageId)
    {
        using var c = repository.Archive.OpenConnection(); using var q = c.CreateCommand();
        q.CommandText = "SELECT source_id,start_sample,end_sample FROM companion_spans WHERE message_id=$id ORDER BY rowid"; q.Parameters.AddWithValue("$id", messageId);
        using var reader = q.ExecuteReader(); var result = new List<CompanionSpan>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2))); return result;
    }
    private void Execute(string sql, params (string Key, object? Value)[] values)
    {
        using var c = repository.Archive.OpenConnection(); using var q = c.CreateCommand(); q.CommandText = sql;
        foreach (var (key, value) in values) q.Parameters.AddWithValue(key, value ?? DBNull.Value); q.ExecuteNonQuery();
    }
}
