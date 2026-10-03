ALTER TABLE "Account" ADD COLUMN "GuestId" uuid;
ALTER TABLE "Account" ADD COLUMN "GuestSecretHash" varchar(64);
ALTER TABLE "Account" ADD CONSTRAINT "Account_GuestCredentialPair"
    CHECK (("GuestId" IS NULL AND "GuestSecretHash" IS NULL)
        OR ("GuestId" IS NOT NULL AND "GuestSecretHash" IS NOT NULL AND "GuestSecretHash" ~ '^[0-9A-Fa-f]{64}$'));
CREATE UNIQUE INDEX "Account_GuestId" ON "Account" ("GuestId") WHERE "GuestId" IS NOT NULL;
