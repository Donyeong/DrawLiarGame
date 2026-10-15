CREATE INDEX IF NOT EXISTS "Session_ParentHash" ON "Session"("ParentHash");
CREATE INDEX IF NOT EXISTS "AuthChallenge_ExpiresAt" ON "AuthChallenge"("ExpiresAt","Id");
CREATE INDEX IF NOT EXISTS "Friendship_IncomingPending" ON "Friendship"("ToAccountId","CreatedAt") WHERE NOT "Accepted";
CREATE INDEX IF NOT EXISTS "Friendship_PendingExpiry" ON "Friendship"("CreatedAt","FromAccountId","ToAccountId") WHERE NOT "Accepted";
CREATE INDEX IF NOT EXISTS "Friendship_ToAccountId" ON "Friendship"("ToAccountId");
