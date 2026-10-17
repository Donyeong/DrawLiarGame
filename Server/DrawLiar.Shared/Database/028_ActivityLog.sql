CREATE TABLE "ActivityLog" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "Kind" text NOT NULL,
    "AccountId" uuid,
    "TargetAccountId" uuid,
    "RoomId" uuid,
    "MatchId" uuid,
    "Detail" json NOT NULL DEFAULT '{}'::json,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX "ActivityLog_Account" ON "ActivityLog" ("AccountId","Id") WHERE "AccountId" IS NOT NULL;
CREATE INDEX "ActivityLog_Target" ON "ActivityLog" ("TargetAccountId","Id") WHERE "TargetAccountId" IS NOT NULL;
CREATE INDEX "ActivityLog_Room" ON "ActivityLog" ("RoomId","Id") WHERE "RoomId" IS NOT NULL;
CREATE INDEX "ActivityLog_CreatedAt" ON "ActivityLog" ("CreatedAt");
CREATE INDEX "MatchRecord_Room" ON "MatchRecord" ("RoomId","PlayedAt" DESC);
