ALTER TABLE "Account" ADD COLUMN "PaidGems" integer NOT NULL DEFAULT 0 CHECK ("PaidGems">=0);
ALTER TABLE "Account" ADD COLUMN "SubscriptionExpiresAt" timestamptz;
ALTER TABLE "Account" ADD COLUMN "ShowSubscriberBadge" boolean NOT NULL DEFAULT false;

CREATE TABLE "CommerceOrder" (
    "OrderId" uuid PRIMARY KEY,
    "AccountId" uuid NOT NULL REFERENCES "Account"("Id"),
    "OperationId" uuid NOT NULL,
    "ProductId" text NOT NULL,
    "Provider" text NOT NULL CHECK ("Provider" IN ('Steam','GooglePlay')),
    "AppScope" text NOT NULL,
    "StoreProductId" text NOT NULL,
    "AccountBinding" text NOT NULL,
    "ProductJson" jsonb NOT NULL,
    "State" text NOT NULL DEFAULT 'Prepared' CHECK ("State" IN ('Prepared','Completed','ReviewRequired')),
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "CompletedAt" timestamptz,
    UNIQUE ("AccountId","OperationId")
);
CREATE INDEX "CommerceOrder_Account" ON "CommerceOrder" ("AccountId","CreatedAt" DESC);
CREATE TABLE "CommerceTransaction" (
    "Provider" text NOT NULL, "AppScope" text NOT NULL, "TransactionHash" text NOT NULL,
    "OrderId" uuid NOT NULL UNIQUE REFERENCES "CommerceOrder"("OrderId"),
    "AccountId" uuid NOT NULL REFERENCES "Account"("Id"),
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("Provider","AppScope","TransactionHash")
);
CREATE TABLE "CommerceLedger" (
    "AccountId" uuid NOT NULL REFERENCES "Account"("Id"), "OperationId" uuid NOT NULL,
    "ProductId" text NOT NULL, "Kind" text NOT NULL,
    "PaidGemsDelta" integer NOT NULL, "BalanceAfter" integer NOT NULL CHECK ("BalanceAfter">=0),
    "SubscriptionExpiresAt" timestamptz, "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("AccountId","Kind","OperationId")
);
CREATE TABLE "CommerceReview" (
    "OrderId" uuid PRIMARY KEY REFERENCES "CommerceOrder"("OrderId"),
    "Reason" text NOT NULL CHECK ("Reason" IN ('Refunded','Revoked')),
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
