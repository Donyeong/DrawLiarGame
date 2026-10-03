CREATE TABLE IF NOT EXISTS "Account" (
    "Id" uuid PRIMARY KEY, "Email" text UNIQUE, "PasswordHash" text,
    "DisplayName" text NOT NULL, "AvatarColor" integer NOT NULL DEFAULT 0,
    "Accessory" integer NOT NULL DEFAULT 0, "Coins" integer NOT NULL DEFAULT 500,
    "IsBanned" boolean NOT NULL DEFAULT false, "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS "ExternalIdentity" (
    "Provider" text NOT NULL, "Subject" text NOT NULL, "AccountId" uuid NOT NULL REFERENCES "Account"("Id"),
    PRIMARY KEY ("Provider", "Subject"), UNIQUE ("AccountId", "Provider")
);
CREATE TABLE IF NOT EXISTS "Session" (
    "Hash" text PRIMARY KEY, "AccountId" uuid NOT NULL REFERENCES "Account"("Id"),
    "Scope" text NOT NULL, "ExpiresAt" timestamptz NOT NULL,
    "ParentHash" text REFERENCES "Session"("Hash") ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS "Session_AccountId" ON "Session"("AccountId");
CREATE INDEX IF NOT EXISTS "Session_ExpiresAt" ON "Session"("ExpiresAt");
CREATE TABLE IF NOT EXISTS "AuthChallenge" (
    "Id" text PRIMARY KEY, "Nonce" text NOT NULL, "ClientId" text NOT NULL,
    "Platform" text NOT NULL, "AccountId" uuid REFERENCES "Account"("Id"), "ExpiresAt" timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS "GameNode" (
    "NodeId" text PRIMARY KEY, "PublicUrl" text NOT NULL, "HeartbeatAt" timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS "DedicatedNode" (
    "NodeId" text PRIMARY KEY, "PublicUrl" text NOT NULL, "Capacity" integer NOT NULL,
    "HeartbeatAt" timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS "Room" (
    "RoomId" uuid PRIMARY KEY, "OwnerAccountId" uuid NOT NULL REFERENCES "Account"("Id"),
    "NodeId" text NOT NULL REFERENCES "DedicatedNode"("NodeId"), "Settings" jsonb NOT NULL,
    "PlayerCount" integer NOT NULL DEFAULT 0, "SpectatorCount" integer NOT NULL DEFAULT 0,
    "IsInProgress" boolean NOT NULL DEFAULT false, "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    "PlayerAccountIds" jsonb NOT NULL DEFAULT '[]', "SpectatorAccountIds" jsonb NOT NULL DEFAULT '[]',
    "CustomTopics" jsonb NOT NULL DEFAULT '[]'
);
CREATE TABLE IF NOT EXISTS "JoinTicket" (
    "Hash" text PRIMARY KEY, "AccountId" uuid NOT NULL REFERENCES "Account"("Id"),
    "RoomId" uuid NOT NULL REFERENCES "Room"("RoomId") ON DELETE CASCADE,
    "SessionHash" text NOT NULL REFERENCES "Session"("Hash") ON DELETE CASCADE,
    "IsSpectator" boolean NOT NULL, "ExpiresAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "JoinTicket_RoomId" ON "JoinTicket"("RoomId");
CREATE TABLE IF NOT EXISTS "Friendship" (
    "FromAccountId" uuid NOT NULL REFERENCES "Account"("Id"),
    "ToAccountId" uuid NOT NULL REFERENCES "Account"("Id"), "Accepted" boolean NOT NULL DEFAULT false,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(), PRIMARY KEY ("FromAccountId", "ToAccountId"),
    CHECK ("FromAccountId" <> "ToAccountId")
);
CREATE UNIQUE INDEX IF NOT EXISTS "Friendship_Pair" ON "Friendship"(LEAST("FromAccountId", "ToAccountId"), GREATEST("FromAccountId", "ToAccountId"));
CREATE TABLE IF NOT EXISTS "OwnedAccessory" (
    "AccountId" uuid NOT NULL REFERENCES "Account"("Id"), "Accessory" integer NOT NULL,
    PRIMARY KEY ("AccountId", "Accessory")
);
CREATE TABLE IF NOT EXISTS "PurchaseReceipt" (
    "AccountId" uuid NOT NULL REFERENCES "Account"("Id"), "OperationId" uuid NOT NULL,
    "ProductId" text NOT NULL, "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("AccountId", "OperationId")
);
