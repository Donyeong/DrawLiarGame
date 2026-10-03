ALTER TABLE "Room" ADD COLUMN IF NOT EXISTS "RoomCode" text;
ALTER TABLE "Room" ADD COLUMN IF NOT EXISTS "Established" boolean NOT NULL DEFAULT false;
CREATE TABLE IF NOT EXISTS "RoomAdmission" (
    "Id" text PRIMARY KEY, "RoomId" uuid NOT NULL REFERENCES "Room"("RoomId") ON DELETE CASCADE,
    "AccountId" uuid NOT NULL REFERENCES "Account"("Id"), "IsSpectator" boolean NOT NULL, "ExpiresAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "RoomAdmission_RoomId" ON "RoomAdmission" ("RoomId");
DO $$
DECLARE room_record record; candidate text;
BEGIN
    FOR room_record IN SELECT "RoomId" FROM "Room" WHERE "RoomCode" IS NULL LOOP
        LOOP
            candidate := translate(upper(substr(md5(gen_random_uuid()::text), 1, 6)), '01', 'XY');
            EXIT WHEN NOT EXISTS (SELECT 1 FROM "Room" WHERE "RoomCode" = candidate);
        END LOOP;
        UPDATE "Room" SET "RoomCode" = candidate WHERE "RoomId" = room_record."RoomId";
    END LOOP;
END $$;
ALTER TABLE "Room" ALTER COLUMN "RoomCode" SET NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "Room_RoomCode" ON "Room" ("RoomCode");
