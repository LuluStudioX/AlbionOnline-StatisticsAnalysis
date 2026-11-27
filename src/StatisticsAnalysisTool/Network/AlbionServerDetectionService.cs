using Serilog;
using StatisticsAnalysisTool.Enumerations;
using System;

namespace StatisticsAnalysisTool.Network;

public class AlbionServerDetectionService
{
    private readonly object _lock = new();
    private static readonly TimeSpan ServerDetectionStabilityDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ServerDetectionMaxPacketGap = TimeSpan.FromSeconds(2);
    private AlbionServerInfo _candidateServer = AlbionServerRegistry.Unknown;
    private DateTime _candidateFirstSeenUtc = DateTime.MinValue;
    private DateTime _candidateLastSeenUtc = DateTime.MinValue;

    public event EventHandler<AlbionServerChangedEventArgs> ServerChanged;

    public AlbionServerInfo CurrentServer { get; private set; } = AlbionServerRegistry.Unknown;

    public ServerLocation CurrentServerLocation => CurrentServer.ServerLocation;

    public DateTime LastServerPacketReceivedUtc { get; private set; } = DateTime.MinValue;

    public bool TryDetectFromSourceIp(string sourceIp)
    {
        var detectedServer = AlbionServerRegistry.GetBySourceIp(sourceIp);

        if (detectedServer.ServerLocation == ServerLocation.Unknown)
        {
            return false;
        }

        LastServerPacketReceivedUtc = DateTime.UtcNow;
        SetCurrentServer(detectedServer);
        return true;
    }

    private void SetCurrentServer(AlbionServerInfo detectedServer)
    {
        AlbionServerChangedEventArgs eventArgs = null;
        var resetToUnknown = false;
        var detectedServerName = detectedServer.Name;

        lock (_lock)
        {
            if (CurrentServer.ServerLocation == detectedServer.ServerLocation)
            {
                _candidateServer = AlbionServerRegistry.Unknown;
                _candidateFirstSeenUtc = DateTime.MinValue;
                return;
            }

            if (CurrentServer.ServerLocation != ServerLocation.Unknown)
            {
                var previousServer = CurrentServer;
                CurrentServer = AlbionServerRegistry.Unknown;
                _candidateServer = detectedServer;
                _candidateFirstSeenUtc = DateTime.UtcNow;
                _candidateLastSeenUtc = _candidateFirstSeenUtc;
                eventArgs = new AlbionServerChangedEventArgs(previousServer, CurrentServer);
                resetToUnknown = true;
            }
            else
            {
                eventArgs = TryPromoteStableCandidate(detectedServer);
            }
        }

        if (eventArgs is null)
        {
            return;
        }

        if (resetToUnknown)
        {
            Log.Information("Albion server detection reset to unknown after receiving {Server} packets", detectedServerName);
            ServerChanged?.Invoke(this, eventArgs);
            return;
        }

        Log.Information("Albion server detected: {Server}", detectedServer.Name);
        ServerChanged?.Invoke(this, eventArgs);
    }

    private AlbionServerChangedEventArgs TryPromoteStableCandidate(AlbionServerInfo detectedServer)
    {
        var now = DateTime.UtcNow;

        // The candidate must be seen without a long pause. Game traffic is many packets per second, but a
        // single old packet (a login screen ping, or one from before the PC went to sleep) must not start
        // a clock that the next packet, maybe hours later, then completes.
        if (_candidateServer.ServerLocation != detectedServer.ServerLocation
            || now - _candidateLastSeenUtc > ServerDetectionMaxPacketGap)
        {
            _candidateServer = detectedServer;
            _candidateFirstSeenUtc = now;
            _candidateLastSeenUtc = now;
            return null;
        }

        _candidateLastSeenUtc = now;

        if (now - _candidateFirstSeenUtc < ServerDetectionStabilityDuration)
        {
            return null;
        }

        var previousServer = CurrentServer;
        CurrentServer = detectedServer;
        _candidateServer = AlbionServerRegistry.Unknown;
        _candidateFirstSeenUtc = DateTime.MinValue;
        return new AlbionServerChangedEventArgs(previousServer, CurrentServer);
    }
}
