ALTER TABLE "Session" ADD COLUMN "DedicatedRoomId" uuid REFERENCES "Room"("RoomId") ON DELETE SET NULL;
CREATE INDEX "Session_DedicatedRoomId" ON "Session" ("DedicatedRoomId") WHERE "DedicatedRoomId" IS NOT NULL;

CREATE TABLE "RoomKick" (
    "RoomId" uuid NOT NULL REFERENCES "Room"("RoomId") ON DELETE CASCADE,
    "AccountId" uuid NOT NULL REFERENCES "Account"("Id") ON DELETE CASCADE,
    "KickedByAccountId" uuid NOT NULL REFERENCES "Account"("Id"),
    "OperationId" uuid NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("RoomId", "AccountId"),
    UNIQUE ("RoomId", "OperationId"),
    CHECK ("AccountId" <> "KickedByAccountId")
);
