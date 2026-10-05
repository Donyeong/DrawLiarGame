CREATE TABLE "TopicWorkshop" (
    "Id" uuid PRIMARY KEY,
    "CreatorAccountId" uuid NOT NULL REFERENCES "Account" ("Id") ON DELETE CASCADE,
    "Name" text NOT NULL CHECK (length("Name") > 0),
    "LanguageCode" text NOT NULL CHECK (length("LanguageCode") > 0),
    "Words" jsonb NOT NULL CHECK (jsonb_typeof("Words") = 'array' AND jsonb_array_length("Words") > 0),
    "DownloadCount" bigint NOT NULL DEFAULT 0 CHECK ("DownloadCount" >= 0),
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    UNIQUE ("CreatorAccountId", "LanguageCode", "Name")
);

CREATE INDEX "TopicWorkshop_LanguageCreated" ON "TopicWorkshop" ("LanguageCode", "CreatedAt" DESC, "Id" DESC);
CREATE INDEX "TopicWorkshop_Created" ON "TopicWorkshop" ("CreatedAt" DESC, "Id" DESC);
