CREATE VIEW catalog.v_category_tree AS
WITH RECURSIVE tree AS (
    SELECT c.id,
           c.parent_category_id,
           c.id AS root_category_id,
           c.code,
           c.name,
           c.sort_order,
           0 AS depth,
           c.name::text AS path,
           ARRAY[c.sort_order, 0] AS sort_key
    FROM catalog.category AS c
    WHERE c.parent_category_id IS NULL
    UNION ALL
    SELECT c.id,
           c.parent_category_id,
           t.root_category_id,
           c.code,
           c.name,
           c.sort_order,
           t.depth + 1,
           t.path || ' > ' || c.name::text,
           t.sort_key || c.sort_order
    FROM catalog.category AS c
    JOIN tree AS t ON t.id = c.parent_category_id
)
SELECT t.id,
       t.parent_category_id,
       t.root_category_id,
       t.code,
       t.name,
       t.depth,
       t.path,
       (SELECT count(*) FROM catalog.product AS p WHERE p.category_id = t.id AND p.is_active) AS active_product_count
FROM tree AS t
ORDER BY t.sort_key, t.path;

COMMENT ON VIEW catalog.v_category_tree IS $$
The category hierarchy flattened with a recursive query: each category with its depth (0 for a root), its full path, its root, and how many active products sit directly in it. Rows are in tree order.
$$;
