#nullable disable
using System;

namespace DrawLiar
{
    public static class MatchRewardRules
    {
        // 화가패스 구독자가 방장인 방의 코인·경험치 배율
        public const decimal PAINTER_HOST_MULTIPLIER = 1.5m;

        public static bool ValidatePolicy(MatchRewardPolicy policy) => policy != null
            && policy.BaseCoinsPerParticipantRound >= 0 && policy.CoinsPerParticipantPoint >= 0;

        public static int Calculate(MatchRewardPolicy policy, long weightedRoundParticipants, long weightedRoundScore, int currentCoins,
            bool painterHost = false)
        {
            if (!ValidatePolicy(policy)) throw new ArgumentException("경기 보상 정책이 올바르지 않습니다.", nameof(policy));
            if (weightedRoundParticipants < 0 || weightedRoundScore < 0)
                throw new ArgumentOutOfRangeException(nameof(weightedRoundParticipants));
            decimal reward = ((decimal)weightedRoundParticipants * policy.BaseCoinsPerParticipantRound
                + (decimal)weightedRoundScore * policy.CoinsPerParticipantPoint) * (painterHost ? PAINTER_HOST_MULTIPLIER : 1m);
            long available = Math.Min(int.MaxValue, (long)int.MaxValue - currentCoins);
            return (int)Math.Min(reward, available);
        }
    }
}
