ALTER TABLE "Account" ADD COLUMN "Experience" bigint NOT NULL DEFAULT 0 CHECK ("Experience" >= 0);
ALTER TABLE "AccountMatch" ADD COLUMN "ExperienceReward" integer NOT NULL DEFAULT 0 CHECK ("ExperienceReward" >= 0);
