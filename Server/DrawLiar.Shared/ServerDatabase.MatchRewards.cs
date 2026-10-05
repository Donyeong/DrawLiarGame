using System.Data;
using System.Text.Json;
using Npgsql;

namespace DrawLiar.Server;

public sealed partial class ServerDatabase
{
    private static readonly Lazy<MatchRewardPolicy> _matchRewardPolicy = new(() =>
    {
        using var stream = typeof(ServerDatabase).Assembly.GetManifestResourceStream("DrawLiar.MatchRewardPolicy.json")
            ?? throw new InvalidDataException("경기 보상 정책이 없습니다.");
        var policy = JsonSerializer.Deserialize<MatchRewardPolicy>(stream, ServerRuntime.Json);
        if (!MatchRewardRules.ValidatePolicy(policy)) throw new InvalidDataException("경기 보상 정책이 올바르지 않습니다.");
        return policy!;
    });

    public static MatchRewardPolicy RewardPolicy => new()
    {
        BaseCoinsPerParticipantRound = _matchRewardPolicy.Value.BaseCoinsPerParticipantRound,
        CoinsPerParticipantPoint = _matchRewardPolicy.Value.CoinsPerParticipantPoint
    };

    public async Task<MatchRewardResponse> MatchRewardAsync(Guid accountId, string matchId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(matchId, out Guid parsedId) || parsedId == Guid.Empty) throw new ApiException("InvalidMatch");
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var response = new MatchRewardResponse { MatchId = parsedId.ToString() };
        await using (var command = Command(connection, transaction,
            "SELECT \"CoinReward\" FROM \"AccountMatch\" WHERE \"MatchId\"=$1 AND \"AccountId\"=$2", parsedId, accountId))
        {
            object? reward = await command.ExecuteScalarAsync(cancellationToken);
            response.Recorded = reward != null;
            response.CoinReward = reward is int coins ? coins : 0;
        }
        response.Profile = await ReadProfile(connection, transaction, accountId);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    private static async Task<int> CreditMatchRewardAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid accountId,
        MatchPlayerResult player, CancellationToken cancellationToken)
    {
        int currentCoins;
        await using (var command = Command(connection, transaction, "SELECT \"Coins\" FROM \"Account\" WHERE \"Id\"=$1 FOR UPDATE", accountId))
            currentCoins = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        int reward = MatchRewardRules.Calculate(_matchRewardPolicy.Value, player.WeightedRoundParticipants, player.WeightedRoundScore, currentCoins);
        if (reward > 0)
        {
            await using var command = Command(connection, transaction, "UPDATE \"Account\" SET \"Coins\"=\"Coins\"+$2 WHERE \"Id\"=$1", accountId, reward);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return reward;
    }

    private static string MatchPayloadHash(MatchResultRequest normalized)
    {
        object payload = normalized.Players.All(player => player.WeightedRoundParticipants == 0 && player.WeightedRoundScore == 0)
            ? new
            {
                normalized.NodeId, normalized.RoomId, normalized.MatchId, normalized.PlayedAt, normalized.Mode, normalized.RoundCount,
                Players = normalized.Players.Select(player => new
                {
                    player.AccountId, player.Score, player.Rank, player.Won, player.RoundsPlayed, player.CitizenRounds,
                    player.LiarRounds, player.CorrectVotes, player.CorrectGuesses
                }).ToArray()
            } : normalized;
        return ServerRuntime.Hash(JsonSerializer.Serialize(payload, ServerRuntime.Json));
    }
}
