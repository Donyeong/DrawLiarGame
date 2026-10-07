INSERT INTO "OwnedAccessory" ("AccountId","Accessory")
SELECT a."Id", p."Accessory"
FROM "Account" a
CROSS JOIN (VALUES (1), (4096), (4194304), (131072)) AS p("Accessory")
WHERE NOT EXISTS (
    SELECT 1 FROM "OwnedAccessory" o
    WHERE o."AccountId"=a."Id" AND (o."Accessory" & p."Accessory")=p."Accessory"
)
ON CONFLICT ("AccountId","Accessory") DO NOTHING;
