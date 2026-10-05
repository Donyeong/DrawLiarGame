ALTER TABLE "Room" ADD COLUMN IF NOT EXISTS "PasswordHash" text;
ALTER TABLE "Room" ADD COLUMN IF NOT EXISTS "AccessVersion" bigint NOT NULL DEFAULT 0;
ALTER TABLE "Room" ADD COLUMN IF NOT EXISTS "ConfigurationVersion" bigint NOT NULL DEFAULT 0;
ALTER TABLE "Room" ADD COLUMN IF NOT EXISTS "LastConfigurationId" uuid;
ALTER TABLE "JoinTicket" ADD COLUMN IF NOT EXISTS "AccessVersion" bigint NOT NULL DEFAULT 0;
ALTER TABLE "RoomAdmission" ADD COLUMN IF NOT EXISTS "AccessVersion" bigint NOT NULL DEFAULT 0;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=current_schema()
        AND table_name='Room' AND column_name='ConfirmedPlayerAccountIds') THEN
        ALTER TABLE "Room" ADD COLUMN "ConfirmedPlayerAccountIds" jsonb NOT NULL DEFAULT '[]';
        ALTER TABLE "Room" ADD COLUMN "ConfirmedSpectatorAccountIds" jsonb NOT NULL DEFAULT '[]';
        UPDATE "Room" r SET
            "ConfirmedPlayerAccountIds"=COALESCE((SELECT jsonb_agg(id) FROM jsonb_array_elements(r."PlayerAccountIds") id
                WHERE NOT EXISTS (SELECT 1 FROM "RoomAdmission" a WHERE a."RoomId"=r."RoomId"
                    AND a."AccountId"::text=id#>>'{}' AND a."ExpiresAt">now())), '[]'),
            "ConfirmedSpectatorAccountIds"=COALESCE((SELECT jsonb_agg(id) FROM jsonb_array_elements(r."SpectatorAccountIds") id
                WHERE NOT EXISTS (SELECT 1 FROM "RoomAdmission" a WHERE a."RoomId"=r."RoomId"
                    AND a."AccountId"::text=id#>>'{}' AND a."ExpiresAt">now())), '[]');
    END IF;
END $$;
