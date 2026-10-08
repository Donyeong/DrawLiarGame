using System.Text.Json;
using DrawLiar.Server;

namespace DrawLiar.DedicatedServer;

internal sealed partial class DedicatedRoom
{
    public async Task RefreshProfileAsync(GameConnection connection, GameplayEnvelope envelope, double now,
        Func<SessionCheckRequest, CancellationToken, Task<ProfileData>> load)
    {
        string requestId = envelope.RequestId ?? "";
        lock (_gate)
        {
            if (!_connections.TryGetValue(connection.AccountId, out var current) || current != connection || !connection.IsAlive) return;
            if (!connection.AcceptRate(envelope.Type ?? "", now)) { if (connection.FrameRateExceeded) connection.Abort(); return; }
            if (envelope.Type != "request" || envelope.Kind != "refreshProfile" || !_session.HasParticipant(connection.PlayerId)
                || requestId.Length > 80 || requestId.Any(char.IsControl))
            {
                ProfileReplyLocked(connection, requestId.Length <= 80 ? requestId : "", false, "InvalidOperation");
                return;
            }
        }
        ProfileData profile;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            profile = await load(new SessionCheckRequest { AccountId = connection.AccountId, SessionToken = connection.SessionToken },
                timeout.Token);
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            lock (_gate)
                if (_connections.TryGetValue(connection.AccountId, out var current) && current == connection && connection.IsAlive)
                    ProfileReplyLocked(connection, requestId, false, exception is ApiException api ? api.Code : "ServerUnavailable");
            return;
        }
        lock (_gate)
        {
            if (!_connections.TryGetValue(connection.AccountId, out var current) || current != connection || !connection.IsAlive
                || !_session.HasParticipant(connection.PlayerId)) return;
            if (profile == null || profile.AccountId != connection.AccountId || string.IsNullOrEmpty(profile.DisplayName))
            {
                ProfileReplyLocked(connection, requestId, false, "InvalidProfile");
                return;
            }
            _session.UpdateProfile(connection.PlayerId, profile.DisplayName, profile.AvatarColor, profile.Accessory, profile.Level);
            BroadcastSnapshotsLocked(RoomRegistry.Now);
            ProfileReplyLocked(connection, requestId, true, "");
        }
    }

    private void ProfileReplyLocked(GameConnection connection, string requestId, bool accepted, string code) => connection.Queue(new GameplayEnvelope
    {
        Type = "profile-refreshed", RequestId = requestId, Accepted = accepted, Code = code,
        Text = accepted ? "" : code == "Unauthorized" ? "로그인이 만료되었습니다. 다시 로그인해 주세요." : "프로필을 불러오지 못했습니다.",
        Sequence = ++_sequence
    });
}
