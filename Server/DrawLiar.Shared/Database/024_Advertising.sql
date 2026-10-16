CREATE TABLE "AdRewardTicket" (
    "Id" uuid PRIMARY KEY,
    "Ticket" text NOT NULL UNIQUE,
    "AccountId" uuid NOT NULL REFERENCES "Account"("Id"),
    "OperationId" uuid NOT NULL,
    "UserId" text NOT NULL,
    "SessionHash" text NOT NULL,
    "Platform" text NOT NULL CHECK ("Platform" IN ('Android','WebGL')),
    "Provider" text NOT NULL CHECK ("Provider" IN ('AdMob','GoogleH5','Subscription')),
    "RewardDay" date NOT NULL,
    "RewardPaidGems" integer NOT NULL CHECK ("RewardPaidGems" BETWEEN 1 AND 100),
    "State" text NOT NULL DEFAULT 'Pending' CHECK ("State" IN ('Pending','Completed')),
    "CreatedAt" timestamptz NOT NULL,
    "ExpiresAt" timestamptz NOT NULL,
    "CompletedAt" timestamptz,
    UNIQUE ("AccountId","OperationId")
);
CREATE INDEX "AdRewardTicket_AccountDay" ON "AdRewardTicket" ("AccountId","RewardDay");
CREATE TABLE "AdRewardDailyClaim" (
    "AccountId" uuid NOT NULL REFERENCES "Account"("Id"),
    "RewardDay" date NOT NULL,
    "TicketId" uuid NOT NULL UNIQUE REFERENCES "AdRewardTicket"("Id"),
    "RewardPaidGems" integer NOT NULL CHECK ("RewardPaidGems" BETWEEN 1 AND 100),
    "CreatedAt" timestamptz NOT NULL,
    PRIMARY KEY ("AccountId","RewardDay")
);
CREATE TABLE "AdRewardTransaction" (
    "Provider" text NOT NULL,
    "TransactionHash" text NOT NULL,
    "TicketId" uuid NOT NULL UNIQUE REFERENCES "AdRewardTicket"("Id"),
    "CreatedAt" timestamptz NOT NULL,
    PRIMARY KEY ("Provider","TransactionHash")
);
