ALTER TABLE "Account" ALTER COLUMN "Accessory" TYPE bigint USING "Accessory"::bigint;
ALTER TABLE "OwnedAccessory" ALTER COLUMN "Accessory" TYPE bigint USING "Accessory"::bigint;

UPDATE "Account"
SET "Accessory"="Accessory" & ~4063232::bigint
WHERE ("Accessory" & 4063232::bigint) <> 0;
