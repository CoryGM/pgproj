CREATE FUNCTION inventory.reject_stock_movement_change()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'inventory.stock_movement is append-only: % is not allowed; insert a compensating movement instead', TG_OP
        USING ERRCODE = 'integrity_constraint_violation';
END;
$$;

COMMENT ON FUNCTION inventory.reject_stock_movement_change() IS $$
Trigger function for inventory.stock_movement that makes the ledger append-only by rejecting every update and delete.
$$;
