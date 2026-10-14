ALTER TABLE "Account" ADD COLUMN "Cosmetics" jsonb NOT NULL DEFAULT '{}'::jsonb;

CREATE TABLE "OwnedCosmetic" (
    "AccountId" uuid NOT NULL REFERENCES "Account"("Id"),
    "ProductId" text NOT NULL,
    "PurchasedAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("AccountId","ProductId")
);
