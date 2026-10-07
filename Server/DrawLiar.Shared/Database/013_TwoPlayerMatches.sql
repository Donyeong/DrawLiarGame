ALTER TABLE "MatchRecord" DROP CONSTRAINT "MatchRecord_PlayerCountBounds";
ALTER TABLE "MatchRecord" ADD CONSTRAINT "MatchRecord_PlayerCountBounds" CHECK ("PlayerCount" BETWEEN 2 AND 64);
