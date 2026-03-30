// Quick DB query script — run via: dotnet run --project scripts/DbQuery
using System;
using System.IO;
using Microsoft.Data.Sqlite;

var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PodcastVideoEditor", "app.db");
Console.WriteLine($"DB: {dbPath}");

using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
conn.Open();

// 1. List all tracks
Console.WriteLine("\n=== TRACKS ===");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT Id, ProjectId, [Order], TrackType, Name, IsVisible FROM Tracks ORDER BY ProjectId, [Order]";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        Console.WriteLine($"  Track: Id={reader["Id"]}, ProjectId={reader["ProjectId"]}, Order={reader["Order"]}, TrackType='{reader["TrackType"]}', Name='{reader["Name"]}', IsVisible={reader["IsVisible"]}");
    }
}

// 2. List text segments
Console.WriteLine("\n=== TEXT SEGMENTS (Kind='text' OR on text tracks) ===");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = @"
        SELECT s.Id, s.TrackId, s.Text, s.Kind, s.StartTime, s.EndTime, t.TrackType, t.Name as TrackName
        FROM Segments s 
        LEFT JOIN Tracks t ON s.TrackId = t.Id
        WHERE s.Kind = 'text' OR t.TrackType = 'text'
        ORDER BY s.StartTime
        LIMIT 20";
    using var reader = cmd.ExecuteReader();
    int count = 0;
    while (reader.Read())
    {
        var text = reader["Text"]?.ToString() ?? "(null)";
        if (text.Length > 50) text = text[..50] + "...";
        Console.WriteLine($"  Seg: Id={reader["Id"]}, TrackId={reader["TrackId"]}, Kind='{reader["Kind"]}', TrackType='{reader["TrackType"]}', Text='{text}', Time={reader["StartTime"]}-{reader["EndTime"]}");
        count++;
    }
    Console.WriteLine($"  Total: {count} text segments");
}

// 3. Check for segments without TrackId
Console.WriteLine("\n=== ORPHANED SEGMENTS (no TrackId) ===");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(*) FROM Segments WHERE TrackId IS NULL OR TrackId = ''";
    Console.WriteLine($"  Orphaned segments: {cmd.ExecuteScalar()}");
}

// 4. Check all distinct Kind values
Console.WriteLine("\n=== DISTINCT Segment.Kind VALUES ===");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT Kind, COUNT(*) as cnt FROM Segments GROUP BY Kind";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        Console.WriteLine($"  Kind='{reader["Kind"]}' count={reader["cnt"]}");
}

// 5. Check all distinct TrackType values
Console.WriteLine("\n=== DISTINCT Track.TrackType VALUES ===");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT TrackType, COUNT(*) as cnt FROM Tracks GROUP BY TrackType";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        Console.WriteLine($"  TrackType='{reader["TrackType"]}' count={reader["cnt"]}");
}

// 6. Check Elements table for TextOverlay entries
Console.WriteLine("\n=== ELEMENTS TABLE (TextOverlay) ===");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(*) FROM Elements WHERE Type = 'TextOverlay'";
    try { Console.WriteLine($"  TextOverlay elements: {cmd.ExecuteScalar()}"); }
    catch (Exception ex) { Console.WriteLine($"  Error: {ex.Message}"); }
}

Console.WriteLine("\nDone.");
