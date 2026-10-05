ALTER TABLE "AccountMatch" ADD COLUMN IF NOT EXISTS "CoinReward" integer NOT NULL DEFAULT 0;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='"AccountMatch"'::regclass AND conname='AccountMatch_CoinRewardBounds') THEN
        ALTER TABLE "AccountMatch" ADD CONSTRAINT "AccountMatch_CoinRewardBounds" CHECK ("CoinReward" >= 0);
    END IF;
END $$;
