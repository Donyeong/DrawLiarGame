CREATE TABLE IF NOT EXISTS "RoomInvitation" (
    "Id" uuid PRIMARY KEY,
    "RoomId" uuid NOT NULL REFERENCES "Room"("RoomId") ON DELETE CASCADE,
    "FromAccountId" uuid NOT NULL REFERENCES "Account"("Id") ON DELETE CASCADE,
    "ToAccountId" uuid NOT NULL REFERENCES "Account"("Id") ON DELETE CASCADE,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "ExpiresAt" timestamptz NOT NULL DEFAULT now()+interval '5 minutes',
    "Pending" boolean NOT NULL DEFAULT true,
    "RespondedAt" timestamptz,
    UNIQUE ("RoomId","FromAccountId","ToAccountId"),
    CHECK ("FromAccountId" <> "ToAccountId"),
    CHECK ("ExpiresAt" > "CreatedAt")
);
CREATE INDEX IF NOT EXISTS "RoomInvitation_Receiver" ON "RoomInvitation"("ToAccountId","ExpiresAt") WHERE "Pending";
CREATE INDEX IF NOT EXISTS "RoomInvitation_Sender" ON "RoomInvitation"("FromAccountId","ExpiresAt") WHERE "Pending";
CREATE INDEX IF NOT EXISTS "RoomInvitation_ExpiresAt" ON "RoomInvitation"("ExpiresAt");
CREATE INDEX IF NOT EXISTS "Friendship_ToAccountId" ON "Friendship"("ToAccountId");
