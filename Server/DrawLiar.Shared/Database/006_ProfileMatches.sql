CREATE TABLE IF NOT EXISTS "RoomGameAuthority" (
    "RoomId" uuid PRIMARY KEY, "NodeId" text NOT NULL REFERENCES "DedicatedNode"("NodeId")
);
INSERT INTO "RoomGameAuthority" ("RoomId","NodeId")
    SELECT "RoomId","NodeId" FROM "Room" ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS "RoomGameAdmission" (
    "RoomId" uuid NOT NULL REFERENCES "RoomGameAuthority"("RoomId"),
    "AccountId" uuid NOT NULL REFERENCES "Account"("Id"),
    PRIMARY KEY ("RoomId","AccountId")
);
INSERT INTO "RoomGameAdmission" ("RoomId","AccountId")
    SELECT r."RoomId",a."Id" FROM "Room" r
    JOIN "Account" a ON r."PlayerAccountIds" ? a."Id"::text OR r."SpectatorAccountIds" ? a."Id"::text
    ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS "MatchRecord" (
    "MatchId" uuid PRIMARY KEY, "RoomId" uuid NOT NULL REFERENCES "RoomGameAuthority"("RoomId"),
    "PlayedAt" timestamptz NOT NULL, "PlayerCount" integer NOT NULL CHECK ("PlayerCount" BETWEEN 3 AND 8),
    "Mode" integer NOT NULL CHECK ("Mode" BETWEEN 0 AND 1),
    "RoundCount" integer NOT NULL CHECK ("RoundCount" > 0), "PayloadHash" text NOT NULL
);
CREATE TABLE IF NOT EXISTS "AccountMatch" (
    "MatchId" uuid NOT NULL REFERENCES "MatchRecord"("MatchId"),
    "AccountId" uuid NOT NULL REFERENCES "Account"("Id"),
    "Score" integer NOT NULL CHECK ("Score" >= 0), "Rank" integer NOT NULL CHECK ("Rank" BETWEEN 1 AND 8),
    "Won" boolean NOT NULL, "RoundsPlayed" integer NOT NULL CHECK ("RoundsPlayed" > 0),
    "CitizenRounds" integer NOT NULL CHECK ("CitizenRounds" >= 0),
    "LiarRounds" integer NOT NULL CHECK ("LiarRounds" >= 0),
    "CorrectVotes" integer NOT NULL CHECK ("CorrectVotes" >= 0),
    "CorrectGuesses" integer NOT NULL CHECK ("CorrectGuesses" >= 0),
    PRIMARY KEY ("MatchId","AccountId"),
    CHECK ("CitizenRounds" + "LiarRounds" = "RoundsPlayed"),
    CHECK ("CorrectVotes" <= "CitizenRounds" AND "CorrectGuesses" <= "LiarRounds")
);
CREATE INDEX IF NOT EXISTS "AccountMatch_AccountId" ON "AccountMatch"("AccountId","MatchId");
