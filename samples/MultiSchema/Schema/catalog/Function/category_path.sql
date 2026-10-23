CREATE FUNCTION catalog.category_path(p_category_id uuid)
RETURNS text
LANGUAGE sql
STABLE
AS $$
    WITH RECURSIVE ancestors AS (
        SELECT c.id, c.parent_category_id, c.name::text AS name, 1 AS depth
        FROM catalog.category AS c
        WHERE c.id = p_category_id
        UNION ALL
        SELECT p.id, p.parent_category_id, p.name::text, a.depth + 1
        FROM catalog.category AS p
        JOIN ancestors AS a ON a.parent_category_id = p.id
    )
    SELECT string_agg(name, ' > ' ORDER BY depth DESC) FROM ancestors;
$$;

COMMENT ON FUNCTION catalog.category_path(uuid) IS $$
The names of a category and all its ancestors, root first, joined with " > ". Uses a recursive query. Null for an unknown category.

@example: Camping > Tents > Backpacking Tents
$$;
