#nullable disable
using System;

namespace DrawLiar
{
    public static class AccountLevelRules
    {
        public const int MAX_LEVEL = 100;
        public const long MAX_LEVEL_EXPERIENCE = 252450L;

        public static int GetLevel(long experience)
        {
            if (experience <= 0) return 1;
            if (experience >= MAX_LEVEL_EXPERIENCE) return MAX_LEVEL;
            int low = 1, high = MAX_LEVEL;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (ExperienceForLevel(middle) <= experience) low = middle;
                else high = middle - 1;
            }
            return low;
        }

        public static long ExperienceForLevel(int level)
        {
            long previous = Math.Max(1, Math.Min(MAX_LEVEL, level)) - 1L;
            return 25L * previous * (previous + 3L);
        }

        public static int RequiredExperienceForNextLevel(int level)
        {
            level = Math.Max(1, Math.Min(MAX_LEVEL, level));
            return level == MAX_LEVEL ? 0 : 100 + (level - 1) * 50;
        }

        public static long ExperienceIntoLevel(long experience)
        {
            int level = GetLevel(experience);
            return level == MAX_LEVEL ? 0 : Math.Max(0, experience) - ExperienceForLevel(level);
        }

        public static int GetBadgeTier(int level)
        {
            if (level >= 100) return 5;
            if (level >= 75) return 4;
            if (level >= 50) return 3;
            if (level >= 25) return 2;
            if (level >= 10) return 1;
            return 0;
        }
    }
}
