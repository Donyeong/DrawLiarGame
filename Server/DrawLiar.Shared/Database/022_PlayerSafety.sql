CREATE TABLE "AccountBlock" (
    "BlockerAccountId" uuid NOT NULL REFERENCES "Account"("Id") ON DELETE CASCADE,
    "BlockedAccountId" uuid NOT NULL REFERENCES "Account"("Id") ON DELETE CASCADE,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("BlockerAccountId", "BlockedAccountId"),
    CHECK ("BlockerAccountId" <> "BlockedAccountId")
);
CREATE INDEX "AccountBlock_Blocked" ON "AccountBlock" ("BlockedAccountId");

CREATE TABLE "PlayerReport" (
    "Id" uuid PRIMARY KEY,
    "ReporterAccountId" uuid NOT NULL REFERENCES "Account"("Id") ON DELETE CASCADE,
    "TargetAccountId" uuid NOT NULL REFERENCES "Account"("Id") ON DELETE CASCADE,
    "Reason" text NOT NULL,
    "Context" text NOT NULL DEFAULT '',
    "RoomId" uuid,
    "WorkshopTopicId" uuid,
    "Status" text NOT NULL DEFAULT 'Open',
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "ResolvedAt" timestamptz,
    CHECK ("ReporterAccountId" <> "TargetAccountId"),
    CHECK ("Status" IN ('Open', 'Resolved', 'Dismissed'))
);
CREATE INDEX "PlayerReport_Status" ON "PlayerReport" ("Status", "CreatedAt" DESC);
CREATE INDEX "PlayerReport_Reporter" ON "PlayerReport" ("ReporterAccountId", "CreatedAt");
