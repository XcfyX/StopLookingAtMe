using System.Text.Json;

namespace PrivacyScreen;

/// <summary>
/// Crash-resilient endpoint-mute journal. All persisted entries had GetMute()==false before locking;
/// already-muted endpoints are never modified. Journal is atomically published BEFORE SetMute(true).
/// </summary>
internal static class AudioMuteManager
{
    private sealed class AudioRestoreRecord
    {
        public int Version { get; set; } = 1;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public List<AudioRestoreEndpoint> Endpoints { get; set; } = new();
    }

    private sealed class AudioRestoreEndpoint
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool OriginalMute { get; set; }
    }

    private static readonly object Sync = new();
    internal static string RestoreFile => Path.Combine(AppConfig.Root, "audio-restore.json");
    internal static bool HasPending => File.Exists(RestoreFile);

    internal static void MuteForLock()
    {
        lock (Sync)
        {
            if (HasPending)
            {
                Log.Write("Audio: mute skipped: previous recovery journal still pending; do not overwrite it");
                return;
            }
            try
            {
                var selected = CoreAudio.FindSpeakers(out string reason);
                var entries = new List<AudioRestoreEndpoint>();
                foreach (var endpoint in selected)
                {
                    try
                    {
                        bool originallyMuted = CoreAudio.GetMute(endpoint.Id);
                        Log.Write($"Audio: candidate name={endpoint.Name}; adapter={endpoint.Adapter}; " +
                            $"formfactor={endpoint.FormFactor}; connector={endpoint.ConnectorType?.ToString() ?? "unknown"}; " +
                            $"topology={endpoint.Evidence}; buspath={endpoint.TopologyPath}; originalMute={originallyMuted}");
                        if (!originallyMuted)
                        {
                            entries.Add(new AudioRestoreEndpoint
                                { Id = endpoint.Id, Name = endpoint.Name, OriginalMute = originallyMuted });
                        }
                    }
                    catch (Exception e) { Log.Write($"Audio: query failed for {endpoint.Name}: {e.Message}"); }
                }
                // Record a single summary even when no speaker is changed.
                if (entries.Count == 0)
                {
                    Log.Write($"Audio: enabled=true, muted=0, selected={selected.Count}, " +
                        $"endpoints=[{string.Join("; ", selected.Select(e => e.Name + " originalMute=true or query failed"))}], " +
                        $"selection={reason}");
                    return;
                }

                // Write-ahead journal: if killed between this write and a SetMute, next launch
                // has enough information to restore every possibly changed endpoint.
                SaveJournal(new AudioRestoreRecord { Endpoints = entries });
                int muted = 0;
                foreach (var entry in entries)
                {
                    try
                    {
                        CoreAudio.SetMute(entry.Id, true);
                        if (!CoreAudio.GetMute(entry.Id))
                            throw new InvalidOperationException("SetMute returned but mute readback was false");
                        muted++;
                    }
                    catch (Exception e) { Log.Write($"Audio: SetMute failed for {entry.Name}: {e.Message}"); }
                }
                Log.Write($"Audio: enabled=true, muted={muted}, selected={selected.Count}, " +
                    $"endpoints=[{string.Join("; ", entries.Select(e => e.Name + " originalMute=" + e.OriginalMute))}], " +
                    $"selection={reason}, journal={(HasPending ? "pending" : "none")}");
            }
            catch (Exception e)
            {
                // Mute is an optional after-lock feature, never a reason to roll back shielding.
                Log.Write("Audio: enabled=true, muted=0, lock mute degraded: " + e);
            }
        }
    }

    /// <summary>Also called unconditionally at app startup, before normal UI and before configuration.</summary>
    internal static void RestorePending(string cause)
    {
        lock (Sync)
        {
            if (!HasPending)
            {
                Log.Write($"Audio: restore ({cause}) success=0, pending=0, journal=none");
                return;
            }
            AudioRestoreRecord record;
            try
            {
                record = JsonSerializer.Deserialize<AudioRestoreRecord>(File.ReadAllText(RestoreFile))
                    ?? throw new InvalidDataException("音频恢复记录为空");
                if (record.Version != 1 || record.Endpoints is null)
                    throw new InvalidDataException("不支持的音频恢复记录格式");
            }
            catch (Exception e)
            {
                Log.Write($"Audio: restore ({cause}) journal unreadable, kept for inspection: {e}");
                return;
            }

            var unresolved = new List<AudioRestoreEndpoint>();
            int restored = 0;
            foreach (var entry in record.Endpoints)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(entry.Id)) throw new InvalidDataException("missing endpoint ID");
                    if (!entry.OriginalMute)
                    {
                        // Never modify already-unmuted endpoints; never set a new mute here.
                        if (CoreAudio.GetMute(entry.Id)) CoreAudio.SetMute(entry.Id, false);
                        if (CoreAudio.GetMute(entry.Id)) throw new InvalidOperationException("mute readback still true");
                    }
                    // OriginalMute=true is handled defensively for future journal formats;
                    // v1 only journals endpoints whose original state was false.
                    restored++;
                }
                catch (Exception e)
                {
                    unresolved.Add(entry);
                    Log.Write($"Audio: restore failed ({cause}) name={entry.Name}, id={entry.Id}: {e.Message}");
                }
            }
            try
            {
                if (unresolved.Count == 0) File.Delete(RestoreFile);
                else SaveJournal(new AudioRestoreRecord
                    { CreatedAt = record.CreatedAt, Endpoints = unresolved });
            }
            catch (Exception e)
            {
                Log.Write($"Audio: restore ({cause}) journal finalization failed: {e}");
                // Keep journal on disk: next startup will retry, even if already restored.
            }
            Log.Write($"Audio: restore ({cause}) success={restored}, pending={unresolved.Count}, " +
                $"journal={(HasPending ? "retained" : "cleared")}");
        }
    }

    private static void SaveJournal(AudioRestoreRecord record)
    {
        Directory.CreateDirectory(AppConfig.Root);
        string temp = RestoreFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(record,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, RestoreFile, true); // atomic replacement on the same NTFS volume
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
}
