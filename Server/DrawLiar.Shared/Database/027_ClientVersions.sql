CREATE TABLE "ClientVersionPolicy" (
    "Platform" text PRIMARY KEY CHECK ("Platform" IN ('Android','Windows')),
    "MinimumVersion" text NOT NULL DEFAULT '',
    "LatestVersion" text NOT NULL DEFAULT '',
    "StoreUrl" text NOT NULL DEFAULT '',
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
