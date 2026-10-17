CREATE TABLE "Mail" (
    "Id" uuid PRIMARY KEY,
    "AccountId" uuid REFERENCES "Account"("Id") ON DELETE CASCADE,
    "Title" text NOT NULL,
    "Message" text NOT NULL DEFAULT '',
    "Coins" integer NOT NULL DEFAULT 0 CHECK ("Coins">=0),
    "PaidGems" integer NOT NULL DEFAULT 0 CHECK ("PaidGems">=0),
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "ExpiresAt" timestamptz NOT NULL,
    CHECK ("Coins"::bigint+"PaidGems"::bigint>0)
);
CREATE INDEX "Mail_AccountExpiry" ON "Mail" ("AccountId","ExpiresAt","CreatedAt" DESC);

CREATE TABLE "MailClaim" (
    "AccountId" uuid NOT NULL REFERENCES "Account"("Id") ON DELETE CASCADE,
    "MailId" uuid NOT NULL REFERENCES "Mail"("Id") ON DELETE CASCADE,
    "ClaimedAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("AccountId","MailId")
);
CREATE INDEX "MailClaim_Mail" ON "MailClaim" ("MailId");
