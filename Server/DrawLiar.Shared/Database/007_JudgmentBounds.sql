DO $$
DECLARE
    legacy_name text;
    existing_expression text;
BEGIN
    FOR legacy_name IN
        SELECT "conname" FROM "pg_constraint"
        WHERE "conrelid" = '"AccountMatch"'::regclass AND "contype" = 'c'
            AND regexp_replace(pg_get_expr("conbin", "conrelid"), '[[:space:]()]', '', 'g')
                = '"CorrectVotes"<="CitizenRounds"AND"CorrectGuesses"<="LiarRounds"'
    LOOP
        EXECUTE format('ALTER TABLE "AccountMatch" DROP CONSTRAINT %I', legacy_name);
    END LOOP;

    SELECT regexp_replace(pg_get_expr("conbin", "conrelid"), '[[:space:]()]', '', 'g')
        INTO existing_expression FROM "pg_constraint"
        WHERE "conrelid" = '"AccountMatch"'::regclass AND "conname" = 'AccountMatch_JudgmentBounds';
    IF existing_expression IS NULL THEN
        ALTER TABLE "AccountMatch" ADD CONSTRAINT "AccountMatch_JudgmentBounds"
            CHECK ("CorrectVotes" <= "RoundsPlayed" AND "CorrectGuesses" <= "LiarRounds");
    ELSIF existing_expression <> '"CorrectVotes"<="RoundsPlayed"AND"CorrectGuesses"<="LiarRounds"' THEN
        RAISE EXCEPTION 'AccountMatch_JudgmentBounds definition mismatch';
    END IF;
END $$;
