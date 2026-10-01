using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

public partial class LibraryViewModel
{
    [ObservableProperty]
    private bool _trackExternalGames = true;

    internal TimeProvider ExternalSessionClock { get; set; } = TimeProvider.System;
    private sealed class PassiveSession
    {
        public long LastTick;
        public TimeSpan Pending;
        public DateTime LastSeenUtc;
        public bool Qualified;
    }
    private readonly Dictionary<int, PassiveSession> _passiveSessions = new();
    private long _sessionActivityGeneration;

    partial void OnTrackExternalGamesChanged(bool value)
    {
        ++_sessionActivityGeneration;
        _settings.TrackExternalGames = value;
        if (!value) StopPassiveTracking();
        _settingsService.Save(_settings);
    }

    // UI-thread coordinator; only process inspection runs on the worker, against an immutable snapshot.
    internal async Task MonitorExternalGamesAsync(CancellationToken ct)
    {
        var detector = new ExternalGameDetector();
        var warned = false;
        List<GameEntry>? candidateLibrary = null;
        ExternalGameCandidate[] candidates = [];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (TrackExternalGames && _allGames.Count > 0)
                {
                    // Id/executable paths are immutable on GameEntry. Rebuild only on library publication,
                    // not every poll; filtering/favoriting doesn't change what processes can be detected.
                    if (!ReferenceEquals(candidateLibrary, _allGames))
                    {
                        candidateLibrary = _allGames;
                        candidates = _allGames.Select(g => new ExternalGameCandidate(g.Id, g.ExecutablePath)).ToArray();
                    }
                    var generation = _sessionActivityGeneration;
                    try
                    {
                        var matches = await Task.Run(() => detector.Detect(candidates, ct), ct);
                        ct.ThrowIfCancellationRequested();
                        if (TrackExternalGames && generation == _sessionActivityGeneration)
                            ApplyExternalObservations(matches);
                        warned = false;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        StopPassiveTracking();
                        if (!warned) Logger.Warn("External game detection failed; will retry.", ex);
                        warned = true;
                    }
                }
                else if (_passiveSessions.Count > 0)
                    StopPassiveTracking();
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { StopPassiveTracking(); }
    }

    internal void ApplyExternalObservations(IReadOnlyList<ExternalGameCandidate> matches)
    {
        if (!TrackExternalGames) return;
        var now = ExternalSessionClock.GetTimestamp();
        var utc = ExternalSessionClock.GetUtcNow().UtcDateTime;
        var present = new HashSet<string>();
        foreach (var match in matches)
        {
            var id = ResolveAlias(match.GameId);
            var live = _allGames.FirstOrDefault(g => g.Id == id);
            if (live is not null && ExternalGameDetector.NormalizePath(live.ExecutablePath) is { } path
                && string.Equals(path, ExternalGameDetector.NormalizePath(match.ExecutablePath), StringComparison.OrdinalIgnoreCase)
                && !_sessionGameIds.Any(s => s.Value == id && !_passiveSessions.ContainsKey(s.Key)))
                present.Add(id);
        }
        foreach (var sessionId in _passiveSessions.Keys.ToArray())
        {
            if (!_sessionGameIds.TryGetValue(sessionId, out var id) || !present.Remove(id))
            {
                EndPassiveSession(sessionId);
                continue;
            }
            var session = _passiveSessions[sessionId];
            var elapsed = ExternalSessionClock.GetElapsedTime(session.LastTick, now);
            // Don't count suspend/resume or a stalled monitor as hours of observed gameplay.
            if (elapsed >= TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(15)) session.Pending += elapsed;
            session.LastTick = now;
            session.LastSeenUtc = utc;
            if (session.Pending >= TimeSpan.FromMinutes(1)) FlushPassiveTime(sessionId, session);
        }
        foreach (var id in present)
        {
            var token = ++_sessionCounter;
            _sessionGameIds[token] = id;
            _passiveSessions[token] = new PassiveSession { LastTick = now, LastSeenUtc = utc };
            Logger.Info($"External game session detected: '{id}'. Tracking from now, not process start.");
        }
        ReapplyRunningBadge();
    }

    private void FlushPassiveTime(int token, PassiveSession session)
    {
        if (!_sessionGameIds.TryGetValue(token, out var id)) return;
        if (!session.Qualified && session.Pending < TimeSpan.FromSeconds(10)) return;
        var seconds = (long)session.Pending.TotalSeconds;
        if (seconds <= 0) return;
        session.Qualified = true;
        session.Pending -= TimeSpan.FromSeconds(seconds);
        AddTrackedPlayTime(id, seconds, session.LastSeenUtc);
    }

    private void EndPassiveSession(int token)
    {
        if (!_passiveSessions.Remove(token, out var session)) return;
        FlushPassiveTime(token, session);
        _sessionGameIds.Remove(token);
        ReapplyRunningBadge();
    }

    private void StopPassiveTrackingForGame(string id)
    {
        foreach (var token in _passiveSessions.Keys.Where(t => _sessionGameIds.GetValueOrDefault(t) == id).ToArray())
            EndPassiveSession(token);
    }

    internal void StopPassiveTracking()
    {
        foreach (var token in _passiveSessions.Keys.ToArray()) EndPassiveSession(token);
    }
}
