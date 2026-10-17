CREATE TABLE "CommerceFinalization" (
    "OrderId" uuid PRIMARY KEY REFERENCES "CommerceOrder"("OrderId"),
    "ProtectedProof" text NOT NULL,
    "Attempts" integer NOT NULL DEFAULT 0,
    "NextAttemptAt" timestamptz NOT NULL DEFAULT now(),
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX "CommerceFinalization_Due" ON "CommerceFinalization" ("NextAttemptAt");
