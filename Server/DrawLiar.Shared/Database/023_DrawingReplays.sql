ALTER TABLE "MatchRecord" ADD COLUMN "DrawingPayloadHash" text;
ALTER TABLE "MatchRecord" ADD COLUMN "DrawingExpiresAt" timestamptz;
ALTER TABLE "MatchRecord" ADD COLUMN "DrawingRetentionDays" integer;
ALTER TABLE "MatchRecord" ADD COLUMN "DrawingsIncomplete" boolean NOT NULL DEFAULT false;
ALTER TABLE "MatchRecord" ADD CONSTRAINT "MatchRecord_DrawingExpiry" CHECK (
    ("DrawingPayloadHash" IS NULL AND "DrawingExpiresAt" IS NULL AND "DrawingRetentionDays" IS NULL)
    OR ("DrawingPayloadHash" IS NOT NULL AND "DrawingExpiresAt" IS NOT NULL AND "DrawingRetentionDays" IS NOT NULL
        AND "DrawingExpiresAt">"PlayedAt" AND "DrawingRetentionDays" BETWEEN 1 AND 365)
);
CREATE TABLE "MatchRoundDrawing" (
    "MatchId" uuid NOT NULL REFERENCES "MatchRecord"("MatchId") ON DELETE CASCADE,
    "Round" integer NOT NULL CHECK ("Round" BETWEEN 1 AND 1000),
    "Topic" text NOT NULL,
    "Word" text NOT NULL,
    "PlayerCount" integer NOT NULL CHECK ("PlayerCount" BETWEEN 1 AND 64),
    "StrokeCount" integer NOT NULL CHECK ("StrokeCount" BETWEEN 0 AND 96000),
    "Payload" bytea NOT NULL CHECK (octet_length("Payload") BETWEEN 1 AND 8388608),
    "ExpiresAt" timestamptz NOT NULL,
    PRIMARY KEY ("MatchId","Round")
);
CREATE INDEX "MatchRoundDrawing_ExpiresAt" ON "MatchRoundDrawing"("ExpiresAt","MatchId","Round");
