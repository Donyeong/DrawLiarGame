ALTER TABLE "TopicWorkshop" ADD COLUMN "RecommendationCount" bigint NOT NULL DEFAULT 0 CHECK ("RecommendationCount" >= 0);

CREATE TABLE "TopicWorkshopRecommendation" (
    "TopicId" uuid NOT NULL REFERENCES "TopicWorkshop"("Id") ON DELETE CASCADE,
    "AccountId" uuid NOT NULL REFERENCES "Account"("Id") ON DELETE CASCADE,
    PRIMARY KEY ("TopicId", "AccountId")
);

CREATE FUNCTION "UpdateTopicWorkshopRecommendationCount"() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        UPDATE "TopicWorkshop" SET "RecommendationCount" = "RecommendationCount" + 1 WHERE "Id" = NEW."TopicId";
    ELSE
        UPDATE "TopicWorkshop" SET "RecommendationCount" = "RecommendationCount" - 1 WHERE "Id" = OLD."TopicId";
    END IF;
    RETURN NULL;
END;
$$;

CREATE TRIGGER "TopicWorkshopRecommendation_Count" AFTER INSERT OR DELETE ON "TopicWorkshopRecommendation"
FOR EACH ROW EXECUTE FUNCTION "UpdateTopicWorkshopRecommendationCount"();

CREATE INDEX "TopicWorkshop_Downloads" ON "TopicWorkshop" ("DownloadCount" DESC, "CreatedAt" DESC, "Id" DESC);
CREATE INDEX "TopicWorkshop_Popular" ON "TopicWorkshop" ("RecommendationCount" DESC, "DownloadCount" DESC, "CreatedAt" DESC, "Id" DESC);
