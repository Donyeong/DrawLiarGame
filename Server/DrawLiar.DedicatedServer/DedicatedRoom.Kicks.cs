using DrawLiar.Server;

namespace DrawLiar.DedicatedServer;

internal sealed partial class DedicatedRoom
{
    public async Task KickAsync(GameConnection connection, GameplayEnvelope envelope, double now,
        Func<KickRoomRequest, CancellationToken, Task<KickRoomResponse>> persist)
    {
        string requestId = envelope.RequestId ?? "";
        GameConnection target;
        KickRoomRequest request;
        lock (_gate)
        {
            if (!_connections.TryGetValue(connection.AccountId, out var current) || current != connection || !connection.IsAlive) return;
            if (!connection.AcceptRate(envelope.Type ?? "", now))
            {
                if (connection.FrameRateExceeded) connection.Abort();
                else KickReplyLocked(connection, requestId, false, "RoomKickPending");
                return;
            }
            if (envelope.Type != "request" || envelope.Kind != "kick" || _ownerAccountId != connection.AccountId
                || envelope.Target == connection.PlayerId || !string.IsNullOrEmpty(envelope.RoomId) && envelope.RoomId != RoomId)
            {
                KickReplyLocked(connection, requestId, false, "RoomKickDenied");
                return;
            }
            if (requestId.Length > 80 || !Guid.TryParse(requestId, out var operation) || operation == Guid.Empty)
            {
                KickReplyLocked(connection, requestId.Length <= 80 ? requestId : "", false, "InvalidOperation");
                return;
            }
            string operationKey = operation.ToString();
            if (_kickReceipts.TryGetValue(operationKey, out var receipt))
            {
                KickReplyLocked(connection, requestId, receipt.Owner == connection.AccountId && receipt.Target == envelope.Target,
                    "InvalidOperation");
                return;
            }
            if (_kickPending || _configurationPending || _configurationSyncRequired)
            {
                KickReplyLocked(connection, requestId, false, "RoomKickPending");
                return;
            }
            target = _connections.Values.FirstOrDefault(peer => peer.PlayerId == envelope.Target && peer.IsAlive)!;
            if (target == null || !_session.Contains(target.PlayerId) || !target.BeginKick())
            {
                KickReplyLocked(connection, requestId, false, "RoomKickTargetUnavailable");
                return;
            }
            request = new KickRoomRequest
            {
                RoomId = RoomId, OwnerAccountId = connection.AccountId, OwnerSessionToken = connection.SessionToken,
                TargetAccountId = target.AccountId, TargetSessionToken = target.SessionToken, OperationId = operationKey
            };
            _kickPending = true;
            _pendingKickAccountId = target.AccountId;
        }
        bool accepted = false;
        string code = "";
        try
        {
            var result = await persist(request, connection.CancellationToken);
            lock (_gate)
            {
                if (result.RoomId != RoomId || result.AccountId != target.AccountId || result.OperationId != request.OperationId)
                    throw new InvalidDataException("강퇴 응답이 요청과 다릅니다.");
                if (_ownerAccountId != connection.AccountId || _pendingKickAccountId != target.AccountId || !_kickPending)
                    throw new InvalidDataException("강퇴 권한 상태가 변경되었습니다.");
                bool actorCurrent = _connections.TryGetValue(connection.AccountId, out var actor) && actor == connection && connection.IsAlive;
                bool targetCurrent = !_connections.TryGetValue(target.AccountId, out var currentTarget) || currentTarget == target;
                ApplyKickLocked(target.AccountId, Math.Max(now, RoomRegistry.Now));
                accepted = actorCurrent && targetCurrent;
                if (!actorCurrent) code = "RoomKickDenied";
                else if (!targetCurrent) code = "RoomKickTargetUnavailable";
                if (_kickReceipts.Count >= 256) _kickReceipts.Remove(_kickReceipts.Keys.First());
                _kickReceipts[request.OperationId] = (connection.AccountId, target.PlayerId);
            }
        }
        catch (ApiException exception) { code = exception.Code; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or IOException)
        {
            code = "ServerUnavailable";
            _statusChanged();
        }
        finally
        {
            request.OwnerSessionToken = request.TargetSessionToken = "";
            lock (_gate)
            {
                _kickPending = false;
                _pendingKickAccountId = "";
                target.EndKick();
                EnsureHostLocked();
                KickReplyLocked(connection, requestId, accepted, code);
                BroadcastSnapshotsLocked(RoomRegistry.Now);
                _statusChanged();
            }
        }
    }

    public void ApplyKicks(RoomKickData data)
    {
        lock (_gate)
        {
            if (data.RoomId != RoomId || data.AccountIds == null) return;
            foreach (string account in data.AccountIds) ApplyKickLocked(account, RoomRegistry.Now);
            EnsureHostLocked();
            BroadcastSnapshotsLocked(RoomRegistry.Now);
        }
    }

    private void ApplyKickLocked(string account, double now)
    {
        _kickedAccountIds.Add(account);
        _reconnectUntil.Remove(account);
        if (_connections.Remove(account, out var target))
        {
            _session.Disconnect(target.PlayerId, now, false);
            target.CloseAfterQueue(new GameplayEnvelope
            {
                Type = "kicked", Code = "RoomKicked", Text = "방장에 의해 강퇴되었습니다.", Sequence = ++_sequence
            });
        }
        else if (_playerIds.TryGetValue(account, out int id))
        {
            if (_session.Contains(id)) _session.Disconnect(id, now, false);
            _session.ReleaseSeat(id);
        }
        RemoveUnusedPlayerIdsLocked();
        if (_connections.Count == 0)
        {
            _closeAfter = _reconnectUntil.Values.DefaultIfEmpty(now).Max();
            _closeRequested = now >= _closeAfter;
        }
        _dirty = true;
        _statusChanged();
    }

    private void KickReplyLocked(GameConnection connection, string requestId, bool accepted, string code) => connection.Queue(new GameplayEnvelope
    {
        Type = "kickResult", RequestId = requestId, Accepted = accepted, Code = accepted ? "" : code,
        Text = accepted ? "" : code switch
        {
            "RoomKickDenied" => "강퇴 권한이 없습니다.",
            "RoomKickTargetUnavailable" => "강퇴할 참가자가 방에 없습니다.",
            "RoomKickPending" => "강퇴 요청을 처리하고 있습니다. 잠시 기다려 주세요.",
            _ => "강퇴하지 못했습니다. 다시 시도하세요."
        },
        Sequence = ++_sequence
    });
}
