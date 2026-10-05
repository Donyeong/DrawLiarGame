DO $$
DECLARE constraint_row record;
BEGIN
    FOR constraint_row IN
        SELECT conname,conrelid FROM pg_constraint
        WHERE contype='c' AND (
            conrelid='"MatchRecord"'::regclass AND regexp_replace(pg_get_expr(conbin,conrelid),'[[:space:]()]','','g')='"PlayerCount">=3AND"PlayerCount"<=8'
            OR conrelid='"AccountMatch"'::regclass AND regexp_replace(pg_get_expr(conbin,conrelid),'[[:space:]()]','','g')='"Rank">=1AND"Rank"<=8')
    LOOP
        EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I',constraint_row.conrelid::regclass,constraint_row.conname);
    END LOOP;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='"MatchRecord"'::regclass AND conname='MatchRecord_PlayerCountBounds') THEN
        ALTER TABLE "MatchRecord" ADD CONSTRAINT "MatchRecord_PlayerCountBounds" CHECK ("PlayerCount" BETWEEN 3 AND 64);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='"AccountMatch"'::regclass AND conname='AccountMatch_RankBounds') THEN
        ALTER TABLE "AccountMatch" ADD CONSTRAINT "AccountMatch_RankBounds" CHECK ("Rank" BETWEEN 1 AND 64);
    END IF;
END $$;
